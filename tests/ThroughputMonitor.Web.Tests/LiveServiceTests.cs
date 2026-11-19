using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ThroughputMonitor.Core.Ipc;
using ThroughputMonitor.Web.Data;

namespace ThroughputMonitor.Web.Tests;

public class LiveServiceTests
{
    private static LiveService NewService(string pipeName) =>
        new(Options.Create(new WebOptions { PipeName = pipeName }));

    private static string NewPipeName() => "tm-web-test-" + Guid.NewGuid().ToString("N");

    /// <summary>A stand-in agent: answers one request with the given response.</summary>
    private static Task<string?> FakeAgentAsync(string pipeName, AgentResponse response, CancellationToken ct) => Task.Run(async () =>
    {
        await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await server.WaitForConnectionAsync(ct);
        var utf8 = new UTF8Encoding(false);
        using var reader = new StreamReader(server, utf8, false, 1024, leaveOpen: true);
        await using var writer = new StreamWriter(server, utf8, 1024, leaveOpen: true) { AutoFlush = true };
        string? request = await reader.ReadLineAsync(ct);
        await writer.WriteLineAsync(JsonSerializer.Serialize(response, AgentIpc.Json).AsMemory(), ct);
        return request;
    }, ct);

    [Fact]
    public async Task Snapshot_from_the_agent_is_returned()
    {
        string pipe = NewPipeName();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var snapshot = new LiveSnapshot(now, 100_000_000, 2_000_000,
            [new LivePoint(now.AddSeconds(-1), 90_000_000, 1), new LivePoint(now, 100_000_000, 2)],
            [new LiveProgram("steam", 99_000_000, 10)],
            new LiveSession(5, now.AddMinutes(-1), "Steam download", null, 1, 2, 3, 4), true);
        var agent = FakeAgentAsync(pipe, new AgentResponse(true, "live", JsonSerializer.Serialize(snapshot, AgentIpc.Json)), cts.Token);

        var result = await NewService(pipe).GetAsync(cts.Token);
        string? sentRequest = await agent;

        Assert.Null(result.Error);
        Assert.NotNull(result.Snapshot);
        Assert.Equal(100_000_000, result.Snapshot!.RxBps);
        Assert.Equal(2, result.Snapshot.Points.Count);
        Assert.Equal("steam", result.Snapshot.Programs[0].Name);
        Assert.Equal("Steam download", result.Snapshot.Session!.Label);
        Assert.Contains("\"cmd\":\"live\"", sentRequest);               // the web app asked for the live command
    }

    [Fact]
    public async Task Missing_agent_gives_a_clear_error_quickly()
    {
        var started = DateTime.UtcNow;

        var result = await NewService(NewPipeName()).GetAsync();

        Assert.Null(result.Snapshot);
        Assert.Contains("not running", result.Error);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(6), "must not hang when the agent is down");
    }

    [Fact]
    public async Task Answer_without_data_is_reported_as_an_old_agent()
    {
        string pipe = NewPipeName();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var agent = FakeAgentAsync(pipe, new AgentResponse(false, "Unknown command 'live'."), cts.Token);

        var result = await NewService(pipe).GetAsync(cts.Token);
        await agent;

        Assert.Null(result.Snapshot);
        Assert.Contains("Unknown command", result.Error);
    }

    [Fact]
    public async Task Cancelling_the_page_is_not_reported_as_an_error()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NewService(NewPipeName()).GetAsync(cts.Token));
    }
}
