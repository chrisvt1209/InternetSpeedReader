using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ThroughputMonitor.Agent.Maintenance;
using ThroughputMonitor.Agent.Storage;
using ThroughputMonitor.Core;

namespace ThroughputMonitor.Agent.Tests;

/// <summary>Integration tests against a real (temporary) SQLite file.</summary>
public sealed class MaintenanceServiceTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 6, 30, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tm-tests-" + Guid.NewGuid().ToString("N"));
    private readonly TestFactory _factory;

    private sealed class TestFactory(string path) : IDbContextFactory<ThroughputDbContext>
    {
        public ThroughputDbContext CreateDbContext() => new(ThroughputDbContext.SqliteOptions(path));
    }

    public MaintenanceServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _factory = new TestFactory(Path.Combine(_dir, "test.db"));
        using var ctx = _factory.CreateDbContext();
        ctx.Database.Migrate();
        ctx.Adapters.Add(new Adapter { Name = "Ethernet" });
        ctx.SaveChanges();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp folder, best effort */ }
    }

    private MaintenanceService NewService(Action<AgentOptions>? configure = null)
    {
        var opt = new AgentOptions();
        configure?.Invoke(opt);
        return new MaintenanceService(_factory, Options.Create(opt), NullLogger<MaintenanceService>.Instance);
    }

    private void AddSamples(DateTime start, int count, double rx = 1_000_000, long? sessionId = null)
    {
        using var ctx = _factory.CreateDbContext();
        for (int i = 0; i < count; i++)
            ctx.Samples.Add(new Sample
            {
                AdapterId = 1, SessionId = sessionId, TimestampUtc = start.AddSeconds(i), IntervalMs = 1000,
                RxBytesPerSec = rx, TxBytesPerSec = 10,
            });
        ctx.SaveChanges();
    }

    [Fact]
    public async Task Complete_minutes_and_hours_are_rolled_up_but_not_the_current_one()
    {
        // "Now" is 12:00:00; buckets must have ended at least 30 s ago, so minutes up to 11:58 and the 10:00 hour are done.
        AddSamples(new DateTime(2026, 6, 30, 10, 58, 0, DateTimeKind.Utc), 240);   // 10:58:00 - 11:01:59
        AddSamples(new DateTime(2026, 6, 30, 11, 58, 50, DateTimeKind.Utc), 20);   // 11:58:50 - 11:59:09

        var result = await NewService().RunOnceAsync(Now, applyRetention: false, CancellationToken.None);

        using var ctx = _factory.CreateDbContext();
        var minutes = await ctx.Rollups.Where(r => r.BucketSeconds == 60).ToListAsync();
        var hours = await ctx.Rollups.Where(r => r.BucketSeconds == 3600).ToListAsync();

        Assert.Equal(minutes.Count + hours.Count, result.RollupsWritten);
        Assert.Contains(minutes, m => m.BucketStartUtc == new DateTime(2026, 6, 30, 10, 58, 0));
        Assert.Contains(minutes, m => m.BucketStartUtc == new DateTime(2026, 6, 30, 11, 1, 0));
        Assert.Contains(minutes, m => m.BucketStartUtc == new DateTime(2026, 6, 30, 11, 58, 0));
        Assert.DoesNotContain(minutes, m => m.BucketStartUtc == new DateTime(2026, 6, 30, 11, 59, 0));   // still settling
        Assert.Contains(hours, h => h.BucketStartUtc == new DateTime(2026, 6, 30, 10, 0, 0));
        Assert.DoesNotContain(hours, h => h.BucketStartUtc == new DateTime(2026, 6, 30, 11, 0, 0));      // hour not over yet
        Assert.Equal(60_000_000, minutes.Single(m => m.BucketStartUtc == new DateTime(2026, 6, 30, 10, 58, 0)).RxBytes);
    }

    [Fact]
    public async Task Running_twice_does_not_duplicate_rollups_and_picks_up_new_data()
    {
        AddSamples(new DateTime(2026, 6, 30, 10, 0, 0, DateTimeKind.Utc), 120);   // minutes 10:00 and 10:01, hour 10:00
        var service = NewService();

        await service.RunOnceAsync(Now, false, CancellationToken.None);
        using (var ctx = _factory.CreateDbContext()) Assert.Equal(3, await ctx.Rollups.CountAsync());

        await service.RunOnceAsync(Now, false, CancellationToken.None);          // nothing new: no duplicates
        using (var ctx = _factory.CreateDbContext()) Assert.Equal(3, await ctx.Rollups.CountAsync());

        AddSamples(new DateTime(2026, 6, 30, 11, 0, 0, DateTimeKind.Utc), 60);   // new data: minute 11:00 (hour 11 is not over)
        await service.RunOnceAsync(Now, false, CancellationToken.None);
        using (var ctx = _factory.CreateDbContext()) Assert.Equal(4, await ctx.Rollups.CountAsync());
    }

    [Fact]
    public async Task Retention_deletes_old_raw_data_but_keeps_sessions_totals_and_hourly_rollups()
    {
        var old = Now.AddDays(-40);
        long sessionId;
        using (var ctx = _factory.CreateDbContext())
        {
            var s = new Session { AdapterId = 1, StartUtc = old, EndUtc = old.AddMinutes(5) };
            s.ProcessTotals.Add(new SessionProcessTotal { ProcessName = "steam", RxBytes = 123 });
            ctx.Sessions.Add(s);
            ctx.SaveChanges();
            sessionId = s.Id;
            ctx.ProcessSamples.Add(new ProcessSample { SessionId = s.Id, TimestampUtc = old, ProcessName = "steam" });
            ctx.SaveChanges();
        }
        AddSamples(old, 120, sessionId: sessionId);                               // 40 days old
        AddSamples(Now.AddDays(-2), 60);                                           // recent

        var result = await NewService().RunOnceAsync(Now, applyRetention: true, CancellationToken.None);

        using var check = _factory.CreateDbContext();
        Assert.Equal(120, result.SamplesDeleted);
        Assert.Equal(1, result.ProcessSamplesDeleted);
        Assert.Equal(60, await check.Samples.CountAsync());                        // only the recent ones remain
        Assert.Equal(1, await check.Sessions.CountAsync());                        // session kept
        Assert.Equal(1, await check.SessionProcessTotals.CountAsync());            // per-session totals kept
        Assert.True(await check.Rollups.AnyAsync(r => r.BucketSeconds == 3600 && r.BucketStartUtc < Now.AddDays(-30)),
            "rollups of the deleted raw data must exist (they are made before deletion)");
    }

    [Fact]
    public async Task Minute_rollups_expire_after_their_own_retention_but_hourly_rollups_stay()
    {
        AddSamples(Now.AddDays(-400), 120);
        var service = NewService();

        await service.RunOnceAsync(Now, applyRetention: false, CancellationToken.None);
        var result = await service.RunOnceAsync(Now, applyRetention: true, CancellationToken.None);

        using var ctx = _factory.CreateDbContext();
        Assert.True(result.MinuteRollupsDeleted > 0);
        Assert.Equal(0, await ctx.Rollups.CountAsync(r => r.BucketSeconds == 60));
        Assert.True(await ctx.Rollups.CountAsync(r => r.BucketSeconds == 3600) > 0);
    }

    [Fact]
    public async Task Sessions_left_open_by_a_crash_are_closed_at_their_last_sample()
    {
        long id;
        using (var ctx = _factory.CreateDbContext())
        {
            var s = new Session { AdapterId = 1, StartUtc = Now.AddMinutes(-10) };      // EndUtc null: agent died
            ctx.Sessions.Add(s);
            ctx.SaveChanges();
            id = s.Id;
        }
        AddSamples(Now.AddMinutes(-10), 90, sessionId: id);                              // last sample at -10min + 89s

        await new SampleWriter(_factory).InitializeAsync(CancellationToken.None);

        using var check = _factory.CreateDbContext();
        var session = await check.Sessions.SingleAsync();
        Assert.Equal(Now.AddMinutes(-10).AddSeconds(89), session.EndUtc);
    }
}
