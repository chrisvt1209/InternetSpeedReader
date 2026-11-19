using Microsoft.Extensions.Options;

namespace ThroughputMonitor.Agent.Maintenance;

/// <summary>Runs rollups every few minutes and the retention clean-up about every six hours.</summary>
public sealed class MaintenanceWorker(
    MaintenanceService maintenance,
    IOptions<AgentOptions> options,
    TimeProvider time,
    ILogger<MaintenanceWorker> log) : BackgroundService
{
    private static readonly TimeSpan RetentionEvery = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            // Let the sampling worker create/migrate the database first.
            await Task.Delay(TimeSpan.FromSeconds(30), time, ct);

            var interval = TimeSpan.FromMinutes(Math.Max(1, options.Value.RollupIntervalMinutes));
            using var timer = new PeriodicTimer(interval, time);
            DateTimeOffset lastRetention = DateTimeOffset.MinValue;

            do
            {
                var now = time.GetUtcNow();
                bool retentionDue = now - lastRetention >= RetentionEvery;
                try
                {
                    await maintenance.RunOnceAsync(now.UtcDateTime, retentionDue, ct);
                    if (retentionDue) lastRetention = now;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogError(ex, "Maintenance pass failed; will retry in {Interval}", interval);
                }
            }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) { }
    }
}
