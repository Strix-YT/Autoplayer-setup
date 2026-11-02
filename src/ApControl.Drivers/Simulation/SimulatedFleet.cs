using System.Collections.Concurrent;
using ApControl.Core.Abstractions;
using ApControl.Core.Models;

namespace ApControl.Drivers.Simulation;

public sealed class SimulationOptions
{
    /// <summary>Number of fake clients to generate when no inventory is configured.</summary>
    public int ClientCount { get; set; } = 32;
    /// <summary>Multiplier on all simulated delays (0 = instant, for tests).</summary>
    public double DelayScale { get; set; } = 1.0;
    /// <summary>Probability that an install or join fails.</summary>
    public double FailureRate { get; set; } = 0.05;
    /// <summary>Probability per status query that an in-game client crashes.</summary>
    public double CrashRate { get; set; } = 0.0005;
    public int? Seed { get; set; }
}

/// <summary>
/// In-memory stand-in for consoles + in-game agent. Lets the dashboard, workflows and
/// tests run end-to-end with no hardware, including realistic delays, failures and crashes.
/// </summary>
public sealed class SimulatedFleet : IPlatformDriver, IGameChannel
{
    private readonly SimulationOptions _options;
    private readonly ConcurrentDictionary<string, Sim> _sims = new(StringComparer.OrdinalIgnoreCase);
    private readonly Random _random;

    public SimulatedFleet(SimulationOptions options)
    {
        _options = options;
        _random = options.Seed is { } seed ? new Random(seed) : Random.Shared;
    }

    /// <summary>Generates an inventory of fake clients: AP-001..AP-NNN, alternating Xbox/PS5, split into two banks.</summary>
    public static List<ClientDefinition> GenerateInventory(int count) =>
        Enumerable.Range(1, count).Select(i => new ClientDefinition
        {
            Id = $"AP-{i:000}",
            Name = $"AP-{i:000}",
            Platform = i % 2 == 0 ? Platform.PS5 : Platform.Xbox,
            Address = $"10.0.{(i - 1) / 16}.{(i - 1) % 16 + 10}",
            Groups = new List<string> { i <= count / 2 ? "Bank A" : "Bank B" }
        }).ToList();

    /// <summary>Test hook: force the next fallible operation on this client to fail.</summary>
    public void FailNext(string clientId) => Get(clientId).ForceFail = true;

    // ---------------- IPlatformDriver ----------------

    public Task<bool> PingAsync(ClientDefinition client, CancellationToken ct)
    {
        var s = Get(client.Id);
        lock (s) { Resolve(s); return Task.FromResult(s.Console != ApState.Offline); }
    }

    public async Task<CommandOutcome> InstallBuildAsync(ClientDefinition client, BuildInfo build, CancellationToken ct)
    {
        await Delay(3000 + Rand(4000), ct);
        var s = Get(client.Id);
        lock (s)
        {
            Resolve(s);
            if (s.Console == ApState.Offline) return CommandOutcome.Fail("Device offline");
            if (Roll(s)) return CommandOutcome.Fail("Package install failed: not enough free space (simulated)");
            s.Build = build.Version;
            s.GameRunning = false;
            return CommandOutcome.Ok();
        }
    }

    public async Task<CommandOutcome> LaunchAsync(ClientDefinition client, LaunchSpec spec, CancellationToken ct)
    {
        await Delay(500, ct);
        var s = Get(client.Id);
        lock (s)
        {
            Resolve(s);
            if (s.Console == ApState.Offline) return CommandOutcome.Fail("Device offline");
            s.Build ??= "CL000000-sim"; // pretend something is installed
            s.GameRunning = true;
            s.Game = ApState.Booting;
            s.Experience = null;
            Schedule(s, ApState.InFrontend, 4000 + Rand(4000));
            return CommandOutcome.Ok();
        }
    }

    public async Task<CommandOutcome> TerminateAsync(ClientDefinition client, CancellationToken ct)
    {
        await Delay(300, ct);
        var s = Get(client.Id);
        lock (s)
        {
            s.GameRunning = false;
            s.Pending = null;
            s.Experience = null;
            return CommandOutcome.Ok();
        }
    }

    public async Task<CommandOutcome> RebootAsync(ClientDefinition client, CancellationToken ct)
    {
        await Delay(300, ct);
        var s = Get(client.Id);
        lock (s)
        {
            s.GameRunning = false;
            s.Experience = null;
            s.Console = ApState.Offline;
            s.ConsoleBackAt = DateTimeOffset.UtcNow.AddMilliseconds(Scale(15000));
            return CommandOutcome.Ok();
        }
    }

    // ---------------- IGameChannel ----------------

    public async Task<CommandOutcome> SendAsync(ClientDefinition client, GameCommand command, CancellationToken ct)
    {
        await Delay(150, ct);
        var s = Get(client.Id);
        lock (s)
        {
            Resolve(s);
            if (!s.GameRunning) return CommandOutcome.Fail("Game not running");
            switch (command.Verb)
            {
                case "join":
                    if (s.Game != ApState.InFrontend) return CommandOutcome.Fail($"Can't join from {s.Game}");
                    if (Roll(s)) return CommandOutcome.Fail("Failed to join server: matchmaking timed out (simulated)");
                    s.Game = ApState.Matchmaking;
                    s.Experience = command.Args.TryGetValue("experience", out var e) ? e : null;
                    Schedule(s, ApState.InGame, 3000 + Rand(5000));
                    return CommandOutcome.Ok();

                case "leave":
                    if (s.Game < ApState.Matchmaking) return CommandOutcome.Ok("Already in front end");
                    s.Experience = null;
                    s.Game = ApState.Matchmaking; // "leaving" spinner
                    Schedule(s, ApState.InFrontend, 1500 + Rand(1500));
                    return CommandOutcome.Ok();

                case "deploy":
                    if (s.Game is not (ApState.InGame or ApState.Deployed)) return CommandOutcome.Fail($"Can't deploy from {s.Game}");
                    Schedule(s, ApState.Deployed, 1000 + Rand(2000));
                    return CommandOutcome.Ok();

                case "ap.start":
                    if (s.Game != ApState.Deployed) return CommandOutcome.Fail($"Can't start autoplayer from {s.Game}");
                    s.Game = ApState.Autoplaying;
                    return CommandOutcome.Ok();

                case "ap.stop":
                    if (s.Game == ApState.Autoplaying) s.Game = ApState.Deployed;
                    return CommandOutcome.Ok();

                default:
                    return CommandOutcome.Fail($"Unknown command '{command.Verb}'");
            }
        }
    }

    public Task<GameStatus?> QueryStatusAsync(ClientDefinition client, CancellationToken ct)
    {
        var s = Get(client.Id);
        lock (s)
        {
            Resolve(s);
            if (!s.GameRunning) return Task.FromResult<GameStatus?>(null);
            if (s.Game >= ApState.InGame && _options.CrashRate > 0 && RandD() < _options.CrashRate)
            {
                s.GameRunning = false; // simulated crash: game just vanishes
                s.Experience = null;
                return Task.FromResult<GameStatus?>(null);
            }
            return Task.FromResult<GameStatus?>(new GameStatus(s.Game, s.Experience, s.Build, null));
        }
    }

    // ---------------- internals ----------------

    private int Rand(int max) { lock (_random) return _random.Next(max); }
    private double RandD() { lock (_random) return _random.NextDouble(); }

    private Sim Get(string id) => _sims.GetOrAdd(id, _ => new Sim());

    private bool Roll(Sim s)
    {
        if (s.ForceFail) { s.ForceFail = false; return true; }
        return _options.FailureRate > 0 && RandD() < _options.FailureRate;
    }

    private void Schedule(Sim s, ApState next, int ms)
    {
        s.Pending = next;
        s.PendingAt = DateTimeOffset.UtcNow.AddMilliseconds(Scale(ms));
    }

    private static void Resolve(Sim s)
    {
        var now = DateTimeOffset.UtcNow;
        if (s.Console == ApState.Offline && s.ConsoleBackAt is { } back && now >= back)
        {
            s.Console = ApState.Online;
            s.ConsoleBackAt = null;
        }
        if (s.Pending is { } p && now >= s.PendingAt)
        {
            s.Game = p;
            s.Pending = null;
        }
    }

    private double Scale(int ms) => ms * Math.Max(0, _options.DelayScale);

    private Task Delay(int ms, CancellationToken ct)
    {
        var scaled = Scale(ms);
        return scaled < 1 ? Task.CompletedTask : Task.Delay(TimeSpan.FromMilliseconds(scaled), ct);
    }

    private sealed class Sim
    {
        public ApState Console = ApState.Online;
        public DateTimeOffset? ConsoleBackAt;
        public bool GameRunning;
        public ApState Game = ApState.Booting;
        public ApState? Pending;
        public DateTimeOffset PendingAt;
        public string? Experience;
        public string? Build;
        public bool ForceFail;
    }
}
