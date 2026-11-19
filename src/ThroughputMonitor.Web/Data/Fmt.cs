using System.Globalization;

namespace ThroughputMonitor.Web.Data;

/// <summary>Display formatting. Rates are stored as bytes per second; the UI shows Mbps and MB/s.</summary>
public static class Fmt
{
    private static CultureInfo C => CultureInfo.CurrentCulture;

    /// <summary>Megabits per second, e.g. "627 Mbps" or "4.2 Mbps".</summary>
    public static string Mbps(double bytesPerSec)
    {
        double mbps = bytesPerSec * 8 / 1e6;
        return (mbps >= 100 ? mbps.ToString("N0", C) : mbps.ToString("N1", C)) + " Mbps";
    }

    /// <summary>Megabytes per second, e.g. "78.4 MB/s".</summary>
    public static string MBps(double bytesPerSec) => (bytesPerSec / 1e6).ToString("N1", C) + " MB/s";

    public static string Bytes(long bytes) => bytes switch
    {
        >= 1_000_000_000 => (bytes / 1e9).ToString("N2", C) + " GB",
        >= 1_000_000 => (bytes / 1e6).ToString("N1", C) + " MB",
        >= 1_000 => (bytes / 1e3).ToString("N0", C) + " kB",
        _ => bytes.ToString("N0", C) + " B",
    };

    public static string Duration(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes:00}m";
        if (t.TotalMinutes >= 1) return $"{t.Minutes}m {t.Seconds:00}s";
        return $"{t.Seconds}s";
    }

    public static string Percent(double fraction) => fraction.ToString("P0", C);

    public static string LinkSpeed(long bitsPerSecond) =>
        bitsPerSecond >= 1_000_000_000 ? (bitsPerSecond / 1e9).ToString("0.##", C) + " Gbps"
        : bitsPerSecond > 0 ? (bitsPerSecond / 1e6).ToString("0", C) + " Mbps" : "-";

    /// <summary>Database timestamps are UTC (SQLite hands them back without a kind).</summary>
    public static DateTime Local(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();

    public static string LocalDateTime(DateTime utc) => Local(utc).ToString("g", C);
    public static string LocalTime(DateTime utc) => Local(utc).ToString("T", C);
}
