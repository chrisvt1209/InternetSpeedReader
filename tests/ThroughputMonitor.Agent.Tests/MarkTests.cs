using System.IO.Pipes;
using ThroughputMonitor.Agent.Commands;
using ThroughputMonitor.Core;
using ThroughputMonitor.Core.Ipc;

namespace ThroughputMonitor.Agent.Tests;

public class SessionMarkerTests
{
    private static readonly DateTime Now = new(2026, 6, 30, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    private static Session Running() => new() { StartUtc = Now.AddMinutes(-3) };

    [Fact]
    public void Mark_labels_the_running_session()
    {
        var marker = new SessionMarker(Lifetime);
        var session = Running();

        var r = marker.Mark(new AgentRequest("mark", "Steam download", "game update", "Patch 1.2"), session, Now, out var touched);

        Assert.True(r.Ok);
        Assert.Same(session, touched);
        Assert.Equal("Steam download", session.Label);
        Assert.Equal("game update", session.Category);
        Assert.Equal("Patch 1.2", session.Note);
    }

    [Fact]
    public void A_second_mark_only_changes_the_fields_it_gives()
    {
        var marker = new SessionMarker(Lifetime);
        var session = Running();
        marker.Mark(new AgentRequest("mark", "Backup", "backup", null), session, Now, out _);

        marker.Mark(new AgentRequest("mark", null, null, "Offsite copy"), session, Now, out _);

        Assert.Equal("Backup", session.Label);
        Assert.Equal("backup", session.Category);
        Assert.Equal("Offsite copy", session.Note);
    }

    [Fact]
    public void Without_a_running_session_the_mark_waits_and_goes_to_the_next_one()
    {
        var marker = new SessionMarker(Lifetime);

        var r = marker.Mark(new AgentRequest("mark", "Game update", "game update"), current: null, Now, out var touched);
        Assert.True(r.Ok);
        Assert.Null(touched);
        Assert.True(marker.HasPending(Now));

        var next = new Session { StartUtc = Now.AddMinutes(5) };
        Assert.True(marker.OnSessionStarted(next, Now.AddMinutes(5)));
        Assert.Equal("Game update", next.Label);
        Assert.Equal("game update", next.Category);
        Assert.False(marker.HasPending(Now.AddMinutes(5)));      // used up

        var later = new Session();
        Assert.False(marker.OnSessionStarted(later, Now.AddMinutes(6)));
        Assert.Null(later.Label);
    }

    [Fact]
    public void A_waiting_mark_expires()
    {
        var marker = new SessionMarker(Lifetime);
        marker.Mark(new AgentRequest("mark", "Old label"), null, Now, out _);

        var session = new Session();
        Assert.False(marker.OnSessionStarted(session, Now.AddMinutes(31)));
        Assert.Null(session.Label);
    }

    [Fact]
    public void Several_waiting_marks_are_merged_and_later_values_win()
    {
        var marker = new SessionMarker(Lifetime);
        marker.Mark(new AgentRequest("mark", "First", "cat"), null, Now, out _);
        marker.Mark(new AgentRequest("mark", "Second", null, "a note"), null, Now.AddMinutes(1), out _);

        var session = new Session();
        marker.OnSessionStarted(session, Now.AddMinutes(2));

        Assert.Equal("Second", session.Label);
        Assert.Equal("cat", session.Category);
        Assert.Equal("a note", session.Note);
    }

    [Fact]
    public void An_empty_mark_is_rejected_and_long_text_is_cut()
    {
        var marker = new SessionMarker(Lifetime);
        var session = Running();

        var empty = marker.Mark(new AgentRequest("mark", "  ", "", null), session, Now, out var touched);
        Assert.False(empty.Ok);
        Assert.Null(touched);

        marker.Mark(new AgentRequest("mark", new string('x', 500), new string('y', 100), new string('z', 5000)), session, Now, out _);
        Assert.Equal(AgentIpc.MaxLabelChars, session.Label!.Length);
        Assert.Equal(AgentIpc.MaxCategoryChars, session.Category!.Length);
        Assert.Equal(AgentIpc.MaxNoteChars, session.Note!.Length);
    }
}

public class CommandPipeTests
{
    [Fact]
    public async Task Command_travels_through_the_pipe_to_the_coordinator_and_the_answer_comes_back()
    {
        string pipeName = "tm-test-" + Guid.NewGuid().ToString("N");
        var coordinator = new MarkCoordinator(TimeProvider.System);
        var server = new PipeServerWorker(
            coordinator,
            Microsoft.Extensions.Options.Options.Create(new AgentOptions { PipeName = pipeName }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PipeServerWorker>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await server.StartAsync(cts.Token);

        // Plays the part of the sampling worker: answers the next command.
        var answerer = Task.Run(async () =>
        {
            PendingCommand? cmd = null;
            while (cmd is null && !cts.IsCancellationRequested)
            {
                if (coordinator.TryDequeue(out var c)) cmd = c;
                else await Task.Delay(20);
            }
            Assert.NotNull(cmd);
            cmd!.Result.SetResult(new AgentResponse(true, $"got {cmd.Request.Cmd}: {cmd.Request.Label}/{cmd.Request.Category}"));
        });

        var response = await AgentIpc.SendAsync(
            new AgentRequest("mark", "Steam download", "game update"), pipeName, TimeSpan.FromSeconds(5), cts.Token);
        await answerer;

        Assert.True(response.Ok);
        Assert.Equal("got mark: Steam download/game update", response.Message);

        await server.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Garbage_on_the_pipe_gets_an_error_answer_not_a_crash()
    {
        string pipeName = "tm-test-" + Guid.NewGuid().ToString("N");
        var server = new PipeServerWorker(
            new MarkCoordinator(TimeProvider.System),
            Microsoft.Extensions.Options.Options.Create(new AgentOptions { PipeName = pipeName }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PipeServerWorker>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await server.StartAsync(cts.Token);

        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(cts.Token);
        await using var writer = new StreamWriter(client, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(client, leaveOpen: true);
        await writer.WriteLineAsync("this is not json");
        string? line = await reader.ReadLineAsync(cts.Token);

        Assert.NotNull(line);
        Assert.Contains("\"ok\":false", line);

        await server.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Coordinator_answers_waiting_commands_when_the_agent_shuts_down()
    {
        var coordinator = new MarkCoordinator(TimeProvider.System);
        var waiting = coordinator.SubmitAsync(new AgentRequest("status"), TimeSpan.FromSeconds(10), CancellationToken.None);

        coordinator.CancelAll("shutting down");

        var response = await waiting;
        Assert.False(response.Ok);
        Assert.Equal("shutting down", response.Message);
    }
}
