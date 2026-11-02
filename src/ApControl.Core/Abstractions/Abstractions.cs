using ApControl.Core.Models;

namespace ApControl.Core.Abstractions;

/// <summary>
/// Device-level operations: talk to the console / PC itself (power, install, launch, kill).
/// Implemented per platform by wrapping vendor tooling (Xbox GDK tools, PS5 prospero-ctrl, ...).
/// </summary>
public interface IPlatformDriver
{
    Task<bool> PingAsync(ClientDefinition client, CancellationToken ct);
    Task<CommandOutcome> InstallBuildAsync(ClientDefinition client, BuildInfo build, CancellationToken ct);
    Task<CommandOutcome> LaunchAsync(ClientDefinition client, LaunchSpec spec, CancellationToken ct);
    Task<CommandOutcome> TerminateAsync(ClientDefinition client, CancellationToken ct);
    Task<CommandOutcome> RebootAsync(ClientDefinition client, CancellationToken ct);
}

/// <summary>
/// Game-level operations: talk to the Autoplayer agent running inside the game
/// (join / leave / deploy / start autoplay, status reporting).
/// </summary>
public interface IGameChannel
{
    Task<CommandOutcome> SendAsync(ClientDefinition client, GameCommand command, CancellationToken ct);

    /// <summary>Returns null when the game is not running or not answering.</summary>
    Task<GameStatus?> QueryStatusAsync(ClientDefinition client, CancellationToken ct);
}

/// <summary>
/// Source of installable builds. This is the seam where Build Buddy (or any build
/// tracker / network share) plugs in.
/// </summary>
public interface IBuildProvider
{
    string Name { get; }
    Task<IReadOnlyList<BuildInfo>> ListBuildsAsync(Platform? platform, CancellationToken ct);
    Task<BuildInfo?> ResolveAsync(string buildId, Platform platform, CancellationToken ct);
}

/// <summary>Picks the driver / channel for a given client.</summary>
public interface IDriverResolver
{
    IPlatformDriver PlatformFor(ClientDefinition client);
    IGameChannel GameFor(ClientDefinition client);
}
