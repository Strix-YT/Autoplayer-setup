using System.Diagnostics;
using System.Text;
using ApControl.Core.Abstractions;
using ApControl.Core.Models;

namespace ApControl.Drivers.Cli;

/// <summary>A command-line invocation with {placeholders}.</summary>
public sealed class CliTemplate
{
    public string FileName { get; set; } = "";
    public string Arguments { get; set; } = "";
    /// <summary>Exit codes treated as success (default: 0).</summary>
    public List<int> SuccessExitCodes { get; set; } = new() { 0 };
    /// <summary>Optional: output must contain this text to count as success (e.g. ping checks).</summary>
    public string? SuccessContains { get; set; }
    public int TimeoutSeconds { get; set; } = 60;
}

/// <summary>
/// CLI templates per operation for one platform. Placeholders:
/// {address} {name} {id} {appId} {args} {packagePath} {buildVersion}
/// Bound from "ApControl:Platforms:{Platform}:Cli".
/// </summary>
public sealed class PlatformCliOptions
{
    public CliTemplate? Ping { get; set; }
    public CliTemplate? Install { get; set; }
    public CliTemplate? Launch { get; set; }
    public CliTemplate? Terminate { get; set; }
    public CliTemplate? Reboot { get; set; }
}

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, bool TimedOut);

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string fileName, string arguments, TimeSpan timeout, CancellationToken ct);
}

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(string fileName, string arguments, TimeSpan timeout, CancellationToken ct)
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) lock (stdout) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (stderr) stderr.AppendLine(e.Data); };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            if (ct.IsCancellationRequested) throw;
            return new ProcessResult(-1, stdout.ToString(), stderr.ToString(), TimedOut: true);
        }
        return new ProcessResult(proc.ExitCode, stdout.ToString(), stderr.ToString(), TimedOut: false);
    }
}

/// <summary>
/// Drives a devkit through its vendor CLI (Xbox GDK xb* tools, PS5 prospero-ctrl, PsExec for PC...).
/// All commands are configuration, so syntax can be corrected per SDK version without a rebuild.
/// </summary>
public sealed class CliPlatformDriver : IPlatformDriver
{
    private readonly PlatformCliOptions _cli;
    private readonly IProcessRunner _runner;
    private readonly string _platformName;

    public CliPlatformDriver(string platformName, PlatformCliOptions cli, IProcessRunner runner)
    {
        _platformName = platformName;
        _cli = cli;
        _runner = runner;
    }

    public async Task<bool> PingAsync(ClientDefinition client, CancellationToken ct)
    {
        if (_cli.Ping is null) return true; // no ping configured: assume reachable
        var result = await RunAsync(_cli.Ping, Vars(client), ct);
        return result.Success;
    }

    public Task<CommandOutcome> InstallBuildAsync(ClientDefinition client, BuildInfo build, CancellationToken ct) =>
        RunAsync(_cli.Install, Vars(client, new()
        {
            ["packagePath"] = build.PackagePath,
            ["buildVersion"] = build.Version
        }), ct);

    public Task<CommandOutcome> LaunchAsync(ClientDefinition client, LaunchSpec spec, CancellationToken ct) =>
        RunAsync(_cli.Launch, Vars(client, new() { ["appId"] = spec.AppId, ["args"] = spec.Arguments }), ct);

    public Task<CommandOutcome> TerminateAsync(ClientDefinition client, CancellationToken ct) =>
        RunAsync(_cli.Terminate, Vars(client), ct);

    public Task<CommandOutcome> RebootAsync(ClientDefinition client, CancellationToken ct) =>
        RunAsync(_cli.Reboot, Vars(client), ct);

    private async Task<CommandOutcome> RunAsync(CliTemplate? template, Dictionary<string, string> vars, CancellationToken ct)
    {
        if (template is null || string.IsNullOrWhiteSpace(template.FileName))
            return CommandOutcome.Fail($"Operation not configured for {_platformName}");

        var args = Expand(template.Arguments, vars);
        var result = await _runner.RunAsync(template.FileName, args, TimeSpan.FromSeconds(template.TimeoutSeconds), ct);
        if (result.TimedOut)
            return CommandOutcome.Fail($"{template.FileName} timed out after {template.TimeoutSeconds}s");

        var output = (result.StdOut + "\n" + result.StdErr).Trim();
        var ok = template.SuccessExitCodes.Contains(result.ExitCode)
                 && (template.SuccessContains is null || output.Contains(template.SuccessContains, StringComparison.OrdinalIgnoreCase));
        return ok
            ? CommandOutcome.Ok()
            : CommandOutcome.Fail($"{template.FileName} exit {result.ExitCode}: {LastLine(output)}");
    }

    public static string Expand(string template, IReadOnlyDictionary<string, string> vars)
    {
        var sb = new StringBuilder(template);
        foreach (var (key, value) in vars) sb.Replace("{" + key + "}", value);
        return sb.ToString();
    }

    private static Dictionary<string, string> Vars(ClientDefinition c, Dictionary<string, string>? extra = null)
    {
        var vars = new Dictionary<string, string>
        {
            ["address"] = c.Address,
            ["name"] = c.Name,
            ["id"] = c.Id
        };
        if (extra != null) foreach (var kv in extra) vars[kv.Key] = kv.Value;
        return vars;
    }

    private static string LastLine(string s)
    {
        var lines = s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? "(no output)" : lines[^1];
    }
}
