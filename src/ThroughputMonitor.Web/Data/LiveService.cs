using System.Text.Json;
using Microsoft.Extensions.Options;
using ThroughputMonitor.Core.Ipc;

namespace ThroughputMonitor.Web.Data;

/// <summary>Either the agent's live snapshot or a message saying why there is none.</summary>
public sealed record LiveResult(LiveSnapshot? Snapshot, string? Error)
{
    public static LiveResult Ok(LiveSnapshot snapshot) => new(snapshot, null);
    public static LiveResult Fail(string error) => new(null, error);
}

/// <summary>Asks the running agent for what it is seeing right now (over the local command pipe).</summary>
public sealed class LiveService(IOptions<WebOptions> options)
{
    /// <summary>Short: when the agent is down the page should say so quickly instead of hanging.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    public async Task<LiveResult> GetAsync(CancellationToken ct = default)
    {
        string pipe = string.IsNullOrWhiteSpace(options.Value.PipeName) ? AgentIpc.DefaultPipeName : options.Value.PipeName;
        try
        {
            var response = await AgentIpc.SendAsync(new AgentRequest("live"), pipe, Timeout, ct);
            if (!response.Ok) return LiveResult.Fail(response.Message);
            if (string.IsNullOrEmpty(response.Data))
                return LiveResult.Fail("The agent answered without data. It may be an older version: update the service.");

            var snapshot = JsonSerializer.Deserialize<LiveSnapshot>(response.Data, AgentIpc.Json);
            return snapshot is null ? LiveResult.Fail("The agent sent an unreadable answer.") : LiveResult.Ok(snapshot);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // the page is going away: not an error
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException
                                      or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return LiveResult.Fail("The agent is not running, or not reachable. Start the service (Get-Service ThroughputMonitorAgent) and this page picks up by itself.");
        }
    }
}
