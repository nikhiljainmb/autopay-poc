namespace BillingCore.Domain;

public class Studio
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Country { get; set; } = "US";
    /// <summary>POC simplification of studio-local charge time: the UTC hour invoices come due.</summary>
    public int BillingHourUtc { get; set; } = 9;
    /// <summary>Ops pause (F7 fan-out gate) — distinct from agreement pause windows (S15).</summary>
    public bool Paused { get; set; }
}

public class Member
{
    public Guid Id { get; set; }
    public int StudioId { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    /// <summary>
    /// Non-authoritative cache only (HLD 5c). Commerce owns the ledger; CollectionService
    /// debits via the bridge and may refresh this mirror for staff displays.
    /// </summary>
    public long AccountBalanceCacheCents { get; set; }
}

public class Instrument
{
    public Guid Id { get; set; }
    public Guid MemberId { get; set; }
    /// <summary>card | bank</summary>
    public string Kind { get; set; } = "card";
    public string Token { get; set; } = "";
    public string? Brand { get; set; }
    public string? Last4 { get; set; }
    /// <summary>credit | debit | unknown | n/a — vault-lite funding-type metadata [TF-A].</summary>
    public string FundingType { get; set; } = "unknown";
    /// <summary>NTI persisted after first approved charge; sent on subsequent MIT charges (HLD 5b).</summary>
    public string? NetworkTransactionId { get; set; }
    public bool Active { get; set; } = true;
}

public class Policy
{
    public string Id { get; set; } = "";
    public string DefinitionJson { get; set; } = "{}";
}

public class Agreement
{
    public Guid Id { get; set; }
    public int StudioId { get; set; }
    public Guid MemberId { get; set; }
    public string ContractId { get; set; } = "";
    /// <summary>active | paused | past_due | disputed | canceled | completed</summary>
    public string State { get; set; } = "active";
    public int Version { get; set; } = 1;
    public long AmountCents { get; set; }
    /// <summary>Next un-invoiced period. Invariant: exactly one open invoice per active calendar agreement.</summary>
    public DateOnly NextPeriodStart { get; set; }
    public string TenderChainJson { get; set; } = "[]";
    public string PolicyId { get; set; } = "standard";
    /// <summary>calendar | depletion (S16 / §4a.2).</summary>
    public string BillingTrigger { get; set; } = "calendar";
    /// <summary>Depletion agreements keep a live subscription until an invoice is materialized.</summary>
    public bool DepletionSubscriptionLive { get; set; }
    /// <summary>Remaining entitlement units for depletion agreements (POC stand-in).</summary>
    public int? EntitlementRemaining { get; set; }
    /// <summary>Agreement pause window start (S15 / §4a.3).</summary>
    public DateOnly? PausedFrom { get; set; }
    /// <summary>Agreement pause window end (inclusive for resume-after).</summary>
    public DateOnly? PausedTo { get; set; }
    /// <summary>Optional pause-fee invoice amount (ProductID -8 analog).</summary>
    public long PauseFeeCents { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class Invoice
{
    public Guid Id { get; set; }
    public Guid AgreementId { get; set; }
    public int StudioId { get; set; }
    public Guid MemberId { get; set; }
    public DateOnly PeriodStart { get; set; }
    /// <summary>cycle | pause_fee</summary>
    public string Kind { get; set; } = "cycle";
    /// <summary>Frozen at materialization, pre-auth. The transaction fee is NOT in here [TF-A].</summary>
    public long BaseAmountCents { get; set; }
    public long ResidualCents { get; set; }
    public DateTime DueAt { get; set; }
    /// <summary>scheduled | collecting | paid | recovered | settling | dunning | written_off | canceled</summary>
    public string State { get; set; } = "scheduled";
    /// <summary>When the invoice entered settling (F8 banking-horizon clock).</summary>
    public DateTime? SettlingSince { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class Attempt
{
    public Guid Id { get; set; }
    public Guid InvoiceId { get; set; }
    public int TenderSlot { get; set; }
    /// <summary>card | account | bank</summary>
    public string TenderType { get; set; } = "card";
    public Guid? InstrumentId { get; set; }
    /// <summary>Base portion collected by this attempt (excludes the fee).</summary>
    public long AmountCents { get; set; }
    /// <summary>Per-attempt surcharge [TF-A]; 0 when suppressed/absent.</summary>
    public long FeeCents { get; set; }
    public string? FundingTypeAtCharge { get; set; }
    /// <summary>null | not_credit | not_opted_in | lookup_failed | pricing_outage</summary>
    public string? FeeSuppressionReason { get; set; }
    public string MitJson { get; set; } = "{}";
    /// <summary>dispatched | approved | declined | pending | returned | unknown | failed</summary>
    public string Outcome { get; set; } = "dispatched";
    public string? DeclineCode { get; set; }
    /// <summary>hard | soft | fixable | bank_return</summary>
    public string? DeclineClass { get; set; }
    public string? GatewayRef { get; set; }
    /// <summary>run | rerun | manual | ladder | selfserve | webhook | poll</summary>
    public string Source { get; set; } = "run";
    public Guid? StudioRunId { get; set; }
    public string IdempotencyKey { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public class IntakeEvent
{
    public string EventId { get; set; } = "";
    public string Type { get; set; } = "";
    public string PayloadJson { get; set; } = "{}";
    public string Status { get; set; } = "processed";
    public DateTime ReceivedAt { get; set; }
}

public class ScheduleRun
{
    public Guid Id { get; set; }
    /// <summary>auto | manual</summary>
    public string Kind { get; set; } = "auto";
    public DateTime WindowFrom { get; set; }
    public DateTime WindowTo { get; set; }
    /// <summary>open | completed</summary>
    public string State { get; set; } = "open";
    public string? ReportJson { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
}

public class StudioRun
{
    public Guid Id { get; set; }
    public Guid ScheduleRunId { get; set; }
    public int StudioId { get; set; }
    /// <summary>queued | running | completed | failed | skipped_paused</summary>
    public string State { get; set; } = "queued";
    public string CountersJson { get; set; } = "{}";
    public DateTime? ClaimedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public class QueueItem
{
    public long Id { get; set; }
    /// <summary>studio_run | recollect</summary>
    public string Kind { get; set; } = "";
    public string PayloadJson { get; set; } = "{}";
    /// <summary>Virtual-clock availability.</summary>
    public DateTime AvailableAt { get; set; }
    /// <summary>Real-clock lease.</summary>
    public DateTime? LockedUntil { get; set; }
    public int Attempts { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? LastError { get; set; }
}

public class OutboxMessage
{
    public long Id { get; set; }
    public string Topic { get; set; } = "";
    public string PayloadJson { get; set; } = "{}";
    public DateTime AvailableAt { get; set; }
    public DateTime? LockedUntil { get; set; }
    public int Attempts { get; set; }
    public DateTime? DispatchedAt { get; set; }
    public string? LastError { get; set; }
}

public class Ladder
{
    public Guid Id { get; set; }
    public Guid InvoiceId { get; set; }
    public string PolicyId { get; set; } = "standard";
    public int Step { get; set; }
    /// <summary>active | dispatched | waiting_member | completed | exhausted</summary>
    public string State { get; set; } = "active";
    public DateTime? NextActionAt { get; set; }
    public string? LastDeclineClass { get; set; }
    public Guid SelfServeToken { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public class ControlWorkItem
{
    public Guid Id { get; set; }
    /// <summary>missing_open_invoice | settling_horizon | unladdered_dunning | fee_dropped_by_outage | recon_*</summary>
    public string Kind { get; set; } = "";
    public string RefKey { get; set; } = "";
    public string DetailJson { get; set; } = "{}";
    public DateTime CreatedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
}

public class TriggerState
{
    public int Id { get; set; } = 1;
    public DateTime LastWindowEnd { get; set; }
}

// ---------- JSON value objects ----------

public class TenderSlotDef
{
    /// <summary>card | account | bank</summary>
    public string Type { get; set; } = "card";
    public Guid? InstrumentId { get; set; }
    public long? CapCents { get; set; }
}

public class PolicyDef
{
    public ClassPolicy Soft { get; set; } = new() { RetryDelaysDays = new() { 2, 4 } };
    public ClassPolicy BankReturn { get; set; } = new() { RetryDelaysDays = new() { 3 } };
    public FixablePolicy Fixable { get; set; } = new();

    public class ClassPolicy
    {
        public List<int> RetryDelaysDays { get; set; } = new();
    }

    public class FixablePolicy
    {
        public int GiveUpDays { get; set; } = 7;
    }

    /// <summary>P6: reject AutoPayRetryDays=0/1 style foot-guns.</summary>
    public static void ValidateOrThrow(PolicyDef policy)
    {
        static void Check(string name, List<int> delays)
        {
            if (delays is null || delays.Count == 0)
                throw new InvalidOperationException($"policy {name}: retry delays must be non-empty");
            if (delays.Any(d => d < 1))
                throw new InvalidOperationException($"policy {name}: each retry delay must be >= 1 day");
        }
        Check("soft", policy.Soft.RetryDelaysDays);
        Check("bank_return", policy.BankReturn.RetryDelaysDays);
        if (policy.Fixable.GiveUpDays < 1)
            throw new InvalidOperationException("policy fixable: giveUpDays must be >= 1");
    }
}

public class RunCounters
{
    public int Picked { get; set; }
    public int Approved { get; set; }
    public int Declined { get; set; }
    public int Pending { get; set; }
    public int Unknown { get; set; }
    public int FeeDropped { get; set; }
    public int SkippedPaused { get; set; }
    public long CollectedCents { get; set; }
    public long SurchargeCents { get; set; }

    public void Absorb(RunCounters other)
    {
        Picked += other.Picked;
        Approved += other.Approved;
        Declined += other.Declined;
        Pending += other.Pending;
        Unknown += other.Unknown;
        FeeDropped += other.FeeDropped;
        SkippedPaused += other.SkippedPaused;
        CollectedCents += other.CollectedCents;
        SurchargeCents += other.SurchargeCents;
    }
}
