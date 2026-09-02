using BillingCore.Api;
using BillingCore.Domain;
using BillingCore.Infrastructure;
using BillingCore.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Postgres resolution: docker-compose (autopay/autopay) on :5433, else embedded (postgres user).
// Always use Database=autopay so EnsureDeleted can drop it (never connect to the open maintenance DB).
// Orphaned embedded from a prior crash still owns :5433 — detect and reuse it.
var dockerCs = builder.Configuration.GetConnectionString("Billing")!;
var embeddedAdminCs = "Host=localhost;Port=5433;Database=postgres;Username=postgres;Include Error Detail=true";
var embeddedCs = "Host=localhost;Port=5433;Database=autopay;Username=postgres;Include Error Detail=true";
string connectionString;
if (!await IsPortOpenAsync("localhost", 5433))
{
    Console.WriteLine("No Postgres on :5433 — starting embedded PostgreSQL (first run downloads ~50MB of binaries)...");
    // Version must exist as a zonky embedded-postgres binary on Maven Central (the library's source).
    var embedded = new MysticMind.PostgresEmbed.PgServer("17.5.0", port: 5433, clearInstanceDirOnStop: true);
    embedded.Start();
    await EnsureDatabaseExistsAsync(embeddedAdminCs, "autopay");
    connectionString = embeddedCs;
    builder.Services.AddSingleton(embedded); // dispose with the host
    Console.WriteLine("Embedded PostgreSQL up on :5433.");
}
else if (await CanConnectAsync(dockerCs))
{
    connectionString = dockerCs;
    Console.WriteLine("Using docker-compose Postgres on :5433.");
}
else if (await CanConnectAsync(embeddedAdminCs))
{
    await EnsureDatabaseExistsAsync(embeddedAdminCs, "autopay");
    connectionString = embeddedCs;
    Console.WriteLine("Using existing embedded Postgres on :5433 (orphan reuse).");
}
else
{
    throw new InvalidOperationException(
        "Port :5433 is open but neither docker (autopay) nor embedded (postgres) credentials work. Free the port and retry.");
}

builder.Services.AddDbContextFactory<BillingDb>(o => o
    .UseNpgsql(connectionString)
    .UseSnakeCaseNamingConvention());

builder.Services.AddSingleton<VirtualClock>();
builder.Services.AddSingleton<IClock>(sp => sp.GetRequiredService<VirtualClock>());

builder.Services.AddHttpClient<ExternalsClient>(c =>
{
    c.BaseAddress = new Uri(builder.Configuration["Externals:BaseUrl"]!);
    c.Timeout = TimeSpan.FromSeconds(30); // per-call CTS enforce the tight money-path timeouts
});

builder.Services.AddSingleton<FeeService>(sp =>
    new FeeService(sp.GetRequiredService<ExternalsClient>()));
builder.Services.AddSingleton<CollectionService>();
builder.Services.AddSingleton<RunService>();
builder.Services.AddSingleton<RecoveryService>();
builder.Services.AddSingleton<AgreementService>();
builder.Services.AddSingleton<ControlsService>();
builder.Services.AddSingleton<IntakeService>();
builder.Services.AddSingleton<BridgeHandlers>();

builder.Services.AddHostedService<TriggerWorker>();
builder.Services.AddHostedService<QueueWorker>();
builder.Services.AddHostedService<LadderWorker>();
builder.Services.AddHostedService<OutboxWorker>();
builder.Services.AddHostedService<PauseResumeWorker>();
builder.Services.AddHostedService<PendingPollWorker>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o => o.SwaggerDoc("v1", new()
{
    Title = "AutoPay Rewrite POC — billing-core",
    Version = "v1",
    Description = "HLD-faithful billing core: agreements, JIT invoices, tender chains, single-writer collections, per-attempt fees [TF-A], recovery ladders, runs, controls."
}));

var app = builder.Build();

// Schema + base rows, with retry while Postgres comes up.
await InitDatabaseAsync(app);

app.UseSwagger();
app.UseSwaggerUI();

Endpoints.MapCore(app);
Endpoints.MapDemo(app);

app.MapGet("/", () => Results.Redirect("/swagger"));

app.Run();

static async Task<bool> IsPortOpenAsync(string host, int port)
{
    try
    {
        using var client = new System.Net.Sockets.TcpClient();
        var connect = client.ConnectAsync(host, port);
        return await Task.WhenAny(connect, Task.Delay(1000)) == connect && client.Connected;
    }
    catch
    {
        return false;
    }
}

static async Task<bool> CanConnectAsync(string connectionString)
{
    try
    {
        await using var conn = new Npgsql.NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        return true;
    }
    catch
    {
        return false;
    }
}

static async Task EnsureDatabaseExistsAsync(string adminConnectionString, string databaseName)
{
    await using var conn = new Npgsql.NpgsqlConnection(adminConnectionString);
    await conn.OpenAsync();
    await using (var exists = new Npgsql.NpgsqlCommand(
                     "SELECT 1 FROM pg_database WHERE datname = @n", conn))
    {
        exists.Parameters.AddWithValue("n", databaseName);
        if (await exists.ExecuteScalarAsync() is not null) return;
    }
    await using var create = new Npgsql.NpgsqlCommand($"CREATE DATABASE {databaseName}", conn);
    await create.ExecuteNonQueryAsync();
}

static async Task InitDatabaseAsync(WebApplication app)
{
    var dbf = app.Services.GetRequiredService<IDbContextFactory<BillingDb>>();
    var clock = app.Services.GetRequiredService<IClock>();
    var log = app.Services.GetRequiredService<ILogger<Program>>();

    for (var attempt = 1; ; attempt++)
    {
        try
        {
            await using var db = await dbf.CreateDbContextAsync();
            // POC: recreate schema each boot so domain deltas always apply (no migration chain).
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            if (!await db.TriggerStates.AnyAsync(t => t.Id == 1))
                db.TriggerStates.Add(new TriggerState { Id = 1, LastWindowEnd = clock.UtcNow });
            if (!await db.Policies.AnyAsync(p => p.Id == "standard"))
            {
                var policy = new PolicyDef();
                PolicyDef.ValidateOrThrow(policy);
                db.Policies.Add(new Policy { Id = "standard", DefinitionJson = Json.Serialize(policy) });
            }
            await db.SaveChangesAsync();
            log.LogInformation("database ready");
            return;
        }
        catch (Exception ex) when (attempt < 15)
        {
            log.LogWarning("waiting for postgres ({Attempt}/15): {Message}", attempt, ex.Message);
            await Task.Delay(1500);
        }
    }
}
