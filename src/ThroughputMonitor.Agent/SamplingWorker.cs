using Microsoft.Extensions.Options;
using ThroughputMonitor.Agent.Commands;
using ThroughputMonitor.Agent.Processes;
using ThroughputMonitor.Agent.Sampling;
using ThroughputMonitor.Agent.Sessions;
using ThroughputMonitor.Agent.Storage;
using ThroughputMonitor.Core;
using ThroughputMonitor.Core.Ipc;

namespace ThroughputMonitor.Agent;

public sealed class SamplingWorker(
    ICounterSource counters,
    ISystemLoadSource load,
    IProcessTrafficSource processes,
    SampleWriter writer,
    MarkCoordinator coordinator,
    IOptions<AgentOptions> options,
    TimeProvider time,
    ILogger<SamplingWorker> log) : BackgroundService
{
    private readonly AgentOptions _opt = options.Value;
    private readonly SessionMarker _marker = new(TimeSpan.FromMinutes(options.Value.PendingMarkMinutes));
    private double _lastRx, _lastTx;

    private readonly List<BufferedSample> _buffer = [];
    private readonly List<BufferedProcessSample> _processBuffer = [];
    private readonly List<Held> _hold = [];                 // active seconds whose session has not started yet
    private readonly HashSet<Session> _dirty = [];
    private SessionDetector _detector = null!;
    private ProcessTrafficTracker _tracker = null!;

    private sealed record Held(BufferedSample Sample, ProcessTrafficSnapshot Traffic, double Seconds);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await writer.InitializeAsync(ct);
        var first = counters.Read();
        int adapterId = await writer.EnsureAdapterAsync(counters.AdapterName, counters.AdapterDescription, first.LinkSpeedBps, ct);
        _detector = new SessionDetector(
            DetectorOptions.FromMbps(_opt.ActiveThresholdMbps, _opt.StartAfterSeconds, _opt.EndAfterSeconds), adapterId);
        _tracker = new ProcessTrafficTracker(_opt.MaxRemotesPerProcess);

        log.LogInformation("Monitoring {Adapter} ({Description}), link {Link} Mbps. Session starts at >= {Thr} Mbps for {Start}s, ends after {End}s below.",
            counters.AdapterName, counters.AdapterDescription, first.LinkSpeedBps / 1_000_000,
            _opt.ActiveThresholdMbps, _opt.StartAfterSeconds, _opt.EndAfterSeconds);

        var prev = first;
        processes.Drain(); // discard anything collected before the first interval
        var nextFlush = time.GetUtcNow().AddSeconds(_opt.FlushIntervalSeconds);
        var lastIdleStored = DateTimeOffset.MinValue;

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_opt.SampleIntervalMs), time);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                HandleCommands(time.GetUtcNow().UtcDateTime);   // speedlog mark / status

                CounterReading cur;
                try { cur = counters.Read(); }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "Could not read adapter counters; skipping sample");
                    continue;
                }

                var traffic = processes.Drain();   // drained every tick so it always matches this interval

                if (!RateCalculator.TryCompute(prev, cur, out double rx, out double tx, out double elapsed))
                {
                    log.LogWarning("Counter reset or zero interval; skipping sample");
                    prev = cur;
                    continue;
                }
                prev = cur;
                _lastRx = rx;
                _lastTx = tx;

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

                if (log.IsEnabled(LogLevel.Debug))
                {
                    var top = traffic.Processes.OrderByDescending(p => p.RxBytes + p.TxBytes).FirstOrDefault();
                    log.LogDebug("RX {Rx:F2} MB/s ({RxMbps:F0} Mbps)  TX {Tx:F2} MB/s  CPU {Cpu:F0}%  top: {Top}",
                        rx / 1e6, rx * 8 / 1e6, tx / 1e6, sysLoad.CpuPercent, top?.Name ?? "-");
                }

                bool flushNow = HandleSample(sample, traffic, now, ref lastIdleStored);

                if (flushNow || now >= nextFlush)
                {
                    await FlushAsync(ct);
                    nextFlush = now.AddSeconds(_opt.FlushIntervalSeconds);
                }
            }
        }
        catch (OperationCanceledException) { }

        // Shutdown: close an open session and write everything that is left.
        coordinator.CancelAll("The agent is shutting down.");
        var closed = _detector.Close();
        if (closed is not null)
        {
            FinishSession(closed);
            _dirty.Add(closed);
            log.LogInformation("Session closed on shutdown: {Summary}", Describe(closed));
        }
        await FlushAsync(CancellationToken.None);
    }

    /// <summary>Routes a sample through the detector and the buffers. Returns true when a flush should happen now.</summary>
    private bool HandleSample(Sample sample, ProcessTrafficSnapshot traffic, DateTimeOffset now, ref DateTimeOffset lastIdleStored)
    {
        double seconds = sample.IntervalMs / 1000.0;
        var result = _detector.Process(sample);
        switch (result.State)
        {
            case SampleState.Pending:
                _hold.Add(new Held(new BufferedSample(sample, null), traffic, seconds));
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
                    _tracker.Reset();
                    foreach (var h in _hold)
                    {
                        _buffer.Add(h.Sample with { Session = session });
                        AddProcessSamples(h.Traffic, h.Seconds, h.Sample.Sample.TimestampUtc, session);
                        _tracker.AddActive(h.Traffic, h.Seconds);
                    }
                    _hold.Clear();
                    log.LogInformation("Session started at {Start:HH:mm:ss} UTC", session.StartUtc);
                    if (_marker.OnSessionStarted(session, now.UtcDateTime))
                        log.LogInformation("Applied the waiting mark to the new session: {Labels}", SessionMarker.Summary(session));
                }

                _buffer.Add(new BufferedSample(sample, session));
                AddProcessSamples(traffic, seconds, sample.TimestampUtc, session);
                if (result.Active) _tracker.AddActive(traffic, seconds);
                else _tracker.AddQuiet(traffic, seconds);
                _dirty.Add(session);

                if (result.Event == SessionEvent.Ended)
                {
                    FinishSession(session);
                    log.LogInformation("Session ended: {Summary}", Describe(session));
                    return true;
                }
                return false;
        }
    }

    /// <summary>Answers commands that arrived through the pipe. Runs on this worker so session state is never touched from another thread.</summary>
    private void HandleCommands(DateTime nowUtc)
    {
        while (coordinator.TryDequeue(out var command))
        {
            if (command.ExpiresUtc < nowUtc)
            {
                command.Result.TrySetResult(new AgentResponse(false, "The command expired before the agent could handle it."));
                continue;
            }

            AgentResponse response;
            switch (command.Request.Cmd.ToLowerInvariant())
            {
                case "mark":
                    response = _marker.Mark(command.Request, _detector?.Current, nowUtc, out var touched);
                    if (touched is not null) _dirty.Add(touched);   // saved with the next flush
                    log.LogInformation("Mark command: {Message}", response.Message);
                    break;
                case "status":
                    response = new AgentResponse(true, Status(nowUtc));
                    break;
                default:
                    response = new AgentResponse(false, $"Unknown command '{command.Request.Cmd}'.");
                    break;
            }
            command.Result.TrySetResult(response);
        }
    }

    private string Status(DateTime nowUtc)
    {
        string rates = $"Now: RX {_lastRx * 8 / 1e6:F1} Mbps, TX {_lastTx * 8 / 1e6:F1} Mbps.";
        var s = _detector?.Current;
        if (s is null)
        {
            string waiting = _marker.HasPending(nowUtc) ? " A mark is waiting for the next session." : "";
            return $"Idle: no session running.{waiting} {rates}";
        }

        var labels = SessionMarker.Summary(s);
        return $"Session running since {s.StartUtc.ToLocalTime():HH:mm:ss} ({(nowUtc - s.StartUtc).TotalSeconds:F0} s)" +
               (labels.Length > 0 ? $", {labels}" : "") +
               $". So far: avg RX {s.AvgRxBps * 8 / 1e6:F0} Mbps, peak RX {s.PeakRxBps * 8 / 1e6:F0} Mbps, " +
               $"{s.TotalRxBytes / 1e9:F2} GB down, {s.TotalTxBytes / 1e9:F2} GB up. {rates}";
    }

    private void AddProcessSamples(ProcessTrafficSnapshot traffic, double seconds, DateTime timestampUtc, Session session)
    {
        foreach (var ps in ProcessTrafficTracker.BuildSamples(traffic, seconds, timestampUtc, _opt.ProcessMinBytesPerSec))
            _processBuffer.Add(new BufferedProcessSample(ps, session));
    }

    /// <summary>Attaches the per-process and per-remote totals to the finished session.</summary>
    private void FinishSession(Session session)
    {
        var (procTotals, remoteTotals) = _tracker.Finish();
        session.ProcessTotals.AddRange(procTotals);
        session.RemoteTotals.AddRange(remoteTotals);
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        if (_buffer.Count == 0 && _processBuffer.Count == 0 && _dirty.Count == 0) return;
        try
        {
            await writer.FlushAsync(_buffer, _processBuffer, _dirty, ct);
            _buffer.Clear();
            _processBuffer.Clear();
            _dirty.Clear();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Database write failed; keeping {Count} samples and retrying", _buffer.Count);
            Trim(_buffer, _opt.MaxBufferedSamples);
            Trim(_processBuffer, _opt.MaxBufferedProcessSamples);
        }
    }

    private void Trim<T>(List<T> list, int max)
    {
        int excess = list.Count - max;
        if (excess <= 0) return;
        list.RemoveRange(0, excess);
        log.LogWarning("Buffer full; dropped {Excess} oldest rows", excess);
    }

    private static string Describe(Session s)
    {
        string top = string.Join(", ", s.ProcessTotals.Take(3)
            .Select(p => $"{p.ProcessName} {(p.RxBytes + p.TxBytes) / 1e9:F2} GB"));
        return $"{(s.EndUtc!.Value - s.StartUtc).TotalSeconds:F0}s, " +
               $"avg RX {s.AvgRxBps * 8 / 1e6:F0} Mbps, peak RX {s.PeakRxBps * 8 / 1e6:F0} Mbps, " +
               $"{s.TotalRxBytes / 1e9:F2} GB down, {s.TotalTxBytes / 1e9:F2} GB up" +
               (top.Length > 0 ? $". Top programs: {top}" : "");
    }
}
