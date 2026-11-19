using Microsoft.EntityFrameworkCore;

namespace ThroughputMonitor.Core;

public class ThroughputDbContext(DbContextOptions<ThroughputDbContext> options) : DbContext(options)
{
    public DbSet<Adapter> Adapters => Set<Adapter>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<Sample> Samples => Set<Sample>();
    public DbSet<ProcessSample> ProcessSamples => Set<ProcessSample>();
    public DbSet<SessionProcessTotal> SessionProcessTotals => Set<SessionProcessTotal>();
    public DbSet<SessionRemoteTotal> SessionRemoteTotals => Set<SessionRemoteTotal>();
    public DbSet<Rollup> Rollups => Set<Rollup>();

    /// <summary>Shared default location so the agent and the web app find the same file.</summary>
    public static string DefaultDatabasePath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                     "ThroughputMonitor", "throughput.db");

    /// <summary>Builds options for a SQLite file. WAL mode is enabled by <see cref="EnableWal"/>.</summary>
    public static DbContextOptions<ThroughputDbContext> SqliteOptions(string dbPath) =>
        new DbContextOptionsBuilder<ThroughputDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;

    /// <summary>WAL lets the agent write while the web app reads.</summary>
    public void EnableWal() => Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Sample>(e =>
        {
            e.HasIndex(x => x.TimestampUtc);
            e.HasIndex(x => x.SessionId);
        });
        b.Entity<ProcessSample>(e =>
        {
            e.HasIndex(x => x.TimestampUtc);
            e.HasIndex(x => x.SessionId);
        });
        b.Entity<Session>(e =>
        {
            e.HasIndex(x => x.StartUtc);
            e.Property(x => x.Source).HasConversion<string>();
            e.HasMany(x => x.ProcessTotals).WithOne().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.RemoteTotals).WithOne().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<Rollup>(e =>
            e.HasIndex(x => new { x.AdapterId, x.BucketSeconds, x.BucketStartUtc }).IsUnique());
    }
}

/// <summary>Used by `dotnet ef` at design time.</summary>
public class DesignTimeFactory : Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<ThroughputDbContext>
{
    public ThroughputDbContext CreateDbContext(string[] args) =>
        new(ThroughputDbContext.SqliteOptions("design-time.db"));
}
