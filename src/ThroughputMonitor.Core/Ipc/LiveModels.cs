namespace ThroughputMonitor.Core.Ipc;

/// <summary>One second of traffic (bytes per second).</summary>
public sealed record LivePoint(DateTime T, double Rx, double Tx);

/// <summary>A program's traffic during the latest second.</summary>
public sealed record LiveProgram(string Name, double RxBps, double TxBps);

/// <summary>The session that is running right now (not yet saved as finished).</summary>
public sealed record LiveSession(
    long Id, DateTime StartUtc, string? Label, string? Category,
    double AvgRxBps, double PeakRxBps, long TotalRxBytes, long TotalTxBytes);

/// <summary>What the agent knows right now: recent traffic held in memory, so it works even when nothing is being stored.</summary>
/// <param name="ProcessTracking">False when the agent is not elevated, so <see cref="Programs"/> is always empty.</param>
public sealed record LiveSnapshot(
    DateTime NowUtc,
    double RxBps,
    double TxBps,
    IReadOnlyList<LivePoint> Points,
    IReadOnlyList<LiveProgram> Programs,
    LiveSession? Session,
    bool ProcessTracking);
