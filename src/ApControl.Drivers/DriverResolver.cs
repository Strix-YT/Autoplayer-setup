using ApControl.Core.Abstractions;
using ApControl.Core.Models;
using ApControl.Drivers.Simulation;

namespace ApControl.Drivers;

/// <summary>
/// Routes each client to its platform driver and game channel.
/// In simulation mode every client goes to the <see cref="SimulatedFleet"/>, regardless of platform.
/// </summary>
public sealed class DriverResolver : IDriverResolver
{
    private readonly IReadOnlyDictionary<Platform, IPlatformDriver> _platforms;
    private readonly IGameChannel _game;
    private readonly SimulatedFleet? _simulation;

    public DriverResolver(IReadOnlyDictionary<Platform, IPlatformDriver> platforms, IGameChannel game, SimulatedFleet? simulation)
    {
        _platforms = platforms;
        _game = game;
        _simulation = simulation;
    }

    public static DriverResolver Simulated(SimulatedFleet fleet) =>
        new(new Dictionary<Platform, IPlatformDriver>(), fleet, fleet);

    public IPlatformDriver PlatformFor(ClientDefinition client)
    {
        if (_simulation != null) return _simulation;
        return _platforms.TryGetValue(client.Platform, out var driver)
            ? driver
            : throw new InvalidOperationException($"No driver configured for platform {client.Platform}");
    }

    public IGameChannel GameFor(ClientDefinition client) => _simulation ?? _game;
}
