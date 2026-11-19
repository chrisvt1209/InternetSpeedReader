using Microsoft.EntityFrameworkCore;
using ThroughputMonitor.Core;
using ThroughputMonitor.Web.Components;
using ThroughputMonitor.Web.Data;

var builder = WebApplication.CreateBuilder(args);

// Local use only: unless a launch profile, --urls or ASPNETCORE_URLS says otherwise, listen on localhost.
// (Not in appsettings.json: a "Urls" value there would override the https launch profile.)
if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
    builder.WebHost.UseUrls("http://localhost:5294");

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var section = builder.Configuration.GetSection(WebOptions.SectionName);
builder.Services.Configure<WebOptions>(section);
var options = section.Get<WebOptions>() ?? new WebOptions();

// The web app only READS what the agent writes: the connection is opened read-only.
builder.Services.AddDbContextFactory<ThroughputDbContext>(o =>
    o.UseSqlite($"Data Source={options.ResolvedDatabasePath};Mode=ReadOnly"));
builder.Services.AddScoped<SessionQueryService>();
builder.Services.AddSingleton<LiveService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
