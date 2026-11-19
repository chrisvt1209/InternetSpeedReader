using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace ThroughputMonitor.Core.Ipc;

/// <summary>One command sent to the running agent (by the speedlog CLI or the web app).</summary>
/// <param name="Cmd">"mark", "status" or "live".</param>
public sealed record AgentRequest(string Cmd, string? Label = null, string? Category = null, string? Note = null);

/// <param name="Message">Text for people.</param>
/// <param name="Data">Optional JSON for programs (the "live" command returns a <see cref="LiveSnapshot"/> here).</param>
public sealed record AgentResponse(bool Ok, string Message, string? Data = null);

/// <summary>Shared contract of the local command channel (a named pipe, one JSON line each way).</summary>
public static class AgentIpc
{
    public const string DefaultPipeName = "ThroughputMonitorAgent";
    public const int MaxMessageChars = 8192;           // requests: small by design
    public const int MaxResponseChars = 262_144;       // responses may carry data, e.g. the live snapshot
    public const int MaxLabelChars = 200;
    public const int MaxCategoryChars = 50;
    public const int MaxNoteChars = 1000;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Reads one line, giving up on lines longer than <paramref name="maxChars"/>.</summary>
    public static async Task<string?> ReadLineLimitedAsync(StreamReader reader, int maxChars, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buffer = new char[1];
        while (await reader.ReadAsync(buffer.AsMemory(), ct) > 0)
        {
            if (buffer[0] == '\n') return sb.ToString().TrimEnd('\r');
            if (sb.Length >= maxChars) throw new InvalidDataException("Message too long");
            sb.Append(buffer[0]);
        }
        return sb.Length > 0 ? sb.ToString() : null;
    }

    /// <summary>Sends a request to the agent and returns its answer. Throws when the agent cannot be reached.</summary>
    public static async Task<AgentResponse> SendAsync(
        AgentRequest request, string pipeName, TimeSpan timeout, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(cts.Token);

        var utf8 = new UTF8Encoding(false);
        await using var writer = new StreamWriter(client, utf8, 1024, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(client, utf8, false, 1024, leaveOpen: true);

        await writer.WriteLineAsync(JsonSerializer.Serialize(request, Json).AsMemory(), cts.Token);
        string? line = await ReadLineLimitedAsync(reader, MaxResponseChars, cts.Token);
        return line is null
            ? new AgentResponse(false, "The agent closed the connection without an answer.")
            : JsonSerializer.Deserialize<AgentResponse>(line, Json) ?? new AgentResponse(false, "Unreadable answer from the agent.");
    }
}
