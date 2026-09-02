# AutoPay Rewrite POC

A demoable C# proof-of-concept of the **AutoPay Rewrite Target Architecture (HLD v2)**: the new
domain model (BillingAgreement / Invoice / CollectionAttempt, tender chains, decline-class recovery
ladders, per-attempt transaction fees `[TF-A]`) executed by the **existing production scheduling
mechanism** (scheduled trigger → ScheduleRun → StudioRun fan-out with idempotent claims, close-out,
rerun, manual runs). Every external service is stubbed with **WireMock.Net**. A console runner
proves each HLD claim end to end — 12 scenarios, 91 checks.

```
DemoRunner (Spectre console, D1-D12 assertions)
      │ REST                                ┌──────────────────────────────┐
      ▼                                     │ Externals :9876 (WireMock)   │
BillingCore :5080 ── HTTP ────────────────► │  gateway · pricing ·         │
  modular monolith                          │  instruments · commerce ·    │
  trigger → runs → paced dispatch →         │  account · notify (+journals)│
  single-writer collections → outbox        └───────────────┬──────────────┘
      │                                            webhook (ACH / chargeback)
      ▼                                                     │
Postgres :5433 (docker compose OR auto-started embedded) ◄──┘
```

## Quick start

Prereqs: .NET 8 SDK. Docker is optional — if nothing listens on `:5433`, BillingCore starts an
**embedded PostgreSQL** automatically (first run downloads binaries from Maven Central). Orphaned
embedded instances from a prior crash are reused.

One command:

```powershell
.\run-demo.ps1
```

Or manually, three terminals:

```powershell
# 1 (optional, only if you have Docker)
docker compose up -d

# 2
dotnet run --project src/Externals

# 3
dotnet run --project src/BillingCore

# then
dotnet run --project src/DemoRunner            # all scenarios
dotnet run --project src/DemoRunner -- D3      # a single scenario
```

Swagger UI: http://localhost:5080/swagger · WireMock journals: `GET :9876/admin/journal/charges|notify|bridge|fee-rows`

## What each scenario proves (HLD traceability)

| # | Scenario | HLD claim proved |
|---|---|---|
| D1 | Golden path | JIT materialization with frozen base amount; MIT context + `SurchargeAmount` on the wire (journal-verified); next period materialized inside the paid transaction (T1); sale with ProductID −13 fee line + `paymentTransactionFees` at the bridge |
| D2 | Idempotency + F4 amend | Event-id dedup (P3); duplicate cycle = DB constraint; ContractAmended absolute rewrite of open invoice + replay dedup; rerun never double-charges |
| D3 | Decline classes | Soft → ladder → `recovered`; hard → write-off + `past_due`; fixable → self-serve + instrument wake (F5); notify idempotency (P13) |
| D4 | Tender chains | Card-up-to-cap + account remainder via **commerce debit bridge** (S20); decline→account as a collection step; **account-only (Method=2)** — zero gateway charges + bridge debit journal |
| D5 | Fees [TF-A] + InstrumentUpdated | Debit → fee zeroed; Pricing outage → fee-less charge + `fee_dropped_by_outage` work item (P15); credit→debit swap; `POST /intake/instrument-events` wakes recovery (F5) |
| D6 | Unknown outcome | Gateway timeout → `unknown`; query-before-retry resolves without a second charge (P7) |
| D7 | Async rail + F8 poll | ACH → `settling`; return webhook → clawback; pending poll backstop settles without webhook |
| D8 | Controls | Sweeper (P10/P14, depletion-aware); settlement recon incl. fee-ledger/−13; phantom gateway row (P11/P15) |
| D9 | Ops studio pause | `Studio.Paused` → `skipped_paused`; resume drains (F7 ops gate) |
| D10 | Agreement pause (S15) | Pause window cancels open cycle invoice, materializes `pause_fee`, auto-resume shifts `NextPeriodStart` |
| D11 | Depletion (S16) | `BillingTrigger=depletion` — no open invoice until entitlement depleted / sweep |
| D12 | Chargeback (F6) | Webhook → agreement `disputed`, collect skipped, entitlement clawback outbox |

## Manual walkthrough (D1 golden path via curl/Swagger)

```powershell
# seed studios/members/instruments; note members.d1.memberId + instrumentId from the response
Invoke-RestMethod -Method Post http://localhost:5080/demo/seed

# sell a contract starting tomorrow (virtual time)
Invoke-RestMethod -Method Post http://localhost:5080/intake/contract-events -ContentType "application/json" -Body (@{
  eventId = [guid]::NewGuid().ToString("N"); type = "ContractSold"; studioId = 1
  memberId = "<members.d1.memberId>"; contractId = "C-manual-1"; amountCents = 10000
  startDate = (Get-Date).AddDays(1).ToString("yyyy-MM-dd")
  tenderChain = @(@{ type = "card"; instrumentId = "<members.d1.instrumentId>" })
  policyId = "standard"
} | ConvertTo-Json -Depth 4)

# advance virtual time past the due hour — the trigger fires within ~1s
Invoke-RestMethod -Method Post http://localhost:5080/demo/time/advance -ContentType "application/json" -Body '{"days":1,"hours":10}'

# watch it pay
Invoke-RestMethod http://localhost:5080/agreements?contractId=C-manual-1
Invoke-RestMethod http://localhost:5080/agreements/<agreementId>/invoices
Invoke-RestMethod http://localhost:5080/invoices/<invoiceId>/attempts     # fee, MIT, gatewayRef
Invoke-RestMethod "http://localhost:9876/admin/journal/charges?token=tok_ok_d1"  # SurchargeAmount on the wire
```

Per-scenario endpoint sequences (bodies as in `src/DemoRunner/Scenarios.cs`, which is the executable
reference):

- **D2**: re-POST intake → `duplicate:true` · amend open invoice amount **and** `periodStart` · materialize-next twice → 409 · rerun
- **D3**: decline tokens · ladders · hard write-off → agreement `past_due` · self-serve · outbox redeliver + notify journal
- **D4**: card+account and account-only chains · `/admin/journal` bridge debits · member cache is non-authoritative
- **D5**: pricing outage · fee_dropped work item · instrument-events wake
- **D6**: `tok_timeout_*` · query-before-retry
- **D7**: bank tender · ACH return · `/admin/ach/mark-settled` + poll-pending (no webhook)
- **D8**: seed-violation · sweep · phantom + reconcile · fee-ledger work items
- **D9**: `POST /studios/{id}/pause|resume` (ops) — not agreement pause
- **D10**: `POST /agreements/pause` · pause_fee invoice · advance past `PausedTo` · rematerialized cycle date-shifted
- **D11**: ContractSold with `billingTrigger=depletion` · entitlement-depleted **and** depletion-sweep backstop
- **D12**: `POST /webhooks/chargeback` · disputed + clawback; next cycle stays `scheduled`

## How the HLD maps onto the code

| HLD concept | Where |
|---|---|
| Domain model (§4): Agreement (pause windows, depletion trigger), Invoice, Attempt, tender chain | `src/BillingCore/Domain/Entities.cs` |
| Constraints are the guarantees (§8) | `src/BillingCore/Infrastructure/BillingDb.cs` |
| Existing-production scheduling preserved | `src/BillingCore/Services/RunService.cs` |
| Single writer + paid-TXN (T1) + account via commerce bridge (§5c) + F7/F8 | `src/BillingCore/Services/CollectionService.cs` |
| Agreement pause / depletion / instrument events | `src/BillingCore/Services/AgreementService.cs` |
| Per-attempt fee orchestration [TF-A] (§4b) | `src/BillingCore/Services/FeeService.cs` |
| Recovery ladders, self-serve, instrument wake (F5) | `src/BillingCore/Services/RecoveryService.cs` |
| Outbox → bridge ACL (`paymentTransactionFees` −13, entitlements, notify) | `src/BillingCore/Services/BridgeHandlers.cs` |
| Controls: sweeper (depletion-aware), recon, fee_dropped work items, P6 policy validation | `src/BillingCore/Services/ControlsService.cs` |
| Gateway/Pricing/Instruments/Commerce/Account/Notify stubs | `src/Externals/Program.cs` |

## Deliberate simplifications vs the HLD (POC honesty list)

- **One deployable**: modular monolith (HLD Alternative G). Separate recovery/gateway deployables are production concerns.
- **SQS → Postgres queue** with leases; outbox likewise — idempotency is demonstrated, not assumed.
- **Virtual clock** with `POST /demo/time/advance`.
- **Amount composition**: base = contract amount (freeze-at-materialization intact; no full tax/discount/proration).
- **Pacing** sequential-per-studio; studio-local due hour approximated in UTC.
- **Embedded Postgres** when Docker isn't available; orphan reuse if `:5433` already holds an embedded instance.
- LaunchDarkly → config/seed flags; membership/commerce are journal sinks at the WireMock bridge.
- **Sale side effects stubbed at bridge** (invoice queue, rewards, unpaids); StatusCode=4 limbo is impossible by design.
- **Pause windows** take effect immediately (`state=paused` on schedule), not deferred until `PausedFrom`. Early unsuspend exists (`POST /agreements/unsuspend`) but is not a DemoRunner scenario.
- **Account credit** is not stubbed — commerce WireMock exposes balance + debit only (collection never credits).
- **F7 agreement-pause gate** lives in `CollectAsync` (with a `pause_fee` exception), not in `ExecuteStudioRunAsync`, so pause fees can still be picked by a studio run.
- **`lookup_failed` fee-drop** opens the same work-item kind as pricing outage; only pricing outage is demoed (D5).
- **Chargeback fee reversal** is deferred (§12.8) — F6 sets `disputed` + clawback only.

## Intentional ASP ≠ HLD redesigns (do not “fix” back to ASP)

These differences are **documented product choices in rewrite HLD v2**, not POC bugs:

| Topic | ASP (legacy) | HLD / this POC |
|---|---|---|
| Decline → account | Only on last `AutoPayRetryDays` day | Immediate next tender-chain step |
| Fee calc failure under TF | V1 fail-hard when LD on | Fail-safe: charge proceeds fee-less + `fee_dropped_by_outage` work item |
| Split fee | Fee on grand total, then split | Per-tender fee on the card amount charged |
| `FeeDeclinedCCToAccount` | Product path | Retired — account is a normal tender step |
| Async ACH StatusCode=6 | Batch limbo | `settling` + F8 query/poll backstop |
| Account balance | Often co-located with billing | Commerce bridge owns the ledger (§5c); billing may cache only |
| Full sale composition / wallet multi-gateway MIT / migration shadow | Production surface | Out of POC scope (honesty only) |

## Layout

```
autopay-poc/
├─ docker-compose.yml          # Postgres 16 on :5433 (optional; embedded fallback exists)
├─ run-demo.ps1                # one-command build + start + demo
├─ docs/ARCHITECTURE.md        # externals boundary + happy-path data flow
├─ nuget.config                # nuget.org only (self-contained restore)
├─ src/BillingCore/            # the billing platform (API :5080 + workers + Postgres schema)
├─ src/Externals/              # WireMock.Net host :9876 (stubs + admin + journals)
└─ src/DemoRunner/             # scenario suite D1-D12 with assertions
```
