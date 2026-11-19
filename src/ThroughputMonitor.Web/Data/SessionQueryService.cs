using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ThroughputMonitor.Core;

namespace ThroughputMonitor.Web.Data;

public enum SessionSort { Start, AvgRx, PeakRx, TotalRx, TotalTx, Label }

public sealed record SessionQuery
{
    /// <summary>Local calendar dates, both inclusive.</summary>
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }

    /// <summary>Matches label, category, note or the name of a program that used the network.</summary>
    public string? Search { get; init; }

    /// <summary>Only sessions whose peak RX reached this many Mbps.</summary>
    public double? MinPeakRxMbps { get; init; }

    public SessionSort Sort { get; init; } = SessionSort.Start;
    public bool Descending { get; init; } = true;
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 25;
}

public sealed record ProgramShare(string Name, long Bytes);

public sealed record SessionRow(
    long Id, DateTime StartUtc, DateTime? EndUtc, string? Label, string? Category, string? Note,
    double AvgRxBps, double PeakRxBps, double AvgTxBps, double PeakTxBps,
    long TotalRxBytes, long TotalTxBytes, IReadOnlyList<ProgramShare> TopPrograms)
{
    public bool IsRunning => EndUtc is null;
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize)
{
    public int PageCount => Math.Max(1, (int)Math.Ceiling(Total / (double)PageSize));
}

public sealed record SessionDetail(
    Session Session,
    IReadOnlyList<SessionProcessTotal> Programs,
    IReadOnlyList<SessionRemoteTotal> Remotes,
    IReadOnlyList<ChartPoint> Points);

/// <summary>Read-only queries for the web pages.</summary>
public sealed class SessionQueryService(IDbContextFactory<ThroughputDbContext> factory, IOptions<WebOptions> options)
{
    public const int TopProgramsPerRow = 3;
    public const int MaxRemotesShown = 50;

    public bool DatabaseExists => File.Exists(options.Value.ResolvedDatabasePath);

    public async Task<PagedResult<SessionRow>> QueryAsync(SessionQuery q, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        IQueryable<Session> query = ctx.Sessions.AsNoTracking();

        if (q.From is { } from)
        {
            DateTime fromUtc = ToUtcStartOfDay(from);
            query = query.Where(s => s.StartUtc >= fromUtc);
        }
        if (q.To is { } to)
        {
            DateTime beforeUtc = ToUtcStartOfDay(to.AddDays(1));   // "to" is inclusive
            query = query.Where(s => s.StartUtc < beforeUtc);
        }
        if (q.MinPeakRxMbps is { } minPeak)
        {
            double minBytesPerSec = minPeak * 1_000_000 / 8;
            query = query.Where(s => s.PeakRxBps >= minBytesPerSec);
        }
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            string pattern = "%" + EscapeLike(q.Search.Trim()) + "%";
            query = query.Where(s =>
                EF.Functions.Like(s.Label!, pattern, "\\") ||
                EF.Functions.Like(s.Category!, pattern, "\\") ||
                EF.Functions.Like(s.Note!, pattern, "\\") ||
                s.ProcessTotals.Any(p => EF.Functions.Like(p.ProcessName, pattern, "\\")));
        }

        int total = await query.CountAsync(ct);

        IOrderedQueryable<Session> ordered = (q.Sort, q.Descending) switch
        {
            (SessionSort.AvgRx, true) => query.OrderByDescending(s => s.AvgRxBps),
            (SessionSort.AvgRx, false) => query.OrderBy(s => s.AvgRxBps),
            (SessionSort.PeakRx, true) => query.OrderByDescending(s => s.PeakRxBps),
            (SessionSort.PeakRx, false) => query.OrderBy(s => s.PeakRxBps),
            (SessionSort.TotalRx, true) => query.OrderByDescending(s => s.TotalRxBytes),
            (SessionSort.TotalRx, false) => query.OrderBy(s => s.TotalRxBytes),
            (SessionSort.TotalTx, true) => query.OrderByDescending(s => s.TotalTxBytes),
            (SessionSort.TotalTx, false) => query.OrderBy(s => s.TotalTxBytes),
            (SessionSort.Label, true) => query.OrderByDescending(s => s.Label),
            (SessionSort.Label, false) => query.OrderBy(s => s.Label),
            (_, true) => query.OrderByDescending(s => s.StartUtc),
            _ => query.OrderBy(s => s.StartUtc),
        };

        int pageSize = Math.Clamp(q.PageSize, 1, 500);
        int page = Math.Max(1, q.Page);
        var sessions = await ordered.ThenBy(s => s.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(ct);

        var ids = sessions.Select(s => s.Id).ToList();
        var programTotals = await ctx.SessionProcessTotals.AsNoTracking()
            .Where(p => ids.Contains(p.SessionId))
            .Select(p => new { p.SessionId, p.ProcessName, p.RxBytes, p.TxBytes })
            .ToListAsync(ct);
        var programsBySession = programTotals
            .GroupBy(p => p.SessionId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<ProgramShare>)g
                    .Select(p => new ProgramShare(p.ProcessName, p.RxBytes + p.TxBytes))
                    .OrderByDescending(p => p.Bytes).Take(TopProgramsPerRow).ToList());

        var rows = sessions.Select(s => new SessionRow(
            s.Id, Utc(s.StartUtc), s.EndUtc is null ? null : Utc(s.EndUtc.Value), s.Label, s.Category, s.Note,
            s.AvgRxBps, s.PeakRxBps, s.AvgTxBps, s.PeakTxBps, s.TotalRxBytes, s.TotalTxBytes,
            programsBySession.GetValueOrDefault(s.Id) ?? [])).ToList();

        return new PagedResult<SessionRow>(rows, total, page, pageSize);
    }

    public async Task<SessionDetail?> GetDetailAsync(long id, int maxChartPoints = 600, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);

        var session = await ctx.Sessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
        if (session is null) return null;

        var programs = (await ctx.SessionProcessTotals.AsNoTracking()
                .Where(p => p.SessionId == id).ToListAsync(ct))
            .OrderByDescending(p => p.RxBytes + p.TxBytes).ToList();

        var remotes = (await ctx.SessionRemoteTotals.AsNoTracking()
                .Where(r => r.SessionId == id).ToListAsync(ct))
            .OrderByDescending(r => r.RxBytes + r.TxBytes).Take(MaxRemotesShown).ToList();

        var raw = await ctx.Samples.AsNoTracking()
            .Where(s => s.SessionId == id)
            .OrderBy(s => s.TimestampUtc)
            .Select(s => new { s.TimestampUtc, s.RxBytesPerSec, s.TxBytesPerSec })
            .ToListAsync(ct);
        var points = raw.Select(s => new ChartPoint(Utc(s.TimestampUtc), s.RxBytesPerSec, s.TxBytesPerSec)).ToList();

        return new SessionDetail(session, programs, remotes, ChartData.Downsample(points, maxChartPoints));
    }

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    /// <summary>Start of the given local calendar day, as UTC.</summary>
    private static DateTime ToUtcStartOfDay(DateTime localDate) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localDate.Date, DateTimeKind.Unspecified), TimeZoneInfo.Local);

    private static string EscapeLike(string text) =>
        text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
