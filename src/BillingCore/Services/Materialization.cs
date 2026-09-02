using BillingCore.Domain;

namespace BillingCore.Services;

public static class Materialization
{
    /// <summary>
    /// JIT materialization (HLD 4): freezes the base amount pre-auth and advances the
    /// agreement's next-period cursor. Always called inside the caller's transaction so
    /// the (agreementId, periodStart, kind) constraint arbitrates races.
    /// POC simplification: base amount = agreement amount (no tax/discount composition).
    /// </summary>
    public static Invoice MaterializeNext(Agreement agreement, Studio studio, DateTime nowUtc, string kind = "cycle", long? amountOverride = null)
    {
        var period = agreement.NextPeriodStart;
        var amount = amountOverride ?? agreement.AmountCents;
        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            AgreementId = agreement.Id,
            StudioId = agreement.StudioId,
            MemberId = agreement.MemberId,
            PeriodStart = period,
            Kind = kind,
            BaseAmountCents = amount,
            ResidualCents = amount,
            DueAt = period.ToDateTime(new TimeOnly(studio.BillingHourUtc, 0), DateTimeKind.Utc),
            State = "scheduled",
            CreatedAt = nowUtc
        };
        if (kind == "cycle")
            agreement.NextPeriodStart = period.AddMonths(1);
        return invoice;
    }

    public static Invoice MaterializePauseFee(Agreement agreement, Studio studio, DateTime nowUtc)
    {
        // Pause-fee identity shares the pause-from date so it coexists with cycle invoices under uniqueness.
        var period = agreement.PausedFrom ?? DateOnly.FromDateTime(nowUtc);
        return new Invoice
        {
            Id = Guid.NewGuid(),
            AgreementId = agreement.Id,
            StudioId = agreement.StudioId,
            MemberId = agreement.MemberId,
            PeriodStart = period,
            Kind = "pause_fee",
            BaseAmountCents = agreement.PauseFeeCents,
            ResidualCents = agreement.PauseFeeCents,
            DueAt = period.ToDateTime(new TimeOnly(studio.BillingHourUtc, 0), DateTimeKind.Utc),
            State = "scheduled",
            CreatedAt = nowUtc
        };
    }
}
