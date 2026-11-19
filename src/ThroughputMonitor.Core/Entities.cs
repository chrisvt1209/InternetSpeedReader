namespace ThroughputMonitor.Core;

/// <summary>A monitored network adapter.</summary>
public class Adapter
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public long LinkSpeedBps { get; set; }
    public bool IsMonitored { get; set; } = true;
}

/// <summary>One detected (or manually marked) period of active internet use.</summary>
public class Session
{
    public long Id { get; set; }
    public int AdapterId { get; set; }
    public Adapter? Adapter { get; set; }

    public DateTime StartUtc { get; set; }
    public DateTime? EndUtc { get; set; }

    public string? Label { get; set; }
    public string? Category { get; set; }
    public string? Note { get; set; }
    public SessionSource Source { get; set; } = SessionSource.Auto;

    // Rates are stored as BYTES per second.
    public double PeakRxBps { get; set; }
    public double PeakTxBps { get; set; }
    public double AvgRxBps { get; set; }
    public double AvgTxBps { get; set; }
    public long TotalRxBytes { get; set; }
    public long TotalTxBytes { get; set; }

    public long MinLinkSpeedBps { get; set; }
    public double AvgCpuPercent { get; set; }
    public double PeakCpuPercent { get; set; }
    public double AvgDiskBytesPerSec { get; set; }
    public double PeakDiskBytesPerSec { get; set; }

    public List<SessionProcessTotal> ProcessTotals { get; set; } = [];
    public List<SessionRemoteTotal> RemoteTotals { get; set; } = [];
}

public enum SessionSource { Auto = 0, Manual = 1 }

/// <summary>Raw 1 s measurement of adapter throughput plus system load.</summary>
public class Sample
{
    public long Id { get; set; }
    public long? SessionId { get; set; }
    public int AdapterId { get; set; }
    public DateTime TimestampUtc { get; set; }
    public int IntervalMs { get; set; }
    public double RxBytesPerSec { get; set; }
    public double TxBytesPerSec { get; set; }
    public long LinkSpeedBps { get; set; }
    public double CpuPercent { get; set; }
    public double DiskBytesPerSec { get; set; }
}

/// <summary>Per-process throughput at one moment (only processes above the activity threshold).</summary>
public class ProcessSample
{
    public long Id { get; set; }
    public long? SessionId { get; set; }
    public DateTime TimestampUtc { get; set; }
    public string ProcessName { get; set; } = "";
    public string? ExePath { get; set; }
    public int Pid { get; set; }
    public double RxBytesPerSec { get; set; }
    public double TxBytesPerSec { get; set; }
}

/// <summary>Per-session summary: how much each program transferred.</summary>
public class SessionProcessTotal
{
    public long Id { get; set; }
    public long SessionId { get; set; }
    public string ProcessName { get; set; } = "";
    public string? ExePath { get; set; }
    public long RxBytes { get; set; }
    public long TxBytes { get; set; }
    public double PeakRxBps { get; set; }
    public double PeakTxBps { get; set; }
}

/// <summary>Per-session summary: which remote server each program talked to.</summary>
public class SessionRemoteTotal
{
    public long Id { get; set; }
    public long SessionId { get; set; }
    public string ProcessName { get; set; } = "";
    public string RemoteIp { get; set; } = "";
    public int? RemotePort { get; set; }
    public string? RemoteHostName { get; set; }
    public long RxBytes { get; set; }
    public long TxBytes { get; set; }
}

/// <summary>Aggregated min/avg/max/p95 per time bucket (60 s or 3600 s).</summary>
public class Rollup
{
    public long Id { get; set; }
    public int AdapterId { get; set; }
    public DateTime BucketStartUtc { get; set; }
    public int BucketSeconds { get; set; }

    public double RxMin { get; set; }
    public double RxAvg { get; set; }
    public double RxMax { get; set; }
    public double RxP95 { get; set; }
    public double TxMin { get; set; }
    public double TxAvg { get; set; }
    public double TxMax { get; set; }
    public double TxP95 { get; set; }
    public long RxBytes { get; set; }
    public long TxBytes { get; set; }
}
