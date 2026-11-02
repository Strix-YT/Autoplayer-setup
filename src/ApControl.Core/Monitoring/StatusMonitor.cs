using ApControl.Core.Abstractions;
using ApControl.Core.Control;
using ApControl.Core.Models;
using ApControl.Core.Registry;

namespace ApControl.Core.Monitoring;

/// <summary>
/// Observability loop: heartbeats every client (device ping + in-game status) so the
/// coordinator sees real lifecycle state without touching the device. Also detects
/// unexpected game loss (crash / disconnect) and flags it as Error.
/// Clients currently executing a command are skipped — the controller owns their state.
/// </summary>
public sealed class StatusMonitor
{
    private readonly ClientRegistry _registry;
    private readonly IDriverResolver _drivers;
    private readonly AutoplayerController _controller;
    private readonly ControlOptions _options;

    public StatusMonitor(ClientRegistry registry, IDriverResolver drivers, AutoplayerController controller, ControlOptions options)
    {
        _registry = registry;
        _drivers = drivers;
        _controller = controller;
        _options = options;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(0.5, _options.HeartbeatSeconds));
        while (!ct.IsCancellationRequested)
        {
            await PollOnceAsync(ct);
            try { await Task.Delay(interval, ct); } catch (OperationCanceledException) { break; }
        }
    }

    public async Task PollOnceAsync(CancellationToken ct)
    {
        var ids = _registry.AllIds.Where(id => !_controller.IsBusy(id)).ToList();
        await Parallel.ForEachAsync(ids, new ParallelOptions { MaxDegreeOfParallelism = 32, CancellationToken = ct },
            async (id, token) =>
            {
                try { await PollClientAsync(id, token); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { _registry.Update(id, ApState.Error, $"Monitor error: {ex.Message}"); }
            });
    }

    private async Task PollClientAsync(string id, CancellationToken ct)
    {
        var def = _registry.Definition(id);
        var previous = _registry.StateOf(id);

        if (!await _drivers.PlatformFor(def).PingAsync(def, ct))
        {
            if (previous != ApState.Offline)
                _registry.Update(id, ApState.Offline, previous >= ApState.Booting ? "Device went offline while in game" : "Device unreachable",
                    experience: new(null), profile: new(null));
            return;
        }

        if (_controller.IsBusy(id)) return; // a command started while we were pinging

        var status = await _drivers.GameFor(def).QueryStatusAsync(def, ct);
        if (status is null)
        {
            // Console is up but the game isn't answering.
            if (previous >= ApState.InFrontend && previous != ApState.Error)
                _registry.Update(id, ApState.Error, $"Game stopped responding (was {previous}) — possible crash or disconnect",
                    experience: new(null), profile: new(null), seen: true);
            else if (previous is ApState.Unknown or ApState.Offline)
                _registry.Update(id, ApState.Online, "Device online", seen: true);
            else
                _registry.Update(id, seen: true);
            return;
        }

        // Booting is owned by the Launch command; don't flap it back.
        _registry.Update(id, status.State, status.Message,
            experience: status.Experience, build: status.BuildVersion ?? _registry.Get(id)?.BuildVersion, seen: true);
    }
}
