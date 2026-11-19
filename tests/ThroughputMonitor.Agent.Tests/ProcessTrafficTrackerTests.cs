using ThroughputMonitor.Agent.Processes;

namespace ThroughputMonitor.Agent.Tests;

public class ProcessTrafficTrackerTests
{
    private const long MB = 1_000_000;

    private static ProcessTraffic P(string name, long rx, long tx = 0, int pid = 1, string? path = null, params RemoteTraffic[] remotes) =>
        new(pid, name, path ?? $@"C:\apps\{name}.exe", rx, tx, remotes.Length > 0 ? remotes : [new RemoteTraffic("1.1.1.1", rx, tx)]);

    private static ProcessTrafficSnapshot Snap(params ProcessTraffic[] p) => new(p);

    [Fact]
    public void Totals_are_summed_per_program_and_sorted_by_volume()
    {
        var t = new ProcessTrafficTracker(20);
        t.AddActive(Snap(P("steam", 100 * MB), P("chrome", 5 * MB)), 1);
        t.AddActive(Snap(P("steam", 90 * MB), P("chrome", 6 * MB)), 1);

        var (procs, _) = t.Finish();

        Assert.Equal(["steam", "chrome"], procs.Select(p => p.ProcessName));
        Assert.Equal(190 * MB, procs[0].RxBytes);
        Assert.Equal(11 * MB, procs[1].RxBytes);
    }

    [Fact]
    public void Several_pids_of_one_program_are_one_row_and_the_peak_is_per_program()
    {
        var t = new ProcessTrafficTracker(20);
        t.AddActive(Snap(P("chrome", 30 * MB, pid: 1), P("chrome", 20 * MB, pid: 2)), 1);
        t.AddActive(Snap(P("chrome", 10 * MB, pid: 1)), 1);

        var (procs, _) = t.Finish();

        var chrome = Assert.Single(procs);
        Assert.Equal(60 * MB, chrome.RxBytes);
        Assert.Equal(50 * MB, chrome.PeakRxBps, 1);        // 30 + 20 in the same second
    }

    [Fact]
    public void Peak_rate_uses_the_interval_length()
    {
        var t = new ProcessTrafficTracker(20);
        t.AddActive(Snap(P("steam", 110 * MB)), 1.1);       // 110 MB over 1.1 s = 100 MB/s

        var (procs, _) = t.Finish();

        Assert.Equal(100 * MB, procs[0].PeakRxBps, 1);
    }

    [Fact]
    public void Quiet_tail_is_excluded_but_a_dip_inside_the_session_is_kept()
    {
        var t = new ProcessTrafficTracker(20);
        t.AddActive(Snap(P("steam", 100 * MB)), 1);
        t.AddQuiet(Snap(P("steam", 1 * MB)), 1);             // dip ...
        t.AddActive(Snap(P("steam", 100 * MB)), 1);          // ... traffic resumes: dip is merged in
        t.AddQuiet(Snap(P("steam", 2 * MB)), 1);             // tail at the end: dropped

        var (procs, _) = t.Finish();

        Assert.Equal(201 * MB, procs[0].RxBytes);
    }

    [Fact]
    public void Remotes_are_summed_per_program_and_limited_to_the_largest()
    {
        var t = new ProcessTrafficTracker(maxRemotesPerProcess: 2);
        t.AddActive(Snap(P("steam", 60 * MB, 0, 1, null,
            new RemoteTraffic("10.0.0.1", 50 * MB, 0),
            new RemoteTraffic("10.0.0.2", 8 * MB, 0),
            new RemoteTraffic("10.0.0.3", 2 * MB, 0))), 1);
        t.AddActive(Snap(P("steam", 50 * MB, 0, 1, null, new RemoteTraffic("10.0.0.1", 50 * MB, 0))), 1);

        var (_, remotes) = t.Finish();

        Assert.Equal(2, remotes.Count);
        Assert.Equal("10.0.0.1", remotes[0].RemoteIp);
        Assert.Equal(100 * MB, remotes[0].RxBytes);
        Assert.Equal("10.0.0.2", remotes[1].RemoteIp);
    }

    [Fact]
    public void Finish_resets_the_tracker_for_the_next_session()
    {
        var t = new ProcessTrafficTracker(20);
        t.AddActive(Snap(P("steam", 100 * MB)), 1);
        t.Finish();

        t.AddActive(Snap(P("chrome", 5 * MB)), 1);
        var (procs, _) = t.Finish();

        Assert.Equal("chrome", Assert.Single(procs).ProcessName);
    }

    [Fact]
    public void Per_second_rows_only_include_processes_above_the_threshold()
    {
        var ts = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var snap = Snap(P("steam", 80 * MB, 1 * MB, pid: 10), P("tiny", 5_000, 0, pid: 11));

        var rows = ProcessTrafficTracker.BuildSamples(snap, 1, ts, minBytesPerSec: 10_000);

        var row = Assert.Single(rows);
        Assert.Equal("steam", row.ProcessName);
        Assert.Equal(10, row.Pid);
        Assert.Equal(80 * MB, row.RxBytesPerSec, 1);
        Assert.Equal(1 * MB, row.TxBytesPerSec, 1);
        Assert.Equal(ts, row.TimestampUtc);
    }

    [Fact]
    public void Empty_snapshots_produce_empty_totals()
    {
        var t = new ProcessTrafficTracker(20);
        t.AddActive(ProcessTrafficSnapshot.Empty, 1);

        var (procs, remotes) = t.Finish();

        Assert.Empty(procs);
        Assert.Empty(remotes);
    }
}
