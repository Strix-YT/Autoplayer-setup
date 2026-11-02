using System.Collections.Concurrent;
using ApControl.Core.Events;
using ApControl.Core.Models;

namespace ApControl.Core.Registry;

/// <summary>Which clients a command targets. Ids, groups and All are unioned.</summary>
public sealed record ClientSelector(
    IReadOnlyList<string>? ClientIds = null,
    IReadOnlyList<string>? Groups = null,
    bool All = false);

/// <summary>
/// Single source of truth for the fleet: static definitions, live state, and named groups.
/// Every state change is published to the event bus so the dashboard updates in real time.
/// </summary>
public sealed class ClientRegistry
{
    private readonly ConcurrentDictionary<string, ClientEntry> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, HashSet<string>> _groups = new(StringComparer.OrdinalIgnoreCase);
    private readonly EventBus _bus;

    public ClientRegistry(IEnumerable<ClientDefinition> definitions, EventBus bus)
    {
        _bus = bus;
        foreach (var def in definitions)
        {
            if (string.IsNullOrWhiteSpace(def.Id)) throw new ArgumentException("Client definition without Id");
            if (string.IsNullOrWhiteSpace(def.Name)) def.Name = def.Id;
            if (!_clients.TryAdd(def.Id, new ClientEntry(def)))
                throw new ArgumentException($"Duplicate client id '{def.Id}'");
            foreach (var g in def.Groups) AddToGroup(g, def.Id);
        }
    }

    public IReadOnlyList<ClientSnapshot> Snapshot(Func<string, bool>? isBusy = null) =>
        _clients.Values
            .Select(c => c.Snapshot(isBusy?.Invoke(c.Definition.Id) ?? false))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public ClientSnapshot? Get(string id, bool busy = false) =>
        _clients.TryGetValue(id, out var c) ? c.Snapshot(busy) : null;

    public ClientDefinition Definition(string id) =>
        _clients.TryGetValue(id, out var c) ? c.Definition : throw new KeyNotFoundException($"Unknown client '{id}'");

    public IEnumerable<string> AllIds => _clients.Keys;

    /// <summary>Set by the controller so every published snapshot carries the live "busy" flag.</summary>
    public Func<string, bool>? BusyProbe { get; set; }

    /// <summary>Publish the current snapshot even if no field changed (e.g. busy flag toggled).</summary>
    public void Publish(string id)
    {
        if (_clients.TryGetValue(id, out var c))
            _bus.Publish("client", id, "", c.Snapshot(BusyProbe?.Invoke(id) ?? false));
    }

    public ApState StateOf(string id) => _clients[id].State;

    /// <summary>Update state and publish a "client" event if anything changed.</summary>
    public void Update(string id, ApState? state = null, string? message = null,
        Optional<string?> experience = default, Optional<string?> build = default,
        Optional<string?> profile = default, bool seen = false, bool busy = false)
    {
        if (!_clients.TryGetValue(id, out var c)) return;
        bool changed;
        lock (c)
        {
            changed = c.Apply(state, message, experience, build, profile, seen);
        }
        if (changed)
            _bus.Publish("client", id, message ?? $"{id}: {c.State}", c.Snapshot(BusyProbe?.Invoke(id) ?? busy));
    }

    // ---- groups ----

    public IReadOnlyDictionary<string, IReadOnlyList<string>> Groups() =>
        _groups.ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Value.OrderBy(x => x).ToList(),
            StringComparer.OrdinalIgnoreCase);

    public void SetGroup(string name, IEnumerable<string> clientIds)
    {
        var ids = clientIds.Where(_clients.ContainsKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _groups[name] = ids;
        foreach (var c in _clients.Values)
        {
            lock (c)
            {
                c.Definition.Groups.RemoveAll(g => g.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (ids.Contains(c.Definition.Id)) c.Definition.Groups.Add(name);
            }
        }
        _bus.Publish("groups", null, $"Group '{name}' now has {ids.Count} clients");
    }

    public bool RemoveGroup(string name)
    {
        if (!_groups.TryRemove(name, out _)) return false;
        foreach (var c in _clients.Values)
            lock (c) c.Definition.Groups.RemoveAll(g => g.Equals(name, StringComparison.OrdinalIgnoreCase));
        _bus.Publish("groups", null, $"Group '{name}' removed");
        return true;
    }

    private void AddToGroup(string group, string id) =>
        _groups.GetOrAdd(group, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(id);

    /// <summary>Expand a selector into a distinct, ordered list of known client ids.</summary>
    public IReadOnlyList<string> Resolve(ClientSelector selector)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (selector.All) result.UnionWith(_clients.Keys);
        if (selector.ClientIds != null)
            result.UnionWith(selector.ClientIds.Where(_clients.ContainsKey));
        if (selector.Groups != null)
            foreach (var g in selector.Groups)
                if (_groups.TryGetValue(g, out var members)) result.UnionWith(members);
        return result.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private sealed class ClientEntry
    {
        public ClientEntry(ClientDefinition def) { Definition = def; StateChangedAt = DateTimeOffset.UtcNow; }
        public ClientDefinition Definition { get; }
        public ApState State { get; private set; } = ApState.Unknown;
        public string? Experience { get; private set; }
        public string? BuildVersion { get; private set; }
        public string? Profile { get; private set; }
        public string? LastMessage { get; private set; }
        public DateTimeOffset? LastSeen { get; private set; }
        public DateTimeOffset StateChangedAt { get; private set; }

        public bool Apply(ApState? state, string? message, Optional<string?> experience,
            Optional<string?> build, Optional<string?> profile, bool seen)
        {
            var changed = false;
            if (state is { } s && s != State) { State = s; StateChangedAt = DateTimeOffset.UtcNow; changed = true; }
            if (message != null && message != LastMessage) { LastMessage = message; changed = true; }
            if (experience.HasValue && experience.Value != Experience) { Experience = experience.Value; changed = true; }
            if (build.HasValue && build.Value != BuildVersion) { BuildVersion = build.Value; changed = true; }
            if (profile.HasValue && profile.Value != Profile) { Profile = profile.Value; changed = true; }
            if (seen) LastSeen = DateTimeOffset.UtcNow; // heartbeat alone doesn't spam events
            return changed;
        }

        public ClientSnapshot Snapshot(bool busy) => new(
            Definition.Id, Definition.Name, Definition.Platform, Definition.Address,
            Definition.Groups.ToList(), State, busy, Experience, BuildVersion, Profile,
            LastMessage, LastSeen, StateChangedAt);
    }
}

/// <summary>Distinguishes "not provided" from "set to null" for partial updates.</summary>
public readonly struct Optional<T>
{
    public Optional(T value) { Value = value; HasValue = true; }
    public bool HasValue { get; }
    public T Value { get; }
    public static implicit operator Optional<T>(T value) => new(value);
}
