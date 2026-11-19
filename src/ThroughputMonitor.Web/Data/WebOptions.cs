using ThroughputMonitor.Core;

namespace ThroughputMonitor.Web.Data;

/// <summary>Bound from the "Web" section of appsettings.json.</summary>
public class WebOptions
{
    public const string SectionName = "Web";

    /// <summary>SQLite file written by the agent. Empty = the shared default under ProgramData.</summary>
    public string? DatabasePath { get; set; }

    /// <summary>Capacity of the internet connection, used for "% of link" and the chart's reference line.</summary>
    public double ReferenceLinkMbps { get; set; } = 1000;

    /// <summary>The "high peak" quick filter shows sessions whose peak RX is at least this many Mbps.</summary>
    public double HighPeakMbps { get; set; } = 900;

    public int DefaultPageSize { get; set; } = 25;

    public string ResolvedDatabasePath =>
        string.IsNullOrWhiteSpace(DatabasePath) ? ThroughputDbContext.DefaultDatabasePath() : Path.GetFullPath(DatabasePath);
}
