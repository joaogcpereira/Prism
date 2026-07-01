// ============================================================
//  MobileAppInstallConnector.cs
//  Source: intune.mobile-apps. Read-only (DeviceManagementApps.Read.All).
//
//  deviceAppManagement/mobileApps?$expand=installSummary: per managed app, the
//  count of devices it is installed / failed / not-installed / pending on. This
//  is DEPLOYMENT status (what Intune was told to push), complementing the
//  discovered/detectedApps INVENTORY (what is actually present). Per-app, not
//  per-user; stored for the dashboard / future scoring.
// ============================================================
using Prism.Connectors.Graph;
using Prism.Connectors.Json;
using Prism.Warehouse;
using Prism.Warehouse.Model;

namespace Prism.Connectors;

public sealed class MobileAppInstallConnector : IConnector
{
    public string Name => "intune.mobile-apps";

    private readonly GraphClient _graph;
    private readonly IIngestionSink _sink;
    private readonly ConnectorOptions _opts;
    private readonly string _runId;
    private readonly ILogger<MobileAppInstallConnector> _log;

    public MobileAppInstallConnector(GraphClient graph, IIngestionSink sink, ConnectorOptions opts, string runId, ILogger<MobileAppInstallConnector> log)
    {
        _graph = graph;
        _sink = sink;
        _opts = opts;
        _runId = runId;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (!_opts.EnableMobileAppsConnector)
        {
            _log.LogInformation("Intune mobile-apps connector disabled (Prism__EnableMobileAppsConnector); skipping.");
            return;
        }

        string snapshotUtc = DateTime.UtcNow.ToString("o");
        var records = new List<FactMobileAppInstall>();

        await foreach (MobileApp a in _graph.GetPagedAsync<MobileAppsResponse, MobileApp>(
            "deviceAppManagement/mobileApps?$expand=installSummary",
            GraphJsonContext.Default.MobileAppsResponse, x => (x.Value, x.NextLink), ct))
        {
            MobileAppInstallSummary? s = a.InstallSummary;
            records.Add(new FactMobileAppInstall(
                AppId: a.Id, DisplayName: a.DisplayName, Publisher: a.Publisher,
                Platform: CleanType(a.ODataType),
                InstalledDeviceCount: s?.InstalledDeviceCount,
                FailedDeviceCount: s?.FailedDeviceCount,
                NotInstalledDeviceCount: s?.NotInstalledDeviceCount,
                PendingInstallDeviceCount: s?.PendingInstallDeviceCount));
        }

        await _sink.WriteAsync("mobile-app-installs", records.Select(x => Envelope(x, snapshotUtc)), ct);
        _log.LogInformation("Done: {Rows} managed app(s) with install summary.", records.Count);
    }

    // "#microsoft.graph.win32LobApp" -> "win32LobApp" (a rough app-type / platform tag).
    private static string? CleanType(string? odataType)
    {
        if (string.IsNullOrEmpty(odataType)) return null;
        int dot = odataType.LastIndexOf('.');
        return dot >= 0 && dot < odataType.Length - 1 ? odataType[(dot + 1)..] : odataType;
    }

    private EntityEnvelope<T> Envelope<T>(T item, string snapshotUtc) =>
        new EntityEnvelope<T>(Name, _runId, snapshotUtc, item);
}
