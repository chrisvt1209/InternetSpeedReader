using System.Text.Json;
using ThroughputMonitor.Agent.Commands;
using ThroughputMonitor.Agent.Live;
using ThroughputMonitor.Agent.Processes;
using ThroughputMonitor.Core;
using ThroughputMonitor.Core.Ipc;

namespace ThroughputMonitor.Agent.Tests;

public class LiveBufferTests
{
    private static LivePoint P(int second) =>
        new(new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc).AddSeconds(second), second * 10, second);

    [Fact]
    public void Empty_buffer_has_no_latest_point()
    {
        var buffer = new LiveBuffer(5);

        Assert.Null(buffer.Latest);
        Assert.Empty(buffer.ToList());
    }

    [Fact]
    public void Points_come_back_oldest_first()
    {
        var buffer = new LiveBuffer(5);
        for (int i = 0; i < 3; i++) buffer.Add(P(i));

        Assert.Equal([0, 1, 2], buffer.ToList().Select(p => (int)p.Tx));
        Assert.Equal(2, buffer.Latest!.Tx);
    }

    [Fact]
    public void When_full_the_oldest_points_are_overwritten_and_order_is_kept()
    {
        var buffer = new LiveBuffer(5);
        for (int i = 0; i < 8; i++) buffer.Add(P(i));        // 0..7 into a ring of 5

        Assert.Equal(5, buffer.Count);
        Assert.Equal([3, 4, 5, 6, 7], buffer.ToList().Select(p => (int)p.Tx));
        Assert.Equal(7, buffer.Latest!.Tx);
    }
}

public class LiveSnapshotBuilderTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static ProcessTraffic Proc(string name, long rx, long tx = 0, int pid = 1) => new(pid, name, null, rx, tx, []);

    private static LiveBuffer BufferWith(double rx, double tx)
    {
        var b = new LiveBuffer(10);
        b.Add(new LivePoint(Now, rx, tx));
        return b;
    }

    [Fact]
    public void Current_rates_come_from_the_latest_point()
    {
        var snap = LiveSnapshotBuilder.Build(Now, BufferWith(125_000_000, 500_000), null, ProcessTrafficSnapshot.Empty, 1, true);

        Assert.Equal(125_000_000, snap.RxBps);
        Assert.Equal(500_000, snap.TxBps);
        Assert.Single(snap.Points);
        Assert.Null(snap.Session);
    }

    [Fact]
    public void Programs_are_grouped_converted_to_per_second_sorted_and_noise_is_dropped()
    {
        var traffic = new ProcessTrafficSnapshot([
            Proc("chrome", 2_000_000, pid: 1), Proc("chrome", 1_000_000, pid: 2),     // one program: 3 MB in 2 s
            Proc("steam", 10_000_000),
            Proc("tiny", 500),                                                         // below the noise floor
        ]);

        var snap = LiveSnapshotBuilder.Build(Now, BufferWith(0, 0), null, traffic, trafficSeconds: 2, processTracking: true);

        Assert.Equal(["steam", "chrome"], snap.Programs.Select(p => p.Name));
        Assert.Equal(5_000_000, snap.Programs[0].RxBps);          // 10 MB / 2 s
        Assert.Equal(1_500_000, snap.Programs[1].RxBps);          // 3 MB / 2 s
    }

    [Fact]
    public void Background_chatter_below_the_noise_floor_is_not_listed()
    {
        var traffic = new ProcessTrafficSnapshot([Proc("quiet", 4_000), Proc("busy", 6_000)]);   // floor is 5 kB/s

        var snap = LiveSnapshotBuilder.Build(Now, BufferWith(0, 0), null, traffic, 1, true);

        Assert.Equal("busy", Assert.Single(snap.Programs).Name);
    }

    [Fact]
    public void Only_the_busiest_programs_are_listed()
    {
        var traffic = new ProcessTrafficSnapshot(Enumerable.Range(1, 20).Select(i => Proc($"app{i}", i * 100_000L)).ToList());

        var snap = LiveSnapshotBuilder.Build(Now, BufferWith(0, 0), null, traffic, 1, true, maxPrograms: 5);

        Assert.Equal(5, snap.Programs.Count);
        Assert.Equal("app20", snap.Programs[0].Name);
    }

    [Fact]
    public void A_running_session_is_included_with_its_label_and_totals()
    {
        var session = new Session
        {
            Id = 7, StartUtc = Now.AddMinutes(-2), Label = "Steam download", Category = "game update",
            AvgRxBps = 100, PeakRxBps = 200, TotalRxBytes = 12_000, TotalTxBytes = 34,
        };

        var snap = LiveSnapshotBuilder.Build(Now, BufferWith(1, 1), session, ProcessTrafficSnapshot.Empty, 1, false);

        Assert.NotNull(snap.Session);
        Assert.Equal(7, snap.Session!.Id);
        Assert.Equal("Steam download", snap.Session.Label);
        Assert.Equal(12_000, snap.Session.TotalRxBytes);
        Assert.False(snap.ProcessTracking);
    }

    [Fact]
    public void Snapshot_survives_the_json_round_trip_used_on_the_pipe()
    {
        var snap = LiveSnapshotBuilder.Build(Now, BufferWith(5, 6), new Session { Id = 1, StartUtc = Now, Label = "x" },
            new ProcessTrafficSnapshot([Proc("steam", 40_000)]), 1, true);

        var json = JsonSerializer.Serialize(snap, AgentIpc.Json);
        var back = JsonSerializer.Deserialize<LiveSnapshot>(json, AgentIpc.Json)!;

        Assert.Equal(snap.NowUtc, back.NowUtc);
        Assert.Equal(snap.Points[0], back.Points[0]);
        Assert.Equal("steam", back.Programs[0].Name);
        Assert.Equal("x", back.Session!.Label);
        Assert.True(back.ProcessTracking);
    }
}

public class LiveIpcTests
{
    [Fact]
    public async Task A_large_data_answer_travels_through_the_pipe()
    {
        string pipeName = "tm-test-" + Guid.NewGuid().ToString("N");
        var coordinator = new MarkCoordinator(TimeProvider.System);
        var server = new PipeServerWorker(
            coordinator,
            Microsoft.Extensions.Options.Options.Create(new AgentOptions { PipeName = pipeName }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PipeServerWorker>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await server.StartAsync(cts.Token);

        string bigData = new string('x', 100_000);                    // far above the 8 KB limit for requests
        var answerer = Task.Run(async () =>
        {
            PendingCommand? cmd = null;
            while (cmd is null && !cts.IsCancellationRequested)
            {
                if (coordinator.TryDequeue(out var c)) cmd = c;
                else await Task.Delay(20);
            }
            cmd!.Result.SetResult(new AgentResponse(true, "live", bigData));
        });

        var response = await AgentIpc.SendAsync(new AgentRequest("live"), pipeName, TimeSpan.FromSeconds(5), cts.Token);
        await answerer;

        Assert.True(response.Ok);
        Assert.Equal(bigData, response.Data);

        await server.StopAsync(CancellationToken.None);
    }
}
