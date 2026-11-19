using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ThroughputMonitor.Core;
using ThroughputMonitor.Web.Data;

namespace ThroughputMonitor.Web.Tests;

/// <summary>Runs the real queries against a temporary SQLite file.</summary>
public sealed class SessionQueryServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tm-web-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;
    private readonly Factory _factory;
    private readonly SessionQueryService _service;

    private sealed class Factory(string path) : IDbContextFactory<ThroughputDbContext>
    {
        public ThroughputDbContext CreateDbContext() => new(ThroughputDbContext.SqliteOptions(path));
    }

    /// <summary>Local noon of the given day, as UTC: stays on the right calendar day in every time zone.</summary>
    private static DateTime LocalNoon(int month, int day) =>
        TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, month, day, 12, 0, 0, DateTimeKind.Unspecified), TimeZoneInfo.Local);

    private const double MB = 1_000_000;

    public SessionQueryServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "test.db");
        _factory = new Factory(_dbPath);
        _service = new SessionQueryService(_factory, Options.Create(new WebOptions { DatabasePath = _dbPath }));

        using var ctx = _factory.CreateDbContext();
        ctx.Database.Migrate();
        ctx.Adapters.Add(new Adapter { Name = "Ethernet" });
        ctx.SaveChanges();

        Add(ctx, LocalNoon(3, 10), minutes: 5, "Steam download", "game update", avgRx: 100 * MB, peakRx: 117 * MB,
            totalRx: 30_000_000_000, programs: [("steam", 29_000_000_000), ("chrome", 100_000_000)]);
        Add(ctx, LocalNoon(3, 11), minutes: 2, "Backup", "backup", avgRx: 5 * MB, peakRx: 8 * MB,
            totalRx: 600_000_000, programs: [("onedrive", 600_000_000)]);
        Add(ctx, LocalNoon(3, 12), minutes: 1, "100%_done", null, avgRx: 50 * MB, peakRx: 60 * MB,
            totalRx: 3_000_000_000, programs: []);
        ctx.SaveChanges();
    }

    private static void Add(ThroughputDbContext ctx, DateTime start, int minutes, string? label, string? category,
        double avgRx, double peakRx, long totalRx, (string Name, long Bytes)[] programs)
    {
        var s = new Session
        {
            AdapterId = 1, StartUtc = start, EndUtc = start.AddMinutes(minutes), Label = label, Category = category,
            AvgRxBps = avgRx, PeakRxBps = peakRx, TotalRxBytes = totalRx,
        };
        foreach (var (name, bytes) in programs)
            s.ProcessTotals.Add(new SessionProcessTotal { ProcessName = name, RxBytes = bytes });
        ctx.Sessions.Add(s);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp folder */ }
    }

    [Fact]
    public async Task Newest_session_comes_first_and_rows_carry_their_top_programs()
    {
        var result = await _service.QueryAsync(new SessionQuery());

        Assert.Equal(3, result.Total);
        Assert.Equal(["100%_done", "Backup", "Steam download"], result.Items.Select(r => r.Label));
        var steam = result.Items.Single(r => r.Label == "Steam download");
        Assert.Equal(["steam", "chrome"], steam.TopPrograms.Select(p => p.Name));
        Assert.Empty(result.Items.Single(r => r.Label == "100%_done").TopPrograms);
    }

    [Fact]
    public async Task Date_range_is_inclusive_on_local_calendar_days()
    {
        var result = await _service.QueryAsync(new SessionQuery { From = new DateTime(2026, 3, 11), To = new DateTime(2026, 3, 11) });

        Assert.Equal("Backup", Assert.Single(result.Items).Label);

        var both = await _service.QueryAsync(new SessionQuery { From = new DateTime(2026, 3, 10), To = new DateTime(2026, 3, 11) });
        Assert.Equal(2, both.Total);
    }

    [Theory]
    [InlineData("steam", "Steam download")]         // label, case-insensitive
    [InlineData("GAME UPDATE", "Steam download")]   // category
    [InlineData("onedrive", "Backup")]              // a program that used the network
    public async Task Search_matches_label_category_and_program_names(string term, string expectedLabel)
    {
        var result = await _service.QueryAsync(new SessionQuery { Search = term });

        Assert.Equal(expectedLabel, Assert.Single(result.Items).Label);
    }

    [Fact]
    public async Task Search_treats_percent_and_underscore_as_plain_text()
    {
        Assert.Equal("100%_done", Assert.Single((await _service.QueryAsync(new SessionQuery { Search = "100%_" })).Items).Label);
        Assert.Equal(0, (await _service.QueryAsync(new SessionQuery { Search = "%" })).Items.Count(r => r.Label == "Backup"));
    }

    [Fact]
    public async Task High_peak_filter_uses_megabits()
    {
        // 117 MB/s = 936 Mbps -> only the Steam session; 60 MB/s = 480 Mbps stays out.
        var result = await _service.QueryAsync(new SessionQuery { MinPeakRxMbps = 900 });

        Assert.Equal("Steam download", Assert.Single(result.Items).Label);
    }

    [Theory]
    [InlineData(SessionSort.AvgRx, true, "Steam download")]
    [InlineData(SessionSort.AvgRx, false, "Backup")]
    [InlineData(SessionSort.TotalRx, true, "Steam download")]
    [InlineData(SessionSort.Label, false, "100%_done")]
    [InlineData(SessionSort.Start, false, "Steam download")]
    public async Task Sorting_works_in_both_directions(SessionSort sort, bool descending, string firstLabel)
    {
        var result = await _service.QueryAsync(new SessionQuery { Sort = sort, Descending = descending });

        Assert.Equal(firstLabel, result.Items[0].Label);
    }

    [Fact]
    public async Task Paging_returns_the_requested_slice_and_total()
    {
        var page2 = await _service.QueryAsync(new SessionQuery { PageSize = 2, Page = 2 });

        Assert.Equal(3, page2.Total);
        Assert.Equal(2, page2.PageCount);
        Assert.Equal("Steam download", Assert.Single(page2.Items).Label);
    }

    [Fact]
    public async Task Detail_returns_programs_in_volume_order_and_a_time_ordered_series()
    {
        long id;
        using (var ctx = _factory.CreateDbContext())
        {
            id = ctx.Sessions.Single(s => s.Label == "Steam download").Id;
            var t0 = LocalNoon(3, 10);
            for (int i = 0; i < 300; i++)
                ctx.Samples.Add(new Sample { AdapterId = 1, SessionId = id, TimestampUtc = t0.AddSeconds(i), IntervalMs = 1000, RxBytesPerSec = i == 150 ? 117 * MB : 90 * MB });
            ctx.SessionRemoteTotals.Add(new SessionRemoteTotal { SessionId = id, ProcessName = "steam", RemoteIp = "1.2.3.4", RxBytes = 5 });
            ctx.SessionRemoteTotals.Add(new SessionRemoteTotal { SessionId = id, ProcessName = "steam", RemoteIp = "5.6.7.8", RxBytes = 50 });
            ctx.SaveChanges();
        }

        var detail = await _service.GetDetailAsync(id, maxChartPoints: 60);

        Assert.NotNull(detail);
        Assert.Equal(["steam", "chrome"], detail!.Programs.Select(p => p.ProcessName));
        Assert.Equal(["5.6.7.8", "1.2.3.4"], detail.Remotes.Select(r => r.RemoteIp));
        Assert.InRange(detail.Points.Count, 50, 60);
        Assert.Equal(117 * MB, detail.Points.Max(p => p.RxBps));          // the peak survived downsampling
        Assert.True(detail.Points.Select(p => p.TimeUtc).SequenceEqual(detail.Points.Select(p => p.TimeUtc).OrderBy(t => t)));
    }

    [Fact]
    public async Task Unknown_session_gives_null()
    {
        Assert.Null(await _service.GetDetailAsync(9999));
    }

    [Fact]
    public async Task The_web_apps_read_only_connection_cannot_write()
    {
        var options = new DbContextOptionsBuilder<ThroughputDbContext>()
            .UseSqlite($"Data Source={_dbPath};Mode=ReadOnly").Options;
        await using var ctx = new ThroughputDbContext(options);

        Assert.Equal(3, await ctx.Sessions.CountAsync());                 // reading works
        ctx.Adapters.Add(new Adapter { Name = "Nope" });
        await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
    }

    [Fact]
    public void Missing_database_is_reported()
    {
        var service = new SessionQueryService(_factory, Options.Create(new WebOptions { DatabasePath = Path.Combine(_dir, "nope.db") }));

        Assert.False(service.DatabaseExists);
        Assert.True(_service.DatabaseExists);
    }
}
