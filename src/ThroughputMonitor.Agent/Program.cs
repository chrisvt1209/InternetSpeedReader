using System.Runtime.Versioning;
using Microsoft.EntityFrameworkCore;
using Serilog;
using ThroughputMonitor.Agent;
using ThroughputMonitor.Agent.Commands;
using ThroughputMonitor.Agent.Maintenance;
using ThroughputMonitor.Agent.Processes;
using ThroughputMonitor.Agent.Sampling;
using ThroughputMonitor.Agent.Storage;
using ThroughputMonitor.Core;

[assembly: SupportedOSPlatform("windows")]   // performance counters + ETW: the agent is Windows-only

// Content root = the exe folder, so appsettings.json is found no matter where the agent is started from
// (a Windows Service starts in C:\Windows\System32).
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

// Machine-specific overrides that survive re-installing/publishing the agent.
builder.Configuration.AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: false);

var section = builder.Configuration.GetSection(AgentOptions.SectionName);
builder.Services.Configure<AgentOptions>(section);
var options = section.Get<AgentOptions>() ?? new AgentOptions();

string dbPath = string.IsNullOrWhiteSpace(options.DatabasePath)
    ? ThroughputDbContext.DefaultDatabasePath()
    : Path.GetFullPath(options.DatabasePath);
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

string logDir = string.IsNullOrWhiteSpace(options.LogDirectory)
    ? Path.Combine(Path.GetDirectoryName(dbPath)!, "logs")
    : Path.GetFullPath(options.LogDirectory);

// Levels come from the "Serilog" section of appsettings.json; sinks are set here because the
// log folder is computed. A service has no console, so the file is what matters.
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        Path.Combine(logDir, "agent-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: options.RetainedLogFiles,
        fileSizeLimitBytes: 20_000_000,
        rollOnFileSizeLimit: true,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

try
{
    builder.Services.AddWindowsService(o => o.ServiceName = "ThroughputMonitorAgent");
    builder.Services.AddSerilog(Log.Logger, dispose: true);

    builder.Services.AddDbContextFactory<ThroughputDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));

    var nic = AdapterSelector.Select(options.AdapterNameContains)
        ?? throw new InvalidOperationException(
            "No suitable network adapter found. Set Agent:AdapterNameContains to part of the adapter name.");

    builder.Services.AddSingleton<ICounterSource>(new NetworkCounterSource(nic));
    if (!OperatingSystem.IsWindows())
        throw new PlatformNotSupportedException("The agent currently supports Windows only.");
    builder.Services.AddSingleton<ISystemLoadSource, PerformanceCounterLoadSource>();

    if (options.TrackProcesses && OperatingSystem.IsWindows() && IsElevated())
    {
        // Registered before the worker, so the ETW session is running when sampling starts.
        builder.Services.AddSingleton<ProcessIdentityResolver>();
        builder.Services.AddSingleton<EtwProcessTrafficSource>();
        builder.Services.AddSingleton<IProcessTrafficSource>(sp => sp.GetRequiredService<EtwProcessTrafficSource>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<EtwProcessTrafficSource>());
    }
    else
    {
        builder.Services.AddSingleton<IProcessTrafficSource, NullProcessTrafficSource>();
        if (options.TrackProcesses)
            Log.Warning("Not running as administrator: per-process traffic is disabled, adapter totals are still recorded");
        else
            Log.Information("Per-process tracking is turned off in the configuration");
    }

    builder.Services.AddSingleton<SampleWriter>();
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddHostedService<SamplingWorker>();
    builder.Services.AddSingleton<MarkCoordinator>();
    if (options.EnableCommandPipe)
        builder.Services.AddHostedService<PipeServerWorker>();
    builder.Services.AddSingleton<MaintenanceService>();
    builder.Services.AddHostedService<MaintenanceWorker>();

    var host = builder.Build();
    Log.Information("Throughput agent starting. Database: {Path}. Logs: {LogDir}", dbPath, logDir);
    await host.RunAsync();
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "The agent stopped because of an unhandled error");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

static bool IsElevated() =>
    OperatingSystem.IsWindows()
    && new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
