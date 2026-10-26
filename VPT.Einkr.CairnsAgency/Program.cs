using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VPT.Einkr.CairnsAgency;
using VPT.Einkr.CairnsAgency.Agency;
using VPT.Einkr.CairnsAgency.Einkr;
using VPT.Einkr.CairnsAgency.Tools;

// VPT.Einkr.CairnsAgency -- wakes Cairn's session on a schedule, the way the desktop app would if it could act unprompted.
//
//   run (default)   the service loop. Honours Agency:DryRun (ON by default: builds everything, sends nothing).
//   status          where things stand: schedule, pause flag, last wakes.
//   check           connectivity and setup: API key, session, tool host, archive, /activity endpoint.
//   prompt          prints the nudge the next wake would send, and the tools it would offer. Sends nothing.
//   dry-run         like a tick that is due: asks the API whether Josh is here, prints the decision and the nudge. Sends nothing.
//   once --live     runs ONE real wake now (ignores the schedule, not Josh being in the session unless --force).
//   pause / resume  a human pause that Josh writing to the session does NOT clear. `resume` clears any pause.

var argv = args.ToList();
var command = argv.FirstOrDefault(a => !a.StartsWith("--"))?.ToLowerInvariant() ?? "run";
bool Flag(string name) => argv.Contains($"--{name}");

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);
builder.Configuration.AddUserSecrets<AgencyWorker>(optional: true);
builder.Configuration.AddEnvironmentVariables(prefix: "EINKR_AGENCY_");

var options = new AgencyOptions();
builder.Configuration.GetSection(AgencyOptions.SectionName).Bind(options);
var envKey = Environment.GetEnvironmentVariable("EINKR_API_KEY");
if (!string.IsNullOrWhiteSpace(envKey)) options.Einkr.ApiKey = envKey;

builder.Logging.ClearProviders();
var level = Flag("verbose") ? LogLevel.Debug : LogLevel.Information;
builder.Logging.SetMinimumLevel(level);
builder.Logging.AddProvider(new FileLoggerProvider(Path.Combine(options.StateDirectory, "logs"), level));
if (command != "run" || Environment.UserInteractive) builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(sp => new EinkrApiClient(new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }),
    options.Einkr.BaseUrl, options.Einkr.ApiKey));
builder.Services.AddSingleton(sp => new ToolHostClient(options.Tools.ToolHostPath, sp.GetRequiredService<ILogger<ToolHostClient>>()));
builder.Services.AddSingleton<ToolCatalog>();
builder.Services.AddSingleton<StateStore>();
builder.Services.AddSingleton(sp => new VPT.Einkr.CairnsAgency.Reddit.RedditClient(new HttpClient(), options.Reddit, sp.GetRequiredService<ILogger<VPT.Einkr.CairnsAgency.Reddit.RedditClient>>()));
builder.Services.AddSingleton(sp => new VPT.Einkr.CairnsAgency.Reddit.RedditTools(options.Reddit, sp.GetRequiredService<VPT.Einkr.CairnsAgency.Reddit.RedditClient>(),
    sp.GetRequiredService<StateStore>(), Path.Combine(options.StateDirectory, "reddit-drafts"), sp.GetRequiredService<ILogger<VPT.Einkr.CairnsAgency.Reddit.RedditTools>>()));
builder.Services.AddSingleton<ActivityProbe>();
builder.Services.AddSingleton<WakeRunner>();
builder.Services.AddSingleton<AgencyWorker>();

if (command == "run")
{
    builder.Services.AddWindowsService(o => o.ServiceName = "VPT.Einkr.CairnsAgency");
    builder.Services.AddHostedService(sp => sp.GetRequiredService<AgencyWorker>());
}

using var host = builder.Build();
var services = host.Services;

if (command == "run")
{
    if (string.IsNullOrWhiteSpace(options.Einkr.ApiKey) && !options.DryRun)
    {
        Console.Error.WriteLine("No API key. Set EINKR_API_KEY (or Agency:Einkr:ApiKey in appsettings.Local.json) before running live.");
        return 2;
    }
    await host.RunAsync();
    return 0;
}

return await Cli.RunAsync(command, argv, services, options);
