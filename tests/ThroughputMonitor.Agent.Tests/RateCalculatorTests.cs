using System.Diagnostics;
using ThroughputMonitor.Agent.Sampling;

namespace ThroughputMonitor.Agent.Tests;

public class RateCalculatorTests
{
    private static long Ticks(double seconds) => (long)(seconds * Stopwatch.Frequency);

    [Fact]
    public void Computes_bytes_per_second_from_real_elapsed_time()
    {
        var a = new CounterReading(1_000, 500, 1_000_000_000, Ticks(10));
        var b = new CounterReading(1_000 + 250_000_000, 500 + 1_000_000, 1_000_000_000, Ticks(12)); // 2 s later

        Assert.True(RateCalculator.TryCompute(a, b, out double rx, out double tx, out double dt));

        Assert.Equal(2.0, dt, 3);
        Assert.Equal(125_000_000, rx, 1);   // 125 MB/s = 1000 Mbps
        Assert.Equal(500_000, tx, 1);
    }

    [Fact]
    public void Uneven_interval_is_not_assumed_to_be_one_second()
    {
        var a = new CounterReading(0, 0, 0, Ticks(0));
        var b = new CounterReading(1_100_000, 0, 0, Ticks(1.1));

        Assert.True(RateCalculator.TryCompute(a, b, out double rx, out _, out _));
        Assert.Equal(1_000_000, rx, 1);
    }

    [Fact]
    public void Counter_going_backwards_is_rejected()
    {
        var a = new CounterReading(5_000_000, 5_000_000, 0, Ticks(0));
        var b = new CounterReading(100, 6_000_000, 0, Ticks(1)); // RX reset

        Assert.False(RateCalculator.TryCompute(a, b, out _, out _, out _));
    }

    [Fact]
    public void Zero_elapsed_time_is_rejected()
    {
        var a = new CounterReading(0, 0, 0, Ticks(5));
        Assert.False(RateCalculator.TryCompute(a, a, out _, out _, out _));
    }
}
