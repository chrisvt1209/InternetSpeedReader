namespace ThroughputMonitor.Agent.Processes;

/// <summary>Bytes exchanged with one remote address during one interval.</summary>
public sealed record RemoteTraffic(string Ip, long RxBytes, long TxBytes);

/// <summary>Bytes of one process (pid) during one interval.</summary>
public sealed record ProcessTraffic(
    int Pid, string Name, string? ExePath, long RxBytes, long TxBytes, IReadOnlyList<RemoteTraffic> Remotes);

public sealed record ProcessTrafficSnapshot(IReadOnlyList<ProcessTraffic> Processes)
{
    public static ProcessTrafficSnapshot Empty { get; } = new([]);
}

/// <summary>Per-process traffic since the previous <see cref="Drain"/> call.</summary>
public interface IProcessTrafficSource
{
    /// <summary>False when per-process data is unavailable (e.g. agent not elevated).</summary>
    bool IsActive { get; }

    ProcessTrafficSnapshot Drain();
}

public sealed class NullProcessTrafficSource : IProcessTrafficSource
{
    public bool IsActive => false;
    public ProcessTrafficSnapshot Drain() => ProcessTrafficSnapshot.Empty;
}
