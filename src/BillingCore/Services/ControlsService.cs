using BillingCore.Domain;
using BillingCore.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace BillingCore.Services;

/// <summary>
/// Controls plane (HLD 8, tier-1): the invariant sweeper makes silent failure modes detectable by
/// construction, and settlement reconciliation proves DB money == gateway money — including the
/// surcharge [TF-A] (P15). Deltas open work items.
/// </summary>
public class ControlsService
{
    private static readonly string[] OpenStates = { "scheduled", "collecting", "dunning", "settling" };
    private static readonly TimeSpan SettlingHorizon = TimeSpan.FromDays(5);

    private readonly IDbContextFactory<BillingDb> _dbf;
    private readonly IClock _clock;
    private readonly ExternalsClient _ext;

    public ControlsService(IDbContextFactory<BillingDb> dbf, IClock clock, ExternalsClient ext)
    {
        _dbf = dbf;
        _clock = clock;
        _ext = ext;
    }

    public async Task<object> SweepAsync()
    {
        var now = _clock.UtcNow;
        await using var db = await _dbf.CreateDbContextAsync();
        var violations = new List<object>();

        // Invariant (HLD 8 / §4a.2): active agreement ⇒ open invoice OR live depletion subscription.
        var orphanAgreements = await db.Agreements
            .Where(a => a.State == "active"
                        && !(a.BillingTrigger == "depletion" && a.DepletionSubscriptionLive)
                        && !db.Invoices.Any(i => i.AgreementId == a.Id && OpenStates.Contains(i.State)))
            .Select(a => a.Id)
            .ToListAsync();
        foreach (var id in orphanAgreements)
            violations.Add(await UpsertWorkItemAsync(db, "missing_open_invoice", id.ToString(),
                new { agreementId = id, detectedAt = now }));

        // Invariant: no dangling settling past the banking horizon (F8) — clocked from SettlingSince.
        var horizon = now - SettlingHorizon;
        var staleSettling = await db.Invoices
            .Where(i => i.State == "settling" && i.SettlingSince != null && i.SettlingSince < horizon)
            .Select(i => i.Id)
            .ToListAsync();
        foreach (var id in staleSettling)
            violations.Add(await UpsertWorkItemAsync(db, "settling_horizon", id.ToString(),
                new { invoiceId = id, horizonDays = SettlingHorizon.TotalDays }));

        // Invariant: every declined invoice is laddered or terminal (recovery sweep).
        var liveLadderStates = new[] { "active", "dispatched", "waiting_member" };
        var unladdered = await db.Invoices
            .Where(i => i.State == "dunning" && !db.Ladders.Any(l => l.InvoiceId == i.Id && liveLadderStates.Contains(l.State)))
            .Select(i => i.Id)
            .ToListAsync();
        foreach (var id in unladdered)
            violations.Add(await UpsertWorkItemAsync(db, "unladdered_dunning", id.ToString(), new { invoiceId = id }));

        return new { checkedAt = now, violations };
    }

    public async Task<object> ReconcileAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var report = await _ext.GetSettlementReportAsync();
        var reportByRef = report.ToDictionary(l => l.GatewayRef);

        var dbCharges = await db.Attempts
            .Where(a => a.Outcome == "approved" && a.GatewayRef != null)
            .Select(a => new { a.GatewayRef, Total = a.AmountCents + a.FeeCents, a.FeeCents })
            .ToListAsync();
        var dbByRef = dbCharges.ToDictionary(a => a.GatewayRef!);

        var deltas = new List<object>();
        foreach (var line in report.Where(l => !dbByRef.ContainsKey(l.GatewayRef)))
            deltas.Add(await UpsertWorkItemAsync(db, "recon_missing_in_db", line.GatewayRef,
                new { line.GatewayRef, line.AmountCents, line.SurchargeCents }));
        foreach (var charge in dbCharges.Where(c => !reportByRef.ContainsKey(c.GatewayRef!)))
            deltas.Add(await UpsertWorkItemAsync(db, "recon_missing_in_gateway", charge.GatewayRef!,
                new { charge.GatewayRef, charge.Total }));
        foreach (var line in report)
        {
            if (!dbByRef.TryGetValue(line.GatewayRef, out var charge)) continue;
            // DB money == gateway money, surcharge included [TF-A].
            if (charge.Total != line.AmountCents || charge.FeeCents != line.SurchargeCents)
                deltas.Add(await UpsertWorkItemAsync(db, "recon_amount_mismatch", line.GatewayRef,
                    new { line.GatewayRef, db = new { charge.Total, charge.FeeCents }, gateway = new { line.AmountCents, line.SurchargeCents } }));
        }

        // Fee ledger parity: bridge −13 / PaymentTransactionFee totals should match attempt fees.
        var feeAttempts = await db.Attempts.Where(a => a.Outcome == "approved" && a.FeeCents > 0).ToListAsync();
        var bridgeFees = await _ext.GetBridgeFeeRowsAsync();
        var attemptFeeTotal = feeAttempts.Sum(a => a.FeeCents);
        var bridgeFeeTotal = bridgeFees.Sum(f => f.AmountCents);
        if (attemptFeeTotal != bridgeFeeTotal)
            deltas.Add(await UpsertWorkItemAsync(db, "recon_fee_ledger_mismatch", "fee-ledger",
                new { attemptFeeTotal, bridgeFeeTotal }));

        return new { reportLines = report.Count, dbCharges = dbCharges.Count, attemptFeeTotal, bridgeFeeTotal, deltas };
    }

    private async Task<object> UpsertWorkItemAsync(BillingDb db, string kind, string refKey, object detail)
    {
        await db.Database.ExecuteSqlInterpolatedAsync(
            $@"INSERT INTO work_items (id, kind, ref_key, detail_json, created_at)
               VALUES ({Guid.NewGuid()}, {kind}, {refKey}, {Json.Serialize(detail)}, {DateTime.UtcNow})
               ON CONFLICT (kind, ref_key) DO NOTHING");
        return new { kind, refKey, detail };
    }
}
