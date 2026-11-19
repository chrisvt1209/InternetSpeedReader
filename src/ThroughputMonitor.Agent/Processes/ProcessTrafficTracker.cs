using ThroughputMonitor.Core;

namespace ThroughputMonitor.Agent.Processes;

/// <summary>
/// Collects per-process and per-remote totals for ONE session. Like the session detector it keeps
/// the quiet tail apart, so totals only cover time up to the last active second.
/// Processes are grouped by name + exe path (all chrome.exe pids become one row).
/// </summary>
public sealed class ProcessTrafficTracker(int maxRemotesPerProcess)
{
    private Totals _main = new();
    private Totals _tail = new();

    public void AddActive(ProcessTrafficSnapshot snapshot, double seconds)
    {
        _main.Merge(_tail);
        _tail = new Totals();
        _main.Add(snapshot, seconds);
    }

    public void AddQuiet(ProcessTrafficSnapshot snapshot, double seconds) => _tail.Add(snapshot, seconds);

    public void Reset()
    {
        _main = new Totals();
        _tail = new Totals();
    }

    /// <summary>Returns the totals (quiet tail excluded) and resets the tracker for the next session.</summary>
    public (List<SessionProcessTotal> Processes, List<SessionRemoteTotal> Remotes) Finish()
    {
        var processes = _main.Processes
            .Select(kv => new SessionProcessTotal
            {
                ProcessName = kv.Key.Name,
                ExePath = kv.Key.Path,
                RxBytes = kv.Value.RxBytes,
                TxBytes = kv.Value.TxBytes,
                PeakRxBps = kv.Value.PeakRxBps,
                PeakTxBps = kv.Value.PeakTxBps,
            })
            .OrderByDescending(p => p.RxBytes + p.TxBytes)
            .ToList();

        var remotes = _main.Remotes
            .GroupBy(kv => kv.Key.Name)
            .SelectMany(g => g.OrderByDescending(kv => kv.Value.RxBytes + kv.Value.TxBytes).Take(maxRemotesPerProcess))
            .Select(kv => new SessionRemoteTotal
            {
                ProcessName = kv.Key.Name,
                RemoteIp = kv.Key.Ip,
                RxBytes = kv.Value.RxBytes,
                TxBytes = kv.Value.TxBytes,
            })
            .OrderByDescending(r => r.RxBytes + r.TxBytes)
            .ToList();

        Reset();
        return (processes, remotes);
    }

    /// <summary>Per-second rows for processes at or above <paramref name="minBytesPerSec"/> (RX + TX).</summary>
    public static List<ProcessSample> BuildSamples(
        ProcessTrafficSnapshot snapshot, double seconds, DateTime timestampUtc, double minBytesPerSec)
    {
        var rows = new List<ProcessSample>();
        foreach (var p in snapshot.Processes)
        {
            double rx = p.RxBytes / seconds, tx = p.TxBytes / seconds;
            if (rx + tx < minBytesPerSec) continue;
            rows.Add(new ProcessSample
            {
                TimestampUtc = timestampUtc,
                ProcessName = p.Name,
                ExePath = p.ExePath,
                Pid = p.Pid,
                RxBytesPerSec = rx,
                TxBytesPerSec = tx,
            });
        }
        return rows;
    }

    private sealed class Totals
    {
        public readonly Dictionary<(string Name, string? Path), ProcessAgg> Processes = [];
        public readonly Dictionary<(string Name, string Ip), RemoteAgg> Remotes = [];

        public void Add(ProcessTrafficSnapshot snapshot, double seconds)
        {
            // Several pids of one program are summed first, so the peak is per program, not per pid.
            var thisSecond = new Dictionary<(string, string?), (long Rx, long Tx)>();
            foreach (var p in snapshot.Processes)
            {
                var key = (p.Name, p.ExePath);
                thisSecond.TryGetValue(key, out var cur);
                thisSecond[key] = (cur.Rx + p.RxBytes, cur.Tx + p.TxBytes);

                foreach (var r in p.Remotes)
                {
                    var rkey = (p.Name, r.Ip);
                    if (!Remotes.TryGetValue(rkey, out var agg)) Remotes[rkey] = agg = new RemoteAgg();
                    agg.RxBytes += r.RxBytes;
                    agg.TxBytes += r.TxBytes;
                }
            }

            foreach (var (key, bytes) in thisSecond)
            {
                if (!Processes.TryGetValue(key, out var agg)) Processes[key] = agg = new ProcessAgg();
                agg.RxBytes += bytes.Rx;
                agg.TxBytes += bytes.Tx;
                agg.PeakRxBps = Math.Max(agg.PeakRxBps, bytes.Rx / seconds);
                agg.PeakTxBps = Math.Max(agg.PeakTxBps, bytes.Tx / seconds);
            }
        }

        public void Merge(Totals other)
        {
            foreach (var (key, o) in other.Processes)
            {
                if (!Processes.TryGetValue(key, out var agg)) Processes[key] = agg = new ProcessAgg();
                agg.RxBytes += o.RxBytes;
                agg.TxBytes += o.TxBytes;
                agg.PeakRxBps = Math.Max(agg.PeakRxBps, o.PeakRxBps);
                agg.PeakTxBps = Math.Max(agg.PeakTxBps, o.PeakTxBps);
            }
            foreach (var (key, o) in other.Remotes)
            {
                if (!Remotes.TryGetValue(key, out var agg)) Remotes[key] = agg = new RemoteAgg();
                agg.RxBytes += o.RxBytes;
                agg.TxBytes += o.TxBytes;
            }
        }
    }

    private sealed class ProcessAgg
    {
        public long RxBytes, TxBytes;
        public double PeakRxBps, PeakTxBps;
    }

    private sealed class RemoteAgg
    {
        public long RxBytes, TxBytes;
    }
}
