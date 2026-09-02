# AutoPay Rewrite POC — Architecture

## System boundary

**BillingCore** (`:5080`) is the modular monolith — intake, runs, collection, recovery, fees, outbox, controls, and workers all run in-process.

**Externals** (`:9876`) is a WireMock host representing every outbound business dependency. BillingCore talks to it only through `ExternalsClient`.

**Postgres** (`:5433`) is infrastructure (docker-compose or embedded fallback), not a business external.

```
DemoRunner ──REST──► BillingCore ──HTTP──► Externals (WireMock)
                         │
                         └── Postgres
Externals ──webhook──► BillingCore  (ACH return/settle)
```

## What we treat as external

| External system | Production analog | WireMock routes | Called from | Purpose |
|---|---|---|---|---|
| **Payments Gateway** | PSP / gateway-svc | `POST /gateway/charge`, `POST /gateway/query`, `GET /gateway/settlement-report` | `CollectionService`, `ControlsService` | MIT card/bank charges with `SurchargeAmount`; idempotent by key; query-before-retry (P7); settlement recon (P11); ACH async webhooks (F8) |
| **Pricing** | Mindbody.Pricing `v2/payment-method/fees` | `POST /pricing/v2/payment-method/fees` | `FeeService` | Only fee brain [TF-A]: 3% credit CNP, Durbin debit suppression, studio opt-in; outage fail-safe (P15) |
| **Instruments** | Payments vault / ngpTokens | `GET /instruments/v1/ngpTokens/{token}`, `PUT .../updateCardProfile` | `FeeService`, `AgreementService` | Funding metadata; enrichment for unknown cards; billing never sees PAN |
| **Commerce — sales** | Legacy sale write-back | `POST /commerce/sales` | `BridgeHandlers` | Sale + ProductID −13 + `paymentTransactionFees` [TF-A] |
| **Commerce — entitlements** | Membership access | `POST /commerce/entitlements`, `POST /commerce/entitlements/clawback` | `BridgeHandlers` | Grant on paid / ACH initiation; clawback on return or chargeback |
| **Commerce — membership** | Member status flags | `POST /commerce/membership` | `BridgeHandlers` | Write-off / declined membership event (D3) |
| **Account ledger** | Commerce-owned balance (HLD §5c) | `POST /commerce/account/balance`, `GET .../balance/{id}`, `POST .../debit` | `Seeder`, `CollectionService` | Method=2 account tender; billing cache only, never authoritative |
| **Notifications** | Email/SMS service | `POST /notify` + `Idempotency-Key` | `BridgeHandlers` | Ladder failure notify; P13 dedup (D3) |

### WireMock admin (demo only, not production externals)

- `POST /admin/reset` — deterministic DemoRunner re-runs
- `POST /admin/pricing/outage`, `POST /admin/phantom` — fault injection (D5/D8)
- `POST /admin/ach/return|settle|mark-settled` — async rail + F8 poll
- `GET /admin/journal/*` — DemoRunner ground truth

### Not modeled as external

- **Recovery** — in-process `RecoveryService`, not a separate deployable
- **Contract intake upstream** — simulated via `POST /intake/contract-events`
- **LaunchDarkly** — seed/config flags
- **SQS** — Postgres `work_queue` + `outbox` with leases
- **EventBridge cron** — `TriggerWorker` on virtual clock
- **Account credit** — not stubbed (balance + debit only)

## Happy path data flow (D1 golden path)

Calendar agreement · credit card · TF-opted-in studio · single card tender.

### Sequence

1. **Contract sold** — `IntakeService` dedups event-id, creates `Agreement`, JIT materializes first `Invoice` (frozen base, state=scheduled).
2. **Time advance** — Virtual clock crosses `DueAt`; `TriggerWorker` opens `ScheduleRun`, fans out `StudioRun`.
3. **Run dispatch** — `QueueWorker` claims studio run; `RunService.ExecuteStudioRun` selects due scheduled invoices.
4. **Single-writer collect** — `CollectionService` locks invoice (`FOR UPDATE`), sets state=collecting, walks tender slot 0.
5. **Fee at dispatch [TF-A]** — `FeeService` → Instruments (funding=credit) → Pricing (3% fee) → fee fields on `Attempt` before gateway.
6. **Gateway charge** — `POST /gateway/charge` with `amountCents = base + fee`, `surchargeAmountCents = fee`, MIT off-session; returns approved + gatewayRef + NTI.
7. **Paid transaction T1** — Same DB transaction: invoice paid, next cycle materialized, outbox `InvoicePaid` enqueued.
8. **Outbox → bridge** — `OutboxWorker` → `BridgeHandlers`: `POST /commerce/sales` (−13 + paymentTransactionFees), `POST /commerce/entitlements` grant.
9. **Proof** — DemoRunner verifies REST state + WireMock journals (`/admin/journal/charges`, `/admin/journal/bridge`).

### Design choices visible on the happy path

- Fee follows the **tender at dispatch**, not the invoice total.
- Money commit (T1) is **separate** from bridge side effects (T3 outbox).
- Gateway idempotency key = attempt key.
- Next period materialized **inside** the paid transaction — duplicate renewal is a constraint violation, not a race.
