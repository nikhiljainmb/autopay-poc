using BillingCore.Domain;
using BillingCore.Services;
using Microsoft.EntityFrameworkCore;

namespace BillingCore.Infrastructure;

/// <summary>Hourly-virtual-time schedule trigger (production EventBridge-cron analog).</summary>
public class TriggerWorker : BackgroundService
{
    private readonly RunService _runs;
    private readonly AgreementService _agreements;
    private readonly ILogger<TriggerWorker> _log;

    public TriggerWorker(RunService runs, AgreementService agreements, ILogger<TriggerWorker> log)
    {
        _runs = runs;
        _agreements = agreements;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _runs.AutoTriggerIfDueAsync();
                // Nightly (virtual-hourly) depletion pull backstop (S16 / HLD §4a.2).
                await _agreements.SweepDepletionAsync();
            }
            catch (Exception ex) { _log.LogError(ex, "trigger worker"); }
            try { await Task.Delay(500, ct); } catch (OperationCanceledException) { }
        }
    }
}

/// <summary>
/// At-least-once consumer over the work_queue table (SQS analog): claim under
/// FOR UPDATE SKIP LOCKED with a real-time lease; availability runs on the virtual clock.
/// </summary>
public class QueueWorker : BackgroundService
{
    private readonly IDbContextFactory<BillingDb> _dbf;
    private readonly IClock _clock;
    private readonly RunService _runs;
    private readonly CollectionService _collection;
    private readonly ILogger<QueueWorker> _log;

    public QueueWorker(IDbContextFactory<BillingDb> dbf, IClock clock, RunService runs, CollectionService collection, ILogger<QueueWorker> log)
    {
        _dbf = dbf;
        _clock = clock;
        _runs = runs;
        _collection = collection;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Two consumer loops: real fan-out parallelism without hiding races.
        await Task.WhenAll(ConsumeLoop(ct), ConsumeLoop(ct));
    }

    private async Task ConsumeLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var worked = false;
            try { worked = await ConsumeOneAsync(); }
            catch (Exception ex) { _log.LogError(ex, "queue worker"); }
            if (!worked)
            {
                try { await Task.Delay(300, ct); } catch (OperationCanceledException) { }
            }
        }
    }

    private async Task<bool> ConsumeOneAsync()
    {
        var vnow = _clock.UtcNow;
        var rnow = DateTime.UtcNow;

        await using var db = await _dbf.CreateDbContextAsync();
        long itemId;
        string kind, payloadJson;
        int attempts;

        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            var item = await db.QueueItems
                .FromSqlInterpolated($@"
                    SELECT * FROM work_queue
                    WHERE completed_at IS NULL AND available_at <= {vnow}
                      AND (locked_until IS NULL OR locked_until < {rnow})
                    ORDER BY id
                    LIMIT 1
                    FOR UPDATE SKIP LOCKED")
                .AsTracking()
                .FirstOrDefaultAsync();
            if (item is null)
            {
                await tx.CommitAsync();
                return false;
            }
            item.LockedUntil = rnow.AddSeconds(60);
            item.Attempts++;
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            (itemId, kind, payloadJson, attempts) = (item.Id, item.Kind, item.PayloadJson, item.Attempts);
        }

        try
        {
            var payload = Json.Deserialize<Dictionary<string, Guid>>(payloadJson);
            switch (kind)
            {
                case "studio_run":
                    await _runs.ExecuteStudioRunAsync(payload["studioRunId"]);
                    break;
                case "recollect":
                    await _collection.CollectAsync(payload["invoiceId"], "ladder");
                    break;
                default:
                    _log.LogWarning("unknown work kind {Kind}", kind);
                    break;
            }
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE work_queue SET completed_at = {DateTime.UtcNow} WHERE id = {itemId}");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "work item {Id} ({Kind}) failed (attempt {Attempts})", itemId, kind, attempts);
            DateTime? deadAt = attempts >= 5 ? DateTime.UtcNow : null;
            await db.Database.ExecuteSqlInterpolatedAsync(
                $@"UPDATE work_queue SET last_error = {ex.Message}, completed_at = {deadAt},
                       locked_until = {DateTime.UtcNow.AddSeconds(3)}
                   WHERE id = {itemId}");
        }
        return true;
    }
}

/// <summary>Recovery ladder scheduler on the virtual clock.</summary>
public class LadderWorker : BackgroundService
{
    private readonly RecoveryService _recovery;
    private readonly ILogger<LadderWorker> _log;

    public LadderWorker(RecoveryService recovery, ILogger<LadderWorker> log)
    {
        _recovery = recovery;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await _recovery.TickAsync(); }
            catch (Exception ex) { _log.LogError(ex, "ladder worker"); }
            try { await Task.Delay(400, ct); } catch (OperationCanceledException) { }
        }
    }
}

/// <summary>
/// Outbox dispatcher (HLD 7): push never chains HTTP-to-HTTP — every side effect leaves the
/// money transaction as an outbox row and is delivered at-least-once from here.
/// </summary>
public class OutboxWorker : BackgroundService
{
    private readonly IDbContextFactory<BillingDb> _dbf;
    private readonly IClock _clock;
    private readonly BridgeHandlers _handlers;
    private readonly ILogger<OutboxWorker> _log;

    public OutboxWorker(IDbContextFactory<BillingDb> dbf, IClock clock, BridgeHandlers handlers, ILogger<OutboxWorker> log)
    {
        _dbf = dbf;
        _clock = clock;
        _handlers = handlers;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var worked = false;
            try { worked = await DispatchOneAsync(); }
            catch (Exception ex) { _log.LogError(ex, "outbox worker"); }
            if (!worked)
            {
                try { await Task.Delay(250, ct); } catch (OperationCanceledException) { }
            }
        }
    }

    private async Task<bool> DispatchOneAsync()
    {
        var vnow = _clock.UtcNow;
        var rnow = DateTime.UtcNow;

        await using var db = await _dbf.CreateDbContextAsync();
        long messageId;
        string topic, payloadJson;
        int attempts;

        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            var message = await db.OutboxMessages
                .FromSqlInterpolated($@"
                    SELECT * FROM outbox
                    WHERE dispatched_at IS NULL AND available_at <= {vnow}
                      AND (locked_until IS NULL OR locked_until < {rnow})
                    ORDER BY id
                    LIMIT 1
                    FOR UPDATE SKIP LOCKED")
                .AsTracking()
                .FirstOrDefaultAsync();
            if (message is null)
            {
                await tx.CommitAsync();
                return false;
            }
            message.LockedUntil = rnow.AddSeconds(30);
            message.Attempts++;
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            (messageId, topic, payloadJson, attempts) = (message.Id, message.Topic, message.PayloadJson, message.Attempts);
        }

        try
        {
            await _handlers.HandleAsync(topic, payloadJson);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE outbox SET dispatched_at = {DateTime.UtcNow} WHERE id = {messageId}");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "outbox {Id} ({Topic}) failed (attempt {Attempts})", messageId, topic, attempts);
            DateTime? deadAt = attempts >= 8 ? DateTime.UtcNow : null;
            await db.Database.ExecuteSqlInterpolatedAsync(
                $@"UPDATE outbox SET last_error = {ex.Message}, dispatched_at = {deadAt},
                       locked_until = {DateTime.UtcNow.AddSeconds(2)}
                   WHERE id = {messageId}");
        }
        return true;
    }
}

/// <summary>Agreement pause auto-resume on the virtual clock (S15).</summary>
public class PauseResumeWorker : BackgroundService
{
    private readonly AgreementService _agreements;
    private readonly ILogger<PauseResumeWorker> _log;

    public PauseResumeWorker(AgreementService agreements, ILogger<PauseResumeWorker> log)
    {
        _agreements = agreements;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await _agreements.TickAutoResumeAsync(); }
            catch (Exception ex) { _log.LogError(ex, "pause resume worker"); }
            try { await Task.Delay(500, ct); } catch (OperationCanceledException) { }
        }
    }
}

/// <summary>F8 missed-webhook poll: pending attempts older than 1 virtual hour are queried.</summary>
public class PendingPollWorker : BackgroundService
{
    private readonly CollectionService _collection;
    private readonly ILogger<PendingPollWorker> _log;

    public PendingPollWorker(CollectionService collection, ILogger<PendingPollWorker> log)
    {
        _collection = collection;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await _collection.PollPendingAttemptsAsync(TimeSpan.FromHours(1)); }
            catch (Exception ex) { _log.LogError(ex, "pending poll worker"); }
            try { await Task.Delay(800, ct); } catch (OperationCanceledException) { }
        }
    }
}
