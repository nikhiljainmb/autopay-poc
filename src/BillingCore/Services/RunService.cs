using BillingCore.Domain;
using BillingCore.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace BillingCore.Services;

/// <summary>
/// The existing-production scheduling mechanism preserved (plan decision): a scheduled trigger
/// opens a ScheduleRun for a window, fans out one StudioRun per studio with due work, StudioRuns
/// are idempotently claimable, and close-out aggregates a run report. Rerun and manual runs reuse
/// the same paths. Selection is sweep-based (everything due up to the window end) so a late or
/// missed trigger can never silently skip work (HLD P10).
/// </summary>
public class RunService
{
    private static readonly TimeSpan AutoInterval = TimeSpan.FromHours(1);

    private readonly IDbContextFactory<BillingDb> _dbf;
    private readonly IClock _clock;
    private readonly CollectionService _collection;
    private readonly ILogger<RunService> _log;

    public RunService(IDbContextFactory<BillingDb> dbf, IClock clock, CollectionService collection, ILogger<RunService> log)
    {
        _dbf = dbf;
        _clock = clock;
        _collection = collection;
        _log = log;
    }

    /// <summary>Called by the trigger worker every poll; fires when a virtual hour has elapsed.</summary>
    public async Task AutoTriggerIfDueAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var state = await db.TriggerStates.SingleOrDefaultAsync(t => t.Id == 1);
        if (state is null) return;
        var now = _clock.UtcNow;
        if (state.LastWindowEnd > now) // clock moved backwards (restart); resync
        {
            state.LastWindowEnd = now;
            await db.SaveChangesAsync();
            return;
        }
        if (now - state.LastWindowEnd >= AutoInterval)
            await TriggerAsync("auto");
    }

    public async Task<ScheduleRun> TriggerAsync(string kind)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var now = _clock.UtcNow;

        var state = await db.TriggerStates
            .FromSqlRaw("SELECT * FROM trigger_state WHERE id = 1 FOR UPDATE")
            .AsTracking()
            .SingleAsync();
        var run = new ScheduleRun
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            WindowFrom = state.LastWindowEnd < now ? state.LastWindowEnd : now,
            WindowTo = now,
            CreatedAt = DateTime.UtcNow
        };
        db.ScheduleRuns.Add(run);
        state.LastWindowEnd = now;

        var studioIds = await db.Invoices
            .Where(i => i.State == "scheduled" && i.DueAt <= run.WindowTo)
            .Select(i => i.StudioId)
            .Distinct()
            .ToListAsync();
        var pausedIds = (await db.Studios
            .Where(s => studioIds.Contains(s.Id) && s.Paused)
            .Select(s => s.Id)
            .ToListAsync()).ToHashSet();

        var fannedOut = new List<StudioRun>();
        foreach (var studioId in studioIds)
        {
            var studioRun = new StudioRun
            {
                Id = Guid.NewGuid(),
                ScheduleRunId = run.Id,
                StudioId = studioId,
                State = pausedIds.Contains(studioId) ? "skipped_paused" : "queued"
            };
            db.StudioRuns.Add(studioRun);
            fannedOut.Add(studioRun);
            if (studioRun.State == "queued")
                db.QueueItems.Add(new QueueItem
                {
                    Kind = "studio_run",
                    PayloadJson = Json.Serialize(new { studioRunId = studioRun.Id }),
                    AvailableAt = now
                });
        }

        CloseOutIfDone(run, fannedOut);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        _log.LogInformation("run {Run} ({Kind}) fanned out to {Count} studios ({Paused} paused)",
            run.Id, kind, studioIds.Count, pausedIds.Count);
        return run;
    }

    public async Task ExecuteStudioRunAsync(Guid studioRunId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var studioRun = await db.StudioRuns.SingleOrDefaultAsync(s => s.Id == studioRunId);
        if (studioRun is null || studioRun.State is not ("queued" or "running")) return; // idempotent claim
        var run = await db.ScheduleRuns.SingleAsync(r => r.Id == studioRun.ScheduleRunId);

        studioRun.State = "running";
        studioRun.ClaimedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        // F7 dispatch gate: studio ops pause — skip charges even if a StudioRun was already queued.
        var studio = await db.Studios.SingleAsync(s => s.Id == studioRun.StudioId);
        if (studio.Paused)
        {
            studioRun.State = "skipped_paused";
            studioRun.CompletedAt = DateTime.UtcNow;
            studioRun.CountersJson = Json.Serialize(new RunCounters { SkippedPaused = 1 });
            await db.SaveChangesAsync();
            await using var txSkip = await db.Database.BeginTransactionAsync();
            var trackedSkip = await db.ScheduleRuns
                .FromSqlInterpolated($"SELECT * FROM schedule_runs WHERE id = {run.Id} FOR UPDATE")
                .AsTracking()
                .SingleAsync();
            CloseOutIfDone(trackedSkip, await db.StudioRuns.Where(s => s.ScheduleRunId == run.Id).ToListAsync());
            await db.SaveChangesAsync();
            await txSkip.CommitAsync();
            return;
        }

        // Pick set: everything due up to the window end and still scheduled. Paid, dunning
        // (ladder-owned) and settling invoices are naturally excluded — rerun is charge-safe.
        var invoiceIds = await db.Invoices
            .Where(i => i.StudioId == studioRun.StudioId && i.State == "scheduled" && i.DueAt <= run.WindowTo)
            .OrderBy(i => i.DueAt)
            .Select(i => i.Id)
            .ToListAsync();

        var counters = new RunCounters { Picked = invoiceIds.Count };
        foreach (var invoiceId in invoiceIds) // paced dispatch: sequential per studio
        {
            try
            {
                await _collection.CollectAsync(invoiceId, run.Kind == "manual" ? "manual" : "run", studioRun.Id, counters);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "collect failed for invoice {Invoice}", invoiceId);
            }
        }

        studioRun.CountersJson = Json.Serialize(counters);
        studioRun.State = "completed";
        studioRun.CompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        // Close-out under the run's row lock so concurrent studio-run completions serialize.
        await using var tx = await db.Database.BeginTransactionAsync();
        var trackedRun = await db.ScheduleRuns
            .FromSqlInterpolated($"SELECT * FROM schedule_runs WHERE id = {run.Id} FOR UPDATE")
            .AsTracking()
            .SingleAsync();
        var siblings = await db.StudioRuns.Where(s => s.ScheduleRunId == run.Id).ToListAsync();
        CloseOutIfDone(trackedRun, siblings);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    /// <summary>Re-fan-out a run's studios; the sweep + single-writer + slot constraint make it charge-safe (D2).</summary>
    public async Task<ScheduleRun?> RerunAsync(Guid scheduleRunId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var run = await db.ScheduleRuns.SingleOrDefaultAsync(r => r.Id == scheduleRunId);
        if (run is null) return null;
        var now = _clock.UtcNow;

        var studioRuns = await db.StudioRuns.Where(s => s.ScheduleRunId == run.Id).ToListAsync();
        var pausedIds = (await db.Studios.Where(s => s.Paused).Select(s => s.Id).ToListAsync()).ToHashSet();
        foreach (var studioRun in studioRuns.Where(s => s.State is "completed" or "failed" or "skipped_paused"))
        {
            if (pausedIds.Contains(studioRun.StudioId))
            {
                studioRun.State = "skipped_paused";
                continue;
            }
            studioRun.State = "queued";
            studioRun.CompletedAt = null;
            db.QueueItems.Add(new QueueItem
            {
                Kind = "studio_run",
                PayloadJson = Json.Serialize(new { studioRunId = studioRun.Id }),
                AvailableAt = now
            });
        }
        run.State = "open";
        run.ClosedAt = null;
        run.ReportJson = null;
        await db.SaveChangesAsync();
        return run;
    }

    /// <summary>Staff-initiated run for one studio (production ForceRetry/manual-run analog).</summary>
    public async Task<ScheduleRun> ManualStudioRunAsync(int studioId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var now = _clock.UtcNow;
        var run = new ScheduleRun
        {
            Id = Guid.NewGuid(),
            Kind = "manual",
            WindowFrom = now,
            WindowTo = now,
            CreatedAt = DateTime.UtcNow
        };
        db.ScheduleRuns.Add(run);
        var studioRun = new StudioRun { Id = Guid.NewGuid(), ScheduleRunId = run.Id, StudioId = studioId, State = "queued" };
        db.StudioRuns.Add(studioRun);
        db.QueueItems.Add(new QueueItem
        {
            Kind = "studio_run",
            PayloadJson = Json.Serialize(new { studioRunId = studioRun.Id }),
            AvailableAt = now
        });
        await db.SaveChangesAsync();
        return run;
    }

    private void CloseOutIfDone(ScheduleRun run, List<StudioRun> studioRuns)
    {
        if (studioRuns.Any(s => s.State is "queued" or "running")) return;

        var aggregate = new RunCounters();
        foreach (var s in studioRuns.Where(s => s.State == "completed"))
            aggregate.Absorb(Json.Deserialize<RunCounters>(s.CountersJson));

        run.State = "completed";
        run.ClosedAt = DateTime.UtcNow;
        run.ReportJson = Json.Serialize(new
        {
            studios = studioRuns.Count,
            skippedPaused = studioRuns.Count(s => s.State == "skipped_paused"),
            counters = aggregate
        });
        _log.LogInformation("run {Run} closed: {Report}", run.Id, run.ReportJson);
    }
}
