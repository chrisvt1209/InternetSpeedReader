using ThroughputMonitor.Agent.Processes;
using ThroughputMonitor.Core;
using ThroughputMonitor.Core.Ipc;

namespace ThroughputMonitor.Agent.Live;

public static class LiveSnapshotBuilder
{
    /// <summary>Programs below this (RX + TX, bytes per second; 5 kB/s = 40 kbps) are left out as background noise.</summary>
    public const double MinProgramBytesPerSec = 5_000;

    /// <param name="traffic">Per-process bytes of the latest interval.</param>
    /// <param name="trafficSeconds">Length of that interval, to turn bytes into bytes per second.</param>
    public static LiveSnapshot Build(
        DateTime nowUtc, LiveBuffer buffer, Session? session,
        ProcessTrafficSnapshot traffic, double trafficSeconds, bool processTracking, int maxPrograms = 8)
    {
        var latest = buffer.Latest;

        // All pids of one program become one entry, like everywhere else.
        double seconds = trafficSeconds > 0 ? trafficSeconds : 1;
        var programs = traffic.Processes
            .GroupBy(p => p.Name)
            .Select(g => new LiveProgram(g.Key, Math.Round(g.Sum(p => p.RxBytes) / seconds), Math.Round(g.Sum(p => p.TxBytes) / seconds)))
            .Where(p => p.RxBps + p.TxBps >= MinProgramBytesPerSec)
            .OrderByDescending(p => p.RxBps + p.TxBps)
            .Take(maxPrograms)
            .ToList();

        var live = session is null ? null : new LiveSession(
            session.Id, session.StartUtc, session.Label, session.Category,
            session.AvgRxBps, session.PeakRxBps, session.TotalRxBytes, session.TotalTxBytes);

        return new LiveSnapshot(nowUtc, latest?.Rx ?? 0, latest?.Tx ?? 0, buffer.ToList(), programs, live, processTracking);
    }
}
