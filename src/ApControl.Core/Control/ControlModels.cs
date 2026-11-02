using ApControl.Core.Models;

namespace ApControl.Core.Control;

/// <summary>Every operation the coordinator can issue to one or many Autoplayers.</summary>
public enum ControlAction
{
    InstallBuild,
    Launch,          // boot game with launch arguments
    Stop,            // terminate game process
    Restart,         // terminate + relaunch with last launch args
    Reboot,          // reboot the console / device
    Join,            // join experience
    Leave,           // leave current experience back to FE
    SwitchExperience,// leave + join another experience
    Deploy,          // spawn
    StartAutoplayer,
    StopAutoplayer
}

/// <summary>One command with its parameters. Unused parameters are ignored per action.</summary>
public sealed class ControlRequest
{
    public ControlAction Action { get; set; }
    public string? BuildId { get; set; }
    public string? ExperienceId { get; set; }
    public string? LaunchArgs { get; set; }
    public string? Profile { get; set; }
    /// <summary>Optional team / side for Deploy (e.g. "US", "RU", "Attack").</summary>
    public string? Team { get; set; }

    public override string ToString() => Action switch
    {
        ControlAction.InstallBuild => $"Install build {BuildId}",
        ControlAction.Join or ControlAction.SwitchExperience => $"{Action} {ExperienceId}",
        ControlAction.StartAutoplayer => $"Start Autoplayer ({Profile ?? "default"})",
        ControlAction.Deploy when Team != null => $"Deploy ({Team})",
        _ => Action.ToString()
    };
}

public sealed record ClientResult(string ClientId, bool Success, string? Message, ApState FinalState, TimeSpan Duration);

/// <summary>Outcome of a bulk command across many clients — the dashboard's exception summary.</summary>
public sealed record BulkResult(
    string Command,
    int Requested,
    int Succeeded,
    IReadOnlyList<ClientResult> Results,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt)
{
    public IReadOnlyList<ClientResult> Failures => Results.Where(r => !r.Success).ToList();
}

/// <summary>Tunables for the controller. Bound from configuration section "ApControl:Control".</summary>
public sealed class ControlOptions
{
    /// <summary>How many clients are driven concurrently during bulk commands.</summary>
    public int MaxParallel { get; set; } = 16;
    public int BootTimeoutSeconds { get; set; } = 240;
    public int MatchmakingTimeoutSeconds { get; set; } = 180;
    public int StateTimeoutSeconds { get; set; } = 60;
    public int InstallTimeoutSeconds { get; set; } = 1800;
    public double StatusPollSeconds { get; set; } = 2;
    public double HeartbeatSeconds { get; set; } = 5;
    public string DefaultProfile { get; set; } = "Standard";
}

/// <summary>Per-platform launch settings. Bound from "ApControl:Platforms:{Platform}".</summary>
public sealed class PlatformLaunchOptions
{
    /// <summary>Title id / AUMID / PC exe the platform tool launches.</summary>
    public string AppId { get; set; } = "";
    public string DefaultLaunchArgs { get; set; } = "";
}
