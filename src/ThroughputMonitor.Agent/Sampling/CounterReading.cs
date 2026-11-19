using System.Diagnostics;

namespace ThroughputMonitor.Agent.Sampling;

/// <summary>Cumulative byte counters of one adapter at one moment.</summary>
/// <param name="Timestamp">Monotonic <see cref="Stopwatch.GetTimestamp"/> value.</param>
public readonly record struct CounterReading(long RxBytes, long TxBytes, long LinkSpeedBps, long Timestamp);

/// <summary>Source of cumulative adapter counters; faked in tests.</summary>
public interface ICounterSource
{
    string AdapterName { get; }
    string AdapterDescription { get; }
    CounterReading Read();
}

/// <summary>System load at one moment.</summary>
public readonly record struct SystemLoad(double CpuPercent, double DiskBytesPerSec);

public interface ISystemLoadSource
{
    SystemLoad Read();
}

public static class RateCalculator
{
    /// <summary>
    /// Bytes per second between two readings, using the real elapsed time.
    /// Returns false when the counters went backwards (adapter reset) or no time passed.
    /// </summary>
    public static bool TryCompute(CounterReading prev, CounterReading cur,
        out double rxBytesPerSec, out double txBytesPerSec, out double elapsedSeconds)
    {
        rxBytesPerSec = txBytesPerSec = 0;
        elapsedSeconds = Stopwatch.GetElapsedTime(prev.Timestamp, cur.Timestamp).TotalSeconds;

        long dRx = cur.RxBytes - prev.RxBytes;
        long dTx = cur.TxBytes - prev.TxBytes;
        if (dRx < 0 || dTx < 0 || elapsedSeconds <= 0) return false;

        rxBytesPerSec = dRx / elapsedSeconds;
        txBytesPerSec = dTx / elapsedSeconds;
        return true;
    }
}
