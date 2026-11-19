using Microsoft.EntityFrameworkCore;
using ThroughputMonitor.Core;

namespace ThroughputMonitor.Agent.Storage;

/// <summary>A sample waiting to be written, with the session it belongs to (if any).</summary>
public sealed record BufferedSample(Sample Sample, Session? Session);

public sealed class SampleWriter(IDbContextFactory<ThroughputDbContext> factory)
{
    /// <summary>Applies migrations and switches SQLite to WAL so the web app can read while we write.</summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        await ctx.Database.MigrateAsync(ct);
        ctx.EnableWal();
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
    /// Writes new/changed sessions first (so they get ids), then the samples.
    /// On failure nothing is lost: the caller keeps its buffer and retries.
    /// </summary>
    public async Task FlushAsync(IReadOnlyList<BufferedSample> samples, IReadOnlyCollection<Session> sessions, CancellationToken ct)
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

        if (samples.Count > 0)
        {
            ctx.Samples.AddRange(samples.Select(b => b.Sample));
            await ctx.SaveChangesAsync(ct);
        }
    }
}
