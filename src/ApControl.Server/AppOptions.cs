using ApControl.Core.Control;
using ApControl.Core.Models;
using ApControl.Core.Workflows;
using ApControl.Drivers.Builds;
using ApControl.Drivers.Cli;
using ApControl.Drivers.Game;
using ApControl.Drivers.Simulation;

namespace ApControl.Server;

/// <summary>Root of the "ApControl" configuration section (appsettings.json).</summary>
public sealed class AppOptions
{
    /// <summary>"Simulated" (no hardware) or "Live" (real devkits).</summary>
    public string Mode { get; set; } = "Simulated";
    public bool IsSimulated => Mode.Equals("Simulated", StringComparison.OrdinalIgnoreCase);

    public ControlOptions Control { get; set; } = new();
    public SimulationOptions Simulation { get; set; } = new();
    public GameChannelOptions Game { get; set; } = new();
    public BuildSourceOptions Builds { get; set; } = new();

    public List<ClientDefinition> Clients { get; set; } = new();
    public List<ExperienceDefinition> Experiences { get; set; } = new();
    public List<string> Profiles { get; set; } = new();
    public List<WorkflowDefinition> Workflows { get; set; } = new();
    public Dictionary<Platform, PlatformOptions> Platforms { get; set; } = new();
}

public sealed class PlatformOptions
{
    public string AppId { get; set; } = "";
    public string DefaultLaunchArgs { get; set; } = "";
    public PlatformCliOptions Cli { get; set; } = new();
}
