// ============================================================
//  ServicePrincipalSignInConnector.cs
//  Source: entra.sp-signins. Read-only (AuditLog.Read.All).
//
//  reports/servicePrincipalSignInActivities (Graph BETA): last sign-in per
//  enterprise application (service principal). Per-app, not per-user - it flags
//  entirely-unused licensed SERVICES (an app nobody has signed into), which
//  complements the per-user per-app sign-ins the SignInConnector already pulls.
//  Stored for the dashboard / app-rationalization; not a per-(user,SKU) signal.
//
//  The endpoint is beta-only, so an ABSOLUTE beta URL is used (the shared
//  GraphClient keeps its v1.0 base; SendWithRetryAsync follows absolute URLs).
// ============================================================
using Prism.Connectors.Graph;
using Prism.Connectors.Json;
using Prism.Warehouse;
using Prism.Warehouse.Model;

namespace Prism.Connectors;

public sealed class ServicePrincipalSignInConnector : IConnector
{
    public string Name => "entra.sp-signins";

    private readonly GraphClient _graph;
    private readonly IIngestionSink _sink;
    private readonly ConnectorOptions _opts;
    private readonly string _runId;
    private readonly ILogger<ServicePrincipalSignInConnector> _log;

    public ServicePrincipalSignInConnector(GraphClient graph, IIngestionSink sink, ConnectorOptions opts, string runId, ILogger<ServicePrincipalSignInConnector> log)
    {
        _graph = graph;
        _sink = sink;
        _opts = opts;
        _runId = runId;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (!_opts.EnableSpSignInConnector)
        {
            _log.LogInformation("Service-principal sign-in connector disabled (Prism__EnableSpSignInConnector); skipping.");
            return;
        }

        string snapshotUtc = DateTime.UtcNow.ToString("o");
        string baseUrl = (_opts.GraphBetaBaseUrl ?? "").TrimEnd('/');
        string url = $"{baseUrl}/reports/servicePrincipalSignInActivities";

        var records = new List<FactServicePrincipalSignIn>();
        await foreach (SpSignIn s in _graph.GetPagedAsync<SpSignInResponse, SpSignIn>(
            url, GraphJsonContext.Default.SpSignInResponse, x => (x.Value, x.NextLink), ct))
        {
            if (string.IsNullOrEmpty(s.AppId)) continue;
            records.Add(new FactServicePrincipalSignIn(
                AppId: s.AppId, DisplayName: null,
                LastSignInUtc: s.LastSignInActivity?.LastSignInDateTime));
        }

        await _sink.WriteAsync("sp-signins", records.Select(x => Envelope(x, snapshotUtc)), ct);
        _log.LogInformation("Done: {Rows} enterprise app sign-in activity row(s).", records.Count);
    }

    private EntityEnvelope<T> Envelope<T>(T item, string snapshotUtc) =>
        new EntityEnvelope<T>(Name, _runId, snapshotUtc, item);
}
