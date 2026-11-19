using ThroughputMonitor.Core;

namespace ThroughputMonitor.Agent.Maintenance;

/// <summary>Pure aggregation of raw samples into min/avg/max/p95 buckets.</summary>
public static class RollupCalculator
{
    /// <summary>Start of the bucket (of <paramref name="bucketSeconds"/> length) that contains <paramref name="t"/>.</summary>
    public static DateTime BucketStart(DateTime t, int bucketSeconds)
    {
        long bucketTicks = TimeSpan.FromSeconds(bucketSeconds).Ticks;
        return new DateTime(t.Ticks - t.Ticks % bucketTicks, DateTimeKind.Utc);
    }

    /// <summary>
    /// One rollup per adapter and bucket. Rates are the mean/min/max/p95 of the stored samples
    /// (outside sessions only a baseline sample per minute is stored); byte totals are rate x interval.
    /// </summary>
    public static List<Rollup> Build(IEnumerable<Sample> samples, int bucketSeconds)
    {
        var result = new List<Rollup>();
        foreach (var group in samples.GroupBy(s => (s.AdapterId, Start: BucketStart(s.TimestampUtc, bucketSeconds))))
        {
            var rx = group.Select(s => s.RxBytesPerSec).ToList();
            var tx = group.Select(s => s.TxBytesPerSec).ToList();

            result.Add(new Rollup
            {
                AdapterId = group.Key.AdapterId,
                BucketStartUtc = group.Key.Start,
                BucketSeconds = bucketSeconds,
                RxMin = rx.Min(),
                RxAvg = rx.Average(),
                RxMax = rx.Max(),
                RxP95 = Percentile(rx, 0.95),
                TxMin = tx.Min(),
                TxAvg = tx.Average(),
                TxMax = tx.Max(),
                TxP95 = Percentile(tx, 0.95),
                RxBytes = (long)Math.Round(group.Sum(s => s.RxBytesPerSec * s.IntervalMs / 1000.0)),
                TxBytes = (long)Math.Round(group.Sum(s => s.TxBytesPerSec * s.IntervalMs / 1000.0)),
            });
        }
        return result;
    }

    /// <summary>Nearest-rank percentile.</summary>
    public static double Percentile(IReadOnlyList<double> values, double p)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(v => v).ToList();
        int rank = (int)Math.Ceiling(p * sorted.Count);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Count - 1)];
    }
}
