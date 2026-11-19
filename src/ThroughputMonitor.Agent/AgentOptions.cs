namespace ThroughputMonitor.Agent;

/// <summary>Bound from the "Agent" section of appsettings.json.</summary>
public class AgentOptions
{
    public const string SectionName = "Agent";

    /// <summary>SQLite file. Empty = shared default under ProgramData.</summary>
    public string? DatabasePath { get; set; }

    /// <summary>Adapter name/description substring. Empty = first wired, non-virtual adapter.</summary>
    public string? AdapterNameContains { get; set; }

    public int SampleIntervalMs { get; set; } = 1000;

    /// <summary>Traffic (RX or TX) at or above this counts as active.</summary>
    public double ActiveThresholdMbps { get; set; } = 5;
    public double StartAfterSeconds { get; set; } = 3;
    public double EndAfterSeconds { get; set; } = 10;

    public int FlushIntervalSeconds { get; set; } = 5;

    /// <summary>Outside sessions only one sample per this many seconds is stored.</summary>
    public int IdleBaselineSeconds { get; set; } = 60;

    /// <summary>Upper bound for unsaved samples if the database is unavailable.</summary>
    public int MaxBufferedSamples { get; set; } = 20_000;
}
