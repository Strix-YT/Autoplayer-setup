namespace ApControl.Core.Models;

/// <summary>Target hardware family of an Autoplayer client.</summary>
public enum Platform
{
    Xbox,
    PS5,
    PC
}

/// <summary>
/// Lifecycle state of an Autoplayer client, from console power through to active autoplay.
/// Ordered roughly by progression so "at least X" comparisons are possible.
/// </summary>
public enum ApState
{
    Unknown = 0,
    Offline = 1,      // console unreachable
    Online = 2,       // console reachable, game not running
    Installing = 3,   // build install in progress
    Booting = 4,      // game launched, not yet in front end
    InFrontend = 5,   // game in main menu / FE
    Matchmaking = 6,  // joining an experience
    InGame = 7,       // in a server, not deployed
    Deployed = 8,     // spawned on the map
    Autoplaying = 9,  // autoplayer actively driving the character
    Error = 10        // last operation failed or crash detected
}

/// <summary>Static inventory entry for one Autoplayer device (from config).</summary>
public sealed class ClientDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public Platform Platform { get; set; } = Platform.Xbox;
    /// <summary>Devkit IP / hostname used by platform tools and the game control channel.</summary>
    public string Address { get; set; } = "";
    /// <summary>Optional override for the in-game control port.</summary>
    public int? GamePort { get; set; }
    public List<string> Groups { get; set; } = new();
}

/// <summary>Immutable view of a client's current state, safe to serialize to the dashboard.</summary>
public sealed record ClientSnapshot(
    string Id,
    string Name,
    Platform Platform,
    string Address,
    IReadOnlyList<string> Groups,
    ApState State,
    bool Busy,
    string? Experience,
    string? BuildVersion,
    string? Profile,
    string? LastMessage,
    DateTimeOffset? LastSeen,
    DateTimeOffset StateChangedAt);

/// <summary>A game experience / mode the Autoplayers can join (Conquest, Breakthrough, ...).</summary>
public sealed class ExperienceDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Optional extra arguments forwarded to the in-game join command (playlist id, server, ...).</summary>
    public Dictionary<string, string> JoinArgs { get; set; } = new();
}

/// <summary>A game build that can be installed on a client.</summary>
public sealed record BuildInfo(
    string Id,
    string Version,
    string Branch,
    Platform Platform,
    string PackagePath,
    DateTimeOffset? CreatedAt = null);

/// <summary>What to run when launching the game on a device.</summary>
public sealed record LaunchSpec(string AppId, string Arguments);

/// <summary>A command sent to the in-game Autoplayer agent.</summary>
public sealed record GameCommand(string Verb, IReadOnlyDictionary<string, string> Args)
{
    public static GameCommand Of(string verb, params (string Key, string Value)[] args) =>
        new(verb, args.ToDictionary(a => a.Key, a => a.Value));
}

/// <summary>Status reported by the in-game agent. Null from a channel means "game not running / not answering".</summary>
public sealed record GameStatus(ApState State, string? Experience, string? BuildVersion, string? Message);

/// <summary>Result of a single driver / channel operation.</summary>
public sealed record CommandOutcome(bool Success, string? Message)
{
    public static CommandOutcome Ok(string? message = null) => new(true, message);
    public static CommandOutcome Fail(string message) => new(false, message);
}
