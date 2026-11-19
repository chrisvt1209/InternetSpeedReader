using Microsoft.Extensions.Options;
using ThroughputMonitor.Agent.Sampling;
using ThroughputMonitor.Agent.Sessions;
using ThroughputMonitor.Agent.Storage;
using ThroughputMonitor.Core;

namespace ThroughputMonitor.Agent;

public sealed class SamplingWorker(
    ICounterSource counters,
    ISystemLoadSource load,
    SampleWriter writer,
    IOptions<AgentOptions> options,
    TimeProvider time,
    ILogger<SamplingWorker> log) : BackgroundService
{
    private readonly AgentOptions _opt = options.Value;

    private readonly List<BufferedSample> _buffer = [];
    private readonly List<BufferedSample> _hold = [];       // active samples whose session has not started yet
    private readonly HashSet<Session> _dirty = [];
    private SessionDetector _detector = null!;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await writer.InitializeAsync(ct);
        var first = counters.Read();
        int adapterId = await writer.EnsureAdapterAsync(counters.AdapterName, counters.AdapterDescription, first.LinkSpeedBps, ct);
        _detector = new SessionDetector(
            DetectorOptions.FromMbps(_opt.ActiveThresholdMbps, _opt.StartAfterSeconds, _opt.EndAfterSeconds), adapterId);

        log.LogInformation("Monitoring {Adapter} ({Description}), link {Link} Mbps. Session starts at >= {Thr} Mbps for {Start}s, ends after {End}s below.",
            counters.AdapterName, counters.AdapterDescription, first.LinkSpeedBps / 1_000_000,
            _opt.ActiveThresholdMbps, _opt.StartAfterSeconds, _opt.EndAfterSeconds);

        var prev = first;
        var nextFlush = time.GetUtcNow().AddSeconds(_opt.FlushIntervalSeconds);
        var lastIdleStored = DateTimeOffset.MinValue;

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_opt.SampleIntervalMs), time);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                CounterReading cur;
                try { cur = counters.Read(); }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "Could not read adapter counters; skipping sample");
                    continue;
                }

                if (!RateCalculator.TryCompute(prev, cur, out double rx, out double tx, out double elapsed))
                {
                    log.LogWarning("Counter reset or zero interval; skipping sample");
                    prev = cur;
                    continue;
                }
                prev = cur;

                var sysLoad = load.Read();
                var now = time.GetUtcNow();
                var sample = new Sample
                {
                    AdapterId = adapterId,
                    TimestampUtc = now.UtcDateTime,
                    IntervalMs = (int)Math.Round(elapsed * 1000),
                    RxBytesPerSec = rx,
                    TxBytesPerSec = tx,
                    LinkSpeedBps = cur.LinkSpeedBps,
                    CpuPercent = sysLoad.CpuPercent,
                    DiskBytesPerSec = sysLoad.DiskBytesPerSec,
                };

                log.LogDebug("RX {Rx:F2} MB/s ({RxMbps:F0} Mbps)  TX {Tx:F2} MB/s  CPU {Cpu:F0}%",
                    rx / 1e6, rx * 8 / 1e6, tx / 1e6, sysLoad.CpuPercent);

                bool flushNow = HandleSample(sample, now, ref lastIdleStored);

                if (flushNow || now >= nextFlush)
                {
                    await FlushAsync(ct);
                    nextFlush = now.AddSeconds(_opt.FlushIntervalSeconds);
                }
            }
        }
        catch (OperationCanceledException) { }

        // Shutdown: close an open session and write everything that is left.
        var closed = _detector.Close();
        if (closed is not null)
        {
            _dirty.Add(closed);
            log.LogInformation("Session closed on shutdown: {Summary}", Describe(closed));
        }
        await FlushAsync(CancellationToken.None);
    }

    /// <summary>Routes a sample through the detector and the buffers. Returns true when a flush should happen now.</summary>
    private bool HandleSample(Sample sample, DateTimeOffset now, ref DateTimeOffset lastIdleStored)
    {
        var result = _detector.Process(sample);
        switch (result.State)
        {
            case SampleState.Pending:
                _hold.Add(new BufferedSample(sample, null));
                return false;

            case SampleState.Idle:
                _hold.Clear(); // a burst too short to be a session
                if (now - lastIdleStored >= TimeSpan.FromSeconds(_opt.IdleBaselineSeconds))
                {
                    _buffer.Add(new BufferedSample(sample, null));
                    lastIdleStored = now;
                }
                return false;

            default: // InSession
                var session = result.Session!;
                if (result.Event == SessionEvent.Started)
                {
                    foreach (var h in _hold) _buffer.Add(h with { Session = session });
                    _hold.Clear();
                    log.LogInformation("Session started at {Start:HH:mm:ss} UTC", session.StartUtc);
                }
                _buffer.Add(new BufferedSample(sample, session));
                _dirty.Add(session);

                if (result.Event == SessionEvent.Ended)
                {
                    log.LogInformation("Session ended: {Summary}", Describe(session));
                    return true;
                }
                return false;
        }
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        if (_buffer.Count == 0 && _dirty.Count == 0) return;
        try
        {
            await writer.FlushAsync(_buffer, _dirty, ct);
            _buffer.Clear();
            _dirty.Clear();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Database write failed; keeping {Count} samples and retrying", _buffer.Count);
            int excess = _buffer.Count - _opt.MaxBufferedSamples;
            if (excess > 0)
            {
                _buffer.RemoveRange(0, excess);
                log.LogWarning("Buffer full; dropped {Excess} oldest samples", excess);
            }
        }
    }

    private static string Describe(Session s) =>
        $"{(s.EndUtc!.Value - s.StartUtc).TotalSeconds:F0}s, " +
        $"avg RX {s.AvgRxBps * 8 / 1e6:F0} Mbps, peak RX {s.PeakRxBps * 8 / 1e6:F0} Mbps, " +
        $"{s.TotalRxBytes / 1e9:F2} GB down, {s.TotalTxBytes / 1e9:F2} GB up";
}
