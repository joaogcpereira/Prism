// ============================================================
//  Program.cs  (Prism.Connectors)
//  Job-style entry point: configure -> run the M365 connector once
//  -> exit (0 ok, 1 failure). This shape drops straight into a
//  Container Apps Job, a scheduled task, or a timer-triggered
//  Function (call LicenseConnector.RunAsync from the trigger).
// ============================================================
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Prism.Connectors;
using Prism.Connectors.Cost;
using Prism.Connectors.Defender;
using Prism.Connectors.Graph;
using Prism.Warehouse;

var builder = Host.CreateApplicationBuilder(args);

var opts = new ConnectorOptions();
builder.Configuration.GetSection(ConnectorOptions.SectionName).Bind(opts);

string runId = $"{DateTime.UtcNow:yyyyMMddTHHmmss}-{Guid.NewGuid():N}".Substring(0, 22);

builder.Services.AddSingleton(opts);
builder.Services.AddSingleton(new AzureTokenProvider(opts.ManagedIdentityClientId));
builder.Services.AddSingleton<GraphClient>(sp =>
    new GraphClient(opts.GraphBaseUrl, sp.GetRequiredService<AzureTokenProvider>(), opts.MaxRetries,
                    opts.ThrottleMaxRetries, opts.MaxRetryAfterSeconds,
                    sp.GetRequiredService<ILogger<GraphClient>>()));
builder.Services.AddSingleton<CostManagementClient>(sp =>
    new CostManagementClient(sp.GetRequiredService<AzureTokenProvider>(), opts.CostApiVersion, opts.MaxRetries,
                             sp.GetRequiredService<ILogger<CostManagementClient>>()));
builder.Services.AddSingleton<IIngestionSink>(sp =>
    opts.Sink.Equals("sql", StringComparison.OrdinalIgnoreCase)
        ? new SqlIngestionSink(
            new WarehouseOptions { ConnectionString = opts.ConnectionString ?? "" },
            sp.GetRequiredService<ILogger<SqlIngestionSink>>())
        : new FileIngestionSink(opts.LandingDirectory, runId, sp.GetRequiredService<ILogger<FileIngestionSink>>()));
builder.Services.AddSingleton<LicenseConnector>(sp =>
    new LicenseConnector(
        sp.GetRequiredService<GraphClient>(),
        sp.GetRequiredService<IIngestionSink>(),
        opts, runId,
        sp.GetRequiredService<ILogger<LicenseConnector>>()));
builder.Services.AddSingleton<ServiceUsageConnector>(sp =>
    new ServiceUsageConnector(
        sp.GetRequiredService<GraphClient>(),
        sp.GetRequiredService<IIngestionSink>(),
        opts, runId,
        sp.GetRequiredService<ILogger<ServiceUsageConnector>>()));
builder.Services.AddSingleton<M365AppUsageConnector>(sp =>
    new M365AppUsageConnector(
        sp.GetRequiredService<GraphClient>(),
        sp.GetRequiredService<IIngestionSink>(),
        opts, runId,
        sp.GetRequiredService<ILogger<M365AppUsageConnector>>()));
builder.Services.AddSingleton<CopilotUsageConnector>(sp =>
    new CopilotUsageConnector(
        sp.GetRequiredService<GraphClient>(),
        sp.GetRequiredService<IIngestionSink>(),
        opts, runId,
        sp.GetRequiredService<ILogger<CopilotUsageConnector>>()));
builder.Services.AddSingleton<TeamsActivityConnector>(sp =>
    new TeamsActivityConnector(sp.GetRequiredService<GraphClient>(), sp.GetRequiredService<IIngestionSink>(),
        opts, runId, sp.GetRequiredService<ILogger<TeamsActivityConnector>>()));
builder.Services.AddSingleton<ServiceDetailConnector>(sp =>
    new ServiceDetailConnector(sp.GetRequiredService<GraphClient>(), sp.GetRequiredService<IIngestionSink>(),
        opts, runId, sp.GetRequiredService<ILogger<ServiceDetailConnector>>()));
builder.Services.AddSingleton<AppHealthConnector>(sp =>
    new AppHealthConnector(sp.GetRequiredService<GraphClient>(), sp.GetRequiredService<IIngestionSink>(),
        opts, runId, sp.GetRequiredService<ILogger<AppHealthConnector>>()));
builder.Services.AddSingleton<MobileAppInstallConnector>(sp =>
    new MobileAppInstallConnector(sp.GetRequiredService<GraphClient>(), sp.GetRequiredService<IIngestionSink>(),
        opts, runId, sp.GetRequiredService<ILogger<MobileAppInstallConnector>>()));
builder.Services.AddSingleton<ServicePrincipalSignInConnector>(sp =>
    new ServicePrincipalSignInConnector(sp.GetRequiredService<GraphClient>(), sp.GetRequiredService<IIngestionSink>(),
        opts, runId, sp.GetRequiredService<ILogger<ServicePrincipalSignInConnector>>()));
builder.Services.AddSingleton<SignInConnector>(sp =>
    new SignInConnector(
        sp.GetRequiredService<GraphClient>(),
        sp.GetRequiredService<IIngestionSink>(),
        opts, runId,
        sp.GetRequiredService<ILogger<SignInConnector>>()));
builder.Services.AddSingleton<CostConnector>(sp =>
    new CostConnector(
        sp.GetRequiredService<CostManagementClient>(),
        sp.GetRequiredService<IIngestionSink>(),
        opts, runId,
        sp.GetRequiredService<ILogger<CostConnector>>()));
builder.Services.AddSingleton<IntuneConnector>(sp =>
    new IntuneConnector(
        sp.GetRequiredService<GraphClient>(),
        sp.GetRequiredService<IIngestionSink>(),
        opts, runId,
        sp.GetRequiredService<ILogger<IntuneConnector>>()));
builder.Services.AddSingleton<DefenderConnector>(sp =>
{
    // Build the Defender client only when enabled and fully configured; otherwise the
    // connector receives null and skips cleanly.
    DefenderClient? dc = null;
    if (opts.EnableDefenderConnector
        && !string.IsNullOrWhiteSpace(opts.DefenderApiBaseUrl)
        && !string.IsNullOrWhiteSpace(opts.DefenderTenantId)
        && !string.IsNullOrWhiteSpace(opts.DefenderAppId))
    {
        dc = new DefenderClient(
            opts.DefenderApiBaseUrl!, opts.DefenderTenantId!, opts.DefenderAppId!, opts.DefenderApiScope,
            sp.GetRequiredService<AzureTokenProvider>(), opts.MaxRetries,
            sp.GetRequiredService<ILogger<DefenderClient>>());
    }
    return new DefenderConnector(dc, sp.GetRequiredService<IIngestionSink>(), opts, runId,
                                 sp.GetRequiredService<ILogger<DefenderConnector>>());
});
// One MdeClient shared by the Defender for Endpoint connectors (inventory + hunting),
// built once on first use and only when enabled and fully configured. Registered as a
// Lazy<> singleton so construction is thread-safe (the previous captured-locals closure
// was not, and would double-build / return null under any concurrent resolution).
builder.Services.AddSingleton(sp => new Lazy<MdeClient?>(() =>
    (opts.EnableMdeConnector || opts.EnableMdeHunting)
        && !string.IsNullOrWhiteSpace(opts.MdeApiBaseUrl)
        && !string.IsNullOrWhiteSpace(opts.MdeTenantId)
        && !string.IsNullOrWhiteSpace(opts.MdeAppId)
        ? new MdeClient(
            opts.MdeApiBaseUrl!, opts.MdeTenantId!, opts.MdeAppId!, opts.MdeApiScope,
            sp.GetRequiredService<AzureTokenProvider>(), opts.MaxRetries, opts.ThrottleMaxRetries,
            opts.MaxRetryAfterSeconds, sp.GetRequiredService<ILogger<MdeClient>>())
        : null));
builder.Services.AddSingleton<MdeSoftwareConnector>(sp =>
    new MdeSoftwareConnector(sp.GetRequiredService<Lazy<MdeClient?>>().Value, sp.GetRequiredService<IIngestionSink>(), opts, runId,
                             sp.GetRequiredService<ILogger<MdeSoftwareConnector>>()));
builder.Services.AddSingleton<MdeHuntingConnector>(sp =>
    new MdeHuntingConnector(sp.GetRequiredService<Lazy<MdeClient?>>().Value, sp.GetRequiredService<IIngestionSink>(), opts, runId,
                            sp.GetRequiredService<ILogger<MdeHuntingConnector>>()));
builder.Services.AddSingleton<PriceConnector>(sp =>
    new PriceConnector(
        sp.GetRequiredService<AzureTokenProvider>(),
        opts, runId,
        sp.GetRequiredService<ILogger<PriceConnector>>()));

using IHost host = builder.Build();
ILogger log = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Prism.Connectors");

log.LogInformation("Prism connector run {RunId} starting (graph={Graph}).", runId, opts.GraphBaseUrl);

// All registered connectors, optionally filtered by Prism:Enabled.
// (Desktop app-usage no longer has a connector: the gateway writes it to the
//  warehouse directly. The old gateway-landing loader is retired.)
IConnector[] all =
[
    host.Services.GetRequiredService<LicenseConnector>(),
    host.Services.GetRequiredService<ServiceUsageConnector>(),
    host.Services.GetRequiredService<M365AppUsageConnector>(),
    host.Services.GetRequiredService<CopilotUsageConnector>(),
    host.Services.GetRequiredService<TeamsActivityConnector>(),
    host.Services.GetRequiredService<ServiceDetailConnector>(),
    host.Services.GetRequiredService<AppHealthConnector>(),
    host.Services.GetRequiredService<MobileAppInstallConnector>(),
    host.Services.GetRequiredService<ServicePrincipalSignInConnector>(),
    host.Services.GetRequiredService<SignInConnector>(),
    host.Services.GetRequiredService<CostConnector>(),
    host.Services.GetRequiredService<IntuneConnector>(),
    host.Services.GetRequiredService<DefenderConnector>(),
    host.Services.GetRequiredService<MdeSoftwareConnector>(),
    host.Services.GetRequiredService<MdeHuntingConnector>(),
    host.Services.GetRequiredService<PriceConnector>(),
];
IConnector[] toRun = opts.Enabled is { Length: > 0 }
    ? all.Where(c => opts.Enabled.Contains(c.Name, StringComparer.OrdinalIgnoreCase)).ToArray()
    : all;

// Run to completion by default. Graph throttling can legitimately stretch a full
// per-device sweep past any fixed deadline, so there is NO artificial timeout unless
// Prism__OverallTimeoutMinutes > 0. SIGTERM (Container Apps stop) and SIGINT (Ctrl+C)
// still cancel cooperatively so the job shuts down cleanly when actually asked to.
using var cts = new CancellationTokenSource();
using var sigTerm = System.Runtime.InteropServices.PosixSignalRegistration.Create(
    System.Runtime.InteropServices.PosixSignal.SIGTERM, c => { c.Cancel = true; cts.Cancel(); });
using var sigInt = System.Runtime.InteropServices.PosixSignalRegistration.Create(
    System.Runtime.InteropServices.PosixSignal.SIGINT, c => { c.Cancel = true; cts.Cancel(); });
if (opts.OverallTimeoutMinutes > 0)
{
    cts.CancelAfter(TimeSpan.FromMinutes(opts.OverallTimeoutMinutes));
    log.LogInformation("Overall safety timeout: {Min} min.", opts.OverallTimeoutMinutes);
}
else
{
    log.LogInformation("No artificial run deadline — running to completion (honoring Graph Retry-After).");
}
int failures = 0;

foreach (IConnector connector in toRun)
{
    try
    {
        log.LogInformation("== connector: {Name} ==", connector.Name);
        await connector.RunAsync(cts.Token);
    }
    catch (Exception ex)
    {
        failures++;
        // One connector failing doesn't abort the rest - partial data is still useful.
        log.LogError(ex, "Connector {Name} FAILED: {Message}", connector.Name, ex.Message);
    }
}

if (failures == 0) { log.LogInformation("Run {RunId} completed ({Count} connector(s)).", runId, toRun.Length); return 0; }
log.LogError("Run {RunId} completed with {Failures} failed connector(s).", runId, failures);
return 1;
