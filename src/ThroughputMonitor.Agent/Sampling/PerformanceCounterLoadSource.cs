using System.Diagnostics;

namespace ThroughputMonitor.Agent.Sampling;

/// <summary>CPU and disk load from Windows performance counters; reports zeros if a counter is unavailable.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class PerformanceCounterLoadSource : ISystemLoadSource, IDisposable
{
    private readonly PerformanceCounter? _cpu;
    private readonly PerformanceCounter? _disk;

    public PerformanceCounterLoadSource(ILogger<PerformanceCounterLoadSource> log)
    {
        _cpu = Create(log, "Processor Information", "% Processor Time", "_Total");
        _disk = Create(log, "PhysicalDisk", "Disk Bytes/sec", "_Total");
    }

    private static PerformanceCounter? Create(ILogger log, string category, string counter, string instance)
    {
        try
        {
            var c = new PerformanceCounter(category, counter, instance, readOnly: true);
            c.NextValue(); // the first read is always 0; prime it
            return c;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Performance counter {Category}\\{Counter} unavailable; recording 0", category, counter);
            return null;
        }
    }

    public SystemLoad Read() => new(Safe(_cpu), Safe(_disk));

    private static double Safe(PerformanceCounter? c)
    {
        try { return c?.NextValue() ?? 0; }
        catch { return 0; }
    }

    public void Dispose()
    {
        _cpu?.Dispose();
        _disk?.Dispose();
    }
}
