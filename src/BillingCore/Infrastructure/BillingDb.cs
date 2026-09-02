using BillingCore.Domain;
using Microsoft.EntityFrameworkCore;

namespace BillingCore.Infrastructure;

public class BillingDb : DbContext
{
    public BillingDb(DbContextOptions<BillingDb> options) : base(options) { }

    public DbSet<Studio> Studios => Set<Studio>();
    public DbSet<Member> Members => Set<Member>();
    public DbSet<Instrument> Instruments => Set<Instrument>();
    public DbSet<Policy> Policies => Set<Policy>();
    public DbSet<Agreement> Agreements => Set<Agreement>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<Attempt> Attempts => Set<Attempt>();
    public DbSet<IntakeEvent> IntakeEvents => Set<IntakeEvent>();
    public DbSet<ScheduleRun> ScheduleRuns => Set<ScheduleRun>();
    public DbSet<StudioRun> StudioRuns => Set<StudioRun>();
    public DbSet<QueueItem> QueueItems => Set<QueueItem>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<Ladder> Ladders => Set<Ladder>();
    public DbSet<ControlWorkItem> ControlWorkItems => Set<ControlWorkItem>();
    public DbSet<TriggerState> TriggerStates => Set<TriggerState>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Studio>().ToTable("studios");
        b.Entity<Member>().ToTable("members");

        b.Entity<Instrument>(e =>
        {
            e.ToTable("instruments");
            e.HasIndex(x => x.Token).IsUnique();
            e.HasIndex(x => x.MemberId);
        });

        b.Entity<Policy>(e =>
        {
            e.ToTable("policies");
            e.HasKey(x => x.Id);
        });

        b.Entity<Agreement>(e =>
        {
            e.ToTable("agreements");
            e.HasIndex(x => x.ContractId);
            e.HasIndex(x => new { x.StudioId, x.State });
        });

        b.Entity<Invoice>(e =>
        {
            e.ToTable("invoices");
            // HLD 8: duplicate billing cycle is a constraint violation, not a bug to detect (P4).
            e.HasIndex(x => new { x.AgreementId, x.PeriodStart, x.Kind })
                .IsUnique()
                .HasDatabaseName("ux_invoices_agreement_period_kind");
            e.HasIndex(x => new { x.StudioId, x.State, x.DueAt });
        });

        b.Entity<Attempt>(e =>
        {
            e.ToTable("attempts");
            // HLD 8: at most one successful attempt per tender slot (partial unique index).
            e.HasIndex(x => new { x.InvoiceId, x.TenderSlot })
                .IsUnique()
                .HasDatabaseName("ux_attempts_one_success_per_slot")
                .HasFilter("outcome = 'approved'");
            e.HasIndex(x => x.IdempotencyKey).IsUnique();
            e.HasIndex(x => x.GatewayRef);
            e.HasIndex(x => x.InvoiceId);
        });

        b.Entity<IntakeEvent>(e =>
        {
            e.ToTable("intake_events");
            // HLD 8: event-id dedup at intake (P3).
            e.HasKey(x => x.EventId);
        });

        b.Entity<ScheduleRun>().ToTable("schedule_runs");

        b.Entity<StudioRun>(e =>
        {
            e.ToTable("studio_runs");
            // Idempotent claim: one StudioRun per (ScheduleRun, Studio), same as production.
            e.HasIndex(x => new { x.ScheduleRunId, x.StudioId })
                .IsUnique()
                .HasDatabaseName("ux_studio_runs_schedule_studio");
        });

        b.Entity<QueueItem>(e =>
        {
            e.ToTable("work_queue");
            e.HasIndex(x => new { x.CompletedAt, x.AvailableAt });
        });

        b.Entity<OutboxMessage>(e =>
        {
            e.ToTable("outbox");
            e.HasIndex(x => new { x.DispatchedAt, x.AvailableAt });
        });

        b.Entity<Ladder>(e =>
        {
            e.ToTable("ladders");
            e.HasIndex(x => x.SelfServeToken).IsUnique();
            // One live ladder per invoice.
            e.HasIndex(x => x.InvoiceId)
                .IsUnique()
                .HasDatabaseName("ux_ladders_one_live_per_invoice")
                .HasFilter("state IN ('active','dispatched','waiting_member')");
        });

        b.Entity<ControlWorkItem>(e =>
        {
            e.ToTable("work_items");
            e.HasIndex(x => new { x.Kind, x.RefKey }).IsUnique();
        });

        b.Entity<TriggerState>(e =>
        {
            e.ToTable("trigger_state");
            e.HasKey(x => x.Id);
        });
    }
}
