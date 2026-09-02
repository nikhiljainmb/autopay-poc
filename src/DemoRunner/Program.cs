using System.Text.Json;
using System.Text.Json.Nodes;
using DemoRunner;
using Spectre.Console;

var billingUrl = Environment.GetEnvironmentVariable("BILLING_URL") ?? "http://localhost:5080";
var externalsUrl = Environment.GetEnvironmentVariable("EXTERNALS_URL") ?? "http://localhost:9876";
var filter = args.FirstOrDefault(); // e.g. "D3" to run a single scenario

AnsiConsole.Write(new FigletText("AutoPay POC").Color(Color.Aqua));
AnsiConsole.MarkupLine("[grey]Rewrite-HLD billing core · existing-production run mechanics · WireMock externals[/]");
AnsiConsole.WriteLine();

var ctx = new Ctx(new Api(billingUrl), new Api(externalsUrl));

// Wait for both hosts.
if (!await ctx.WaitUntil(async () => await ctx.Billing.TryGetAsync("/demo/time") is not null, "billing-core up", 60000) ||
    !await ctx.WaitUntil(async () => await ctx.Ext.TryGetAsync("/admin/journal/charges") is not null, "externals up", 30000))
{
    AnsiConsole.MarkupLine("[red]Hosts not reachable. Start Postgres, Externals and BillingCore first (see README).[/]");
    return 2;
}

ctx.Seed = (await ctx.Billing.PostAsync("/demo/seed")).Body!;
AnsiConsole.MarkupLine("[grey]Seeded studios and scenario members (commerce account ledger initialized).[/]");
AnsiConsole.WriteLine();

var scenarios = new (string Name, Func<Ctx, Task> Run)[]
{
    ("D1 Golden path — contract to paid, fee + MIT + next period", Scenarios.D1),
    ("D2 Idempotency — replay, rerun, F4 amend, duplicate cycle blocked", Scenarios.D2),
    ("D3 Decline classes — soft ladder, hard stop, fixable self-serve", Scenarios.D3),
    ("D4 Tender chains — split, decline-to-account, account-only Method=2", Scenarios.D4),
    ("D5 Fees [TF-A] — debit, outage, swap, enrichment, InstrumentUpdated", Scenarios.D5),
    ("D6 Unknown outcome — query-before-retry, no double charge", Scenarios.D6),
    ("D7 Async rail — settling, webhook return, poll backstop", Scenarios.D7),
    ("D8 Controls — sweeper, reconciliation, fee-drop work items", Scenarios.D8),
    ("D9 Ops studio pause (F7) — skipped while paused, drains on resume", Scenarios.D9),
    ("D10 Agreement pause windows (S15) — pause_fee + auto-resume", Scenarios.D10),
    ("D11 Depletion wake (S16) — dormant until entitlement depleted", Scenarios.D11),
    ("D12 Chargeback (F6) — disputed + clawback", Scenarios.D12)
};

var failedScenarios = 0;
foreach (var (name, run) in scenarios)
{
    if (filter is not null && !name.StartsWith(filter, StringComparison.OrdinalIgnoreCase)) continue;

    ctx.BeginScenario(name);
    try
    {
        await run(ctx);
    }
    catch (Exception ex)
    {
        ctx.Check($"scenario crashed: {ex.Message}", false);
    }
    if (!ctx.EndScenario()) failedScenarios++;
}

AnsiConsole.WriteLine();
var total = ctx.TotalChecks;
var passed = ctx.PassedChecks;
var summary = new Table().Border(TableBorder.Rounded);
summary.AddColumn("Result");
summary.AddColumn("Checks");
summary.AddRow(
    failedScenarios == 0 ? "[green bold]ALL SCENARIOS PASSED[/]" : $"[red bold]{failedScenarios} SCENARIO(S) FAILED[/]",
    $"{passed}/{total} checks passed");
AnsiConsole.Write(summary);

return failedScenarios == 0 ? 0 : 1;

namespace DemoRunner
{
    public class Api
    {
        private readonly HttpClient _http;

        public Api(string baseUrl) => _http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };

        public async Task<JsonNode?> GetAsync(string path)
        {
            var resp = await _http.GetAsync(path);
            var text = await resp.Content.ReadAsStringAsync();
            return text.Length == 0 ? null : JsonNode.Parse(text);
        }

        public async Task<JsonNode?> TryGetAsync(string path)
        {
            try
            {
                var resp = await _http.GetAsync(path);
                return resp.IsSuccessStatusCode ? JsonNode.Parse(await resp.Content.ReadAsStringAsync()) : null;
            }
            catch
            {
                return null;
            }
        }

        public async Task<(int Status, JsonNode? Body)> PostAsync(string path, object? body = null)
        {
            var content = new StringContent(
                body is null ? "{}" : JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                System.Text.Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync(path, content);
            var text = await resp.Content.ReadAsStringAsync();
            return ((int)resp.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text));
        }
    }

    public class Ctx
    {
        public Api Billing { get; }
        public Api Ext { get; }
        public JsonNode Seed { get; set; } = new JsonObject();
        public Dictionary<string, object> State { get; } = new();
        public int TotalChecks { get; private set; }
        public int PassedChecks { get; private set; }

        private readonly List<(string Desc, bool Ok)> _current = new();
        private string _scenarioName = "";

        public Ctx(Api billing, Api ext)
        {
            Billing = billing;
            Ext = ext;
        }

        public void BeginScenario(string name)
        {
            _scenarioName = name;
            _current.Clear();
            AnsiConsole.MarkupLine($"[bold aqua]▶ {Markup.Escape(name)}[/]");
        }

        public bool EndScenario()
        {
            var ok = _current.All(c => c.Ok);
            foreach (var (desc, pass) in _current)
                AnsiConsole.MarkupLine(pass ? $"  [green]✓[/] {Markup.Escape(desc)}" : $"  [red]✗ {Markup.Escape(desc)}[/]");
            AnsiConsole.MarkupLine(ok
                ? $"  [green bold]{Markup.Escape(_scenarioName.Split(' ')[0])} passed[/]"
                : $"  [red bold]{Markup.Escape(_scenarioName.Split(' ')[0])} FAILED[/]");
            AnsiConsole.WriteLine();
            return ok;
        }

        public void Check(string desc, bool ok, string? detail = null)
        {
            TotalChecks++;
            if (ok) PassedChecks++;
            _current.Add((detail is null || ok ? desc : $"{desc} — {detail}", ok));
        }

        public async Task<bool> WaitUntil(Func<Task<bool>> predicate, string what, int timeoutMs = 25000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    if (await predicate()) return true;
                }
                catch
                {
                    // keep polling
                }
                await Task.Delay(300);
            }
            AnsiConsole.MarkupLine($"  [yellow]timeout waiting for: {Markup.Escape(what)}[/]");
            return false;
        }

        // ---------- domain helpers ----------

        public (Guid MemberId, Guid InstrumentId, int StudioId, string Token) Member(string key)
        {
            var m = Seed["members"]![key]!;
            return (Guid.Parse(m["memberId"]!.GetValue<string>()),
                    Guid.Parse(m["instrumentId"]!.GetValue<string>()),
                    m["studioId"]!.GetValue<int>(),
                    m["token"]!.GetValue<string>());
        }

        public static object Card(Guid instrumentId, long? capCents = null) => new { type = "card", instrumentId, capCents };
        public static object Bank(Guid instrumentId) => new { type = "bank", instrumentId };
        public static object Account() => new { type = "account" };

        public async Task<DateTime> VirtualNow()
        {
            var t = await Billing.GetAsync("/demo/time");
            return DateTime.Parse(t!["utcNow"]!.GetValue<string>(), null, System.Globalization.DateTimeStyles.RoundtripKind);
        }

        /// <summary>Sell a contract starting tomorrow (virtual); returns (agreementId, invoiceId?, eventBody).</summary>
        public async Task<(Guid AgreementId, Guid? InvoiceId, object EventBody)> NewContract(
            string memberKey, long amountCents, params object[] slots) =>
            await NewContract(memberKey, amountCents, billingTrigger: "calendar", entitlementRemaining: null, slots);

        public async Task<(Guid AgreementId, Guid? InvoiceId, object EventBody)> NewContract(
            string memberKey, long amountCents, string billingTrigger, int? entitlementRemaining, params object[] slots)
        {
            var (memberId, _, studioId, _) = Member(memberKey);
            var startDate = (await VirtualNow()).Date.AddDays(1);
            var body = new
            {
                eventId = Guid.NewGuid().ToString("N"),
                type = "ContractSold",
                studioId,
                memberId,
                contractId = $"C-{memberKey}-{Guid.NewGuid().ToString("N")[..6]}",
                amountCents,
                startDate = startDate.ToString("yyyy-MM-dd"),
                tenderChain = slots,
                policyId = "standard",
                billingTrigger,
                entitlementRemaining
            };
            var (status, resp) = await Billing.PostAsync("/intake/contract-events", body);
            if (status != 200) throw new InvalidOperationException($"intake failed: {status} {resp}");
            Guid? invoiceId = resp!["invoiceId"] is null || resp["invoiceId"]!.GetValueKind() == System.Text.Json.JsonValueKind.Null
                ? null
                : Guid.Parse(resp["invoiceId"]!.GetValue<string>());
            return (Guid.Parse(resp["agreementId"]!.GetValue<string>()), invoiceId, body);
        }

        public async Task<JsonNode> Invoice(Guid id) => (await Billing.GetAsync($"/invoices/{id}"))!;

        public async Task<JsonArray> Attempts(Guid invoiceId) =>
            (JsonArray)(await Billing.GetAsync($"/invoices/{invoiceId}/attempts"))!;

        public async Task<JsonArray> Ladders(Guid invoiceId) =>
            (JsonArray)(await Billing.GetAsync($"/invoices/{invoiceId}/ladders"))!;

        /// <summary>Advance the virtual clock to the invoice's due time + margin; the auto trigger fires.</summary>
        public async Task AdvanceToDue(Guid invoiceId, int extraMinutes = 35)
        {
            var invoice = await Invoice(invoiceId);
            var due = DateTime.Parse(invoice["dueAt"]!.GetValue<string>(), null, System.Globalization.DateTimeStyles.RoundtripKind);
            await Billing.PostAsync("/demo/time/advance", new { to = due.AddMinutes(extraMinutes) });
        }

        public Task<bool> WaitInvoiceState(Guid invoiceId, params string[] states) =>
            WaitUntil(async () =>
            {
                var invoice = await Invoice(invoiceId);
                return states.Contains(invoice["state"]!.GetValue<string>());
            }, $"invoice {invoiceId} in [{string.Join('|', states)}]");

        public async Task<JsonArray> ExtCharges(string query) =>
            (JsonArray)(await Ext.GetAsync($"/admin/journal/charges?{query}"))!;

        public async Task<JsonArray> Bridge(string type, Guid invoiceId) =>
            (JsonArray)(await Ext.GetAsync($"/admin/journal/bridge?type={type}&contains={invoiceId}"))!;
    }
}
