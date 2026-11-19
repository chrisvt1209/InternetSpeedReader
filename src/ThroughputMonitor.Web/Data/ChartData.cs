namespace ThroughputMonitor.Web.Data;

/// <summary>One point of a throughput chart (rates in bytes per second).</summary>
public readonly record struct ChartPoint(DateTime TimeUtc, double RxBps, double TxBps);

public static class ChartData
{
    /// <summary>
    /// Reduces a long series to at most <paramref name="maxPoints"/> points. Each bucket keeps the HIGHEST
    /// RX and TX value inside it, so short peaks stay visible, and the time of its middle sample.
    /// </summary>
    public static IReadOnlyList<ChartPoint> Downsample(IReadOnlyList<ChartPoint> points, int maxPoints)
    {
        if (maxPoints < 2 || points.Count <= maxPoints) return points;

        int bucketSize = (int)Math.Ceiling(points.Count / (double)maxPoints);
        var result = new List<ChartPoint>(maxPoints + 1);
        for (int start = 0; start < points.Count; start += bucketSize)
        {
            int end = Math.Min(start + bucketSize, points.Count);
            double rx = 0, tx = 0;
            for (int i = start; i < end; i++)
            {
                rx = Math.Max(rx, points[i].RxBps);
                tx = Math.Max(tx, points[i].TxBps);
            }
            result.Add(new ChartPoint(points[start + (end - start) / 2].TimeUtc, rx, tx));
        }
        return result;
    }

    /// <summary>
    /// Picks a readable axis: a tick step of 1, 2, 2.5 or 5 times a power of ten with at most six intervals,
    /// and the smallest multiple of that step that reaches <paramref name="max"/>.
    /// </summary>
    public static (double Step, double AxisMax) NiceAxis(double max)
    {
        if (max <= 0) return (1, 1);
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(max / 5)));
        foreach (double mult in new[] { 1, 2, 2.5, 5, 10 })
        {
            double step = mult * magnitude;
            double intervals = Math.Ceiling(max / step - 1e-9);
            if (intervals <= 6) return (step, step * intervals);
        }
        return (10 * magnitude, 10 * magnitude * Math.Ceiling(max / (10 * magnitude)));
    }
}
