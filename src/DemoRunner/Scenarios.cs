using System.Text.Json.Nodes;

namespace DemoRunner;

public static class Scenarios
{
    private static string S(JsonNode? n) => n?.GetValue<string>() ?? "";
    private static long L(JsonNode? n) => n?.GetValue<long>() ?? 0;

    // ---------------------------------------------------------------------------------------
    public static async Task D1(Ctx ctx)
    {
        var m = ctx.Member("d1");
        var (agreementId, invoiceIdNullable, eventBody) = await ctx.NewContract("d1", 10000, Ctx.Card(m.InstrumentId));
        var invoiceId = invoiceIdNullable!.Value;
        ctx.State["d1.agreementId"] = agreementId;
        ctx.State["d1.invoiceId"] = invoiceId;
        ctx.State["d1.eventBody"] = eventBody;

        var invoice = await ctx.Invoice(invoiceId);
        ctx.Check("intake materialized a scheduled invoice (JIT, frozen base amount)",
            S(invoice["state"]) == "scheduled" && L(invoice["baseAmountCents"]) == 10000);

        await ctx.AdvanceToDue(invoiceId);
        ctx.Check("nightly trigger picked and paid the invoice", await ctx.WaitInvoiceState(invoiceId, "paid"));

        var attempts = await ctx.Attempts(invoiceId);
        var attempt = attempts.Single()!;
        ctx.Check("single approved card attempt on slot 0",
            S(attempt["outcome"]) == "approved" && S(attempt["tenderType"]) == "card");
        ctx.Check("fee computed at dispatch: 3% of base, funding=credit [TF-A]",
            L(attempt["feeCents"]) == 300 && S(attempt["fundingTypeAtCharge"]) == "credit",
            $"fee={L(attempt["feeCents"])} funding={S(attempt["fundingTypeAtCharge"])}");
        ctx.Check("MIT context tagged off-session merchant-initiated",
            S(attempt["mitJson"]).Contains("\"offSession\":true"));

        var charges = await ctx.ExtCharges($"token={m.Token}");
        var charge = charges.Single()!;
        ctx.Check("gateway received base+fee with SurchargeAmount as dedicated field [TF-A]",
            L(charge["amountCents"]) == 10300 && L(charge["surchargeCents"]) == 300);
        ctx.Check("gateway journal shows MIT tags on the wire",
            S(charge["mit"]).Contains("offSession"));

        var invoices = (JsonArray)(await ctx.Billing.GetAsync($"/agreements/{agreementId}/invoices"))!;
        ctx.Check("next period materialized inside the paid transaction (T1)",
            invoices.Count == 2 && invoices.Any(i => S(i!["state"]) == "scheduled"));

        ctx.Check("bridge got the sale with the ProductID -13 fee line [TF-A]",
            await ctx.WaitUntil(async () =>
                (await ctx.Bridge("sale", invoiceId)).Any(e => S(e!["body"]).Contains("-13")), "sale at bridge"));
        ctx.Check("bridge sale includes PaymentTransactionFee-shaped rows [TF-A]",
            await ctx.WaitUntil(async () =>
                (await ctx.Bridge("sale", invoiceId)).Any(e =>
                {
                    var body = S(e!["body"]);
                    return body.Contains("paymentTransactionFees") && body.Contains("saleDetailProductId") && body.Contains("-13");
                }), "fee rows"));
        ctx.Check("bridge got the entitlement grant",
            await ctx.WaitUntil(async () => (await ctx.Bridge("entitlement", invoiceId)).Count >= 1, "entitlement at bridge"));

        var studioRun = await ctx.Billing.GetAsync($"/studio-runs/{S(attempt["studioRunId"])}");
        ctx.State["d1.scheduleRunId"] = S(studioRun!["scheduleRunId"]);
    }

    // ---------------------------------------------------------------------------------------
    public static async Task D2(Ctx ctx)
    {
        var (agreementId, invoiceId, eventBody) =
            ((Guid)ctx.State["d1.agreementId"], (Guid)ctx.State["d1.invoiceId"], ctx.State["d1.eventBody"]);

        var (_, replay) = await ctx.Billing.PostAsync("/intake/contract-events", eventBody);
        ctx.Check("replayed ContractSold event is a dedup no-op (P3)",
            replay!["duplicate"]!.GetValue<bool>());

        var contractId = System.Text.Json.JsonSerializer
            .SerializeToNode(eventBody, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!
            ["contractId"]!.GetValue<string>();
        var agreements = (JsonArray)(await ctx.Billing.GetAsync($"/agreements?contractId={contractId}"))!;
        ctx.Check("still exactly one agreement for the contract", agreements.Count == 1);

        var (status, conflict) = await ctx.Billing.PostAsync($"/demo/agreements/{agreementId}/materialize-next");
        ctx.Check("duplicate billing cycle blocked by the DB constraint (P4)",
            status == 409 && S(conflict!["blockedByConstraint"]) == "ux_invoices_agreement_period_kind",
            $"status={status}");

        var runId = (string)ctx.State["d1.scheduleRunId"];
        await ctx.Billing.PostAsync($"/runs/{runId}/rerun");
        ctx.Check("rerun completed", await ctx.WaitUntil(async () =>
            S((await ctx.Billing.GetAsync($"/runs/{runId}"))!["run"]!["state"]) == "completed", "rerun close-out"));

        var attempts = await ctx.Attempts(invoiceId);
        ctx.Check("rerun did not double-charge: still one attempt (single-writer + slot constraint)",
            attempts.Count == 1);
        var charges = await ctx.ExtCharges($"token={ctx.Member("d1").Token}");
        ctx.Check("gateway journal confirms exactly one money movement ever", charges.Count == 1);

        // F4: absolute amendment rewrites the open (next) invoice; replay is a no-op.
        var openInvoices = (JsonArray)(await ctx.Billing.GetAsync($"/agreements/{agreementId}/invoices"))!;
        var open = openInvoices.First(i => S(i!["state"]) == "scheduled")!;
        var newPeriod = DateOnly.Parse(S(open["periodStart"])[..10]).AddDays(1);
        var amendBody = new
        {
            eventId = Guid.NewGuid().ToString("N"),
            type = "ContractAmended",
            studioId = 1,
            memberId = ctx.Member("d1").MemberId,
            contractId = contractId,
            agreementId,
            amountCents = 12000,
            periodStart = newPeriod.ToString("yyyy-MM-dd")
        };
        await ctx.Billing.PostAsync("/intake/contract-events", amendBody);
        var rewritten = await ctx.Invoice(Guid.Parse(S(open["id"])));
        ctx.Check("F4 absolute amendment rewrote open invoice amount and periodStart",
            L(rewritten["baseAmountCents"]) == 12000 && S(rewritten["periodStart"]).StartsWith(newPeriod.ToString("yyyy-MM-dd")));
        var (_, amendReplay) = await ctx.Billing.PostAsync("/intake/contract-events", amendBody);
        ctx.Check("F4 amendment replay is a dedup no-op", amendReplay!["duplicate"]!.GetValue<bool>());
    }

    // ---------------------------------------------------------------------------------------
    public static async Task D3(Ctx ctx)
    {
        // --- soft decline -> ladder retry -> recovered ---
        var soft = ctx.Member("d3soft");
        var (_, softInvoiceN, _) = await ctx.NewContract("d3soft", 10000, Ctx.Card(soft.InstrumentId));
        var softInvoice = softInvoiceN!.Value;
        await ctx.AdvanceToDue(softInvoice);
        ctx.Check("soft decline routed invoice to dunning", await ctx.WaitInvoiceState(softInvoice, "dunning"));

        // Ladder creation is an outbox hop (T3): wait for it, don't assume it.
        ctx.Check("ladder active with a policy-scheduled retry (not a scan window)",
            await ctx.WaitUntil(async () =>
            {
                var ls = await ctx.Ladders(softInvoice);
                return ls.Count == 1 && S(ls[0]!["state"]) == "active";
            }, "active ladder"));
        var ladderId = S((await ctx.Ladders(softInvoice))[0]!["id"]);

        ctx.Check("failure notification sent once with idempotency key (P13)",
            await ctx.WaitUntil(async () =>
                (await ctx.Ext.GetAsync($"/admin/journal/notify?key=ladder:{ladderId}:step:0"))!["requests"]!.GetValue<int>() == 1,
                "notification delivered"));

        await ctx.Billing.PostAsync("/demo/time/advance", new { days = 2, minutes = 10 });
        ctx.Check("retry fired after +2 virtual days and recovered the invoice",
            await ctx.WaitInvoiceState(softInvoice, "recovered"));
        var softAttempts = await ctx.Attempts(softInvoice);
        ctx.Check("recovery attempt re-entered the same collection path (source=ladder)",
            softAttempts.Count == 2 && S(softAttempts[1]!["source"]) == "ladder" && S(softAttempts[1]!["outcome"]) == "approved");

        await ctx.Billing.PostAsync("/demo/outbox/redeliver-last", new { topic = "NotifyRequest" });
        await Task.Delay(1500);
        var notifyAfter = await ctx.Ext.GetAsync($"/admin/journal/notify?key=ladder:{ladderId}:step:0");
        ctx.Check("outbox redelivery deduped downstream: delivered once despite 2 requests",
            notifyAfter!["requests"]!.GetValue<int>() >= 2 && notifyAfter["delivered"]!.GetValue<int>() == 1);

        // --- hard decline -> immediate terminal, zero doomed retries ---
        var hard = ctx.Member("d3hard");
        var (hardAgreementId, hardInvoiceN, _) = await ctx.NewContract("d3hard", 10000, Ctx.Card(hard.InstrumentId));
        var hardInvoice = hardInvoiceN!.Value;
        await ctx.AdvanceToDue(hardInvoice);
        ctx.Check("hard decline written off immediately (F3)", await ctx.WaitInvoiceState(hardInvoice, "written_off"));
        ctx.Check("hard write-off flips the agreement to past_due",
            S((await ctx.Billing.GetAsync($"/agreements/{hardAgreementId}"))!["state"]) == "past_due");
        ctx.Check("membership flagged via bridge event",
            await ctx.WaitUntil(async () => (await ctx.Bridge("membership", hardInvoice)).Count >= 1, "membership event"));

        await ctx.Billing.PostAsync("/demo/time/advance", new { days = 4 });
        await Task.Delay(2000); // give any (wrong) retry a chance to happen
        ctx.Check("no doomed re-auths after 4 days: attempts still 1 (P1)",
            (await ctx.Attempts(hardInvoice)).Count == 1);

        // --- fixable decline -> self-serve fix -> recovered ---
        var fix = ctx.Member("d3fix");
        var (_, fixInvoiceN, _) = await ctx.NewContract("d3fix", 10000, Ctx.Card(fix.InstrumentId));
        var fixInvoice = fixInvoiceN!.Value;
        await ctx.AdvanceToDue(fixInvoice);
        ctx.Check("fixable decline parked waiting on the member", await ctx.WaitUntil(async () =>
        {
            var ls = await ctx.Ladders(fixInvoice);
            return ls.Count == 1 && S(ls[0]!["state"]) == "waiting_member";
        }, "waiting_member ladder"));

        var fixLadder = (await ctx.Ladders(fixInvoice))[0]!;
        var token = S(fixLadder["selfServeToken"]);
        // Token must avoid the Externals behavior markers (softok/softalways/hard/fix/timeout/ach).
        var (_, updated) = await ctx.Billing.PostAsync($"/selfserve/{token}/update-instrument",
            new { newToken = "tok_ok_d3replacement", brand = "visa", last4 = "1111", fundingType = "credit" });
        ctx.Check("self-serve card update woke the ladder (F5)", updated!["laddersWoken"]!.GetValue<int>() >= 1);
        ctx.Check("recollect recovered the invoice with the new card",
            await ctx.WaitInvoiceState(fixInvoice, "recovered"));
        var newCharges = await ctx.ExtCharges("token=tok_ok_d3replacement");
        ctx.Check("gateway charged the replacement card with the fee [TF-A]",
            newCharges.Count == 1 && L(newCharges[0]!["surchargeCents"]) == 300);
    }

    // ---------------------------------------------------------------------------------------
    public static async Task D4(Ctx ctx)
    {
        // --- card capped + account remainder ---
        var cap = ctx.Member("d4cap");
        var (_, capInvoiceN, _) = await ctx.NewContract("d4cap", 10000, Ctx.Card(cap.InstrumentId, 6000), Ctx.Account());
        var capInvoice = capInvoiceN!.Value;
        await ctx.AdvanceToDue(capInvoice);
        ctx.Check("chain covered the invoice: card cap + account remainder (S20)",
            await ctx.WaitInvoiceState(capInvoice, "paid"));

        var attempts = await ctx.Attempts(capInvoice);
        var card = attempts.FirstOrDefault(a => S(a!["tenderType"]) == "card");
        var account = attempts.FirstOrDefault(a => S(a!["tenderType"]) == "account");
        ctx.Check("card slot took its cap with its own fee [TF-A]",
            card is not null && L(card["amountCents"]) == 6000 && L(card["feeCents"]) == 180);
        ctx.Check("account tender took the remainder, never surcharged",
            account is not null && L(account["amountCents"]) == 4000 && L(account["feeCents"]) == 0);
        var member = await ctx.Billing.GetAsync($"/members/{cap.MemberId}");
        ctx.Check("member account cache reflects commerce debit of the remainder",
            L(member!["accountBalanceCacheCents"]) == 1000);
        ctx.Check("commerce ledger recorded the account debit (HLD 5c boundary)",
            await ctx.WaitUntil(async () => (await ctx.Bridge("account_debit", capInvoice)).Count >= 1, "account_debit"));

        // --- decline -> account fallback, partial cover -> dunning with residual ---
        var fb = ctx.Member("d4fb");
        var (_, fbInvoiceN, _) = await ctx.NewContract("d4fb", 10000, Ctx.Card(fb.InstrumentId), Ctx.Account());
        var fbInvoice = fbInvoiceN!.Value;
        await ctx.AdvanceToDue(fbInvoice);
        ctx.Check("partial cover leaves the invoice in dunning (not paid)",
            await ctx.WaitInvoiceState(fbInvoice, "dunning"));
        var fbInvoiceNode = await ctx.Invoice(fbInvoice);
        ctx.Check("residual = base minus the account partial (recovery collects the residual only)",
            L(fbInvoiceNode["residualCents"]) == 7000);
        var fbAttempts = await ctx.Attempts(fbInvoice);
        ctx.Check("decline-to-account is a collection step, not a write-off side effect",
            fbAttempts.Any(a => S(a!["tenderType"]) == "card" && S(a["outcome"]) == "declined") &&
            fbAttempts.Any(a => S(a!["tenderType"]) == "account" && S(a["outcome"]) == "approved" && L(a["amountCents"]) == 3000));
        ctx.Check("ladder opened for the residual",
            await ctx.WaitUntil(async () => (await ctx.Ladders(fbInvoice)).Count == 1, "residual ladder"));

        // --- pure Method=2 account-only ---
        var acct = ctx.Member("d4acct");
        var (_, acctInvoiceN, _) = await ctx.NewContract("d4acct", 8000, Ctx.Account());
        var acctInvoice = acctInvoiceN!.Value;
        await ctx.AdvanceToDue(acctInvoice);
        ctx.Check("account-only tender paid without a gateway charge (S20 Method=2)",
            await ctx.WaitInvoiceState(acctInvoice, "paid"));
        ctx.Check("no gateway journal entries for the account-only member token",
            (await ctx.ExtCharges($"token={acct.Token}")).Count == 0);
        ctx.Check("commerce debit covered the full base amount",
            await ctx.WaitUntil(async () => (await ctx.Bridge("account_debit", acctInvoice)).Count >= 1, "account-only debit"));
    }

    // ---------------------------------------------------------------------------------------
    public static async Task D5(Ctx ctx)
    {
        // --- debit: Durbin suppression at the fee brain ---
        var debit = ctx.Member("d5debit");
        var (_, debitInvoiceN, _) = await ctx.NewContract("d5debit", 10000, Ctx.Card(debit.InstrumentId));
        var debitInvoice = debitInvoiceN!.Value;
        await ctx.AdvanceToDue(debitInvoice);
        await ctx.WaitInvoiceState(debitInvoice, "paid");
        var debitAttempt = (await ctx.Attempts(debitInvoice)).Single()!;
        ctx.Check("debit card: fee zeroed by Pricing suppression, reason recorded",
            L(debitAttempt["feeCents"]) == 0 && S(debitAttempt["feeSuppressionReason"]) == "not_credit");
        var debitCharge = (await ctx.ExtCharges($"token={debit.Token}")).Single()!;
        ctx.Check("gateway saw no SurchargeAmount (null, never 0) [TF-A]",
            L(debitCharge["amountCents"]) == 10000 && debitCharge["surchargeCents"] is null);

        // --- Pricing outage: fail-safe, metered ---
        await ctx.Ext.PostAsync("/admin/pricing/outage", new { on = true });
        var outage = ctx.Member("d5outage");
        var (_, outageInvoiceN, _) = await ctx.NewContract("d5outage", 10000, Ctx.Card(outage.InstrumentId));
        var outageInvoice = outageInvoiceN!.Value;
        await ctx.AdvanceToDue(outageInvoice);
        await ctx.WaitInvoiceState(outageInvoice, "paid");
        await ctx.Ext.PostAsync("/admin/pricing/outage", new { on = false });
        var outageAttempt = (await ctx.Attempts(outageInvoice)).Single()!;
        ctx.Check("Pricing outage: charge proceeded fee-less, never blocked (fail-safe)",
            S(outageAttempt["outcome"]) == "approved" && L(outageAttempt["feeCents"]) == 0 &&
            S(outageAttempt["feeSuppressionReason"]) == "pricing_outage");
        var outageStudioRun = await ctx.Billing.GetAsync($"/studio-runs/{S(outageAttempt["studioRunId"])}");
        ctx.Check("fee drop metered in the run counters (P15)",
            S(outageStudioRun!["countersJson"]).Contains("\"feeDropped\":1"));
        ctx.Check("fee drop opened a controls work item (P15)",
            await ctx.WaitUntil(async () =>
            {
                var items = (JsonArray)(await ctx.Billing.GetAsync("/controls/work-items"))!;
                return items.Any(w => S(w!["kind"]) == "fee_dropped_by_outage" && S(w["refKey"]) == outageInvoice.ToString());
            }, "fee_dropped work item"));

        // --- credit -> debit swap mid-ladder via vault InstrumentUpdated (F5), not only self-serve ---
        var swap = ctx.Member("d5swap");
        var (_, swapInvoiceN, _) = await ctx.NewContract("d5swap", 10000, Ctx.Card(swap.InstrumentId));
        var swapInvoice = swapInvoiceN!.Value;
        await ctx.AdvanceToDue(swapInvoice);
        await ctx.WaitInvoiceState(swapInvoice, "dunning");
        var firstAttempt = (await ctx.Attempts(swapInvoice)).Single()!;
        ctx.Check("declined credit attempt carried a fee",
            L(firstAttempt["feeCents"]) == 300 && S(firstAttempt["fundingTypeAtCharge"]) == "credit");
        await ctx.WaitUntil(async () => (await ctx.Ladders(swapInvoice)).Count >= 1, "swap ladder");
        await ctx.Billing.PostAsync("/intake/instrument-events", new
        {
            eventId = Guid.NewGuid().ToString("N"),
            memberId = swap.MemberId,
            newToken = "tok_debit_new_d5",
            fundingType = "debit",
            brand = "mc",
            last4 = "2222"
        });
        ctx.Check("InstrumentUpdated woke ladders and fee disappeared on debit tender [TF-A]",
            await ctx.WaitInvoiceState(swapInvoice, "recovered"));
        var lastAttempt = (await ctx.Attempts(swapInvoice)).Last()!;
        ctx.Check("fee lawfully disappeared on the new tender (credit->debit) [TF-A]",
            S(lastAttempt["outcome"]) == "approved" && L(lastAttempt["feeCents"]) == 0 &&
            S(lastAttempt["feeSuppressionReason"]) == "not_credit");

        // --- unknown funding: enrichment resolves to credit ---
        var unknown = ctx.Member("d5unknown");
        var (_, unknownInvoiceN, _) = await ctx.NewContract("d5unknown", 10000, Ctx.Card(unknown.InstrumentId));
        var unknownInvoice = unknownInvoiceN!.Value;
        await ctx.AdvanceToDue(unknownInvoice);
        await ctx.WaitInvoiceState(unknownInvoice, "paid");
        var unknownAttempt = (await ctx.Attempts(unknownInvoice)).Single()!;
        ctx.Check("unclassified card enriched via Instruments, then fee applied [TF-A]",
            S(unknownAttempt["fundingTypeAtCharge"]) == "credit" && L(unknownAttempt["feeCents"]) == 300);

        // --- studio not opted in: no fee row, no surcharge ---
        var s2 = ctx.Member("s2");
        var (_, s2InvoiceN, _) = await ctx.NewContract("s2", 10000, Ctx.Card(s2.InstrumentId));
        var s2Invoice = s2InvoiceN!.Value;
        await ctx.AdvanceToDue(s2Invoice);
        await ctx.WaitInvoiceState(s2Invoice, "paid");
        var s2Attempt = (await ctx.Attempts(s2Invoice)).Single()!;
        ctx.Check("non-opted-in studio: no fee (merchant opt-in lives in Pricing)",
            L(s2Attempt["feeCents"]) == 0 && S(s2Attempt["feeSuppressionReason"]) == "not_opted_in");

        // P6: invalid policy rejected
        var (badStatus, badBody) = await ctx.Billing.PostAsync("/policies", new
        {
            id = "bad",
            definition = new { soft = new { retryDelaysDays = new[] { 0 } }, bankReturn = new { retryDelaysDays = new[] { 3 } }, fixable = new { giveUpDays = 7 } }
        });
        ctx.Check("P6 policy validation rejects zero-day retry foot-gun",
            badStatus == 400 && S(badBody!["error"]).Contains(">= 1"));
    }

    // ---------------------------------------------------------------------------------------
    public static async Task D6(Ctx ctx)
    {
        var m = ctx.Member("d6");
        var (_, invoiceIdN, _) = await ctx.NewContract("d6", 10000, Ctx.Card(m.InstrumentId));
        var invoiceId = invoiceIdN!.Value;
        await ctx.AdvanceToDue(invoiceId);

        ctx.Check("gateway timeout left the attempt unknown — no guessing", await ctx.WaitUntil(async () =>
        {
            var attempts = await ctx.Attempts(invoiceId);
            return attempts.Count == 1 && S(attempts[0]!["outcome"]) == "unknown";
        }, "unknown attempt"));
        ctx.Check("invoice stayed run-eligible, not falsely settled",
            S((await ctx.Invoice(invoiceId))["state"]) == "scheduled");

        await ctx.Billing.PostAsync($"/studios/{m.StudioId}/run");
        ctx.Check("next run queried before retrying and resolved to paid (P7)",
            await ctx.WaitInvoiceState(invoiceId, "paid"));

        var attempts2 = await ctx.Attempts(invoiceId);
        ctx.Check("still exactly one attempt — resolved, not re-charged",
            attempts2.Count == 1 && S(attempts2[0]!["outcome"]) == "approved");
        var charges = await ctx.ExtCharges($"token={m.Token}");
        ctx.Check("gateway journal: exactly one charge ever hit the money rail",
            charges.Count == 1);
    }

    // ---------------------------------------------------------------------------------------
    public static async Task D7(Ctx ctx)
    {
        var m = ctx.Member("d7");
        var (_, invoiceIdN, _) = await ctx.NewContract("d7", 10000, Ctx.Bank(m.InstrumentId));
        var invoiceId = invoiceIdN!.Value;
        await ctx.AdvanceToDue(invoiceId);
        ctx.Check("ACH initiated: invoice settling (async rail first-class)",
            await ctx.WaitInvoiceState(invoiceId, "settling"));
        ctx.Check("entitlement granted at initiation (HLD decision 3 default)",
            await ctx.WaitUntil(async () => (await ctx.Bridge("entitlement", invoiceId)).Count >= 1, "entitlement"));

        var attempt = (await ctx.Attempts(invoiceId)).Single()!;
        var gatewayRef = S(attempt["gatewayRef"]);
        await ctx.Ext.PostAsync("/admin/ach/return", new { gatewayRef, code = "R01" });

        ctx.Check("return webhook moved the invoice to dunning(bank_return) (F8)",
            await ctx.WaitInvoiceState(invoiceId, "dunning"));
        var returned = (await ctx.Attempts(invoiceId)).Single()!;
        ctx.Check("attempt marked returned with bank_return class",
            S(returned["outcome"]) == "returned" && S(returned["declineClass"]) == "bank_return");
        ctx.Check("entitlement clawed back via the bridge",
            await ctx.WaitUntil(async () => (await ctx.Bridge("clawback", invoiceId)).Count >= 1, "clawback"));
        ctx.Check("recovery ladder opened for the return",
            await ctx.WaitUntil(async () => (await ctx.Ladders(invoiceId)).Count >= 1, "ladder"));

        // F8 poll backstop: pending ACH settles via query without a webhook.
        var pollMember = ctx.Member("d7poll");
        var (_, pollInvoiceN, _) = await ctx.NewContract("d7poll", 10000, Ctx.Bank(pollMember.InstrumentId));
        var pollInvoice = pollInvoiceN!.Value;
        await ctx.AdvanceToDue(pollInvoice);
        await ctx.WaitInvoiceState(pollInvoice, "settling");
        var pollAttempt = (await ctx.Attempts(pollInvoice)).Single()!;
        // Mark gateway journal as settled so /gateway/query returns approved/settled.
        await ctx.Ext.PostAsync("/admin/ach/mark-settled", new { gatewayRef = S(pollAttempt["gatewayRef"]) });
        // Advance past the 1h poll age and force a poll tick.
        await ctx.Billing.PostAsync("/demo/time/advance", new { hours = 2 });
        await ctx.Billing.PostAsync("/controls/poll-pending");
        ctx.Check("missed-webhook poll resolved settling invoice to paid (F8 pull backstop)",
            await ctx.WaitInvoiceState(pollInvoice, "paid", "recovered"));
    }

    // ---------------------------------------------------------------------------------------
    public static async Task D8(Ctx ctx)
    {
        var agreementId = (Guid)ctx.State["d1.agreementId"];
        await ctx.Billing.PostAsync("/demo/seed-violation", new { agreementId });
        var (_, sweep) = await ctx.Billing.PostAsync("/controls/sweep");
        var violations = (JsonArray)sweep!["violations"]!;
        ctx.Check("sweeper caught the silent non-billing seed (active agreement, no open invoice)",
            violations.Any(v => S(v!["kind"]) == "missing_open_invoice" && S(v["refKey"]) == agreementId.ToString()));

        await ctx.Ext.PostAsync("/admin/phantom", new { on = true });
        var (_, recon) = await ctx.Billing.PostAsync("/controls/reconcile");
        await ctx.Ext.PostAsync("/admin/phantom", new { on = false });
        var deltas = (JsonArray)recon!["deltas"]!;
        ctx.Check("reconciliation flagged the gateway-side phantom charge (DB money == gateway money)",
            deltas.Any(d => S(d!["kind"]) == "recon_missing_in_db" && S(d["refKey"]) == "ch_phantom"));
        ctx.Check("no amount/fee-ledger mismatches for real charges [TF-A]",
            !deltas.Any(d => S(d!["kind"]) is "recon_amount_mismatch" or "recon_fee_ledger_mismatch"));

        var workItems = (JsonArray)(await ctx.Billing.GetAsync("/controls/work-items"))!;
        ctx.Check("deltas and fee-drop events opened work items",
            workItems.Count >= 2 && workItems.Any(w => S(w!["kind"]) == "fee_dropped_by_outage"));

        var runId = (string)ctx.State["d1.scheduleRunId"];
        var run = await ctx.Billing.GetAsync($"/runs/{runId}");
        ctx.Check("run close-out report present with counters",
            S(run!["run"]!["state"]) == "completed" && S(run["run"]!["reportJson"]).Contains("counters"));
    }

    // ---------------------------------------------------------------------------------------
    public static async Task D9(Ctx ctx)
    {
        var m = ctx.Member("d9");
        var (_, invoiceIdN, _) = await ctx.NewContract("d9", 10000, Ctx.Card(m.InstrumentId));
        var invoiceId = invoiceIdN!.Value;

        await ctx.Billing.PostAsync($"/studios/{m.StudioId}/pause");
        await ctx.AdvanceToDue(invoiceId);

        ctx.Check("run recorded the studio as skipped_paused (ops pause / F7)", await ctx.WaitUntil(async () =>
        {
            var runs = (JsonArray)(await ctx.Billing.GetAsync("/runs"))!;
            foreach (var runRow in runs.Take(5))
            {
                var run = await ctx.Billing.GetAsync($"/runs/{S(runRow!["id"])}");
                var studioRuns = (JsonArray)run!["studioRuns"]!;
                if (studioRuns.Any(s => s!["studioId"]!.GetValue<int>() == m.StudioId && S(s["state"]) == "skipped_paused"))
                    return true;
            }
            return false;
        }, "skipped_paused studio run"));

        await Task.Delay(1500);
        ctx.Check("invoice untouched while studio paused", S((await ctx.Invoice(invoiceId))["state"]) == "scheduled");

        await ctx.Billing.PostAsync($"/studios/{m.StudioId}/resume");
        await ctx.Billing.PostAsync("/demo/time/advance", new { hours = 2 });
        ctx.Check("resume drained the backlog on the next trigger (F7)",
            await ctx.WaitInvoiceState(invoiceId, "paid"));
    }

    // ---------------------------------------------------------------------------------------
    public static async Task D10(Ctx ctx)
    {
        var m = ctx.Member("d10");
        var (agreementId, invoiceIdN, _) = await ctx.NewContract("d10", 10000, Ctx.Card(m.InstrumentId));
        var cycleInvoice = invoiceIdN!.Value;
        var nextBefore = DateOnly.Parse(S((await ctx.Billing.GetAsync($"/agreements/{agreementId}"))!["nextPeriodStart"])[..10]);
        var now = await ctx.VirtualNow();
        var from = DateOnly.FromDateTime(now.Date.AddDays(1));
        var to = from.AddDays(6);
        var pauseDays = to.DayNumber - from.DayNumber + 1;

        var (_, pause) = await ctx.Billing.PostAsync("/agreements/pause", new
        {
            agreementId,
            from = from.ToString("yyyy-MM-dd"),
            to = to.ToString("yyyy-MM-dd"),
            pauseFeeCents = 1500
        });
        ctx.Check("agreement entered paused state with a pause_fee invoice (S15)",
            S((await ctx.Billing.GetAsync($"/agreements/{agreementId}"))!["state"]) == "paused"
            && pause!["pauseFeeInvoiceId"] is not null);

        var pauseFeeId = Guid.Parse(S(pause!["pauseFeeInvoiceId"]));
        await ctx.AdvanceToDue(pauseFeeId);
        ctx.Check("pause_fee invoice collected during the window",
            await ctx.WaitInvoiceState(pauseFeeId, "paid"));

        // Original cycle in the pause window should have been canceled.
        ctx.Check("open cycle inside the pause window was canceled",
            S((await ctx.Invoice(cycleInvoice))["state"]) == "canceled");

        // Auto-resume after PausedTo: advance past end and wait for state flip.
        await ctx.Billing.PostAsync("/demo/time/advance", new { to = to.ToDateTime(TimeOnly.MinValue).AddDays(1).AddHours(1) });
        DateOnly? shiftedPeriod = null;
        ctx.Check("auto-resume restored agreement to active and rematerialized a cycle (S15)",
            await ctx.WaitUntil(async () =>
            {
                var a = await ctx.Billing.GetAsync($"/agreements/{agreementId}");
                if (S(a!["state"]) != "active") return false;
                var invoices = (JsonArray)(await ctx.Billing.GetAsync($"/agreements/{agreementId}/invoices"))!;
                var scheduled = invoices.FirstOrDefault(i => S(i!["kind"]) == "cycle" && S(i["state"]) == "scheduled");
                if (scheduled is null) return false;
                shiftedPeriod = DateOnly.Parse(S(scheduled["periodStart"])[..10]);
                return true;
            }, "agreement resumed"));
        ctx.Check("auto-resume shifted the rematerialized cycle by the pause duration",
            shiftedPeriod == nextBefore.AddDays(pauseDays));
    }

    // ---------------------------------------------------------------------------------------
    public static async Task D11(Ctx ctx)
    {
        var m = ctx.Member("d11");
        var (agreementId, invoiceId, _) = await ctx.NewContract(
            "d11", 10000, billingTrigger: "depletion", entitlementRemaining: 3, Ctx.Card(m.InstrumentId));
        ctx.Check("depletion agreement starts with no open invoice (dormant)",
            invoiceId is null);

        var (_, sweep1) = await ctx.Billing.PostAsync("/controls/sweep");
        var v1 = (JsonArray)sweep1!["violations"]!;
        ctx.Check("sweeper does not flag depletion subscription as missing_open_invoice",
            !v1.Any(v => S(v!["kind"]) == "missing_open_invoice" && S(v["refKey"]) == agreementId.ToString()));

        await ctx.Billing.PostAsync("/intake/entitlement-depleted", new
        {
            eventId = Guid.NewGuid().ToString("N"),
            agreementId,
            remaining = 0
        });
        await ctx.Billing.PostAsync("/controls/depletion-sweep");
        var invoices = (JsonArray)(await ctx.Billing.GetAsync($"/agreements/{agreementId}/invoices"))!;
        ctx.Check("entitlement depletion materialized a cycle invoice (S16)",
            invoices.Count >= 1 && invoices.Any(i => S(i!["state"]) == "scheduled"));

        var due = invoices.First(i => S(i!["state"]) == "scheduled")!;
        await ctx.AdvanceToDue(Guid.Parse(S(due["id"])));
        ctx.Check("depletion invoice collected on the normal run path",
            await ctx.WaitInvoiceState(Guid.Parse(S(due["id"])), "paid"));

        // Sweep backstop: remaining already 0, live subscription, no EntitlementDepleted event.
        var (sweepAgId, sweepOnlyInv, _) = await ctx.NewContract(
            "d11", 10000, billingTrigger: "depletion", entitlementRemaining: 0, Ctx.Card(m.InstrumentId));
        ctx.Check("zero-remaining depletion stays dormant until the sweep (S16 pull backstop)",
            sweepOnlyInv is null);
        await ctx.Billing.PostAsync("/controls/depletion-sweep");
        var sweepInvoices = (JsonArray)(await ctx.Billing.GetAsync($"/agreements/{sweepAgId}/invoices"))!;
        ctx.Check("depletion sweep materialized the invoice without an entitlement event",
            sweepInvoices.Any(i => S(i!["state"]) == "scheduled"));
    }

    // ---------------------------------------------------------------------------------------
    public static async Task D12(Ctx ctx)
    {
        var m = ctx.Member("d12");
        var (agreementId, invoiceIdN, _) = await ctx.NewContract("d12", 10000, Ctx.Card(m.InstrumentId));
        var invoiceId = invoiceIdN!.Value;
        await ctx.AdvanceToDue(invoiceId);
        await ctx.WaitInvoiceState(invoiceId, "paid");

        await ctx.Billing.PostAsync("/webhooks/chargeback", new { invoiceId, reason = "customer_dispute" });
        var agreement = await ctx.Billing.GetAsync($"/agreements/{agreementId}");
        ctx.Check("chargeback flipped agreement to disputed (F6)", S(agreement!["state"]) == "disputed");
        ctx.Check("entitlement clawed back on chargeback",
            await ctx.WaitUntil(async () => (await ctx.Bridge("clawback", invoiceId)).Count >= 1, "chargeback clawback"));

        // Orchestrator gate: disputed agreements cannot be re-collected.
        var outcome = await ctx.Billing.PostAsync($"/studios/{m.StudioId}/run");
        await Task.Delay(1500);
        // Force a collect via self-serve won't apply; use a second open invoice if any — for disputed,
        // CollectAsync on a scheduled next invoice should skip.
        var invoices = (JsonArray)(await ctx.Billing.GetAsync($"/agreements/{agreementId}/invoices"))!;
        var next = invoices.FirstOrDefault(i => S(i!["state"]) == "scheduled");
        ctx.Check("paid chargeback leaves a next cycle to gate", next is not null);
        if (next is null) return;
        await ctx.AdvanceToDue(Guid.Parse(S(next["id"])));
        await Task.Delay(2000);
        ctx.Check("disputed agreement blocks collection of the next invoice (F7 orchestrator gate)",
            S((await ctx.Invoice(Guid.Parse(S(next["id"]))))["state"]) == "scheduled");
    }
}
