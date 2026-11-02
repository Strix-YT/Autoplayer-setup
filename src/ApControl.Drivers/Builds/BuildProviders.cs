using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ApControl.Core.Abstractions;
using ApControl.Core.Models;

namespace ApControl.Drivers.Builds;

public sealed class BuildSourceOptions
{
    /// <summary>"Static", "Folder" or "BuildBuddy".</summary>
    public string Provider { get; set; } = "Static";
    public List<StaticBuild> Static { get; set; } = new();
    public FolderBuildOptions Folder { get; set; } = new();
    public BuildBuddyOptions BuildBuddy { get; set; } = new();
}

public sealed class StaticBuild
{
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    public string Branch { get; set; } = "";
    public Dictionary<Platform, string> Packages { get; set; } = new();
}

/// <summary>Fixed list of builds from configuration — fine for the first playtests and for simulation.</summary>
public sealed class StaticBuildProvider : IBuildProvider
{
    private readonly List<StaticBuild> _builds;
    public StaticBuildProvider(IEnumerable<StaticBuild> builds) => _builds = builds.ToList();
    public string Name => "Static config";

    public Task<IReadOnlyList<BuildInfo>> ListBuildsAsync(Platform? platform, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<BuildInfo>>(_builds
            .SelectMany(b => b.Packages.Select(p => new BuildInfo(b.Id, b.Version, b.Branch, p.Key, p.Value)))
            .Where(b => platform is null || b.Platform == platform)
            .ToList());

    public async Task<BuildInfo?> ResolveAsync(string buildId, Platform platform, CancellationToken ct) =>
        (await ListBuildsAsync(platform, ct)).FirstOrDefault(b => b.Id.Equals(buildId, StringComparison.OrdinalIgnoreCase));
}

public sealed class FolderBuildOptions
{
    /// <summary>Root of the build share, e.g. \\builds\bf\qv. Each subfolder is a build id.</summary>
    public string Root { get; set; } = "";
    /// <summary>Relative package path per platform inside a build folder, e.g. "Xbox\\*.xvc".</summary>
    public Dictionary<Platform, string> PackagePattern { get; set; } = new();
    public string Branch { get; set; } = "";
    public int MaxBuilds { get; set; } = 20;
}

/// <summary>Scans a network share where each subfolder is a build (newest first).</summary>
public sealed class FolderBuildProvider : IBuildProvider
{
    private readonly FolderBuildOptions _options;
    public FolderBuildProvider(FolderBuildOptions options) => _options = options;
    public string Name => $"Folder {_options.Root}";

    public Task<IReadOnlyList<BuildInfo>> ListBuildsAsync(Platform? platform, CancellationToken ct)
    {
        var list = new List<BuildInfo>();
        if (!Directory.Exists(_options.Root)) return Task.FromResult<IReadOnlyList<BuildInfo>>(list);

        var dirs = new DirectoryInfo(_options.Root).GetDirectories()
            .OrderByDescending(d => d.CreationTimeUtc).Take(_options.MaxBuilds);
        foreach (var dir in dirs)
        foreach (var (plat, pattern) in _options.PackagePattern)
        {
            if (platform != null && plat != platform) continue;
            var sub = Path.GetDirectoryName(pattern) ?? "";
            var searchDir = Path.Combine(dir.FullName, sub);
            if (!Directory.Exists(searchDir)) continue;
            var file = Directory.EnumerateFileSystemEntries(searchDir, Path.GetFileName(pattern)).FirstOrDefault();
            if (file != null)
                list.Add(new BuildInfo(dir.Name, dir.Name, _options.Branch, plat, file, dir.CreationTimeUtc));
        }
        return Task.FromResult<IReadOnlyList<BuildInfo>>(list);
    }

    public async Task<BuildInfo?> ResolveAsync(string buildId, Platform platform, CancellationToken ct) =>
        (await ListBuildsAsync(platform, ct)).FirstOrDefault(b => b.Id.Equals(buildId, StringComparison.OrdinalIgnoreCase));
}

public sealed class BuildBuddyOptions
{
    public string BaseUrl { get; set; } = "";
    public string? ApiKey { get; set; }
    public string BuildsPath { get; set; } = "/api/builds";
    public string? Branch { get; set; }
}

/// <summary>
/// Integration point for Build Buddy.
///
/// ASSUMPTION (to be confirmed against Build Buddy's real interface): Build Buddy exposes
/// GET {BaseUrl}{BuildsPath}?platform=Xbox&amp;branch=... returning
/// [{ "id": "...", "version": "...", "branch": "...", "platform": "Xbox", "packagePath": "\\\\share\\...", "createdAt": "..." }]
/// If Build Buddy is a CLI or a .NET library instead, replace the body of FetchAsync — nothing
/// else in the system changes, because everything goes through <see cref="IBuildProvider"/>.
/// </summary>
public sealed class BuildBuddyBuildProvider : IBuildProvider
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly HttpClient _http;
    private readonly BuildBuddyOptions _options;

    public BuildBuddyBuildProvider(HttpClient http, BuildBuddyOptions options)
    {
        _http = http;
        _options = options;
        if (!string.IsNullOrWhiteSpace(options.BaseUrl)) _http.BaseAddress = new Uri(options.BaseUrl);
        if (!string.IsNullOrWhiteSpace(options.ApiKey)) _http.DefaultRequestHeaders.Add("X-Api-Key", options.ApiKey);
    }

    public string Name => "Build Buddy";

    public Task<IReadOnlyList<BuildInfo>> ListBuildsAsync(Platform? platform, CancellationToken ct) => FetchAsync(platform, ct);

    public async Task<BuildInfo?> ResolveAsync(string buildId, Platform platform, CancellationToken ct) =>
        (await FetchAsync(platform, ct)).FirstOrDefault(b => b.Id.Equals(buildId, StringComparison.OrdinalIgnoreCase));

    private async Task<IReadOnlyList<BuildInfo>> FetchAsync(Platform? platform, CancellationToken ct)
    {
        if (_http.BaseAddress is null) return Array.Empty<BuildInfo>();
        var query = new List<string>();
        if (platform != null) query.Add($"platform={platform}");
        if (!string.IsNullOrWhiteSpace(_options.Branch)) query.Add($"branch={Uri.EscapeDataString(_options.Branch)}");
        var url = _options.BuildsPath + (query.Count > 0 ? "?" + string.Join("&", query) : "");

        var builds = await _http.GetFromJsonAsync<List<BuildInfo>>(url, Json, ct);
        return builds ?? new List<BuildInfo>();
    }
}
