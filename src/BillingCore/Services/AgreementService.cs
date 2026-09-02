using BillingCore.Domain;
using BillingCore.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace BillingCore.Services;

public record PauseAgreementDto(Guid AgreementId, DateOnly From, DateOnly To, long PauseFeeCents);
public record EarlyUnsuspendDto(Guid AgreementId);
public record EntitlementDepletedDto(string EventId, Guid AgreementId, int Remaining);
public record InstrumentEventDto(string EventId, Guid MemberId, Guid? InstrumentId, string? NewToken, string? FundingType, string? Brand, string? Last4);

/// <summary>
/// Agreement lifecycle beyond contract intake: pause windows (S15), depletion wake (S16),
/// instrument vault events (F5), and auto-resume on the virtual clock.
/// </summary>
public class AgreementService
{
    private readonly IDbContextFactory<BillingDb> _dbf;
    private readonly IClock _clock;
    private readonly RecoveryService _recovery;
    private readonly ILogger<AgreementService> _log;

    public AgreementService(IDbContextFactory<BillingDb> dbf, IClock clock, RecoveryService recovery, ILogger<AgreementService> log)
    {
        _dbf = dbf;
        _clock = clock;
        _recovery = recovery;
        _log = log;
    }

    public async Task<object> SchedulePauseAsync(PauseAgreementDto dto)
    {
        var now = _clock.UtcNow;
        await using var db = await _dbf.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();

        var agreement = await db.Agreements
            .FromSqlInterpolated($"SELECT * FROM agreements WHERE id = {dto.AgreementId} FOR UPDATE")
            .AsTracking()
            .SingleAsync();
        var studio = await db.Studios.SingleAsync(s => s.Id == agreement.StudioId);

        agreement.PausedFrom = dto.From;
        agreement.PausedTo = dto.To;
        agreement.PauseFeeCents = dto.PauseFeeCents;
        agreement.State = "paused";
        agreement.Version++;

        // Cancel open cycle invoices that fall inside the pause window (S15).
        var openCycles = await db.Invoices
            .Where(i => i.AgreementId == agreement.Id && i.Kind == "cycle" && i.State == "scheduled"
                        && i.PeriodStart >= dto.From && i.PeriodStart <= dto.To)
            .ToListAsync();
        foreach (var inv in openCycles) inv.State = "canceled";

        Invoice? pauseFee = null;
        if (dto.PauseFeeCents > 0)
        {
            pauseFee = Materialization.MaterializePauseFee(agreement, studio, now);
            db.Invoices.Add(pauseFee);
        }

        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return new { agreement.Id, agreement.State, agreement.PausedFrom, agreement.PausedTo, pauseFeeInvoiceId = pauseFee?.Id, canceledCycles = openCycles.Count };
    }

    public async Task<object> EarlyUnsuspendAsync(EarlyUnsuspendDto dto)
    {
        var now = _clock.UtcNow;
        await using var db = await _dbf.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();

        var agreement = await db.Agreements
            .FromSqlInterpolated($"SELECT * FROM agreements WHERE id = {dto.AgreementId} FOR UPDATE")
            .AsTracking()
            .SingleAsync();
        if (agreement.State != "paused")
        {
            await tx.CommitAsync();
            return new { resumed = false, reason = "not_paused" };
        }

        ResumeAgreement(db, agreement, now, early: true);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return new { resumed = true, agreement.NextPeriodStart, agreement.State };
    }

    /// <summary>Auto-resume any paused agreement whose PausedTo has passed on the virtual clock.</summary>
    public async Task<int> TickAutoResumeAsync()
    {
        var now = _clock.UtcNow;
        var today = DateOnly.FromDateTime(now);
        await using var db = await _dbf.CreateDbContextAsync();
        var due = await db.Agreements
            .Where(a => a.State == "paused" && a.PausedTo != null && a.PausedTo < today)
            .ToListAsync();
        foreach (var agreement in due)
            ResumeAgreement(db, agreement, now, early: false);
        if (due.Count > 0) await db.SaveChangesAsync();
        return due.Count;
    }

    private void ResumeAgreement(BillingDb db, Agreement agreement, DateTime now, bool early)
    {
        var from = agreement.PausedFrom ?? DateOnly.FromDateTime(now);
        var to = agreement.PausedTo ?? DateOnly.FromDateTime(now);
        // Auto-resume shifts future periodStarts by the pause duration (S15 / ASP unsuspend).
        var days = Math.Max(0, to.DayNumber - from.DayNumber + 1);
        if (!early && days > 0)
            agreement.NextPeriodStart = agreement.NextPeriodStart.AddDays(days);

        agreement.State = "active";
        agreement.PausedFrom = null;
        agreement.PausedTo = null;
        agreement.PauseFeeCents = 0;
        agreement.Version++;

        foreach (var fee in db.Invoices
                     .Where(i => i.AgreementId == agreement.Id && i.Kind == "pause_fee" && i.State == "scheduled")
                     .ToList())
            fee.State = "canceled";

        if (agreement.BillingTrigger == "calendar")
        {
            var hasOpen = db.Invoices.Any(i => i.AgreementId == agreement.Id
                && (i.State == "scheduled" || i.State == "collecting" || i.State == "dunning" || i.State == "settling"));
            if (!hasOpen)
            {
                var studio = db.Studios.Single(s => s.Id == agreement.StudioId);
                db.Invoices.Add(Materialization.MaterializeNext(agreement, studio, now));
            }
        }

        _log.LogInformation("agreement {Id} resumed (early={Early}), nextPeriod={Next}", agreement.Id, early, agreement.NextPeriodStart);
    }

    public async Task<object> HandleEntitlementDepletedAsync(EntitlementDepletedDto dto)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var now = _clock.UtcNow;

        db.IntakeEvents.Add(new IntakeEvent
        {
            EventId = dto.EventId,
            Type = "EntitlementDepleted",
            PayloadJson = Json.Serialize(dto),
            ReceivedAt = now
        });
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException)
        {
            return new { duplicate = true };
        }

        var agreement = await db.Agreements
            .FromSqlInterpolated($"SELECT * FROM agreements WHERE id = {dto.AgreementId} FOR UPDATE")
            .AsTracking()
            .SingleAsync();
        agreement.EntitlementRemaining = dto.Remaining;

        Guid? invoiceId = null;
        if (dto.Remaining <= 0 && agreement.BillingTrigger == "depletion" && agreement.State == "active")
            invoiceId = await MaterializeDepletionInvoiceAsync(db, agreement, now);

        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return new { duplicate = false, invoiceId, agreement.EntitlementRemaining };
    }

    /// <summary>Nightly depletion sweep: active depletion agreements with remaining=0 and live subscription get an invoice.</summary>
    public async Task<object> SweepDepletionAsync()
    {
        var now = _clock.UtcNow;
        await using var db = await _dbf.CreateDbContextAsync();
        // Live + remaining=null means "armed, waiting for usage" — do not treat as depleted.
        var candidates = await db.Agreements
            .Where(a => a.State == "active" && a.BillingTrigger == "depletion"
                        && a.DepletionSubscriptionLive
                        && a.EntitlementRemaining != null && a.EntitlementRemaining <= 0)
            .ToListAsync();

        var created = new List<Guid>();
        foreach (var agreement in candidates)
        {
            var hasOpen = await db.Invoices.AnyAsync(i => i.AgreementId == agreement.Id
                && (i.State == "scheduled" || i.State == "collecting" || i.State == "dunning" || i.State == "settling"));
            if (hasOpen) continue;
            created.Add(await MaterializeDepletionInvoiceAsync(db, agreement, now));
        }
        if (created.Count > 0) await db.SaveChangesAsync();
        return new { materialized = created };
    }

    private static async Task<Guid> MaterializeDepletionInvoiceAsync(BillingDb db, Agreement agreement, DateTime now)
    {
        var studio = await db.Studios.SingleAsync(s => s.Id == agreement.StudioId);
        // Depletion wake: period = today (virtual), then advance next cursor.
        agreement.NextPeriodStart = DateOnly.FromDateTime(now);
        var invoice = Materialization.MaterializeNext(agreement, studio, now);
        db.Invoices.Add(invoice);
        agreement.DepletionSubscriptionLive = false; // subscription consumed until next entitlement grant cycle
        return invoice.Id;
    }

    public async Task<object> HandleInstrumentEventAsync(InstrumentEventDto dto)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var now = _clock.UtcNow;

        db.IntakeEvents.Add(new IntakeEvent
        {
            EventId = dto.EventId,
            Type = "InstrumentUpdated",
            PayloadJson = Json.Serialize(dto),
            ReceivedAt = now
        });
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException)
        {
            return new { duplicate = true };
        }

        Instrument instrument;
        if (dto.InstrumentId is { } id)
        {
            instrument = await db.Instruments.SingleAsync(i => i.Id == id);
            if (dto.NewToken is not null) instrument.Token = dto.NewToken;
            if (dto.FundingType is not null) instrument.FundingType = dto.FundingType;
            if (dto.Brand is not null) instrument.Brand = dto.Brand;
            if (dto.Last4 is not null) instrument.Last4 = dto.Last4;
        }
        else
        {
            // Replace active card instruments for the member (card-updater style).
            var old = await db.Instruments.Where(i => i.MemberId == dto.MemberId && i.Kind == "card" && i.Active).ToListAsync();
            foreach (var o in old) o.Active = false;
            instrument = new Instrument
            {
                Id = Guid.NewGuid(),
                MemberId = dto.MemberId,
                Kind = "card",
                Token = dto.NewToken ?? throw new InvalidOperationException("NewToken required"),
                FundingType = dto.FundingType ?? "unknown",
                Brand = dto.Brand,
                Last4 = dto.Last4,
                Active = true
            };
            db.Instruments.Add(instrument);
            foreach (var agreement in await db.Agreements.Where(a => a.MemberId == dto.MemberId && a.State != "canceled").ToListAsync())
            {
                var chain = Json.Deserialize<List<TenderSlotDef>>(agreement.TenderChainJson);
                foreach (var slot in chain.Where(s => s.Type == "card"))
                    slot.InstrumentId = instrument.Id;
                agreement.TenderChainJson = Json.Serialize(chain);
            }
        }

        var woken = await _recovery.WakeForMemberAsync(db, dto.MemberId);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return new { duplicate = false, instrumentId = instrument.Id, laddersWoken = woken };
    }
}
