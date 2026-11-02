using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ApControl.Core.Abstractions;
using ApControl.Core.Models;

namespace ApControl.Drivers.Game;

public sealed class GameChannelOptions
{
    /// <summary>TCP port the in-game Autoplayer agent listens on.</summary>
    public int Port { get; set; } = 4711;
    public int TimeoutSeconds { get; set; } = 5;
}

/// <summary>
/// Talks to the in-game Autoplayer agent with newline-delimited JSON over TCP.
/// One request per connection keeps it stateless and robust to game restarts.
/// See docs/GAME_PROTOCOL.md for the contract the game side implements.
///
///   → {"cmd":"status"}
///   ← {"ok":true,"state":"InGame","experience":"conquest","build":"CL123456","message":null}
///   → {"cmd":"join","args":{"experience":"conquest"}}
///   ← {"ok":true}
/// </summary>
public sealed class TcpJsonGameChannel : IGameChannel
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly GameChannelOptions _options;

    public TcpJsonGameChannel(GameChannelOptions options) => _options = options;

    public async Task<CommandOutcome> SendAsync(ClientDefinition client, GameCommand command, CancellationToken ct)
    {
        try
        {
            var reply = await RequestAsync(client, new Request(command.Verb, command.Args), ct);
            if (reply is null) return CommandOutcome.Fail("No reply from game agent");
            return reply.Ok ? CommandOutcome.Ok(reply.Message) : CommandOutcome.Fail(reply.Message ?? "Game agent rejected command");
        }
        catch (Exception ex) when (ex is SocketException or IOException or TimeoutException or JsonException)
        {
            return CommandOutcome.Fail($"Game agent unreachable: {ex.Message}");
        }
    }

    public async Task<GameStatus?> QueryStatusAsync(ClientDefinition client, CancellationToken ct)
    {
        try
        {
            var reply = await RequestAsync(client, new Request("status", null), ct);
            if (reply is null || !reply.Ok || reply.State is null) return null;
            return new GameStatus(reply.State.Value, reply.Experience, reply.Build, reply.Message);
        }
        catch (Exception ex) when (ex is SocketException or IOException or TimeoutException or JsonException)
        {
            return null; // game not running / not answering
        }
    }

    private async Task<Reply?> RequestAsync(ClientDefinition client, Request request, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
        try
        {
            using var tcp = new TcpClient { NoDelay = true };
            await tcp.ConnectAsync(client.Address, client.GamePort ?? _options.Port, cts.Token);
            await using var stream = tcp.GetStream();

            var payload = JsonSerializer.Serialize(request, Json) + "\n";
            await stream.WriteAsync(Encoding.UTF8.GetBytes(payload), cts.Token);

            using var reader = new StreamReader(stream, Encoding.UTF8);
            var line = await reader.ReadLineAsync(cts.Token);
            return string.IsNullOrWhiteSpace(line) ? null : JsonSerializer.Deserialize<Reply>(line, Json);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"No answer from {client.Address} within {_options.TimeoutSeconds}s");
        }
    }

    private sealed record Request(string Cmd, IReadOnlyDictionary<string, string>? Args);

    private sealed record Reply(bool Ok, ApState? State, string? Experience, string? Build, string? Message);
}
