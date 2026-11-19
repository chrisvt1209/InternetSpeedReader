using Microsoft.EntityFrameworkCore;
using ThroughputMonitor.Core;

namespace ThroughputMonitor.Agent.Storage;

/// <summary>A sample waiting to be written, with the session it belongs to (if any).</summary>
public sealed record BufferedSample(Sample Sample, Session? Session);

/// <summary>A per-process row waiting to be written, with its session.</summary>
public sealed record BufferedProcessSample(ProcessSample Sample, Session Session);

public sealed class SampleWriter(IDbContextFactory<ThroughputDbContext> factory)
{
    /// <summary>Applies migrations and switches SQLite to WAL so the web app can read while we write.</summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        await ctx.Database.MigrateAsync(ct);
        ctx.EnableWal();
        await CloseOrphanedSessionsAsync(ctx, ct);
    }

    /// <summary>
    /// A session still open at startup belongs to an agent that crashed or lost power. Close it at its
    /// last stored sample so it does not look like it is running forever. Its per-process totals
    /// (written at session end) are missing, but the per-second rows survive.
    /// </summary>
    private static async Task CloseOrphanedSessionsAsync(ThroughputDbContext ctx, CancellationToken ct)
    {
        var open = await ctx.Sessions.Where(s => s.EndUtc == null).ToListAsync(ct);
        foreach (var s in open)
        {
            DateTime? last = await ctx.Samples
                .Where(x => x.SessionId == s.Id)
                .OrderByDescending(x => x.TimestampUtc)
                .Select(x => (DateTime?)x.TimestampUtc)
                .FirstOrDefaultAsync(ct);
            s.EndUtc = last ?? s.StartUtc;
        }
        if (open.Count > 0) await ctx.SaveChangesAsync(ct);
    }

    public async Task<int> EnsureAdapterAsync(string name, string description, long linkSpeedBps, CancellationToken ct)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var adapter = await ctx.Adapters.FirstOrDefaultAsync(a => a.Name == name, ct);
        if (adapter is null)
        {
            adapter = new Adapter { Name = name };
            ctx.Adapters.Add(adapter);
        }
        adapter.Description = description;
        adapter.LinkSpeedBps = linkSpeedBps;
        await ctx.SaveChangesAsync(ct);
        return adapter.Id;
    }

    /// <summary>
    /// Writes new/changed sessions first (so they get ids; finished sessions bring their per-process
    /// and per-remote totals along), then the samples. On failure nothing is lost: the caller keeps
    /// its buffers and retries.
    /// </summary>
    public async Task FlushAsync(
        IReadOnlyList<BufferedSample> samples,
        IReadOnlyList<BufferedProcessSample> processSamples,
        IReadOnlyCollection<Session> sessions,
        CancellationToken ct)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);

        if (sessions.Count > 0)
        {
            foreach (var s in sessions)
            {
                if (s.Id == 0) ctx.Sessions.Add(s);
                else ctx.Sessions.Update(s);
            }
            await ctx.SaveChangesAsync(ct);
        }

        foreach (var b in samples)
        {
            if (b.Session is { Id: 0 })
                throw new InvalidOperationException("Sample refers to a session that has not been saved.");
            b.Sample.SessionId = b.Session?.Id;
        }
        foreach (var b in processSamples)
        {
            if (b.Session.Id == 0)
                throw new InvalidOperationException("Process sample refers to a session that has not been saved.");
            b.Sample.SessionId = b.Session.Id;
        }

        if (samples.Count > 0 || processSamples.Count > 0)
        {
            ctx.Samples.AddRange(samples.Select(b => b.Sample));
            ctx.ProcessSamples.AddRange(processSamples.Select(b => b.Sample));
            await ctx.SaveChangesAsync(ct);
        }
    }
}
