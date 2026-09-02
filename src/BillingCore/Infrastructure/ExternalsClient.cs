using System.Net.Http.Json;

namespace BillingCore.Infrastructure;

// ---------- Gateway port (HLD 5b): typed contract, MIT context and SurchargeAmount are part of the type ----------

public record MitContext(string Initiator, bool OffSession, string? NetworkTransactionId);

/// <summary>
/// SurchargeAmountCents mirrors the verified Payments API semantics: cents, already included
/// within AmountCents, and null (never 0) when absent [TF-A].
/// </summary>
public record ChargeRequest(
    string Token,
    long AmountCents,
    long? SurchargeAmountCents,
    string Currency,
    string Rail,
    MitContext Mit,
    string IdempotencyKey);

public record ChargeResult(string Status, string? DeclineCode, string? GatewayRef, string? NetworkTransactionId);

public record QueryResult(string Status, string? DeclineCode, string? GatewayRef);

public record SettlementLine(string GatewayRef, long AmountCents, long SurchargeCents);

public record FeeLine(string FeeType, long FeeAmountInCents);
public record FeeResponse(List<FeeLine> ApplicableFees);

public class GatewayTimeoutException : Exception
{
    public GatewayTimeoutException(string message) : base(message) { }
}

/// <summary>All outbound HTTP to the WireMock externals host. One client, per-call timeouts.</summary>
public class ExternalsClient
{
    private readonly HttpClient _http;

    public ExternalsClient(HttpClient http) => _http = http;

    // -- Gateway --

    public async Task<ChargeResult> ChargeAsync(ChargeRequest request)
    {
        // Tight timeout on the money call so the unknown-outcome path is realistic (D6).
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            var resp = await _http.PostAsJsonAsync("/gateway/charge", request, Json.Options, cts.Token);
            resp.EnsureSuccessStatusCode();
            return (await resp.Content.ReadFromJsonAsync<ChargeResult>(Json.Options))!;
        }
        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException)
        {
            throw new GatewayTimeoutException($"gateway charge timed out (key {request.IdempotencyKey})");
        }
    }

    public async Task<QueryResult> QueryAsync(string idempotencyKey)
    {
        var resp = await _http.PostAsJsonAsync("/gateway/query", new { idempotencyKey }, Json.Options);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<QueryResult>(Json.Options))!;
    }

    public async Task<List<SettlementLine>> GetSettlementReportAsync()
    {
        return (await _http.GetFromJsonAsync<List<SettlementLine>>("/gateway/settlement-report", Json.Options))!;
    }

    // -- Pricing (the fee brain; POC mirror of v2/payment-method/fees) --

    public async Task<FeeResponse> GetFeesAsync(int studioId, long amountCents, string cardFundingType)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var resp = await _http.PostAsJsonAsync("/pricing/v2/payment-method/fees",
            new { subscriberId = studioId, transactionFeePaymentMethod = "CardNotPresent", amountInCents = amountCents, cardFundingType },
            Json.Options, cts.Token);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<FeeResponse>(Json.Options, cts.Token))!;
    }

    // -- Instruments (funding-type metadata + enrichment; POC mirror of ngpTokens) --

    public async Task<string?> GetFundingTypeAsync(string token)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var doc = await _http.GetFromJsonAsync<Dictionary<string, Dictionary<string, string>>>(
            $"/instruments/v1/ngpTokens/{token}", Json.Options, cts.Token);
        return doc != null && doc.TryGetValue("cardMetadata", out var meta) && meta.TryGetValue("cardFundingType", out var ft)
            ? ft
            : null;
    }

    public async Task<string?> EnrichFundingTypeAsync(string token)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var resp = await _http.PutAsJsonAsync($"/instruments/v1/ngpTokens/{token}/updateCardProfile", new { }, Json.Options, cts.Token);
        resp.EnsureSuccessStatusCode();
        var doc = await resp.Content.ReadFromJsonAsync<Dictionary<string, string>>(Json.Options, cts.Token);
        return doc != null && doc.TryGetValue("cardFundingType", out var ft) ? ft : null;
    }

    // -- Commerce bridge (sale + fee rows, entitlements, membership) --

    public async Task PostSaleAsync(object payload) =>
        (await _http.PostAsJsonAsync("/commerce/sales", payload, Json.Options)).EnsureSuccessStatusCode();

    public record BridgeFeeRow(long AmountCents, string? PaymentRef);

    public async Task<List<BridgeFeeRow>> GetBridgeFeeRowsAsync()
    {
        var entries = await _http.GetFromJsonAsync<List<Dictionary<string, object>>>("/admin/journal/bridge?type=sale", Json.Options)
                      ?? new();
        // Prefer dedicated admin endpoint if present.
        try
        {
            var fees = await _http.GetFromJsonAsync<List<BridgeFeeRow>>("/admin/journal/fee-rows", Json.Options);
            if (fees is not null) return fees;
        }
        catch { /* fall through */ }
        return new();
    }

    public async Task PostEntitlementAsync(object payload) =>
        (await _http.PostAsJsonAsync("/commerce/entitlements", payload, Json.Options)).EnsureSuccessStatusCode();

    public async Task PostClawbackAsync(object payload) =>
        (await _http.PostAsJsonAsync("/commerce/entitlements/clawback", payload, Json.Options)).EnsureSuccessStatusCode();

    public async Task PostMembershipEventAsync(object payload) =>
        (await _http.PostAsJsonAsync("/commerce/membership", payload, Json.Options)).EnsureSuccessStatusCode();

    // -- Commerce-owned account ledger (HLD 5c) --

    public record AccountBalance(Guid MemberId, long BalanceCents);
    public record AccountDebitResult(bool Ok, long DebitedCents, long BalanceCents, string? DeclineCode);

    public async Task ResetAsync() =>
        (await _http.PostAsync("/admin/reset", null)).EnsureSuccessStatusCode();

    public async Task SetAccountBalanceAsync(Guid memberId, long balanceCents) =>
        (await _http.PostAsJsonAsync("/commerce/account/balance", new { memberId, balanceCents }, Json.Options)).EnsureSuccessStatusCode();

    public async Task<AccountBalance> GetAccountBalanceAsync(Guid memberId) =>
        (await _http.GetFromJsonAsync<AccountBalance>($"/commerce/account/balance/{memberId}", Json.Options))!;

    public async Task<AccountDebitResult> DebitAccountAsync(Guid memberId, long amountCents, string idempotencyKey, Guid? invoiceId = null)
    {
        var resp = await _http.PostAsJsonAsync("/commerce/account/debit",
            new { memberId, amountCents, idempotencyKey, invoiceId }, Json.Options);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<AccountDebitResult>(Json.Options))!;
    }

    // -- Notifications (idempotency key = dedup by design, P13) --

    public async Task SendNotificationAsync(string idempotencyKey, object payload)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/notify")
        {
            Content = JsonContent.Create(payload, options: Json.Options)
        };
        req.Headers.Add("Idempotency-Key", idempotencyKey);
        (await _http.SendAsync(req)).EnsureSuccessStatusCode();
    }
}
