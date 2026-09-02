using BillingCore.Domain;
using BillingCore.Infrastructure;
using BillingCore.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BillingCore.Api;

public record WebhookDto(string Type, string GatewayRef, string? Code);

public static class Endpoints
{
    public static void MapCore(WebApplication app)
    {
        // ---- Intake (contract events in, dedup + absolute) ----
        app.MapPost("/intake/contract-events", async (ContractEventDto ev, IntakeService intake) =>
        {
            var result = await intake.ProcessAsync(ev);
            return result.Error is null ? Results.Ok(result) : Results.BadRequest(result);
        }).WithTags("Intake");

        // ---- Agreements / invoices / attempts ----
        app.MapGet("/agreements", async (string? contractId, IDbContextFactory<BillingDb> dbf) =>
        {
            await using var db = await dbf.CreateDbContextAsync();
            var q = db.Agreements.AsQueryable();
            if (contractId is not null) q = q.Where(a => a.ContractId == contractId);
            return Results.Ok(await q.OrderBy(a => a.CreatedAt).ToListAsync());
        }).WithTags("Billing");

        app.MapGet("/agreements/{id:guid}", async (Guid id, IDbContextFactory<BillingDb> dbf) =>
        {
            await using var db = await dbf.CreateDbContextAsync();
            var agreement = await db.Agreements.SingleOrDefaultAsync(a => a.Id == id);
            return agreement is null ? Results.NotFound() : Results.Ok(agreement);
        }).WithTags("Billing");

        app.MapGet("/agreements/{id:guid}/invoices", async (Guid id, IDbContextFactory<BillingDb> dbf) =>
        {
            await using var db = await dbf.CreateDbContextAsync();
            return Results.Ok(await db.Invoices.Where(i => i.AgreementId == id).OrderBy(i => i.PeriodStart).ToListAsync());
        }).WithTags("Billing");

        app.MapGet("/invoices/{id:guid}", async (Guid id, IDbContextFactory<BillingDb> dbf) =>
        {
            await using var db = await dbf.CreateDbContextAsync();
            var invoice = await db.Invoices.SingleOrDefaultAsync(i => i.Id == id);
            return invoice is null ? Results.NotFound() : Results.Ok(invoice);
        }).WithTags("Billing");

        app.MapGet("/invoices/{id:guid}/attempts", async (Guid id, IDbContextFactory<BillingDb> dbf) =>
        {
            await using var db = await dbf.CreateDbContextAsync();
            return Results.Ok(await db.Attempts.Where(a => a.InvoiceId == id).OrderBy(a => a.CreatedAt).ToListAsync());
        }).WithTags("Billing");

        app.MapGet("/invoices/{id:guid}/ladders", async (Guid id, IDbContextFactory<BillingDb> dbf) =>
        {
            await using var db = await dbf.CreateDbContextAsync();
            return Results.Ok(await db.Ladders.Where(l => l.InvoiceId == id).OrderBy(l => l.CreatedAt).ToListAsync());
        }).WithTags("Billing");

        app.MapGet("/members/{id:guid}", async (Guid id, IDbContextFactory<BillingDb> dbf) =>
        {
            await using var db = await dbf.CreateDbContextAsync();
            var member = await db.Members.SingleOrDefaultAsync(m => m.Id == id);
            return member is null ? Results.NotFound() : Results.Ok(member);
        }).WithTags("Billing");

        // ---- Runs (existing-production mechanism: trigger, fan-out, rerun, manual) ----
        app.MapPost("/runs/trigger", async (RunService runs) => Results.Ok(await runs.TriggerAsync("auto")))
            .WithTags("Runs");

        app.MapGet("/runs/{id:guid}", async (Guid id, IDbContextFactory<BillingDb> dbf) =>
        {
            await using var db = await dbf.CreateDbContextAsync();
            var run = await db.ScheduleRuns.SingleOrDefaultAsync(r => r.Id == id);
            if (run is null) return Results.NotFound();
            var studioRuns = await db.StudioRuns.Where(s => s.ScheduleRunId == id).ToListAsync();
            return Results.Ok(new { run, studioRuns });
        }).WithTags("Runs");

        app.MapGet("/runs", async (IDbContextFactory<BillingDb> dbf) =>
        {
            await using var db = await dbf.CreateDbContextAsync();
            return Results.Ok(await db.ScheduleRuns.OrderByDescending(r => r.CreatedAt).Take(20).ToListAsync());
        }).WithTags("Runs");

        app.MapPost("/runs/{id:guid}/rerun", async (Guid id, RunService runs) =>
        {
            var run = await runs.RerunAsync(id);
            return run is null ? Results.NotFound() : Results.Ok(run);
        }).WithTags("Runs");

        app.MapPost("/studios/{id:int}/run", async (int id, RunService runs) =>
            Results.Ok(await runs.ManualStudioRunAsync(id))).WithTags("Runs");

        app.MapGet("/studio-runs/{id:guid}", async (Guid id, IDbContextFactory<BillingDb> dbf) =>
        {
            await using var db = await dbf.CreateDbContextAsync();
            var studioRun = await db.StudioRuns.SingleOrDefaultAsync(s => s.Id == id);
            return studioRun is null ? Results.NotFound() : Results.Ok(studioRun);
        }).WithTags("Runs");

        // ---- Studios (ops pause gate, F7) ----
        app.MapGet("/studios", async (IDbContextFactory<BillingDb> dbf) =>
        {
            await using var db = await dbf.CreateDbContextAsync();
            return Results.Ok(await db.Studios.OrderBy(s => s.Id).ToListAsync());
        }).WithTags("Studios");

        app.MapPost("/studios/{id:int}/pause", async (int id, IDbContextFactory<BillingDb> dbf) =>
        {
            await using var db = await dbf.CreateDbContextAsync();
            var studio = await db.Studios.SingleAsync(s => s.Id == id);
            studio.Paused = true;
            await db.SaveChangesAsync();
            return Results.Ok(studio);
        }).WithTags("Studios");

        app.MapPost("/studios/{id:int}/resume", async (int id, IDbContextFactory<BillingDb> dbf) =>
        {
            await using var db = await dbf.CreateDbContextAsync();
            var studio = await db.Studios.SingleAsync(s => s.Id == id);
            studio.Paused = false;
            await db.SaveChangesAsync();
            return Results.Ok(studio);
        }).WithTags("Studios");

        // ---- Member self-serve (recovery links) ----
        app.MapGet("/selfserve/{token:guid}", async (Guid token, RecoveryService recovery) =>
        {
            var status = await recovery.GetSelfServeStatusAsync(token);
            return status is null ? Results.NotFound() : Results.Ok(status);
        }).WithTags("SelfServe");

        app.MapPost("/selfserve/{token:guid}/update-instrument", async (Guid token, UpdateInstrumentDto dto, RecoveryService recovery) =>
        {
            var result = await recovery.UpdateInstrumentAsync(token, dto);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).WithTags("SelfServe");

        app.MapPost("/selfserve/{token:guid}/pay-now", async (Guid token, RecoveryService recovery, CollectionService collection, IDbContextFactory<BillingDb> dbf) =>
        {
            await using var db = await dbf.CreateDbContextAsync();
            var ladder = await db.Ladders.SingleOrDefaultAsync(l => l.SelfServeToken == token);
            if (ladder is null) return Results.NotFound();
            var outcome = await collection.CollectAsync(ladder.InvoiceId, "selfserve");
            return Results.Ok(outcome);
        }).WithTags("SelfServe");

        // ---- Gateway webhooks (async rails, F8) + chargeback (F6) ----
        app.MapPost("/webhooks/gateway", async (WebhookDto dto, CollectionService collection) =>
            Results.Ok(await collection.HandleGatewayWebhookAsync(dto.Type, dto.GatewayRef, dto.Code)))
            .WithTags("Webhooks");

        app.MapPost("/webhooks/chargeback", async (ChargebackDto dto, CollectionService collection) =>
            Results.Ok(await collection.HandleChargebackAsync(dto.InvoiceId, dto.Reason)))
            .WithTags("Webhooks");

        // ---- Agreement lifecycle (S15 pause, S16 depletion, F5 instrument events) ----
        app.MapPost("/agreements/pause", async (PauseAgreementDto dto, AgreementService agreements) =>
            Results.Ok(await agreements.SchedulePauseAsync(dto))).WithTags("Agreements");

        app.MapPost("/agreements/unsuspend", async (EarlyUnsuspendDto dto, AgreementService agreements) =>
            Results.Ok(await agreements.EarlyUnsuspendAsync(dto))).WithTags("Agreements");

        app.MapPost("/intake/entitlement-depleted", async (EntitlementDepletedDto dto, AgreementService agreements) =>
            Results.Ok(await agreements.HandleEntitlementDepletedAsync(dto))).WithTags("Intake");

        app.MapPost("/intake/instrument-events", async (InstrumentEventDto dto, AgreementService agreements) =>
            Results.Ok(await agreements.HandleInstrumentEventAsync(dto))).WithTags("Intake");

        app.MapPost("/controls/depletion-sweep", async (AgreementService agreements) =>
            Results.Ok(await agreements.SweepDepletionAsync())).WithTags("Controls");

        app.MapPost("/controls/poll-pending", async (CollectionService collection) =>
            Results.Ok(await collection.PollPendingAttemptsAsync(TimeSpan.FromHours(1)))).WithTags("Controls");

        // ---- Policies (P6 validation) ----
        app.MapPost("/policies", async (PolicyUpsertDto dto, IDbContextFactory<BillingDb> dbf) =>
        {
            try
            {
                PolicyDef.ValidateOrThrow(dto.Definition);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            await using var db = await dbf.CreateDbContextAsync();
            var row = await db.Policies.SingleOrDefaultAsync(p => p.Id == dto.Id);
            if (row is null)
            {
                row = new Policy { Id = dto.Id };
                db.Policies.Add(row);
            }
            row.DefinitionJson = Json.Serialize(dto.Definition);
            await db.SaveChangesAsync();
            return Results.Ok(row);
        }).WithTags("Policies");

        // ---- Controls plane ----
        app.MapPost("/controls/sweep", async (ControlsService controls) => Results.Ok(await controls.SweepAsync()))
            .WithTags("Controls");

        app.MapPost("/controls/reconcile", async (ControlsService controls) => Results.Ok(await controls.ReconcileAsync()))
            .WithTags("Controls");

        app.MapGet("/controls/work-items", async (IDbContextFactory<BillingDb> dbf) =>
        {
            await using var db = await dbf.CreateDbContextAsync();
            return Results.Ok(await db.ControlWorkItems.OrderBy(w => w.CreatedAt).ToListAsync());
        }).WithTags("Controls");
    }

    public record ChargebackDto(Guid InvoiceId, string? Reason);
    public record PolicyUpsertDto(string Id, PolicyDef Definition);

    // -------------------------------------------------------------------------------------------

    public record AdvanceTimeDto(int? Days, int? Hours, int? Minutes, DateTime? To);
    public record MaterializeDto(DateOnly? PeriodStart);
    public record SeedViolationDto(Guid AgreementId);
    public record RedeliverDto(string Topic);

    public static void MapDemo(WebApplication app)
    {
        app.MapGet("/demo/time", (IClock clock) => Results.Ok(new { utcNow = clock.UtcNow }))
            .WithTags("Demo");

        app.MapPost("/demo/time/advance", async (AdvanceTimeDto dto, VirtualClock clock, AgreementService agreements) =>
        {
            if (dto.To is { } target) clock.AdvanceTo(DateTime.SpecifyKind(target, DateTimeKind.Utc));
            else clock.Advance(new TimeSpan(dto.Days ?? 0, dto.Hours ?? 0, dto.Minutes ?? 0, 0));
            // Pause auto-resume ticks on the same virtual clock as the demo advances.
            await agreements.TickAutoResumeAsync();
            return Results.Ok(new { utcNow = clock.UtcNow });
        }).WithTags("Demo");

        app.MapPost("/demo/seed", async (IDbContextFactory<BillingDb> dbf, IClock clock, ExternalsClient ext) =>
            Results.Ok(await Seeder.SeedAsync(dbf, clock, ext))).WithTags("Demo");

        // Forces a duplicate-cycle insert so the unique constraint is demonstrably the guard (D2/P4).
        app.MapPost("/demo/agreements/{id:guid}/materialize-next", async (Guid id, MaterializeDto dto, IDbContextFactory<BillingDb> dbf, IClock clock) =>
        {
            await using var db = await dbf.CreateDbContextAsync();
            var agreement = await db.Agreements.SingleAsync(a => a.Id == id);
            var studio = await db.Studios.SingleAsync(s => s.Id == agreement.StudioId);
            var open = await db.Invoices.FirstOrDefaultAsync(i => i.AgreementId == id && i.State == "scheduled");
            var period = dto.PeriodStart ?? open?.PeriodStart ?? agreement.NextPeriodStart;

            db.Invoices.Add(new Invoice
            {
                Id = Guid.NewGuid(),
                AgreementId = agreement.Id,
                StudioId = agreement.StudioId,
                MemberId = agreement.MemberId,
                PeriodStart = period,
                Kind = "cycle",
                BaseAmountCents = agreement.AmountCents,
                ResidualCents = agreement.AmountCents,
                DueAt = period.ToDateTime(new TimeOnly(studio.BillingHourUtc, 0), DateTimeKind.Utc),
                State = "scheduled",
                CreatedAt = clock.UtcNow
            });
            try
            {
                await db.SaveChangesAsync();
                return Results.Ok(new { materialized = period });
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" } pg)
            {
                return Results.Conflict(new { blockedByConstraint = pg.ConstraintName, periodStart = period });
            }
        }).WithTags("Demo");

        // Seeds an invariant violation (cancels the open invoice under an active agreement) for the sweeper demo (D8).
        app.MapPost("/demo/seed-violation", async (SeedViolationDto dto, IDbContextFactory<BillingDb> dbf) =>
        {
            await using var db = await dbf.CreateDbContextAsync();
            var open = await db.Invoices.FirstOrDefaultAsync(i => i.AgreementId == dto.AgreementId && i.State == "scheduled");
            if (open is null) return Results.NotFound(new { error = "no open invoice to cancel" });
            open.State = "canceled";
            await db.SaveChangesAsync();
            return Results.Ok(new { canceledInvoice = open.Id });
        }).WithTags("Demo");

        // Re-enqueues the last outbox message of a topic: proves at-least-once delivery + downstream dedup (D3/P13).
        app.MapPost("/demo/outbox/redeliver-last", async (RedeliverDto dto, IDbContextFactory<BillingDb> dbf, IClock clock) =>
        {
            await using var db = await dbf.CreateDbContextAsync();
            var last = await db.OutboxMessages.Where(m => m.Topic == dto.Topic)
                .OrderByDescending(m => m.Id).FirstOrDefaultAsync();
            if (last is null) return Results.NotFound(new { error = $"no outbox message with topic {dto.Topic}" });
            db.OutboxMessages.Add(new OutboxMessage
            {
                Topic = last.Topic,
                PayloadJson = last.PayloadJson,
                AvailableAt = clock.UtcNow
            });
            await db.SaveChangesAsync();
            return Results.Ok(new { redelivered = last.Id });
        }).WithTags("Demo");
    }
}
