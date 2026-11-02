using System.Text.Json;
using System.Text.Json.Serialization;
using ApControl.Core.Abstractions;
using ApControl.Core.Control;
using ApControl.Core.Events;
using ApControl.Core.Models;
using ApControl.Core.Monitoring;
using ApControl.Core.Registry;
using ApControl.Core.Workflows;
using ApControl.Drivers;
using ApControl.Drivers.Builds;
using ApControl.Drivers.Cli;
using ApControl.Drivers.Game;
using ApControl.Drivers.Simulation;
using ApControl.Server;
using Microsoft.AspNetCore.Http.Json;

var builder = WebApplication.CreateBuilder(args);

// Machine-specific overrides (real devkit inventory, AppIds, Build Buddy key). Git-ignored.
// Command-line args are re-applied afterwards so they still win.
builder.Configuration
    .AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false)
    .AddCommandLine(args);

var opts = builder.Configuration.GetSection("ApControl").Get<AppOptions>() ?? new AppOptions();

builder.Services.Configure<JsonOptions>(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

// ---------------- composition ----------------
builder.Services.AddSingleton(opts);
builder.Services.AddSingleton(opts.Control);
builder.Services.AddSingleton<EventBus>();

if (opts.IsSimulated) builder.Services.AddSingleton(new SimulatedFleet(opts.Simulation));

builder.Services.AddSingleton<IDriverResolver>(sp =>
{
    var sim = sp.GetService<SimulatedFleet>();
    if (sim != null) return DriverResolver.Simulated(sim);

    var runner = new ProcessRunner();
    var platformDrivers = opts.Platforms.ToDictionary(
        p => p.Key,
        p => (IPlatformDriver)new CliPlatformDriver(p.Key.ToString(), p.Value.Cli, runner));
    return new DriverResolver(platformDrivers, new TcpJsonGameChannel(opts.Game), null);
});

builder.Services.AddHttpClient();
builder.Services.AddSingleton<IBuildProvider>(sp => opts.Builds.Provider.ToLowerInvariant() switch
{
    "folder" => new FolderBuildProvider(opts.Builds.Folder),
    "buildbuddy" => new BuildBuddyBuildProvider(
        sp.GetRequiredService<IHttpClientFactory>().CreateClient("BuildBuddy"), opts.Builds.BuildBuddy),
    _ => new StaticBuildProvider(opts.Builds.Static)
});

builder.Services.AddSingleton(sp =>
{
    var clients = opts.Clients.Count > 0 || !opts.IsSimulated
        ? opts.Clients
        : SimulatedFleet.GenerateInventory(opts.Simulation.ClientCount);
    return new ClientRegistry(clients, sp.GetRequiredService<EventBus>());
});

builder.Services.AddSingleton(sp => new AutoplayerController(
    sp.GetRequiredService<ClientRegistry>(),
    sp.GetRequiredService<IDriverResolver>(),
    sp.GetRequiredService<IBuildProvider>(),
    sp.GetRequiredService<EventBus>(),
    opts.Control,
    opts.Platforms.ToDictionary(p => p.Key, p => new PlatformLaunchOptions { AppId = p.Value.AppId, DefaultLaunchArgs = p.Value.DefaultLaunchArgs }),
    opts.Experiences));

builder.Services.AddSingleton<WorkflowRunner>();
builder.Services.AddSingleton<StatusMonitor>();
builder.Services.AddHostedService<MonitorHostedService>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

// ---------------- API ----------------
var api = app.MapGroup("/api");

api.MapGet("/meta", (AppOptions o, ClientRegistry reg) => new
{
    mode = o.Mode,
    experiences = o.Experiences.Select(e => new { e.Id, e.Name }),
    profiles = o.Profiles,
    workflows = o.Workflows.Select(w => new { w.Name, w.Description, steps = w.Steps.Select(s => s.ToString()) }),
    groups = reg.Groups(),
    actions = Enum.GetNames<ControlAction>()
});

api.MapGet("/clients", (ClientRegistry reg, AutoplayerController ctl) => reg.Snapshot(ctl.IsBusy));

api.MapGet("/clients/{id}", (string id, ClientRegistry reg, AutoplayerController ctl) =>
    reg.Get(id, ctl.IsBusy(id)) is { } c ? Results.Ok(c) : Results.NotFound());

api.MapGet("/groups", (ClientRegistry reg) => reg.Groups());

api.MapPut("/groups/{name}", (string name, GroupBody body, ClientRegistry reg) =>
{
    reg.SetGroup(name, body.ClientIds ?? new List<string>());
    return Results.Ok(reg.Groups());
});

api.MapDelete("/groups/{name}", (string name, ClientRegistry reg) =>
    reg.RemoveGroup(name) ? Results.NoContent() : Results.NotFound());

api.MapGet("/builds", async (IBuildProvider builds, Platform? platform, CancellationToken ct) =>
    new { provider = builds.Name, builds = await builds.ListBuildsAsync(platform, ct) });

// Bulk command. Waits for completion and returns per-client results (exception summary).
api.MapPost("/commands", async (CommandBody body, ClientRegistry reg, AutoplayerController ctl, CancellationToken ct) =>
{
    var ids = reg.Resolve(body.Target ?? new ClientSelector());
    if (ids.Count == 0) return Results.BadRequest(new { error = "No clients selected" });
    // Commands keep running if the browser disconnects; results stream to /api/events too.
    var result = await ctl.ExecuteAsync(body.Request, ids, CancellationToken.None);
    return Results.Ok(result);
});

api.MapGet("/workflows/runs", (WorkflowRunner runner) => runner.Runs());

api.MapGet("/workflows/runs/{runId}", (string runId, WorkflowRunner runner) =>
    runner.Get(runId) is { } r ? Results.Ok(r) : Results.NotFound());

api.MapPost("/workflows/{name}/run", (string name, ClientSelector target, AppOptions o, ClientRegistry reg, WorkflowRunner runner) =>
{
    var wf = o.Workflows.FirstOrDefault(w => w.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    if (wf is null) return Results.NotFound(new { error = $"Unknown workflow '{name}'" });
    var ids = reg.Resolve(target);
    if (ids.Count == 0) return Results.BadRequest(new { error = "No clients selected" });
    return Results.Accepted(value: runner.Start(wf, ids));
});

api.MapPost("/workflows/runs/{runId}/retry", (string runId, WorkflowRunner runner) =>
    runner.RetryFailed(runId) is { } r ? Results.Accepted(value: r) : Results.NotFound());

api.MapGet("/events/recent", (EventBus bus, int? max) => bus.Recent(max ?? 200));

// Server-Sent Events: live client state, command results and workflow progress.
api.MapGet("/events", async (HttpContext ctx, EventBus bus, Microsoft.Extensions.Options.IOptions<JsonOptions> json) =>
{
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Response.Headers["X-Accel-Buffering"] = "no";
    using var sub = bus.Subscribe();
    var ct = ctx.RequestAborted;
    try
    {
        await ctx.Response.WriteAsync(": connected\n\n", ct);
        await ctx.Response.Body.FlushAsync(ct);
        await foreach (var evt in sub.Reader.ReadAllAsync(ct))
        {
            var data = JsonSerializer.Serialize(evt, json.Value.SerializerOptions);
            await ctx.Response.WriteAsync($"data: {data}\n\n", ct);
            await ctx.Response.Body.FlushAsync(ct);
        }
    }
    catch (OperationCanceledException) { /* browser closed */ }
});

app.Logger.LogInformation("Autoplayer Control running in {Mode} mode", opts.Mode);
app.Run();

// ---------------- request bodies ----------------
public sealed record CommandBody(ControlRequest Request, ClientSelector? Target);
public sealed record GroupBody(List<string>? ClientIds);

/// <summary>Hosts the observability loop for the lifetime of the server.</summary>
public sealed class MonitorHostedService : BackgroundService
{
    private readonly StatusMonitor _monitor;
    private readonly EventBus _bus;
    public MonitorHostedService(StatusMonitor monitor, EventBus bus) { _monitor = monitor; _bus = bus; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _bus.Publish("log", null, "Status monitor started");
        await _monitor.RunAsync(stoppingToken);
    }
}

// Needed so WebApplicationFactory-based integration tests can reference the entry point.
public partial class Program { }
