using Microsoft.EntityFrameworkCore;
using ThroughputMonitor.Agent;
using ThroughputMonitor.Agent.Sampling;
using ThroughputMonitor.Agent.Storage;
using ThroughputMonitor.Core;

// Content root = the exe folder, so appsettings.json is found no matter where the agent is started from.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

var section = builder.Configuration.GetSection(AgentOptions.SectionName);
builder.Services.Configure<AgentOptions>(section);
var options = section.Get<AgentOptions>() ?? new AgentOptions();

string dbPath = string.IsNullOrWhiteSpace(options.DatabasePath)
    ? ThroughputDbContext.DefaultDatabasePath()
    : Path.GetFullPath(options.DatabasePath);
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
builder.Services.AddDbContextFactory<ThroughputDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));

var nic = AdapterSelector.Select(options.AdapterNameContains)
    ?? throw new InvalidOperationException(
        "No suitable network adapter found. Set Agent:AdapterNameContains to part of the adapter name.");

builder.Services.AddSingleton<ICounterSource>(new NetworkCounterSource(nic));
if (!OperatingSystem.IsWindows())
    throw new PlatformNotSupportedException("The agent currently supports Windows only.");
builder.Services.AddSingleton<ISystemLoadSource, PerformanceCounterLoadSource>();
builder.Services.AddSingleton<SampleWriter>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHostedService<SamplingWorker>();

var host = builder.Build();
host.Services.GetRequiredService<ILogger<Program>>().LogInformation("Database: {Path}", dbPath);
await host.RunAsync();
