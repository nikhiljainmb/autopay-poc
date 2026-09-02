using BillingCore.Domain;
using BillingCore.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BillingCore.Services;

public record ContractEventDto(
    string EventId,
    string Type,
    int StudioId,
    Guid MemberId,
    string ContractId,
    long AmountCents,
    DateOnly? StartDate,
    List<TenderSlotDef>? TenderChain,
    string? PolicyId,
    Guid? AgreementId,
    DateOnly? PeriodStart,
    string? BillingTrigger = null,
    int? EntitlementRemaining = null);

public record IntakeResult(bool Duplicate, Guid? AgreementId, Guid? InvoiceId, string? Error);

/// <summary>
/// Contract intake ACL (HLD 5c): event-id dedup makes upstream replays no-ops (P3),
/// and amendments are absolute writes ("periodStart := X"), never relative math.
/// </summary>
public class IntakeService
{
    private readonly IDbContextFactory<BillingDb> _dbf;
    private readonly IClock _clock;

    public IntakeService(IDbContextFactory<BillingDb> dbf, IClock clock)
    {
        _dbf = dbf;
        _clock = clock;
    }

    public async Task<IntakeResult> ProcessAsync(ContractEventDto ev)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var now = _clock.UtcNow;

        db.IntakeEvents.Add(new IntakeEvent
        {
            EventId = ev.EventId,
            Type = ev.Type,
            PayloadJson = Json.Serialize(ev),
            ReceivedAt = now
        });
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            return new IntakeResult(true, null, null, null);
        }

        switch (ev.Type)
        {
            case "ContractSold":
            {
                if (ev.StartDate is null || ev.TenderChain is null or { Count: 0 })
                    return new IntakeResult(false, null, null, "ContractSold requires startDate and tenderChain");

                var trigger = string.IsNullOrEmpty(ev.BillingTrigger) ? "calendar" : ev.BillingTrigger;
                var studio = await db.Studios.SingleAsync(s => s.Id == ev.StudioId);
                var agreement = new Agreement
                {
                    Id = Guid.NewGuid(),
                    StudioId = ev.StudioId,
                    MemberId = ev.MemberId,
                    ContractId = ev.ContractId,
                    AmountCents = ev.AmountCents,
                    NextPeriodStart = ev.StartDate.Value,
                    TenderChainJson = Json.Serialize(ev.TenderChain),
                    PolicyId = ev.PolicyId ?? "standard",
                    BillingTrigger = trigger,
                    DepletionSubscriptionLive = trigger == "depletion",
                    EntitlementRemaining = trigger == "depletion" ? (ev.EntitlementRemaining ?? 5) : null,
                    CreatedAt = now
                };
                db.Agreements.Add(agreement);

                // Depletion agreements stay dormant (no open invoice) until entitlement hits zero (S16).
                Guid? invoiceId = null;
                if (trigger == "calendar")
                {
                    var invoice = Materialization.MaterializeNext(agreement, studio, now);
                    db.Invoices.Add(invoice);
                    invoiceId = invoice.Id;
                }

                await db.SaveChangesAsync();
                await tx.CommitAsync();
                return new IntakeResult(false, agreement.Id, invoiceId, null);
            }
            case "ContractAmended":
            {
                var agreement = ev.AgreementId is { } agId
                    ? await db.Agreements.SingleOrDefaultAsync(a => a.Id == agId)
                    : await db.Agreements.SingleOrDefaultAsync(a => a.ContractId == ev.ContractId);
                if (agreement is null) return new IntakeResult(false, null, null, "agreement not found");

                agreement.Version++;
                if (ev.AmountCents > 0) agreement.AmountCents = ev.AmountCents;
                if (ev.PeriodStart is { } ps) agreement.NextPeriodStart = ps;

                // F4: the open, untouched invoice is re-materialized under the amended terms.
                var open = await db.Invoices.SingleOrDefaultAsync(i => i.AgreementId == agreement.Id && i.State == "scheduled");
                if (open is not null)
                {
                    var studio = await db.Studios.SingleAsync(s => s.Id == agreement.StudioId);
                    if (ev.AmountCents > 0)
                    {
                        open.BaseAmountCents = agreement.AmountCents;
                        open.ResidualCents = agreement.AmountCents;
                    }
                    if (ev.PeriodStart is { } newPs)
                    {
                        open.PeriodStart = newPs;
                        open.DueAt = newPs.ToDateTime(new TimeOnly(studio.BillingHourUtc, 0), DateTimeKind.Utc);
                        agreement.NextPeriodStart = newPs.AddMonths(1);
                    }
                }
                await db.SaveChangesAsync();
                await tx.CommitAsync();
                return new IntakeResult(false, agreement.Id, open?.Id, null);
            }
            default:
                return new IntakeResult(false, null, null, $"unknown event type {ev.Type}");
        }
    }
}
