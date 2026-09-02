using BillingCore.Domain;
using BillingCore.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace BillingCore.Services;

public record DeclinedPayload(Guid InvoiceId, string DeclineClass, long ResidualCents);

public record UpdateInstrumentDto(string NewToken, string? Brand, string? Last4, string? FundingType, string? Kind);

/// <summary>
/// Recovery context (HLD 5b): decline-class ladders as durable state rows driven by a worker on the
/// virtual clock. Notifications are ladder steps with idempotency keys (P13). Self-serve links wake
/// ladders early (F5); retries re-enter the same single-writer collection path.
/// </summary>
public class RecoveryService
{
    private readonly IDbContextFactory<BillingDb> _dbf;
    private readonly IClock _clock;
    private readonly ILogger<RecoveryService> _log;

    public RecoveryService(IDbContextFactory<BillingDb> dbf, IClock clock, ILogger<RecoveryService> log)
    {
        _dbf = dbf;
        _clock = clock;
        _log = log;
    }

    /// <summary>Outbox handler for InvoiceDeclined: start or advance the invoice's ladder per policy.</summary>
    public async Task HandleDeclinedAsync(DeclinedPayload payload)
    {
        var now = _clock.UtcNow;
        await using var db = await _dbf.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();

        var invoice = await db.Invoices
            .FromSqlInterpolated($"SELECT * FROM invoices WHERE id = {payload.InvoiceId} FOR UPDATE")
            .AsTracking()
            .SingleOrDefaultAsync();
        if (invoice is null || invoice.State != "dunning")
        {
            await tx.CommitAsync();
            return; // recovered/written off in the meantime; replay-safe no-op
        }

        var agreement = await db.Agreements.SingleAsync(a => a.Id == invoice.AgreementId);
        var policyRow = await db.Policies.SingleAsync(p => p.Id == agreement.PolicyId);
        var policy = Json.Deserialize<PolicyDef>(policyRow.DefinitionJson);

        if (payload.DeclineClass == "hard")
        {
            // F3: hard declines never re-auth (P1). Terminal immediately.
            invoice.State = "written_off";
            agreement.State = "past_due";
            foreach (var l in LiveLadders(db, invoice.Id)) { l.State = "exhausted"; l.CompletedAt = now; }
            CollectionService.EnqueueOutbox(db, "WrittenOff",
                new { invoiceId = invoice.Id, memberId = invoice.MemberId, studioId = invoice.StudioId, reason = "hard_decline" }, now);
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return;
        }

        var ladder = LiveLadders(db, invoice.Id).FirstOrDefault();
        if (ladder is null)
        {
            ladder = new Ladder
            {
                Id = Guid.NewGuid(),
                InvoiceId = invoice.Id,
                PolicyId = agreement.PolicyId,
                Step = 0,
                SelfServeToken = Guid.NewGuid(),
                CreatedAt = now
            };
            db.Ladders.Add(ladder);
        }
        else
        {
            ladder.Step++; // a retry declined again
        }
        ladder.LastDeclineClass = payload.DeclineClass;

        if (payload.DeclineClass == "fixable")
        {
            ladder.State = "waiting_member";
            ladder.NextActionAt = now.AddDays(policy.Fixable.GiveUpDays); // give-up horizon
            EnqueueNotify(db, ladder, invoice, "payment_fix_needed", now);
        }
        else
        {
            var delays = payload.DeclineClass == "bank_return" ? policy.BankReturn.RetryDelaysDays : policy.Soft.RetryDelaysDays;
            if (ladder.Step < delays.Count)
            {
                ladder.State = "active";
                ladder.NextActionAt = now.AddDays(delays[ladder.Step]);
                EnqueueNotify(db, ladder, invoice, "payment_failed", now);
            }
            else
            {
                ladder.State = "exhausted";
                ladder.CompletedAt = now;
                invoice.State = "written_off";
                agreement.State = "past_due";
                CollectionService.EnqueueOutbox(db, "WrittenOff",
                    new { invoiceId = invoice.Id, memberId = invoice.MemberId, studioId = invoice.StudioId, reason = "retries_exhausted" }, now);
            }
        }

        await db.SaveChangesAsync();
        await tx.CommitAsync();
        _log.LogInformation("ladder {Ladder} for invoice {Invoice}: step {Step} state {State} next {Next}",
            ladder.Id, invoice.Id, ladder.Step, ladder.State, ladder.NextActionAt);
    }

    /// <summary>Ladder worker tick: fire due retries, give up on stale waiting_member ladders.</summary>
    public async Task TickAsync()
    {
        var now = _clock.UtcNow;
        await using var db = await _dbf.CreateDbContextAsync();

        var dueRetries = await db.Ladders
            .Where(l => l.State == "active" && l.NextActionAt != null && l.NextActionAt <= now)
            .ToListAsync();
        foreach (var ladder in dueRetries)
        {
            ladder.State = "dispatched";
            db.QueueItems.Add(new QueueItem
            {
                Kind = "recollect",
                PayloadJson = Json.Serialize(new { invoiceId = ladder.InvoiceId, ladderId = ladder.Id }),
                AvailableAt = now
            });
        }

        var givenUp = await db.Ladders
            .Where(l => l.State == "waiting_member" && l.NextActionAt != null && l.NextActionAt <= now)
            .ToListAsync();
        foreach (var ladder in givenUp)
        {
            ladder.State = "exhausted";
            ladder.CompletedAt = now;
            var invoice = await db.Invoices.SingleAsync(i => i.Id == ladder.InvoiceId);
            if (invoice.State == "dunning")
            {
                invoice.State = "written_off";
                var agreement = await db.Agreements.SingleAsync(a => a.Id == invoice.AgreementId);
                agreement.State = "past_due";
                CollectionService.EnqueueOutbox(db, "WrittenOff",
                    new { invoiceId = invoice.Id, memberId = invoice.MemberId, studioId = invoice.StudioId, reason = "member_never_fixed" }, now);
            }
        }

        if (dueRetries.Count > 0 || givenUp.Count > 0)
            await db.SaveChangesAsync();
    }

    /// <summary>F5: an instrument update wakes the member's ladders immediately.</summary>
    public async Task<int> WakeForMemberAsync(BillingDb db, Guid memberId)
    {
        var now = _clock.UtcNow;
        var ladders = await (
            from l in db.Ladders
            join i in db.Invoices on l.InvoiceId equals i.Id
            where i.MemberId == memberId && (l.State == "waiting_member" || l.State == "active")
            select l).ToListAsync();
        foreach (var ladder in ladders)
        {
            ladder.State = "active";
            ladder.NextActionAt = now;
        }
        return ladders.Count;
    }

    public async Task<object?> GetSelfServeStatusAsync(Guid token)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var ladder = await db.Ladders.SingleOrDefaultAsync(l => l.SelfServeToken == token);
        if (ladder is null) return null;
        var invoice = await db.Invoices.SingleAsync(i => i.Id == ladder.InvoiceId);
        var member = await db.Members.SingleAsync(m => m.Id == invoice.MemberId);
        return new
        {
            member = member.Name,
            invoiceId = invoice.Id,
            invoiceState = invoice.State,
            residualCents = invoice.ResidualCents,
            ladderState = ladder.State,
            ladderStep = ladder.Step,
            lastDeclineClass = ladder.LastDeclineClass
        };
    }

    /// <summary>Member self-serve: replace the card behind the agreement's card slots and wake ladders.</summary>
    public async Task<object?> UpdateInstrumentAsync(Guid token, UpdateInstrumentDto dto)
    {
        var now = _clock.UtcNow;
        await using var db = await _dbf.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();

        var ladder = await db.Ladders.SingleOrDefaultAsync(l => l.SelfServeToken == token);
        if (ladder is null) return null;
        var invoice = await db.Invoices.SingleAsync(i => i.Id == ladder.InvoiceId);
        var agreement = await db.Agreements.SingleAsync(a => a.Id == invoice.AgreementId);

        var instrument = new Instrument
        {
            Id = Guid.NewGuid(),
            MemberId = invoice.MemberId,
            Kind = dto.Kind ?? "card",
            Token = dto.NewToken,
            Brand = dto.Brand,
            Last4 = dto.Last4,
            FundingType = dto.FundingType ?? "unknown",
            Active = true
        };
        db.Instruments.Add(instrument);

        var chain = Json.Deserialize<List<TenderSlotDef>>(agreement.TenderChainJson);
        var replacedIds = new List<Guid>();
        foreach (var slot in chain.Where(s => s.Type == "card" && s.InstrumentId != null))
        {
            replacedIds.Add(slot.InstrumentId!.Value);
            slot.InstrumentId = instrument.Id;
        }
        agreement.TenderChainJson = Json.Serialize(chain);
        foreach (var old in await db.Instruments.Where(i => replacedIds.Contains(i.Id)).ToListAsync())
            old.Active = false;

        var woken = await WakeForMemberAsync(db, invoice.MemberId);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return new { instrumentId = instrument.Id, laddersWoken = woken };
    }

    private static IEnumerable<Ladder> LiveLadders(BillingDb db, Guid invoiceId) =>
        db.Ladders.Where(l => l.InvoiceId == invoiceId &&
                              (l.State == "active" || l.State == "dispatched" || l.State == "waiting_member"))
            .AsEnumerable();

    private static void EnqueueNotify(BillingDb db, Ladder ladder, Invoice invoice, string template, DateTime now) =>
        CollectionService.EnqueueOutbox(db, "NotifyRequest", new
        {
            idempotencyKey = $"ladder:{ladder.Id}:step:{ladder.Step}",
            memberId = invoice.MemberId,
            invoiceId = invoice.Id,
            template,
            selfServeToken = ladder.SelfServeToken,
            residualCents = invoice.ResidualCents
        }, now);
}
