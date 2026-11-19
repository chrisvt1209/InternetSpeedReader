namespace ThroughputMonitor.Agent;

/// <summary>Bound from the "Agent" section of appsettings.json.</summary>
public class AgentOptions
{
    public const string SectionName = "Agent";

    /// <summary>SQLite file. Empty = shared default under ProgramData.</summary>
    public string? DatabasePath { get; set; }

    /// <summary>Adapter name/description substring. Empty = first wired, non-virtual adapter.</summary>
    public string? AdapterNameContains { get; set; }

    /// <summary>Log folder. Empty = a "logs" folder next to the database.</summary>
    public string? LogDirectory { get; set; }

    /// <summary>Daily log files kept before the oldest is deleted.</summary>
    public int RetainedLogFiles { get; set; } = 14;

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

    /// <summary>Per-process tracking via ETW. Needs administrator rights; without them the agent logs a warning and records adapter totals only.</summary>
    public bool TrackProcesses { get; set; } = true;

    /// <summary>A process gets a per-second row only when its RX + TX is at least this many bytes/s.</summary>
    public double ProcessMinBytesPerSec { get; set; } = 10_000;

    /// <summary>Remote addresses stored per program per session (largest first).</summary>
    public int MaxRemotesPerProcess { get; set; } = 20;

    public int MaxBufferedProcessSamples { get; set; } = 100_000;

    /// <summary>Named pipe for the speedlog CLI. Empty = default name.</summary>
    public string? PipeName { get; set; }

    /// <summary>Turn the command pipe (speedlog mark / status) on or off.</summary>
    public bool EnableCommandPipe { get; set; } = true;

    /// <summary>A mark given while no session runs waits this long for the next session to start.</summary>
    public int PendingMarkMinutes { get; set; } = 30;

    /// <summary>How often the maintenance job aggregates rollups.</summary>
    public int RollupIntervalMinutes { get; set; } = 5;

    /// <summary>Raw 1 s samples and per-second process rows older than this are deleted (sessions and totals are kept).</summary>
    public int RawRetentionDays { get; set; } = 30;

    /// <summary>Per-minute rollups older than this are deleted (per-hour rollups are kept).</summary>
    public int MinuteRollupRetentionDays { get; set; } = 365;
}
