using ThroughputMonitor.Core;

namespace ThroughputMonitor.Agent.Sessions;

public sealed record DetectorOptions(double ActiveThresholdBytesPerSec, double StartAfterSeconds, double EndAfterSeconds)
{
    public static DetectorOptions FromMbps(double thresholdMbps, double startAfter, double endAfter) =>
        new(thresholdMbps * 1_000_000 / 8, startAfter, endAfter);
}

public enum SampleState
{
    /// <summary>Not in a session; the sample is not part of one.</summary>
    Idle,
    /// <summary>Active, but not yet long enough to count as a session. Caller should hold the sample.</summary>
    Pending,
    /// <summary>The sample belongs to <see cref="DetectorResult.Session"/>.</summary>
    InSession,
}

public enum SessionEvent { None, Started, Ended }

/// <param name="Active">Whether this sample itself was at or above the activity threshold.</param>
public readonly record struct DetectorResult(SampleState State, SessionEvent Event, Session? Session, bool Active = false);

/// <summary>
/// State machine that turns a stream of 1 s samples into sessions.
/// Start: active for <c>StartAfterSeconds</c> in a row. End: quiet for <c>EndAfterSeconds</c> in a row.
/// Statistics only cover time up to the last active sample, so the quiet tail never dilutes averages.
/// </summary>
public sealed class SessionDetector(DetectorOptions options, int adapterId)
{
    private readonly List<Sample> _pending = [];
    private double _pendingSeconds;

    private Session? _session;
    private readonly Accumulator _main = new();
    private readonly Accumulator _tail = new();
    private double _quietSeconds;
    private DateTime _lastActiveUtc;

    public Session? Current => _session;

    /// <summary>Samples seen while a start is still pending (the caller may need to hold these back).</summary>
    public IReadOnlyList<Sample> PendingSamples => _pending;

    public DetectorResult Process(Sample s)
    {
        double seconds = s.IntervalMs / 1000.0;
        bool active = Math.Max(s.RxBytesPerSec, s.TxBytesPerSec) >= options.ActiveThresholdBytesPerSec;

        if (_session is null)
        {
            if (!active)
            {
                _pending.Clear();
                _pendingSeconds = 0;
                return new(SampleState.Idle, SessionEvent.None, null);
            }

            _pending.Add(s);
            _pendingSeconds += seconds;
            if (_pendingSeconds < options.StartAfterSeconds)
                return new(SampleState.Pending, SessionEvent.None, null, true);

            return Start(s);
        }

        if (active)
        {
            _main.Merge(_tail);
            _tail.Reset();
            _main.Add(s, seconds);
            _lastActiveUtc = s.TimestampUtc;
            _quietSeconds = 0;
            Apply();
            return new(SampleState.InSession, SessionEvent.None, _session, true);
        }

        _tail.Add(s, seconds);
        _quietSeconds += seconds;
        if (_quietSeconds >= options.EndAfterSeconds)
        {
            var ended = End();
            return new(SampleState.InSession, SessionEvent.Ended, ended, false);
        }
        return new(SampleState.InSession, SessionEvent.None, _session, false);
    }

    /// <summary>Ends the open session (e.g. on shutdown). Returns it, or null when none was open.</summary>
    public Session? Close()
    {
        _pending.Clear();
        _pendingSeconds = 0;
        return _session is null ? null : End();
    }

    private DetectorResult Start(Sample current)
    {
        var first = _pending[0];
        _session = new Session
        {
            AdapterId = adapterId,
            StartUtc = first.TimestampUtc.AddMilliseconds(-first.IntervalMs),
            Source = SessionSource.Auto,
        };
        _main.Reset();
        _tail.Reset();
        foreach (var p in _pending) _main.Add(p, p.IntervalMs / 1000.0);
        _pending.Clear();
        _pendingSeconds = 0;
        _quietSeconds = 0;
        _lastActiveUtc = current.TimestampUtc;
        Apply();
        return new(SampleState.InSession, SessionEvent.Started, _session, true);
    }

    private Session End()
    {
        var ended = _session!;
        Apply();
        ended.EndUtc = _lastActiveUtc;
        _session = null;
        _main.Reset();
        _tail.Reset();
        _quietSeconds = 0;
        return ended;
    }

    private void Apply()
    {
        var s = _session!;
        double secs = Math.Max(_main.Seconds, 0.001);
        s.PeakRxBps = _main.PeakRx;
        s.PeakTxBps = _main.PeakTx;
        s.TotalRxBytes = (long)Math.Round(_main.RxBytes);
        s.TotalTxBytes = (long)Math.Round(_main.TxBytes);
        s.AvgRxBps = _main.RxBytes / secs;
        s.AvgTxBps = _main.TxBytes / secs;
        s.MinLinkSpeedBps = _main.MinLinkSpeedBps;
        s.AvgCpuPercent = _main.CpuSeconds / secs;
        s.PeakCpuPercent = _main.PeakCpu;
        s.AvgDiskBytesPerSec = _main.DiskSeconds / secs;
        s.PeakDiskBytesPerSec = _main.PeakDisk;
    }

    private sealed class Accumulator
    {
        public double Seconds, RxBytes, TxBytes, CpuSeconds, DiskSeconds;
        public double PeakRx, PeakTx, PeakCpu, PeakDisk;
        public long MinLinkSpeedBps;

        public void Reset()
        {
            Seconds = RxBytes = TxBytes = CpuSeconds = DiskSeconds = 0;
            PeakRx = PeakTx = PeakCpu = PeakDisk = 0;
            MinLinkSpeedBps = 0;
        }

        public void Add(Sample s, double seconds)
        {
            Seconds += seconds;
            RxBytes += s.RxBytesPerSec * seconds;
            TxBytes += s.TxBytesPerSec * seconds;
            CpuSeconds += s.CpuPercent * seconds;
            DiskSeconds += s.DiskBytesPerSec * seconds;
            PeakRx = Math.Max(PeakRx, s.RxBytesPerSec);
            PeakTx = Math.Max(PeakTx, s.TxBytesPerSec);
            PeakCpu = Math.Max(PeakCpu, s.CpuPercent);
            PeakDisk = Math.Max(PeakDisk, s.DiskBytesPerSec);
            TrackLink(s.LinkSpeedBps);
        }

        public void Merge(Accumulator o)
        {
            if (o.Seconds == 0) return;
            Seconds += o.Seconds;
            RxBytes += o.RxBytes;
            TxBytes += o.TxBytes;
            CpuSeconds += o.CpuSeconds;
            DiskSeconds += o.DiskSeconds;
            PeakCpu = Math.Max(PeakCpu, o.PeakCpu);
            PeakDisk = Math.Max(PeakDisk, o.PeakDisk);
            PeakRx = Math.Max(PeakRx, o.PeakRx);
            PeakTx = Math.Max(PeakTx, o.PeakTx);
            TrackLink(o.MinLinkSpeedBps);
        }

        private void TrackLink(long bps)
        {
            if (bps <= 0) return;
            MinLinkSpeedBps = MinLinkSpeedBps == 0 ? bps : Math.Min(MinLinkSpeedBps, bps);
        }
    }
}
