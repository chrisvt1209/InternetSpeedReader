using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ThroughputMonitor.Core;

namespace ThroughputMonitor.Agent.Maintenance;

/// <summary>Result of one maintenance pass, mostly for logging and tests.</summary>
public sealed record MaintenanceResult(
    int RollupsWritten, int SamplesDeleted, int ProcessSamplesDeleted, int MinuteRollupsDeleted);

/// <summary>
/// Aggregates raw samples into per-minute and per-hour rollups and applies the retention policy.
/// Sessions, per-session process/remote totals and hourly rollups are never deleted.
/// </summary>
public sealed class MaintenanceService(
    IDbContextFactory<ThroughputDbContext> factory,
    IOptions<AgentOptions> options,
    ILogger<MaintenanceService> log)
{
    private readonly AgentOptions _opt = options.Value;

    /// <summary>Only buckets that ended at least this long ago are rolled up (the agent flushes every few seconds).</summary>
    private const int SettleSeconds = 30;

    private static readonly int[] BucketSizes = [60, 3600];

    public async Task<MaintenanceResult> RunOnceAsync(DateTime nowUtc, bool applyRetention, CancellationToken ct)
    {
        int rollups = 0;
        foreach (int size in BucketSizes)
            rollups += await RollUpAsync(size, nowUtc, ct);

        int samples = 0, processSamples = 0, minuteRollups = 0;
        if (applyRetention)
            (samples, processSamples, minuteRollups) = await ApplyRetentionAsync(nowUtc, ct);

        return new MaintenanceResult(rollups, samples, processSamples, minuteRollups);
    }

    private async Task<int> RollUpAsync(int bucketSeconds, DateTime nowUtc, CancellationToken ct)
    {
        // Complete buckets only: everything before the start of the bucket that contains (now - settle).
        DateTime limit = RollupCalculator.BucketStart(nowUtc.AddSeconds(-SettleSeconds), bucketSeconds);
        int written = 0;

        await using var ctx = await factory.CreateDbContextAsync(ct);
        var adapterIds = await ctx.Adapters.Select(a => a.Id).ToListAsync(ct);

        foreach (int adapterId in adapterIds)
        {
            // Resume at the last rolled-up bucket (it is recomputed, so late samples are picked up).
            DateTime? last = await ctx.Rollups
                .Where(r => r.AdapterId == adapterId && r.BucketSeconds == bucketSeconds)
                .OrderByDescending(r => r.BucketStartUtc)
                .Select(r => (DateTime?)r.BucketStartUtc)
                .FirstOrDefaultAsync(ct);

            DateTime? from = last;
            if (from is null)
            {
                DateTime? firstSample = await ctx.Samples
                    .Where(s => s.AdapterId == adapterId)
                    .OrderBy(s => s.TimestampUtc)
                    .Select(s => (DateTime?)s.TimestampUtc)
                    .FirstOrDefaultAsync(ct);
                if (firstSample is null) continue;
                from = RollupCalculator.BucketStart(firstSample.Value, bucketSeconds);
            }

            DateTime start = DateTime.SpecifyKind(from.Value, DateTimeKind.Utc);
            while (start < limit)
            {
                DateTime end = start.AddDays(1) < limit ? start.AddDays(1) : limit;   // one day at a time keeps memory small

                var samples = await ctx.Samples.AsNoTracking()
                    .Where(s => s.AdapterId == adapterId && s.TimestampUtc >= start && s.TimestampUtc < end)
                    .ToListAsync(ct);

                if (samples.Count > 0)
                    written += await UpsertAsync(ctx, RollupCalculator.Build(samples, bucketSeconds), adapterId, bucketSeconds, start, end, ct);

                start = end;
            }
        }

        if (written > 0)
            log.LogInformation("Rollups: {Count} x {Size}s buckets written", written, bucketSeconds);
        return written;
    }

    private static async Task<int> UpsertAsync(
        ThroughputDbContext ctx, List<Rollup> fresh, int adapterId, int bucketSeconds, DateTime start, DateTime end, CancellationToken ct)
    {
        var existing = await ctx.Rollups
            .Where(r => r.AdapterId == adapterId && r.BucketSeconds == bucketSeconds
                        && r.BucketStartUtc >= start && r.BucketStartUtc < end)
            .ToListAsync(ct);
        var byStart = existing.ToDictionary(r => DateTime.SpecifyKind(r.BucketStartUtc, DateTimeKind.Utc));

        foreach (var r in fresh)
        {
            if (byStart.TryGetValue(r.BucketStartUtc, out var row))
            {
                row.RxMin = r.RxMin; row.RxAvg = r.RxAvg; row.RxMax = r.RxMax; row.RxP95 = r.RxP95;
                row.TxMin = r.TxMin; row.TxAvg = r.TxAvg; row.TxMax = r.TxMax; row.TxP95 = r.TxP95;
                row.RxBytes = r.RxBytes; row.TxBytes = r.TxBytes;
            }
            else
            {
                ctx.Rollups.Add(r);
            }
        }
        await ctx.SaveChangesAsync(ct);
        ctx.ChangeTracker.Clear();
        return fresh.Count;
    }

    private async Task<(int Samples, int ProcessSamples, int MinuteRollups)> ApplyRetentionAsync(DateTime nowUtc, CancellationToken ct)
    {
        DateTime rawCutoff = nowUtc.AddDays(-_opt.RawRetentionDays);
        DateTime minuteCutoff = nowUtc.AddDays(-_opt.MinuteRollupRetentionDays);

        await using var ctx = await factory.CreateDbContextAsync(ct);

        int samples = await ctx.Samples.Where(s => s.TimestampUtc < rawCutoff).ExecuteDeleteAsync(ct);
        int processSamples = await ctx.ProcessSamples.Where(s => s.TimestampUtc < rawCutoff).ExecuteDeleteAsync(ct);
        int minuteRollups = await ctx.Rollups
            .Where(r => r.BucketSeconds == 60 && r.BucketStartUtc < minuteCutoff).ExecuteDeleteAsync(ct);

        if (samples + processSamples + minuteRollups > 0)
        {
            log.LogInformation("Retention: deleted {Samples} samples, {Process} process rows older than {Days} days, {Minute} minute rollups older than {MinuteDays} days",
                samples, processSamples, _opt.RawRetentionDays, minuteRollups, _opt.MinuteRollupRetentionDays);
            try { await ctx.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE);", ct); }
            catch (Exception ex) { log.LogDebug(ex, "WAL checkpoint skipped"); }
        }
        return (samples, processSamples, minuteRollups);
    }
}
