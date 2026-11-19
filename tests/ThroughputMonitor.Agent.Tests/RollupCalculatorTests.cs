using ThroughputMonitor.Agent.Maintenance;
using ThroughputMonitor.Core;

namespace ThroughputMonitor.Agent.Tests;

public class RollupCalculatorTests
{
    private static readonly DateTime T0 = new(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc);

    private static Sample S(double offsetSeconds, double rx, double tx = 0, int adapter = 1, int intervalMs = 1000) => new()
    {
        AdapterId = adapter,
        TimestampUtc = T0.AddSeconds(offsetSeconds),
        IntervalMs = intervalMs,
        RxBytesPerSec = rx,
        TxBytesPerSec = tx,
    };

    [Fact]
    public void Bucket_start_floors_to_the_minute_and_the_hour()
    {
        var t = new DateTime(2026, 3, 1, 10, 42, 17, 500, DateTimeKind.Utc);

        Assert.Equal(new DateTime(2026, 3, 1, 10, 42, 0, DateTimeKind.Utc), RollupCalculator.BucketStart(t, 60));
        Assert.Equal(new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc), RollupCalculator.BucketStart(t, 3600));
        Assert.Equal(DateTimeKind.Utc, RollupCalculator.BucketStart(t, 60).Kind);
    }

    [Fact]
    public void Samples_are_grouped_per_minute_with_min_avg_max_and_bytes()
    {
        var samples = new[]
        {
            S(5, 100), S(10, 300), S(20, 200),            // minute 10:00
            S(65, 1_000),                                  // minute 10:01
        };

        var rollups = RollupCalculator.Build(samples, 60).OrderBy(r => r.BucketStartUtc).ToList();

        Assert.Equal(2, rollups.Count);
        var first = rollups[0];
        Assert.Equal(T0, first.BucketStartUtc);
        Assert.Equal(60, first.BucketSeconds);
        Assert.Equal(100, first.RxMin);
        Assert.Equal(200, first.RxAvg);
        Assert.Equal(300, first.RxMax);
        Assert.Equal(600, first.RxBytes);                  // 100 + 300 + 200 bytes/s x 1 s
        Assert.Equal(T0.AddMinutes(1), rollups[1].BucketStartUtc);
        Assert.Equal(1_000, rollups[1].RxBytes);
    }

    [Fact]
    public void Bytes_use_the_real_interval_of_each_sample()
    {
        var rollups = RollupCalculator.Build([S(1, 1_000, 500, intervalMs: 2000)], 60);

        Assert.Equal(2_000, rollups[0].RxBytes);
        Assert.Equal(1_000, rollups[0].TxBytes);
    }

    [Fact]
    public void P95_uses_nearest_rank()
    {
        var samples = Enumerable.Range(1, 20).Select(i => S(i, i * 10)).ToList();   // 10 .. 200

        var rollup = RollupCalculator.Build(samples, 60).Single();

        Assert.Equal(190, rollup.RxP95);                   // rank ceil(0.95 * 20) = 19 -> 190
        Assert.Equal(200, rollup.RxMax);
    }

    [Fact]
    public void Single_sample_fills_every_statistic()
    {
        var rollup = RollupCalculator.Build([S(1, 500, 50)], 60).Single();

        Assert.Equal(500, rollup.RxMin);
        Assert.Equal(500, rollup.RxAvg);
        Assert.Equal(500, rollup.RxMax);
        Assert.Equal(500, rollup.RxP95);
        Assert.Equal(50, rollup.TxP95);
    }

    [Fact]
    public void Adapters_are_rolled_up_separately_and_hour_buckets_merge_minutes()
    {
        var samples = new[] { S(5, 100, adapter: 1), S(65, 300, adapter: 1), S(5, 900, adapter: 2) };

        var hourly = RollupCalculator.Build(samples, 3600);

        Assert.Equal(2, hourly.Count);
        var a1 = hourly.Single(r => r.AdapterId == 1);
        Assert.Equal(200, a1.RxAvg);
        Assert.Equal(400, a1.RxBytes);
        Assert.Equal(900, hourly.Single(r => r.AdapterId == 2).RxMax);
    }

    [Fact]
    public void Percentile_of_nothing_is_zero()
    {
        Assert.Equal(0, RollupCalculator.Percentile([], 0.95));
    }
}
