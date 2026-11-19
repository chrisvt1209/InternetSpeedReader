using System.Collections.Concurrent;
using ThroughputMonitor.Core.Ipc;

namespace ThroughputMonitor.Agent.Commands;

/// <summary>A command from the CLI waiting for the sampling worker to handle it.</summary>
public sealed record PendingCommand(AgentRequest Request, TaskCompletionSource<AgentResponse> Result, DateTime ExpiresUtc);

/// <summary>
/// Hands commands from the pipe server (any thread) to the sampling worker, which owns the
/// session state. The worker drains the queue once per tick and completes each command.
/// </summary>
public sealed class MarkCoordinator(TimeProvider time)
{
    private readonly ConcurrentQueue<PendingCommand> _queue = new();

    public async Task<AgentResponse> SubmitAsync(AgentRequest request, TimeSpan timeout, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<AgentResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Enqueue(new PendingCommand(request, tcs, time.GetUtcNow().UtcDateTime + timeout));
        try
        {
            return await tcs.Task.WaitAsync(timeout, ct);
        }
        catch (TimeoutException)
        {
            return new AgentResponse(false, "The agent did not answer in time (is it still starting up?).");
        }
    }

    public bool TryDequeue(out PendingCommand command) => _queue.TryDequeue(out command!);

    /// <summary>Answers everything still waiting (agent shutting down).</summary>
    public void CancelAll(string reason)
    {
        while (_queue.TryDequeue(out var c))
            c.Result.TrySetResult(new AgentResponse(false, reason));
    }
}
