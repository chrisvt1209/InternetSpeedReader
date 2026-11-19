using System.Globalization;
using ThroughputMonitor.Web.Data;

namespace ThroughputMonitor.Web.Tests;

public class ChartAndFormatTests
{
    public ChartAndFormatTests() => CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

    private static ChartPoint P(int second, double rx, double tx = 0) =>
        new(new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc).AddSeconds(second), rx, tx);

    [Fact]
    public void Short_series_is_left_alone()
    {
        var points = Enumerable.Range(0, 50).Select(i => P(i, i)).ToList();

        Assert.Same(points, ChartData.Downsample(points, 100));
    }

    [Fact]
    public void Long_series_is_reduced_and_keeps_the_peaks()
    {
        var points = Enumerable.Range(0, 1000).Select(i => P(i, rx: i == 503 ? 120_000_000 : 1_000, tx: i == 777 ? 5_000_000 : 10)).ToList();

        var reduced = ChartData.Downsample(points, 100);

        Assert.InRange(reduced.Count, 90, 100);
        Assert.Equal(120_000_000, reduced.Max(p => p.RxBps));     // the single spike survives
        Assert.Equal(5_000_000, reduced.Max(p => p.TxBps));
        Assert.True(reduced.SequenceEqual(reduced.OrderBy(p => p.TimeUtc)), "time order must be kept");
    }

    [Fact]
    public void Value_series_is_reduced_keeping_each_bucket_peak()
    {
        var values = Enumerable.Range(0, 500).Select(i => i == 250 ? 99.0 : 1.0).ToList();

        var reduced = ChartData.DownsampleMax(values, 28);

        Assert.InRange(reduced.Count, 25, 28);
        Assert.Equal(99.0, reduced.Max());
        Assert.Same(values, ChartData.DownsampleMax(values, 1000));      // short series untouched
    }

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(0.7, 0.2, 0.8)]
    [InlineData(5, 1, 5)]
    [InlineData(23, 5, 25)]
    [InlineData(977, 200, 1000)]
    [InlineData(1000, 200, 1000)]
    [InlineData(1001, 200, 1200)]
    [InlineData(1146, 200, 1200)]
    [InlineData(1900, 500, 2000)]
    public void Axis_has_a_readable_step_and_at_most_six_intervals(double max, double expectedStep, double expectedAxisMax)
    {
        var (step, axisMax) = ChartData.NiceAxis(max);

        Assert.Equal(expectedStep, step, 6);
        Assert.Equal(expectedAxisMax, axisMax, 6);
        Assert.True(axisMax >= max);
        Assert.True(axisMax / step <= 6.000001);
    }

    [Theory]
    [InlineData(125_000_000, "1,000 Mbps")]          // 1000 Mbps link
    [InlineData(78_400_000, "627 Mbps")]
    [InlineData(500_000, "4.0 Mbps")]
    public void Rates_are_shown_in_megabits(double bytesPerSec, string expected) =>
        Assert.Equal(expected, Fmt.Mbps(bytesPerSec));

    [Fact]
    public void Megabytes_per_second_are_decimal()
    {
        Assert.Equal("78.4 MB/s", Fmt.MBps(78_400_000));
    }

    [Theory]
    [InlineData(13_250_000_000, "13.25 GB")]
    [InlineData(250_000_000, "250.0 MB")]
    [InlineData(2_600, "3 kB")]
    [InlineData(999, "999 B")]
    public void Byte_totals_use_decimal_units(long bytes, string expected) =>
        Assert.Equal(expected, Fmt.Bytes(bytes));

    [Theory]
    [InlineData(45, "45s")]
    [InlineData(169, "2m 49s")]
    [InlineData(3_725, "1h 02m")]
    public void Durations_are_compact(int seconds, string expected) =>
        Assert.Equal(expected, Fmt.Duration(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Link_speed_is_readable()
    {
        Assert.Equal("1 Gbps", Fmt.LinkSpeed(1_000_000_000));
        Assert.Equal("100 Mbps", Fmt.LinkSpeed(100_000_000));
        Assert.Equal("-", Fmt.LinkSpeed(0));
    }
}
