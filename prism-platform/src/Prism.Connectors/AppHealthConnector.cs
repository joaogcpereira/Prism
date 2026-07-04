// ============================================================
//  AppHealthConnector.cs
//  Source: intune.app-health. Read-only (DeviceManagementManagedDevices.Read.All).
//
//  Intune Endpoint Analytics - App Health
//  (deviceManagement/userExperienceAnalyticsAppHealthApplicationPerformance):
//  tenant/app-level app usage duration + active-device count + crash/hang stats,
//  collected agent-independently. Corroborates whether an app is actually used
//  across the fleet (e.g. a Visio/Project desktop with near-zero usage org-wide),
//  for devices the Prism agent doesn't cover. Stored for the dashboard / future
//  scoring; NOT a per-user signal, so it does not feed per-(user,SKU) scoring.
// ============================================================
using Prism.Connectors.Graph;
using Prism.Connectors.Json;
using Prism.Warehouse;
using Prism.Warehouse.Model;

namespace Prism.Connectors;

public sealed class AppHealthConnector : IConnector
{
    public string Name => "intune.app-health";

    private readonly GraphClient _graph;
    private readonly IIngestionSink _sink;
    private readonly ConnectorOptions _opts;
    private readonly string _runId;
    private readonly ILogger<AppHealthConnector> _log;

    public AppHealthConnector(GraphClient graph, IIngestionSink sink, ConnectorOptions opts, string runId, ILogger<AppHealthConnector> log)
    {
        _graph = graph;
        _sink = sink;
        _opts = opts;
        _runId = runId;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (!_opts.EnableAppHealthConnector)
        {
            _log.LogInformation("App Health connector disabled (Prism__EnableAppHealthConnector); skipping.");
            return;
        }

        string snapshotUtc = DateTime.UtcNow.ToString("o");
        var records = new List<FactAppHealth>();

        await foreach (AppHealthPerf p in _graph.GetPagedAsync<AppHealthResponse, AppHealthPerf>(
            "deviceManagement/userExperienceAnalyticsAppHealthApplicationPerformance",
            GraphJsonContext.Default.AppHealthResponse, x => (x.Value, x.NextLink), ct))
        {
            records.Add(new FactAppHealth(
                AppName: p.AppName, AppDisplayName: p.AppDisplayName, AppPublisher: p.AppPublisher,
                AppUsageDuration: p.AppUsageDuration, ActiveDeviceCount: p.ActiveDeviceCount,
                AppCrashCount: p.AppCrashCount, AppHangCount: p.AppHangCount,
                AppHealthScore: p.AppHealthScore, MeanTimeToFailureInMinutes: p.MeanTimeToFailureInMinutes));
        }

        await _sink.WriteAsync("app-health", records.Select(x => Envelope(x, snapshotUtc)), ct);
        _log.LogInformation("Done: {Rows} app-health row(s).", records.Count);
    }

    private EntityEnvelope<T> Envelope<T>(T item, string snapshotUtc) =>
        new EntityEnvelope<T>(Name, _runId, snapshotUtc, item);
}
