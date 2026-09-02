using BillingCore.Domain;
using BillingCore.Infrastructure;

namespace BillingCore.Services;

public record FeeQuote(long FeeCents, string? FundingType, string? SuppressionReason);

/// <summary>
/// Per-attempt fee orchestration [TF-A] (HLD 4b): the fee follows the tender, not the invoice.
/// Funding type from vault metadata (with Instruments enrichment when unclassified), fee from
/// Pricing (the only fee brain), and every failure fails safe to "no fee, never block".
/// </summary>
public class FeeService
{
    private readonly ExternalsClient _ext;

    public FeeService(ExternalsClient ext) => _ext = ext;

    public async Task<FeeQuote> QuoteAsync(Studio studio, Instrument instrument, long amountCents)
    {
        if (instrument.Kind != "card") return new FeeQuote(0, "n/a", "not_card");

        var funding = instrument.FundingType;
        if (string.IsNullOrEmpty(funding) || funding == "unknown")
        {
            try
            {
                var ft = await _ext.GetFundingTypeAsync(instrument.Token);
                if (ft is null || ft == "unknown")
                    ft = await _ext.EnrichFundingTypeAsync(instrument.Token);
                if (ft is not null && ft != "unknown")
                {
                    instrument.FundingType = ft; // enrich vault-lite metadata; caller's TXN persists it
                    funding = ft;
                }
                else
                {
                    return new FeeQuote(0, "unknown", "lookup_failed");
                }
            }
            catch
            {
                return new FeeQuote(0, "unknown", "lookup_failed");
            }
        }

        try
        {
            var fees = await _ext.GetFeesAsync(studio.Id, amountCents, funding);
            var fee = fees.ApplicableFees.FirstOrDefault(f => f.FeeType == "TransactionFee");
            if (fee is null) return new FeeQuote(0, funding, "not_opted_in");
            if (fee.FeeAmountInCents <= 0)
                return new FeeQuote(0, funding, funding != "credit" ? "not_credit" : "zero_fee");
            return new FeeQuote(fee.FeeAmountInCents, funding, null);
        }
        catch
        {
            // Fail-safe: charge proceeds without the fee; metered via run counters (P15).
            return new FeeQuote(0, funding, "pricing_outage");
        }
    }

    /// <summary>Suppressions that represent a dropped fee (outage/lookup), not a lawful zero.</summary>
    public static bool IsDroppedFee(string? suppressionReason) =>
        suppressionReason is "pricing_outage" or "lookup_failed";
}
