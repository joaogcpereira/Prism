// ============================================================
//  SignInConnector.cs
//  Source: entra.app-signins. Read-only (AuditLog.Read.All +
//  Directory.Read.All).
//
//  Closes the biggest evidence gap: WEB/SERVICE-first usage. The
//  agent and MDE hunting only see desktop EXEs; getOffice365Active-
//  UserDetail only sees Exchange/SharePoint/OneDrive/Teams/Yammer/
//  Skype. A user who lives in the Power BI *service* or Project *web*
//  is invisible to all of them - but every such session is an Entra
//  sign-in. We pull /auditLogs/signIns for the configured apps and
//  aggregate to "last time user U signed into app A (+ count)".
//
//  Volume control: Entra only retains ~30 days of sign-ins, and the
//  store is large, so we ALWAYS server-side $filter by createdDateTime
//  AND (when app ids are configured) by appId, and page with the
//  throttle-aware GraphClient. Aggregation happens in-memory; only the
//  per-(user,app) summary is persisted, never raw sign-in events.
// ============================================================
using Prism.Connectors.Graph;
using Prism.Connectors.Json;
using Prism.Warehouse;
using Prism.Warehouse.Model;

namespace Prism.Connectors;

public sealed class SignInConnector : IConnector
{
    public string Name => "entra.app-signins";

    private readonly GraphClient _graph;
    private readonly IIngestionSink _sink;
    private readonly ConnectorOptions _opts;
    private readonly string _runId;
    private readonly ILogger<SignInConnector> _log;

    public SignInConnector(GraphClient graph, IIngestionSink sink, ConnectorOptions opts, string runId, ILogger<SignInConnector> log)
    {
        _graph = graph;
        _sink = sink;
        _opts = opts;
        _runId = runId;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (!_opts.EnableSignInConnector)
        {
            _log.LogInformation("Per-app sign-in connector disabled (Prism__EnableSignInConnector); skipping.");
            return;
        }

        int days = Math.Clamp(_opts.SignInLookbackDays, 1, 30);                 // Entra retains ~30d
        string sinceUtc = DateTime.UtcNow.AddDays(-days).ToString("yyyy-MM-ddTHH:mm:ssZ");
        string snapshotUtc = DateTime.UtcNow.ToString("o");

        // Server-side $filter: time-bounded, SUCCESSFUL sign-ins only (errorCode 0 drops
        // failed-auth noise), narrowed by appId when the operator listed the licence-relevant
        // apps (Power BI, Project, Visio web, etc.).
        string[] appIds = (_opts.SignInAppIds ?? [])
            .Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).Distinct().ToArray();
        string appClause = appIds.Length > 0
            ? " and (" + string.Join(" or ", appIds.Select(a => $"appId eq '{a.Replace("'", "''")}'")) + ")"
            : "";
        string baseFilter = $"createdDateTime ge {sinceUtc} and status/errorCode eq 0" + appClause;

        bool pullNonInteractive = _opts.SignInIncludeNonInteractive && appIds.Length > 0;
        _log.LogInformation("Reading per-app sign-ins for {Apps} app(s), {Days}d window (non-interactive: {NI})...",
            appIds.Length == 0 ? "ALL" : appIds.Length.ToString(), days, pullNonInteractive);

        // Aggregate in-memory: (userId|appId) -> last time + count. Raw events never persisted.
        var agg = new Dictionary<string, FactAppSignIn>(StringComparer.OrdinalIgnoreCase);
        long scanned = 0;

        // Pass 1: interactive sign-ins (the endpoint's default event type).
        scanned += await PageIntoAsync(agg, baseFilter, days, ct);

        // Pass 2: non-interactive sign-ins - a user's own client silently redeeming a token to
        // reach the licensed service, i.e. real usage. Very high volume, so only when the query
        // is bounded to specific apps; otherwise it would scan the whole tenant's token traffic.
        if (pullNonInteractive)
        {
            // Degrade gracefully: if a tenant rejects the non-interactive $filter, keep the
            // interactive results rather than failing the whole connector.
            try
            {
                scanned += await PageIntoAsync(agg, baseFilter + " and signInEventTypes/any(t:t eq 'nonInteractiveUser')", days, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning("Per-app sign-ins: non-interactive pass failed ({Msg}); continuing with interactive only. " +
                    "If this is a Graph $filter rejection, set Prism__SignInIncludeNonInteractive=false.", ex.Message);
            }
        }
        else if (_opts.SignInIncludeNonInteractive)
            _log.LogInformation("Per-app sign-ins: non-interactive pass skipped (no SignInAppIds configured - it would be unbounded).");

        await _sink.WriteAsync("app-signins", agg.Values.Select(x => Envelope(x, snapshotUtc)), ct);
        _log.LogInformation("Per-app sign-ins: {Pairs} (user, app) pair(s) from {Scanned} event(s), {Days}d window.",
            agg.Count, scanned, days);
    }

    // Page one $filter query and fold each sign-in into the (userId|appId) aggregate. Called
    // once per event-type pass; the shared dictionary merges interactive + non-interactive.
    private async Task<long> PageIntoAsync(Dictionary<string, FactAppSignIn> agg, string filter, int days, CancellationToken ct)
    {
        string url =
            "auditLogs/signIns?$top=1000" +
            "&$select=userId,userPrincipalName,appId,appDisplayName,createdDateTime" +
            "&$filter=" + Uri.EscapeDataString(filter);

        long scanned = 0;
        await foreach (GraphSignIn s in _graph.GetPagedAsync<GraphSignInsResponse, GraphSignIn>(
            url, GraphJsonContext.Default.GraphSignInsResponse, p => (p.Value, p.NextLink), ct))
        {
            scanned++;
            if (string.IsNullOrEmpty(s.UserId) || string.IsNullOrEmpty(s.AppId)) continue;
            string key = s.UserId + "|" + s.AppId;
            if (agg.TryGetValue(key, out FactAppSignIn? cur))
                agg[key] = cur with { LastSignInUtc = Later(cur.LastSignInUtc, s.CreatedDateTime), SignInCount = cur.SignInCount + 1 };
            else
                agg[key] = new FactAppSignIn(
                    UserId: s.UserId, UserPrincipalName: s.UserPrincipalName,
                    AppId: s.AppId, AppDisplayName: s.AppDisplayName,
                    LastSignInUtc: s.CreatedDateTime, SignInCount: 1, WindowDays: days);
        }
        return scanned;
    }

    private static string? Later(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a)) return b;
        if (string.IsNullOrEmpty(b)) return a;
        return string.CompareOrdinal(a, b) >= 0 ? a : b;   // ISO-8601 UTC => lexical compare is chronological
    }

    private EntityEnvelope<T> Envelope<T>(T item, string snapshotUtc) =>
        new EntityEnvelope<T>(Name, _runId, snapshotUtc, item);
}
