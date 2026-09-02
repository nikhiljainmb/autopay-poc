using BillingCore.Domain;
using BillingCore.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace BillingCore.Services;

public record CollectOutcome(string Result, Guid InvoiceId);

public static class DeclineClassifier
{
    public static string Classify(string? code) => code switch
    {
        "insufficient_funds" or "try_again_later" or "processing_error" => "soft",
        "do_not_honor" or "stolen_card" or "pickup_card" or "fraudulent" => "hard",
        "expired_card" or "invalid_card" or "incorrect_cvc" => "fixable",
        "insufficient_account_balance" => "soft",
        _ => "soft"
    };

    /// <summary>hard outranks fixable outranks soft when routing a mixed-outcome invoice to recovery.</summary>
    public static string Worst(string? a, string b)
    {
        static int Rank(string c) => c switch { "hard" => 3, "fixable" => 2, _ => 1 };
        return a is null || Rank(b) > Rank(a) ? b : a;
    }
}

/// <summary>
/// Collection orchestrator (HLD 5a): single writer per invoice via SELECT ... FOR UPDATE.
/// Walks the ordered tender chain, computes the per-tender fee at dispatch [TF-A], and completes
/// money state in one transaction: attempt outcome + invoice state + next-period materialization (T1).
/// </summary>
public class CollectionService
{
    private readonly IDbContextFactory<BillingDb> _dbf;
    private readonly IClock _clock;
    private readonly ExternalsClient _gateway;
    private readonly FeeService _fees;
    private readonly ILogger<CollectionService> _log;

    public CollectionService(IDbContextFactory<BillingDb> dbf, IClock clock, ExternalsClient gateway, FeeService fees, ILogger<CollectionService> log)
    {
        _dbf = dbf;
        _clock = clock;
        _gateway = gateway;
        _fees = fees;
        _log = log;
    }

    public async Task<CollectOutcome> CollectAsync(Guid invoiceId, string source, Guid? studioRunId = null, RunCounters? counters = null)
    {
        counters ??= new RunCounters();
        var now = _clock.UtcNow;

        await using var db = await _dbf.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();

        // Single writer per invoice: the row lock serializes runs, reruns, ladder retries and pay-now.
        var invoice = await db.Invoices
            .FromSqlInterpolated($"SELECT * FROM invoices WHERE id = {invoiceId} FOR UPDATE")
            .AsTracking()
            .SingleOrDefaultAsync();
        if (invoice is null) return new CollectOutcome("not_found", invoiceId);

        if (invoice.State is "paid" or "recovered" or "written_off" or "canceled" or "settling")
        {
            await tx.CommitAsync();
            return new CollectOutcome($"noop_{invoice.State}", invoiceId);
        }

        var agreement = await db.Agreements.SingleAsync(a => a.Id == invoice.AgreementId);
        var studio = await db.Studios.SingleAsync(s => s.Id == invoice.StudioId);
        var member = await db.Members.SingleAsync(m => m.Id == invoice.MemberId);

        // F7 orchestrator gate: studio ops pause and agreement pause/dispute block collection.
        // Exception: pause_fee invoices are collected *during* the agreement pause window (S15).
        if (studio.Paused
            || agreement.State == "disputed"
            || (agreement.State == "paused" && invoice.Kind != "pause_fee"))
        {
            counters.SkippedPaused++;
            await tx.CommitAsync();
            return new CollectOutcome($"skipped_{agreement.State}_{(studio.Paused ? "studio" : "agreement")}", invoiceId);
        }

        var slots = Json.Deserialize<List<TenderSlotDef>>(agreement.TenderChainJson);
        var attempts = await db.Attempts.Where(a => a.InvoiceId == invoice.Id).OrderBy(a => a.CreatedAt).ToListAsync();
        var hadPriorDecline = attempts.Any(a => a.Outcome is "declined" or "returned");

        invoice.State = "collecting";
        string? worstDeclineClass = null;
        var anyDecline = false;
        var anyPending = false;
        var blockedOnUnknown = false;

        for (var slotIdx = 0; slotIdx < slots.Count && invoice.ResidualCents > 0; slotIdx++)
        {
            var slot = slots[slotIdx];
            if (attempts.Any(a => a.TenderSlot == slotIdx && a.Outcome == "approved")) continue;

            // Query-before-retry (HLD F1/P7): an unresolved attempt on this slot must be
            // settled with the gateway before any new money movement.
            var unknown = attempts.LastOrDefault(a => a.TenderSlot == slotIdx && a.Outcome == "unknown");
            if (unknown is not null)
            {
                var q = await _gateway.QueryAsync(unknown.IdempotencyKey);
                switch (q.Status)
                {
                    case "approved":
                        unknown.Outcome = "approved";
                        unknown.GatewayRef = q.GatewayRef;
                        invoice.ResidualCents -= unknown.AmountCents;
                        counters.Approved++;
                        counters.CollectedCents += unknown.AmountCents;
                        counters.SurchargeCents += unknown.FeeCents;
                        continue;
                    case "declined":
                        unknown.Outcome = "declined";
                        unknown.DeclineCode = q.DeclineCode;
                        unknown.DeclineClass = DeclineClassifier.Classify(q.DeclineCode);
                        worstDeclineClass = DeclineClassifier.Worst(worstDeclineClass, unknown.DeclineClass);
                        anyDecline = true;
                        counters.Declined++;
                        continue; // fall through to the next tender in the chain
                    default: // not_found: safe to retry this slot with a fresh attempt
                        unknown.Outcome = "failed";
                        break;
                }
            }

            switch (slot.Type)
            {
                case "card":
                case "bank":
                {
                    var instrument = await db.Instruments.SingleOrDefaultAsync(i => i.Id == slot.InstrumentId && i.Active);
                    if (instrument is null)
                    {
                        _log.LogWarning("invoice {Invoice} slot {Slot}: no active instrument", invoice.Id, slotIdx);
                        continue;
                    }

                    var slotAmount = Math.Min(invoice.ResidualCents, slot.CapCents ?? invoice.ResidualCents);

                    // The fee follows the tender [TF-A]: computed here, at dispatch, per tender.
                    var quote = slot.Type == "card"
                        ? await _fees.QuoteAsync(studio, instrument, slotAmount)
                        : new FeeQuote(0, "n/a", "not_card");
                    if (FeeService.IsDroppedFee(quote.SuppressionReason))
                    {
                        counters.FeeDropped++;
                        await UpsertFeeDropWorkItemAsync(db, invoice.Id, quote.SuppressionReason!);
                    }

                    var attempt = new Attempt
                    {
                        Id = Guid.NewGuid(),
                        InvoiceId = invoice.Id,
                        TenderSlot = slotIdx,
                        TenderType = slot.Type,
                        InstrumentId = instrument.Id,
                        AmountCents = slotAmount,
                        FeeCents = quote.FeeCents,
                        FundingTypeAtCharge = quote.FundingType,
                        FeeSuppressionReason = quote.SuppressionReason,
                        MitJson = Json.Serialize(new MitContext("merchant", true, instrument.NetworkTransactionId)),
                        Outcome = "dispatched",
                        Source = source,
                        StudioRunId = studioRunId,
                        IdempotencyKey = Guid.NewGuid().ToString("N"),
                        CreatedAt = now
                    };
                    db.Attempts.Add(attempt);
                    attempts.Add(attempt);
                    await db.SaveChangesAsync(); // attempt row exists before dispatch (idempotency check ↔ dispatch, T1)

                    ChargeResult result;
                    try
                    {
                        result = await _gateway.ChargeAsync(new ChargeRequest(
                            instrument.Token,
                            AmountCents: slotAmount + quote.FeeCents,
                            SurchargeAmountCents: quote.FeeCents > 0 ? quote.FeeCents : null,
                            Currency: "USD",
                            Rail: slot.Type,
                            Mit: new MitContext("merchant", true, instrument.NetworkTransactionId),
                            IdempotencyKey: attempt.IdempotencyKey));
                    }
                    catch (GatewayTimeoutException)
                    {
                        // Ambiguity: never guess, never continue the chain — money may have moved.
                        attempt.Outcome = "unknown";
                        counters.Unknown++;
                        blockedOnUnknown = true;
                        goto ResolveInvoice;
                    }

                    switch (result.Status)
                    {
                        case "approved":
                            attempt.Outcome = "approved";
                            attempt.GatewayRef = result.GatewayRef;
                            if (result.NetworkTransactionId is not null && instrument.NetworkTransactionId is null)
                                instrument.NetworkTransactionId = result.NetworkTransactionId; // NTI persistence (HLD 5b)
                            invoice.ResidualCents -= slotAmount;
                            counters.Approved++;
                            counters.CollectedCents += slotAmount;
                            counters.SurchargeCents += quote.FeeCents;
                            break;
                        case "pending":
                            attempt.Outcome = "pending";
                            attempt.GatewayRef = result.GatewayRef;
                            anyPending = true;
                            goto ResolveInvoice; // async rail: outcome arrives via webhook/poll, days later
                        default:
                            attempt.Outcome = "declined";
                            attempt.DeclineCode = result.DeclineCode;
                            attempt.DeclineClass = DeclineClassifier.Classify(result.DeclineCode);
                            worstDeclineClass = DeclineClassifier.Worst(worstDeclineClass, attempt.DeclineClass);
                            anyDecline = true;
                            counters.Declined++;
                            break;
                    }
                    break;
                }
                case "account":
                {
                    // Account balance is commerce-owned (HLD 5c) — never surcharged; billed via bridge.
                    var want = invoice.ResidualCents;
                    var debit = await _gateway.DebitAccountAsync(member.Id, want, Guid.NewGuid().ToString("N"), invoice.Id);
                    member.AccountBalanceCacheCents = debit.BalanceCents;
                    var amount = debit.Ok ? debit.DebitedCents : 0;
                    var attempt = new Attempt
                    {
                        Id = Guid.NewGuid(),
                        InvoiceId = invoice.Id,
                        TenderSlot = slotIdx,
                        TenderType = "account",
                        AmountCents = amount,
                        Outcome = debit.Ok && amount > 0 ? "approved" : "declined",
                        DeclineCode = debit.Ok && amount > 0 ? null : (debit.DeclineCode ?? "insufficient_account_balance"),
                        DeclineClass = debit.Ok && amount > 0 ? null : "soft",
                        Source = source,
                        StudioRunId = studioRunId,
                        IdempotencyKey = Guid.NewGuid().ToString("N"),
                        CreatedAt = now
                    };
                    db.Attempts.Add(attempt);
                    attempts.Add(attempt);
                    if (attempt.Outcome == "approved")
                    {
                        invoice.ResidualCents -= amount;
                        counters.Approved++;
                        counters.CollectedCents += amount;
                    }
                    else
                    {
                        worstDeclineClass = DeclineClassifier.Worst(worstDeclineClass, "soft");
                        anyDecline = true;
                        counters.Declined++;
                    }
                    break;
                }
            }
        }

    ResolveInvoice:
        string resultLabel;
        if (invoice.ResidualCents <= 0)
        {
            resultLabel = ApplyPaid(db, invoice, agreement, studio, attempts, hadPriorDecline, source, now, grantEntitlement: true);
        }
        else if (anyPending)
        {
            invoice.State = "settling";
            invoice.SettlingSince = now;
            counters.Pending++;
            // HLD open decision 3 default: ACH entitlement granted at initiation, clawed back on return.
            EnqueueOutbox(db, "EntitlementGrant", new { invoiceId = invoice.Id, memberId = invoice.MemberId, studioId = invoice.StudioId, reason = "async_rail_initiated" }, now);
            resultLabel = "settling";
        }
        else if (blockedOnUnknown)
        {
            invoice.State = "scheduled"; // stays run-eligible; resolution happens via query-before-retry
            resultLabel = "unknown_blocked";
        }
        else if (anyDecline || invoice.ResidualCents < invoice.BaseAmountCents)
        {
            invoice.State = "dunning";
            EnqueueOutbox(db, "InvoiceDeclined", new
            {
                invoiceId = invoice.Id,
                declineClass = worstDeclineClass ?? "soft",
                residualCents = invoice.ResidualCents
            }, now);
            resultLabel = "dunning";
        }
        else
        {
            invoice.State = "scheduled";
            resultLabel = "no_tender_available";
        }

        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return new CollectOutcome(resultLabel, invoice.Id);
    }

    /// <summary>Resolve a pending (async-rail) attempt from a gateway webhook. Idempotent by attempt outcome.</summary>
    public async Task<object> HandleGatewayWebhookAsync(string type, string gatewayRef, string? code)
    {
        var now = _clock.UtcNow;
        await using var db = await _dbf.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();

        var attempt = await db.Attempts.SingleOrDefaultAsync(a => a.GatewayRef == gatewayRef && a.Outcome == "pending");
        if (attempt is null)
        {
            await tx.CommitAsync();
            return new { handled = false, reason = "no_pending_attempt_for_ref" };
        }

        var invoice = await db.Invoices
            .FromSqlInterpolated($"SELECT * FROM invoices WHERE id = {attempt.InvoiceId} FOR UPDATE")
            .AsTracking()
            .SingleAsync();
        var agreement = await db.Agreements.SingleAsync(a => a.Id == invoice.AgreementId);
        var studio = await db.Studios.SingleAsync(s => s.Id == invoice.StudioId);

        string resultLabel;
        switch (type)
        {
            case "ach_settled":
                attempt.Outcome = "approved";
                invoice.ResidualCents -= attempt.AmountCents;
                resultLabel = invoice.ResidualCents <= 0
                    // Entitlement was granted at initiation; the settlement records the sale only.
                    ? ApplyPaid(db, invoice, agreement, studio, new List<Attempt> { attempt }, hadPriorDecline: false, source: "webhook", now, grantEntitlement: false)
                    : invoice.State; // partial rails not modeled; keep state
                break;
            case "ach_return":
                attempt.Outcome = "returned";
                attempt.DeclineCode = code ?? "R01";
                attempt.DeclineClass = "bank_return";
                invoice.State = "dunning";
                EnqueueOutbox(db, "Clawback", new { invoiceId = invoice.Id, memberId = invoice.MemberId, studioId = invoice.StudioId, reason = "bank_return" }, now);
                EnqueueOutbox(db, "InvoiceDeclined", new { invoiceId = invoice.Id, declineClass = "bank_return", residualCents = invoice.ResidualCents }, now);
                resultLabel = "dunning";
                break;
            default:
                await tx.CommitAsync();
                return new { handled = false, reason = $"unknown webhook type {type}" };
        }

        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return new { handled = true, invoiceState = resultLabel };
    }

    /// <summary>F6 chargeback: agreement disputed, collection paused, entitlement clawed back.</summary>
    public async Task<object> HandleChargebackAsync(Guid invoiceId, string? reason)
    {
        var now = _clock.UtcNow;
        await using var db = await _dbf.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();

        var invoice = await db.Invoices
            .FromSqlInterpolated($"SELECT * FROM invoices WHERE id = {invoiceId} FOR UPDATE")
            .AsTracking()
            .SingleOrDefaultAsync();
        if (invoice is null) return new { handled = false, reason = "not_found" };

        var agreement = await db.Agreements.SingleAsync(a => a.Id == invoice.AgreementId);
        agreement.State = "disputed";
        agreement.Version++;
        // Fee reversal deferred (§12.8); entitlement reverse is the bridge clawback.
        EnqueueOutbox(db, "Clawback", new { invoiceId = invoice.Id, memberId = invoice.MemberId, studioId = invoice.StudioId, reason = reason ?? "chargeback" }, now);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return new { handled = true, agreementState = agreement.State };
    }

    /// <summary>F8 pull backstop: resolve pending attempts older than the poll age via gateway query.</summary>
    public async Task<object> PollPendingAttemptsAsync(TimeSpan minAge)
    {
        var now = _clock.UtcNow;
        await using var db = await _dbf.CreateDbContextAsync();
        var cutoff = now - minAge;
        var pending = await db.Attempts
            .Where(a => a.Outcome == "pending" && a.CreatedAt <= cutoff)
            .OrderBy(a => a.CreatedAt)
            .Take(20)
            .ToListAsync();

        var results = new List<object>();
        foreach (var attempt in pending)
        {
            var q = await _gateway.QueryAsync(attempt.IdempotencyKey);
            var type = q.Status switch
            {
                "approved" or "settled" => "ach_settled",
                "returned" or "declined" => "ach_return",
                _ => null
            };
            if (type is null)
            {
                results.Add(new { attempt.Id, status = q.Status, action = "still_pending" });
                continue;
            }
            var handled = await HandleGatewayWebhookAsync(type, attempt.GatewayRef ?? "", q.DeclineCode);
            // Mark source of resolution as poll for the audit trail when we settled.
            await using var db2 = await _dbf.CreateDbContextAsync();
            var tracked = await db2.Attempts.SingleOrDefaultAsync(a => a.Id == attempt.Id);
            if (tracked is not null && tracked.Source != "poll")
            {
                tracked.Source = "poll";
                await db2.SaveChangesAsync();
            }
            results.Add(new { attempt.Id, status = q.Status, action = type, handled });
        }
        return new { polled = pending.Count, results };
    }

    private static async Task UpsertFeeDropWorkItemAsync(BillingDb db, Guid invoiceId, string reason)
    {
        await db.Database.ExecuteSqlInterpolatedAsync(
            $@"INSERT INTO work_items (id, kind, ref_key, detail_json, created_at)
               VALUES ({Guid.NewGuid()}, {"fee_dropped_by_outage"}, {invoiceId.ToString()}, {Json.Serialize(new { invoiceId, reason })}, {DateTime.UtcNow})
               ON CONFLICT (kind, ref_key) DO NOTHING");
    }

    /// <summary>
    /// The paid transaction (HLD T1): attempt success, invoice paid/recovered, ladder completion and
    /// next-period materialization commit atomically — duplicate renewal is a constraint violation, not a race.
    /// </summary>
    private string ApplyPaid(BillingDb db, Invoice invoice, Agreement agreement, Studio studio,
        List<Attempt> attempts, bool hadPriorDecline, string source, DateTime now, bool grantEntitlement)
    {
        invoice.State = hadPriorDecline && source is "ladder" or "selfserve" or "webhook" or "poll" ? "recovered" : "paid";
        invoice.SettlingSince = null;

        foreach (var ladder in db.Ladders.Where(l => l.InvoiceId == invoice.Id &&
                     (l.State == "active" || l.State == "dispatched" || l.State == "waiting_member")))
        {
            ladder.State = "completed";
            ladder.CompletedAt = now;
        }

        if (agreement.State is "past_due") agreement.State = "active";

        // Next cycle only for calendar cycle invoices under an active (non-paused) agreement.
        if (agreement.State == "active" && invoice.Kind == "cycle" && agreement.BillingTrigger == "calendar")
        {
            var next = Materialization.MaterializeNext(agreement, studio, now);
            db.Invoices.Add(next);
        }
        else if (agreement.BillingTrigger == "depletion" && invoice.Kind == "cycle")
        {
            // After a depletion charge, re-arm the subscription for the next entitlement cycle.
            agreement.DepletionSubscriptionLive = true;
            agreement.EntitlementRemaining = null;
        }

        var approved = attempts.Where(a => a.Outcome == "approved").ToList();
        EnqueueOutbox(db, "InvoicePaid", new
        {
            invoiceId = invoice.Id,
            agreementId = agreement.Id,
            memberId = invoice.MemberId,
            studioId = invoice.StudioId,
            baseCents = invoice.BaseAmountCents,
            feeCents = approved.Sum(a => a.FeeCents),
            grantEntitlement,
            payments = approved.Select(a => new { a.TenderType, a.GatewayRef, a.AmountCents, a.FeeCents }).ToList()
        }, now);

        return invoice.State;
    }

    internal static void EnqueueOutbox(BillingDb db, string topic, object payload, DateTime now) =>
        db.OutboxMessages.Add(new OutboxMessage
        {
            Topic = topic,
            PayloadJson = Json.Serialize(payload),
            AvailableAt = now
        });
}
