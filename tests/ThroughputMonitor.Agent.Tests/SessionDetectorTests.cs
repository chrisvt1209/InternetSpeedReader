using ThroughputMonitor.Agent.Sessions;
using ThroughputMonitor.Core;

namespace ThroughputMonitor.Agent.Tests;

public class SessionDetectorTests
{
    private const double MB = 1_000_000;                    // bytes per second
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    // threshold 5 Mbps = 625 kB/s, start after 3 s, end after 10 s
    private static SessionDetector NewDetector() => new(DetectorOptions.FromMbps(5, 3, 10), adapterId: 1);

    private int _second;
    private Sample S(double rx, double tx = 0, double cpu = 10, double disk = 0, long link = 1_000_000_000) => new()
    {
        TimestampUtc = T0.AddSeconds(++_second),
        IntervalMs = 1000,
        RxBytesPerSec = rx,
        TxBytesPerSec = tx,
        CpuPercent = cpu,
        DiskBytesPerSec = disk,
        LinkSpeedBps = link,
    };

    [Fact]
    public void Idle_traffic_never_starts_a_session()
    {
        var d = NewDetector();
        for (int i = 0; i < 30; i++)
            Assert.Equal(SampleState.Idle, d.Process(S(1_000)).State);
        Assert.Null(d.Current);
    }

    [Fact]
    public void Burst_shorter_than_start_time_is_not_a_session()
    {
        var d = NewDetector();
        Assert.Equal(SampleState.Pending, d.Process(S(100 * MB)).State);
        Assert.Equal(SampleState.Pending, d.Process(S(100 * MB)).State);
        Assert.Equal(SampleState.Idle, d.Process(S(0)).State);
        Assert.Null(d.Current);
    }

    [Fact]
    public void Session_starts_after_three_active_seconds_and_covers_the_pending_samples()
    {
        var d = NewDetector();
        d.Process(S(100 * MB));
        d.Process(S(100 * MB));
        var r = d.Process(S(100 * MB));

        Assert.Equal(SampleState.InSession, r.State);
        Assert.Equal(SessionEvent.Started, r.Event);
        Assert.Equal(T0, r.Session!.StartUtc);                 // first active sample ended at T0+1s and lasted 1 s
        Assert.Equal(300 * MB, r.Session.TotalRxBytes, 1);     // all three seconds are counted
        Assert.Equal(100 * MB, r.Session.AvgRxBps, 1);
        Assert.Equal(1, r.Session.AdapterId);
    }

    [Fact]
    public void Tx_alone_also_counts_as_activity()
    {
        var d = NewDetector();
        d.Process(S(0, 50 * MB));
        d.Process(S(0, 50 * MB));
        var r = d.Process(S(0, 50 * MB));

        Assert.Equal(SessionEvent.Started, r.Event);
        Assert.Equal(150 * MB, r.Session!.TotalTxBytes, 1);
    }

    [Fact]
    public void Session_ends_after_ten_quiet_seconds_and_the_quiet_tail_is_excluded()
    {
        var d = NewDetector();
        for (int i = 0; i < 5; i++) d.Process(S(100 * MB));    // 5 active seconds

        DetectorResult last = default;
        for (int i = 0; i < 9; i++)
        {
            last = d.Process(S(1_000));
            Assert.Equal(SessionEvent.None, last.Event);        // still open
        }
        last = d.Process(S(1_000));                              // 10th quiet second

        Assert.Equal(SessionEvent.Ended, last.Event);
        Assert.Null(d.Current);

        var s = last.Session!;
        Assert.Equal(T0.AddSeconds(5), s.EndUtc);                // end = last active sample
        Assert.Equal(500 * MB, s.TotalRxBytes, 1);               // tail bytes not counted
        Assert.Equal(100 * MB, s.AvgRxBps, 1);                   // average not diluted by the tail
    }

    [Fact]
    public void Short_dip_inside_a_session_does_not_end_it_and_is_counted()
    {
        var d = NewDetector();
        for (int i = 0; i < 3; i++) d.Process(S(100 * MB));     // start
        for (int i = 0; i < 4; i++) d.Process(S(0));             // 4 quiet seconds
        var r = d.Process(S(100 * MB));                          // traffic resumes

        Assert.Equal(SessionEvent.None, r.Event);
        Assert.NotNull(d.Current);
        Assert.Equal(400 * MB, r.Session!.TotalRxBytes, 1);      // 4 active seconds
        Assert.Equal(400 * MB / 8, r.Session.AvgRxBps, 1);       // spread over 8 s (dip is part of the session)
    }

    [Fact]
    public void Peaks_cpu_disk_and_min_link_speed_are_tracked()
    {
        var d = NewDetector();
        d.Process(S(80 * MB, 1 * MB, cpu: 20, disk: 10 * MB, link: 1_000_000_000));
        d.Process(S(120 * MB, 3 * MB, cpu: 60, disk: 50 * MB, link: 100_000_000));
        var r = d.Process(S(100 * MB, 2 * MB, cpu: 40, disk: 30 * MB, link: 1_000_000_000));

        var s = r.Session!;
        Assert.Equal(120 * MB, s.PeakRxBps, 1);
        Assert.Equal(3 * MB, s.PeakTxBps, 1);
        Assert.Equal(60, s.PeakCpuPercent, 3);
        Assert.Equal(40, s.AvgCpuPercent, 3);
        Assert.Equal(50 * MB, s.PeakDiskBytesPerSec, 1);
        Assert.Equal(30 * MB, s.AvgDiskBytesPerSec, 1);
        Assert.Equal(100_000_000, s.MinLinkSpeedBps);            // link dropped to 100 Mbps at some point
    }

    [Fact]
    public void Close_ends_an_open_session_and_returns_it()
    {
        var d = NewDetector();
        for (int i = 0; i < 4; i++) d.Process(S(100 * MB));

        var closed = d.Close();

        Assert.NotNull(closed);
        Assert.Equal(T0.AddSeconds(4), closed!.EndUtc);
        Assert.Null(d.Current);
        Assert.Null(d.Close());
    }

    [Fact]
    public void A_new_session_can_start_after_the_previous_one_ended()
    {
        var d = NewDetector();
        for (int i = 0; i < 3; i++) d.Process(S(100 * MB));
        for (int i = 0; i < 10; i++) d.Process(S(0));
        Assert.Null(d.Current);

        for (int i = 0; i < 2; i++) d.Process(S(50 * MB));
        var r = d.Process(S(50 * MB));

        Assert.Equal(SessionEvent.Started, r.Event);
        Assert.Equal(150 * MB, r.Session!.TotalRxBytes, 1);      // not mixed with the first session
    }
}
