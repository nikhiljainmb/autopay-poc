using System.Text.Json;
using BillingCore.Infrastructure;

namespace BillingCore.Services;

/// <summary>
/// Outbox topic routing. Commerce, membership and notifications live behind the bridge ACL
/// (HLD 5c): they consume outcomes; they never interleave with the charge. InvoiceDeclined is
/// the one internal topic — it feeds the in-process Recovery module.
/// </summary>
public class BridgeHandlers
{
    private readonly ExternalsClient _ext;
    private readonly RecoveryService _recovery;

    public BridgeHandlers(ExternalsClient ext, RecoveryService recovery)
    {
        _ext = ext;
        _recovery = recovery;
    }

    public async Task HandleAsync(string topic, string payloadJson)
    {
        var payload = JsonDocument.Parse(payloadJson).RootElement;
        switch (topic)
        {
            case "InvoicePaid":
            {
                var feeCents = payload.GetProperty("feeCents").GetInt64();
                var lines = new List<object>
                {
                    new { product = "membership_cycle", amountCents = payload.GetProperty("baseCents").GetInt64() }
                };
                var paymentTransactionFees = new List<object>();
                if (feeCents > 0)
                {
                    // Fee persistence mirrors CSC Option A: ProductID -13 sale detail + PaymentTransactionFee row [TF-A].
                    lines.Add(new { productId = -13, product = "transaction_fee", amountCents = feeCents });
                    if (payload.TryGetProperty("payments", out var payments))
                    {
                        foreach (var p in payments.EnumerateArray())
                        {
                            var pFee = p.TryGetProperty("feeCents", out var fc) ? fc.GetInt64() : 0;
                            if (pFee <= 0) continue;
                            paymentTransactionFees.Add(new
                            {
                                paymentRef = p.TryGetProperty("gatewayRef", out var gr) ? gr.GetString() : null,
                                amountCents = pFee,
                                saleDetailProductId = -13
                            });
                        }
                    }
                    if (paymentTransactionFees.Count == 0)
                        paymentTransactionFees.Add(new { paymentRef = (string?)null, amountCents = feeCents, saleDetailProductId = -13 });
                }

                await _ext.PostSaleAsync(new
                {
                    invoiceId = payload.GetProperty("invoiceId").GetGuid(),
                    memberId = payload.GetProperty("memberId").GetGuid(),
                    studioId = payload.GetProperty("studioId").GetInt32(),
                    lines,
                    paymentTransactionFees,
                    payments = payload.GetProperty("payments")
                });
                if (payload.GetProperty("grantEntitlement").GetBoolean())
                    await _ext.PostEntitlementAsync(new
                    {
                        invoiceId = payload.GetProperty("invoiceId").GetGuid(),
                        memberId = payload.GetProperty("memberId").GetGuid(),
                        reason = "invoice_paid"
                    });
                break;
            }
            case "EntitlementGrant":
                await _ext.PostEntitlementAsync(payload);
                break;
            case "Clawback":
                await _ext.PostClawbackAsync(payload);
                break;
            case "WrittenOff":
                await _ext.PostMembershipEventAsync(new
                {
                    memberId = payload.GetProperty("memberId").GetGuid(),
                    invoiceId = payload.GetProperty("invoiceId").GetGuid(),
                    state = "declined",
                    reason = payload.GetProperty("reason").GetString()
                });
                break;
            case "InvoiceDeclined":
                await _recovery.HandleDeclinedAsync(new DeclinedPayload(
                    payload.GetProperty("invoiceId").GetGuid(),
                    payload.GetProperty("declineClass").GetString()!,
                    payload.GetProperty("residualCents").GetInt64()));
                break;
            case "NotifyRequest":
                await _ext.SendNotificationAsync(payload.GetProperty("idempotencyKey").GetString()!, payload);
                break;
            default:
                throw new InvalidOperationException($"unknown outbox topic {topic}");
        }
    }
}
