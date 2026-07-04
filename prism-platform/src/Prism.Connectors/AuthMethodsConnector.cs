// ============================================================
//  AuthMethodsConnector.cs
//  Source: graph.authmethods. Read-only. OFF by default
//  (Prism__EnableAuthMethods=true; needs AuditLog.Read.All,
//  which the sign-in activity pull already requires).
//
//  Pulls /reports/authenticationMethods/userRegistrationDetails:
//  per-user MFA/SSPR registration state and the registered method
//  list. WHY: a licensed holder who never even registered an
//  authentication method was, in practice, never onboarded - a
//  POSITIVE corroboration for NEVER_ACTIVE that plain telemetry
//  absence can't provide. (Per doctrine it still never triggers a
//  reclaim by itself; it raises the review ranking and gives the
//  reviewer a concrete, checkable fact.)
// ============================================================
using Prism.Connectors.Graph;
using Prism.Connectors.Json;
using Prism.Warehouse;
using Prism.Warehouse.Model;

namespace Prism.Connectors;

public sealed class AuthMethodsConnector : IConnector
{
    private const string SourceName = "graph.authmethods";
    public string Name => SourceName;

    private readonly GraphClient _graph;
    private readonly IIngestionSink _sink;
    private readonly ConnectorOptions _opts;
    private readonly string _runId;
    private readonly ILogger<AuthMethodsConnector> _log;

    public AuthMethodsConnector(GraphClient graph, IIngestionSink sink, ConnectorOptions opts, string runId,
        ILogger<AuthMethodsConnector> log)
    {
        _graph = graph; _sink = sink; _opts = opts; _runId = runId; _log = log;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (!_opts.EnableAuthMethods)
        {
            _log.LogInformation("Auth-methods connector disabled (Prism__EnableAuthMethods=false); skipping.");
            return;
        }

        string snapshotUtc = DateTime.UtcNow.ToString("o");
        _log.LogInformation("Fetching authentication-method registration details...");

        var rows = new List<FactAuthMethod>();
        await foreach (AuthRegistrationDetail d in _graph.GetPagedAsync<AuthRegistrationResponse, AuthRegistrationDetail>(
            "reports/authenticationMethods/userRegistrationDetails?$top=" + Math.Min(_opts.PageSize, 999),
            GraphJsonContext.Default.AuthRegistrationResponse, p => (p.Value, p.NextLink), ct))
        {
            if (string.IsNullOrEmpty(d.Id)) continue;
            string? methods = d.MethodsRegistered is { Count: > 0 } ? string.Join(",", d.MethodsRegistered) : null;
            if (methods is { Length: > 1024 }) methods = methods[..1024];   // column width guard
            rows.Add(new FactAuthMethod(
                UserId: d.Id,
                UserPrincipalName: d.UserPrincipalName,
                IsAdmin: d.IsAdmin,
                IsMfaRegistered: d.IsMfaRegistered,
                IsMfaCapable: d.IsMfaCapable,
                IsPasswordlessCapable: d.IsPasswordlessCapable,
                IsSsprRegistered: d.IsSsprRegistered,
                IsSsprEnabled: d.IsSsprEnabled,
                IsSsprCapable: d.IsSsprCapable,
                MethodsRegistered: methods,
                DefaultMethod: d.UserPreferredMethodForSecondaryAuthentication,
                LastUpdatedDateTime: d.LastUpdatedDateTime));
        }

        int unregistered = rows.Count(r => r.IsMfaRegistered == false);
        _log.LogInformation("Auth methods: {Rows} user(s); {None} with NO MFA registered.", rows.Count, unregistered);
        await _sink.WriteAsync("auth-methods", rows.Select(x => Envelope(x, snapshotUtc)), ct);
    }

    private EntityEnvelope<T> Envelope<T>(T item, string snapshotUtc) => new(Name, _runId, snapshotUtc, item);
}
