using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WireMock;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using WireMock.Settings;
using WireMock.Types;
using WireMock.Util;

// ============================================================================================
// Externals host: every service outside billing-core, stubbed with WireMock.Net.
//   Gateway (charge/query/settlement) · Pricing (fee brain) · Instruments (funding metadata)
//   Commerce bridge (sales/entitlements/membership) · Notifications (idempotency-keyed)
// Behavior is keyed by instrument token markers; admin endpoints toggle outage/phantom
// scenarios, fire ACH webhooks, and expose journals the DemoRunner uses as proof.
// ============================================================================================

var billingUrl = Environment.GetEnvironmentVariable("BILLING_URL") ?? "http://localhost:5080";
var http = new HttpClient();

var server = WireMockServer.Start(new WireMockServerSettings
{
    Urls = new[] { "http://localhost:9876" }
});

Console.WriteLine($"Externals (WireMock.Net) listening on {server.Url}");
Console.WriteLine($"Forwarding gateway webhooks to {billingUrl}");

// ----------------------------------------- state -------------------------------------------

var chargeCounts = new ConcurrentDictionary<string, int>();          // token -> charge count
var journal = new ConcurrentDictionary<string, ChargeEntry>();       // idempotencyKey -> entry
var fundingOverrides = new ConcurrentDictionary<string, string>();   // token -> enriched funding
var notifyJournal = new ConcurrentDictionary<string, NotifyEntry>(); // idempotency key -> entry
var bridgeJournal = new ConcurrentBag<BridgeEntry>();
var feeRows = new ConcurrentBag<FeeRow>();
var accountBalances = new ConcurrentDictionary<Guid, long>();
var accountDebits = new ConcurrentDictionary<string, AccountDebitResult>(); // idempotency
var pricingOutage = false;
var phantomInReport = false;
var optedInStudios = new HashSet<int> { 1, 3 };

string DeriveFunding(string token) =>
    fundingOverrides.TryGetValue(token, out var over) ? over
    : token.Contains("debit") ? "debit"
    : token.Contains("unknown") ? "unknown"
    : token.Contains("ach") ? "n/a"
    : "credit";

static string Hash(string input, string prefix)
{
    var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(input));
    return prefix + Convert.ToHexString(bytes)[..12].ToLowerInvariant();
}

static ResponseMessage JsonResponse(object body, int status = 200) => new()
{
    StatusCode = status,
    Headers = new Dictionary<string, WireMockList<string>> { ["Content-Type"] = new("application/json") },
    BodyData = new BodyData
    {
        DetectedBodyType = BodyType.String,
        BodyAsString = JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))
    }
};

// ----------------------------------------- gateway -----------------------------------------

server.Given(Request.Create().WithPath("/gateway/charge").UsingPost())
    .RespondWith(Response.Create().WithCallback(req =>
    {
        var body = JsonNode.Parse(req.Body!)!;
        var token = body["token"]!.GetValue<string>();
        var key = body["idempotencyKey"]!.GetValue<string>();
        var amount = body["amountCents"]!.GetValue<long>();
        var surcharge = body["surchargeAmountCents"]?.GetValue<long?>();
        var rail = body["rail"]?.GetValue<string>() ?? "card";
        var mit = body["mit"]?.ToJsonString() ?? "{}";

        // Idempotent gateway: same key returns the recorded outcome.
        if (journal.TryGetValue(key, out var existing))
            return JsonResponse(new { status = existing.Status is "settled" or "returned" ? "approved" : existing.Status, declineCode = existing.DeclineCode, gatewayRef = existing.GatewayRef, networkTransactionId = existing.Nti });

        var count = chargeCounts.AddOrUpdate(token, 1, (_, c) => c + 1);
        string status;
        string? declineCode = null;
        var sleepMs = 0;

        if (token.Contains("softok")) (status, declineCode) = count == 1 ? ("declined", "insufficient_funds") : ("approved", null);
        else if (token.Contains("softalways")) (status, declineCode) = ("declined", "insufficient_funds");
        else if (token.Contains("hard")) (status, declineCode) = ("declined", "do_not_honor");
        else if (token.Contains("fix")) (status, declineCode) = ("declined", "expired_card");
        else if (token.Contains("timeout")) { status = "approved"; if (count == 1) sleepMs = 5000; }
        else if (token.Contains("ach")) status = "pending";
        else status = "approved";

        var entry = new ChargeEntry(
            Key: key, Token: token, AmountCents: amount, SurchargeCents: surcharge, Rail: rail, Mit: mit,
            Status: status, DeclineCode: declineCode,
            GatewayRef: status is "approved" or "pending" ? Hash(key, "ch_") : null,
            Nti: status == "approved" && rail == "card" ? Hash(token, "nti_") : null);
        journal[key] = entry;

        // The timeout scenario: money moved (journal has it), but the caller gives up waiting (D6).
        if (sleepMs > 0) Thread.Sleep(sleepMs);

        return JsonResponse(new { status = entry.Status, declineCode = entry.DeclineCode, gatewayRef = entry.GatewayRef, networkTransactionId = entry.Nti });
    }));

server.Given(Request.Create().WithPath("/gateway/query").UsingPost())
    .RespondWith(Response.Create().WithCallback(req =>
    {
        var key = JsonNode.Parse(req.Body!)!["idempotencyKey"]!.GetValue<string>();
        return journal.TryGetValue(key, out var entry)
            ? JsonResponse(new { status = entry.Status is "settled" ? "approved" : entry.Status, declineCode = entry.DeclineCode, gatewayRef = entry.GatewayRef })
            : JsonResponse(new { status = "not_found" });
    }));

server.Given(Request.Create().WithPath("/gateway/settlement-report").UsingGet())
    .RespondWith(Response.Create().WithCallback(_ =>
    {
        var lines = journal.Values
            .Where(e => e.Status is "approved" or "settled")
            .Select(e => new { gatewayRef = e.GatewayRef!, amountCents = e.AmountCents, surchargeCents = e.SurchargeCents ?? 0 })
            .ToList<object>();
        if (phantomInReport)
            lines.Add(new { gatewayRef = "ch_phantom", amountCents = 12345L, surchargeCents = 0L });
        return JsonResponse(lines);
    }));

// ----------------------------------------- pricing -----------------------------------------

server.Given(Request.Create().WithPath("/pricing/v2/payment-method/fees").UsingPost())
    .RespondWith(Response.Create().WithCallback(req =>
    {
        if (pricingOutage) return JsonResponse(new { error = "pricing unavailable" }, 500);
        var body = JsonNode.Parse(req.Body!)!;
        var studioId = body["subscriberId"]!.GetValue<int>();
        var amount = body["amountInCents"]!.GetValue<long>();
        var funding = body["cardFundingType"]?.GetValue<string>() ?? "unknown";

        if (!optedInStudios.Contains(studioId))
            return JsonResponse(new { applicableFees = Array.Empty<object>() });

        // Durbin suppression lives here, in the fee brain: non-credit => fee rewritten to zero.
        var fee = funding == "credit" ? amount * 3 / 100 : 0;
        return JsonResponse(new { applicableFees = new[] { new { feeType = "TransactionFee", feeAmountInCents = fee } } });
    }));

// --------------------------------------- instruments ---------------------------------------

server.Given(Request.Create().WithPath("/instruments/v1/ngpTokens/*/updateCardProfile").UsingPut())
    .RespondWith(Response.Create().WithCallback(req =>
    {
        var token = req.PathSegments[3];
        fundingOverrides[token] = "credit"; // enrichment resolves the demo's unclassified card to credit
        return JsonResponse(new { cardFundingType = "credit" });
    }));

server.Given(Request.Create().WithPath("/instruments/v1/ngpTokens/*").UsingGet())
    .RespondWith(Response.Create().WithCallback(req =>
    {
        var token = req.PathSegments[3];
        return JsonResponse(new { cardMetadata = new { cardFundingType = DeriveFunding(token) } });
    }));

// ------------------------------------- commerce bridge -------------------------------------

server.Given(Request.Create().WithPath("/commerce/sales").UsingPost())
    .RespondWith(Response.Create().WithCallback(req =>
    {
        var body = req.Body ?? "{}";
        bridgeJournal.Add(new BridgeEntry("sale", body, DateTime.UtcNow));
        try
        {
            var node = JsonNode.Parse(body);
            if (node?["paymentTransactionFees"] is JsonArray fees)
            {
                foreach (var f in fees)
                {
                    feeRows.Add(new FeeRow(
                        f!["amountCents"]!.GetValue<long>(),
                        f["paymentRef"]?.GetValue<string>(),
                        node["invoiceId"]?.GetValue<string>()));
                }
            }
        }
        catch { /* journal best-effort */ }
        return JsonResponse(new { accepted = true });
    }));

foreach (var (path, type) in new[]
         {
             ("/commerce/entitlements", "entitlement"),
             ("/commerce/entitlements/clawback", "clawback"),
             ("/commerce/membership", "membership")
         })
{
    server.Given(Request.Create().WithPath(path).UsingPost())
        .RespondWith(Response.Create().WithCallback(req =>
        {
            bridgeJournal.Add(new BridgeEntry(type, req.Body ?? "{}", DateTime.UtcNow));
            return JsonResponse(new { accepted = true });
        }));
}

// Commerce-owned account ledger (HLD 5c) — billing never mutates this locally.
server.Given(Request.Create().WithPath("/commerce/account/balance").UsingPost())
    .RespondWith(Response.Create().WithCallback(req =>
    {
        var body = JsonNode.Parse(req.Body!)!;
        var memberId = Guid.Parse(body["memberId"]!.GetValue<string>());
        var balance = body["balanceCents"]!.GetValue<long>();
        accountBalances[memberId] = balance;
        return JsonResponse(new { memberId, balanceCents = balance });
    }));

server.Given(Request.Create().WithPath("/commerce/account/balance/*").UsingGet())
    .RespondWith(Response.Create().WithCallback(req =>
    {
        var memberId = Guid.Parse(req.PathSegments[^1]);
        accountBalances.TryGetValue(memberId, out var balance);
        return JsonResponse(new { memberId, balanceCents = balance });
    }));

server.Given(Request.Create().WithPath("/commerce/account/debit").UsingPost())
    .RespondWith(Response.Create().WithCallback(req =>
    {
        var body = JsonNode.Parse(req.Body!)!;
        var memberId = Guid.Parse(body["memberId"]!.GetValue<string>());
        var amount = body["amountCents"]!.GetValue<long>();
        var key = body["idempotencyKey"]!.GetValue<string>();
        if (accountDebits.TryGetValue(key, out var prior))
            return JsonResponse(prior);

        accountBalances.TryGetValue(memberId, out var balance);
        AccountDebitResult result;
        if (balance <= 0 || amount <= 0)
            result = new AccountDebitResult(false, 0, balance, "insufficient_account_balance");
        else
        {
            var debit = Math.Min(balance, amount);
            accountBalances[memberId] = balance - debit;
            result = new AccountDebitResult(true, debit, accountBalances[memberId], null);
        }
        accountDebits[key] = result;
        bridgeJournal.Add(new BridgeEntry("account_debit", req.Body ?? "{}", DateTime.UtcNow));
        return JsonResponse(new { ok = result.Ok, debitedCents = result.DebitedCents, balanceCents = result.BalanceCents, declineCode = result.DeclineCode });
    }));

// -------------------------------------- notifications --------------------------------------

server.Given(Request.Create().WithPath("/notify").UsingPost())
    .RespondWith(Response.Create().WithCallback(req =>
    {
        var key = req.Headers != null && req.Headers.TryGetValue("Idempotency-Key", out var v) ? v.ToString() : "(none)";
        var entry = notifyJournal.AddOrUpdate(key,
            _ => new NotifyEntry(key, req.Body ?? "{}", Requests: 1, Delivered: 1),
            (_, e) => e with { Requests = e.Requests + 1 });
        var delivered = entry.Requests == 1;
        return JsonResponse(new { delivered, duplicate = !delivered });
    }));

// ------------------------------------------ admin ------------------------------------------

server.Given(Request.Create().WithPath("/admin/reset").UsingPost())
    .RespondWith(Response.Create().WithCallback(_ =>
    {
        chargeCounts.Clear();
        journal.Clear();
        fundingOverrides.Clear();
        notifyJournal.Clear();
        bridgeJournal.Clear();
        feeRows.Clear();
        accountBalances.Clear();
        accountDebits.Clear();
        pricingOutage = false;
        phantomInReport = false;
        return JsonResponse(new { reset = true });
    }));

server.Given(Request.Create().WithPath("/admin/pricing/outage").UsingPost())
    .RespondWith(Response.Create().WithCallback(req =>
    {
        pricingOutage = JsonNode.Parse(req.Body!)!["on"]!.GetValue<bool>();
        return JsonResponse(new { pricingOutage });
    }));

server.Given(Request.Create().WithPath("/admin/phantom").UsingPost())
    .RespondWith(Response.Create().WithCallback(req =>
    {
        phantomInReport = JsonNode.Parse(req.Body!)!["on"]!.GetValue<bool>();
        return JsonResponse(new { phantomInReport });
    }));

foreach (var (path, hook) in new[] { ("/admin/ach/return", "ach_return"), ("/admin/ach/settle", "ach_settled") })
{
    server.Given(Request.Create().WithPath(path).UsingPost())
        .RespondWith(Response.Create().WithCallback(req =>
        {
            var body = JsonNode.Parse(req.Body!)!;
            var gatewayRef = body["gatewayRef"]!.GetValue<string>();
            var code = body["code"]?.GetValue<string>();
            var entry = journal.Values.FirstOrDefault(e => e.GatewayRef == gatewayRef);
            if (entry is null) return JsonResponse(new { error = "unknown gatewayRef" }, 404);

            journal[entry.Key] = entry with { Status = hook == "ach_settled" ? "settled" : "returned" };

            // Fire the webhook to billing-core out-of-band, like a real PSP.
            var payload = JsonSerializer.Serialize(new { type = hook, gatewayRef, code });
            _ = Task.Run(async () =>
            {
                try
                {
                    await http.PostAsync($"{billingUrl}/webhooks/gateway",
                        new StringContent(payload, Encoding.UTF8, "application/json"));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"webhook forward failed: {ex.Message}");
                }
            });
            return JsonResponse(new { forwarded = hook, gatewayRef });
        }));
}

// Journal-only settle — no webhook — so the F8 poll backstop can be demonstrated.
server.Given(Request.Create().WithPath("/admin/ach/mark-settled").UsingPost())
    .RespondWith(Response.Create().WithCallback(req =>
    {
        var gatewayRef = JsonNode.Parse(req.Body!)!["gatewayRef"]!.GetValue<string>();
        var entry = journal.Values.FirstOrDefault(e => e.GatewayRef == gatewayRef);
        if (entry is null) return JsonResponse(new { error = "unknown gatewayRef" }, 404);
        journal[entry.Key] = entry with { Status = "settled" };
        return JsonResponse(new { marked = "settled", gatewayRef, webhook = false });
    }));

server.Given(Request.Create().WithPath("/admin/journal/charges").UsingGet())
    .RespondWith(Response.Create().WithCallback(req =>
    {
        string? Get(string name) =>
            req.Query != null && req.Query.TryGetValue(name, out var v) ? v.ToString() : null;
        var key = Get("key");
        var gatewayRef = Get("ref");
        var token = Get("token");
        var entries = journal.Values
            .Where(e => key == null || e.Key == key)
            .Where(e => gatewayRef == null || e.GatewayRef == gatewayRef)
            .Where(e => token == null || e.Token == token)
            .ToList();
        return JsonResponse(entries);
    }));

server.Given(Request.Create().WithPath("/admin/journal/notify").UsingGet())
    .RespondWith(Response.Create().WithCallback(req =>
    {
        var key = req.Query != null && req.Query.TryGetValue("key", out var v) ? v.ToString() : null;
        return key is null
            ? JsonResponse(notifyJournal.Values.ToList())
            : notifyJournal.TryGetValue(key, out var entry)
                ? JsonResponse(entry)
                : JsonResponse(new NotifyEntry(key, "{}", 0, 0));
    }));

server.Given(Request.Create().WithPath("/admin/journal/bridge").UsingGet())
    .RespondWith(Response.Create().WithCallback(req =>
    {
        string? Get(string name) =>
            req.Query != null && req.Query.TryGetValue(name, out var v) ? v.ToString() : null;
        var type = Get("type");
        var contains = Get("contains");
        var entries = bridgeJournal
            .Where(e => type == null || e.Type == type)
            .Where(e => contains == null || e.Body.Contains(contains, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.At)
            .ToList();
        return JsonResponse(entries);
    }));

server.Given(Request.Create().WithPath("/admin/journal/fee-rows").UsingGet())
    .RespondWith(Response.Create().WithCallback(_ =>
        JsonResponse(feeRows.Select(f => new { amountCents = f.AmountCents, paymentRef = f.PaymentRef, invoiceId = f.InvoiceId }).ToList())));

Console.WriteLine("Stubs ready: gateway, pricing, instruments, commerce, account, notify (+admin).");
new ManualResetEvent(false).WaitOne();

internal sealed record ChargeEntry(
    string Key, string Token, long AmountCents, long? SurchargeCents, string Rail, string Mit,
    string Status, string? DeclineCode, string? GatewayRef, string? Nti);

internal sealed record NotifyEntry(string Key, string Body, int Requests, int Delivered);

internal sealed record BridgeEntry(string Type, string Body, DateTime At);

internal sealed record FeeRow(long AmountCents, string? PaymentRef, string? InvoiceId);

internal sealed record AccountDebitResult(bool Ok, long DebitedCents, long BalanceCents, string? DeclineCode);
