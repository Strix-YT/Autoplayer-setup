using System.Collections.Concurrent;
using System.Diagnostics;
using ApControl.Core.Abstractions;
using ApControl.Core.Events;
using ApControl.Core.Models;
using ApControl.Core.Registry;

namespace ApControl.Core.Control;

/// <summary>
/// Executes control actions against one or many Autoplayer clients, in parallel,
/// updating the registry as clients move through their lifecycle.
/// </summary>
public sealed class AutoplayerController
{
    private readonly ClientRegistry _registry;
    private readonly IDriverResolver _drivers;
    private readonly IBuildProvider _builds;
    private readonly EventBus _bus;
    private readonly ControlOptions _options;
    private readonly IReadOnlyDictionary<Platform, PlatformLaunchOptions> _platforms;
    private readonly IReadOnlyDictionary<string, ExperienceDefinition> _experiences;

    private readonly ConcurrentDictionary<string, byte> _busy = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, LaunchSpec> _lastLaunch = new(StringComparer.OrdinalIgnoreCase);

    public AutoplayerController(
        ClientRegistry registry,
        IDriverResolver drivers,
        IBuildProvider builds,
        EventBus bus,
        ControlOptions options,
        IReadOnlyDictionary<Platform, PlatformLaunchOptions> platforms,
        IEnumerable<ExperienceDefinition> experiences)
    {
        _registry = registry;
        _drivers = drivers;
        _builds = builds;
        _bus = bus;
        _options = options;
        _platforms = platforms;
        _experiences = experiences.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);
        _registry.BusyProbe = IsBusy;
    }

    public bool IsBusy(string clientId) => _busy.ContainsKey(clientId);

    public async Task<BulkResult> ExecuteAsync(ControlRequest request, IReadOnlyList<string> clientIds, CancellationToken ct = default)
    {
        var started = DateTimeOffset.UtcNow;
        var label = request.ToString();
        _bus.Publish("log", null, $"▶ {label} → {clientIds.Count} client(s)");

        var results = new ConcurrentBag<ClientResult>();
        await Parallel.ForEachAsync(clientIds,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, _options.MaxParallel), CancellationToken = ct },
            async (id, token) => results.Add(await ExecuteOneAsync(request, id, token)));

        var ordered = results.OrderBy(r => r.ClientId, StringComparer.OrdinalIgnoreCase).ToList();
        var bulk = new BulkResult(label, clientIds.Count, ordered.Count(r => r.Success), ordered, started, DateTimeOffset.UtcNow);
        _bus.Publish("command", null, $"■ {label}: {bulk.Succeeded}/{bulk.Requested} succeeded", bulk);
        return bulk;
    }

    public async Task<ClientResult> ExecuteOneAsync(ControlRequest request, string clientId, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        if (!_busy.TryAdd(clientId, 0))
            return new ClientResult(clientId, false, "Client is busy with another command", _registry.StateOf(clientId), sw.Elapsed);

        _registry.Publish(clientId); // show busy spinner

        CommandOutcome outcome;
        try
        {
            outcome = await DispatchAsync(request, _registry.Definition(clientId), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            outcome = CommandOutcome.Fail("Cancelled");
        }
        catch (Exception ex)
        {
            outcome = CommandOutcome.Fail($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _busy.TryRemove(clientId, out _);
        }

        if (!outcome.Success)
            _registry.Update(clientId, ApState.Error, $"{request}: {outcome.Message}");
        _registry.Publish(clientId); // clear busy spinner

        return new ClientResult(clientId, outcome.Success, outcome.Message, _registry.StateOf(clientId), sw.Elapsed);
    }

    private Task<CommandOutcome> DispatchAsync(ControlRequest r, ClientDefinition c, CancellationToken ct) => r.Action switch
    {
        ControlAction.InstallBuild => InstallAsync(c, r.BuildId, ct),
        ControlAction.Launch => LaunchAsync(c, r.LaunchArgs, ct),
        ControlAction.Stop => StopAsync(c, ct),
        ControlAction.Restart => RestartAsync(c, ct),
        ControlAction.Reboot => RebootAsync(c, ct),
        ControlAction.Join => JoinAsync(c, r.ExperienceId, ct),
        ControlAction.Leave => LeaveAsync(c, ct),
        ControlAction.SwitchExperience => SwitchAsync(c, r.ExperienceId, ct),
        ControlAction.Deploy => DeployAsync(c, r.Team, ct),
        ControlAction.StartAutoplayer => StartApAsync(c, r.Profile, ct),
        ControlAction.StopAutoplayer => StopApAsync(c, ct),
        _ => Task.FromResult(CommandOutcome.Fail($"Unsupported action {r.Action}"))
    };

    // ---------------- device level ----------------

    private async Task<CommandOutcome> InstallAsync(ClientDefinition c, string? buildId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(buildId)) return CommandOutcome.Fail("No build specified");
        var build = await _builds.ResolveAsync(buildId, c.Platform, ct);
        if (build is null) return CommandOutcome.Fail($"Build '{buildId}' not found for {c.Platform} in {_builds.Name}");

        _registry.Update(c.Id, ApState.Installing, $"Installing {build.Version}");
        using var timeout = Timeout(ct, _options.InstallTimeoutSeconds);
        var result = await _drivers.PlatformFor(c).InstallBuildAsync(c, build, timeout.Token);
        if (result.Success)
            _registry.Update(c.Id, ApState.Online, $"Installed {build.Version}", build: build.Version, experience: new(null));
        return result;
    }

    private async Task<CommandOutcome> LaunchAsync(ClientDefinition c, string? args, CancellationToken ct)
    {
        var platform = _platforms.TryGetValue(c.Platform, out var p) ? p : new PlatformLaunchOptions();
        var spec = new LaunchSpec(platform.AppId, string.IsNullOrWhiteSpace(args) ? platform.DefaultLaunchArgs : args!);
        _lastLaunch[c.Id] = spec;

        _registry.Update(c.Id, ApState.Booting, "Launching game");
        var result = await _drivers.PlatformFor(c).LaunchAsync(c, spec, ct);
        if (!result.Success) return result;

        return await WaitForStateAsync(c, s => s >= ApState.InFrontend && s != ApState.Error,
            _options.BootTimeoutSeconds, "reach front end", ct);
    }

    private async Task<CommandOutcome> StopAsync(ClientDefinition c, CancellationToken ct)
    {
        var result = await _drivers.PlatformFor(c).TerminateAsync(c, ct);
        if (result.Success)
            _registry.Update(c.Id, ApState.Online, "Game stopped", experience: new(null), profile: new(null));
        return result;
    }

    private async Task<CommandOutcome> RestartAsync(ClientDefinition c, CancellationToken ct)
    {
        var stop = await StopAsync(c, ct);
        if (!stop.Success) return stop;
        var args = _lastLaunch.TryGetValue(c.Id, out var spec) ? spec.Arguments : null;
        return await LaunchAsync(c, args, ct);
    }

    private async Task<CommandOutcome> RebootAsync(ClientDefinition c, CancellationToken ct)
    {
        var result = await _drivers.PlatformFor(c).RebootAsync(c, ct);
        if (result.Success)
            _registry.Update(c.Id, ApState.Offline, "Rebooting", experience: new(null), profile: new(null));
        return result;
    }

    // ---------------- game level ----------------

    private async Task<CommandOutcome> JoinAsync(ClientDefinition c, string? experienceId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(experienceId)) return CommandOutcome.Fail("No experience specified");
        var args = new Dictionary<string, string> { ["experience"] = experienceId };
        if (_experiences.TryGetValue(experienceId, out var exp))
            foreach (var kv in exp.JoinArgs) args[kv.Key] = kv.Value;

        var sent = await SendAsync(c, new GameCommand("join", args), ApState.Matchmaking, $"Joining {experienceId}", ct);
        if (!sent.Success) return sent;
        _registry.Update(c.Id, experience: experienceId);
        return await WaitForStateAsync(c, s => s is ApState.InGame or ApState.Deployed or ApState.Autoplaying,
            _options.MatchmakingTimeoutSeconds, $"join {experienceId}", ct);
    }

    private async Task<CommandOutcome> LeaveAsync(ClientDefinition c, CancellationToken ct)
    {
        var sent = await SendAsync(c, GameCommand.Of("leave"), null, "Leaving experience", ct);
        if (!sent.Success) return sent;
        var waited = await WaitForStateAsync(c, s => s == ApState.InFrontend, _options.StateTimeoutSeconds, "return to front end", ct);
        if (waited.Success) _registry.Update(c.Id, experience: new(null), profile: new(null));
        return waited;
    }

    private async Task<CommandOutcome> SwitchAsync(ClientDefinition c, string? experienceId, CancellationToken ct)
    {
        if (_registry.StateOf(c.Id) >= ApState.Matchmaking && _registry.StateOf(c.Id) != ApState.Error)
        {
            var leave = await LeaveAsync(c, ct);
            if (!leave.Success) return leave;
        }
        return await JoinAsync(c, experienceId, ct);
    }

    private async Task<CommandOutcome> DeployAsync(ClientDefinition c, string? team, CancellationToken ct)
    {
        var cmd = team is null ? GameCommand.Of("deploy") : GameCommand.Of("deploy", ("team", team));
        var sent = await SendAsync(c, cmd, null, team is null ? "Deploying" : $"Deploying ({team})", ct);
        if (!sent.Success) return sent;
        return await WaitForStateAsync(c, s => s is ApState.Deployed or ApState.Autoplaying, _options.StateTimeoutSeconds, "deploy", ct);
    }

    private async Task<CommandOutcome> StartApAsync(ClientDefinition c, string? profile, CancellationToken ct)
    {
        profile = string.IsNullOrWhiteSpace(profile) ? _options.DefaultProfile : profile;
        var sent = await SendAsync(c, GameCommand.Of("ap.start", ("profile", profile)), null, $"Starting Autoplayer ({profile})", ct);
        if (!sent.Success) return sent;
        _registry.Update(c.Id, profile: profile);
        return await WaitForStateAsync(c, s => s == ApState.Autoplaying, _options.StateTimeoutSeconds, "start autoplayer", ct);
    }

    private async Task<CommandOutcome> StopApAsync(ClientDefinition c, CancellationToken ct)
    {
        var sent = await SendAsync(c, GameCommand.Of("ap.stop"), null, "Stopping Autoplayer", ct);
        if (!sent.Success) return sent;
        return await WaitForStateAsync(c, s => s != ApState.Autoplaying, _options.StateTimeoutSeconds, "stop autoplayer", ct);
    }

    private async Task<CommandOutcome> SendAsync(ClientDefinition c, GameCommand cmd, ApState? transitional, string message, CancellationToken ct)
    {
        var state = _registry.StateOf(c.Id);
        if (state is ApState.Offline or ApState.Online or ApState.Installing or ApState.Unknown)
            return CommandOutcome.Fail($"Game is not running (state {state})");

        _registry.Update(c.Id, transitional, message);
        return await _drivers.GameFor(c).SendAsync(c, cmd, ct);
    }

    /// <summary>Polls the in-game agent until the predicate holds or the timeout expires.</summary>
    private async Task<CommandOutcome> WaitForStateAsync(ClientDefinition c, Func<ApState, bool> done, int timeoutSeconds, string what, CancellationToken ct)
    {
        var channel = _drivers.GameFor(c);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
        var poll = TimeSpan.FromSeconds(Math.Max(0.05, _options.StatusPollSeconds));
        ApState? last = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var status = await channel.QueryStatusAsync(c, ct);
            if (status != null)
            {
                last = status.State;
                _registry.Update(c.Id, status.State, status.Message, build: status.BuildVersion ?? _registry.Get(c.Id)?.BuildVersion, seen: true);
                if (status.State == ApState.Error)
                    return CommandOutcome.Fail(status.Message ?? $"Game reported error while trying to {what}");
                if (done(status.State))
                    return CommandOutcome.Ok();
            }
            await Task.Delay(poll, ct);
        }
        return CommandOutcome.Fail($"Timed out after {timeoutSeconds}s waiting to {what} (last state: {last?.ToString() ?? "no response"})");
    }

    private static CancellationTokenSource Timeout(CancellationToken ct, int seconds)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(seconds));
        return cts;
    }
}
