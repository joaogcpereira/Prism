// ============================================================
//  LicenseConnector.cs
//  Source: m365.identity-license. Read-only.
//  Pulls Entra users (with their licence-assignment states and
//  sign-in activity) and the tenant's subscribedSkus, normalizes
//  both into the canonical Prism model, and emits three snapshots:
//    users.ndjson, skus.ndjson, license-assignments.ndjson
//
//  Scopes used (all already granted to the managed identity):
//    User.Read.All             -> users, employeeHireDate, licence states
//    User-LifeCycleInfo.Read.All -> employeeLeaveDateTime (leaver pipeline)
//    Organization.Read.All     -> subscribedSkus
//    AuditLog.Read.All         -> signInActivity
//
//  Note: report anonymization (displayConcealedNames) does NOT
//  affect these directory calls - UPNs here are always real. It
//  only conceals the *usage report* endpoints (a later connector).
// ============================================================
using Prism.Connectors.Graph;
using Prism.Connectors.Json;
using Prism.Warehouse;
using Prism.Warehouse.Model;

namespace Prism.Connectors;

public sealed class LicenseConnector : IConnector
{
    private const string SourceName = "m365.identity-license";
    public string Name => SourceName;

    private readonly GraphClient _graph;
    private readonly IIngestionSink _sink;
    private readonly ConnectorOptions _opts;
    private readonly string _runId;
    private readonly ILogger<LicenseConnector> _log;

    public LicenseConnector(GraphClient graph, IIngestionSink sink, ConnectorOptions opts, string runId, ILogger<LicenseConnector> log)
    {
        _graph = graph;
        _sink = sink;
        _opts = opts;
        _runId = runId;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        string snapshotUtc = DateTime.UtcNow.ToString("o");

        // ---- 1. Subscribed SKUs (the seat inventory) --------------------
        _log.LogInformation("Fetching subscribedSkus...");
        var skus = new List<DimSku>();
        var skuPartById = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        await foreach (GraphSubscribedSku s in _graph.GetPagedAsync<GraphSkusResponse, GraphSubscribedSku>(
            "subscribedSkus", GraphJsonContext.Default.GraphSkusResponse,
            p => (p.Value, p.NextLink), ct))
        {
            skus.Add(new DimSku(
                SkuId: s.SkuId,
                SkuPartNumber: s.SkuPartNumber,
                DisplayName: SkuNames.Friendly(s.SkuPartNumber),
                CapabilityStatus: s.CapabilityStatus,
                PrepaidUnitsEnabled: s.PrepaidUnits?.Enabled ?? 0,
                PrepaidUnitsWarning: s.PrepaidUnits?.Warning ?? 0,
                PrepaidUnitsSuspended: s.PrepaidUnits?.Suspended ?? 0,
                ConsumedUnits: s.ConsumedUnits));
            skuPartById[s.SkuId] = s.SkuPartNumber;
        }
        await _sink.WriteAsync("skus", skus.Select(x => Envelope(x, snapshotUtc)), ct);

        // ---- 2. Users + their licence assignment states -----------------
        _log.LogInformation("Fetching users (+ licence states, sign-in activity)...");
        // employeeLeaveDateTime is a PROTECTED property requiring User-LifeCycleInfo.Read.All;
        // selecting it without that grant 403s the entire /users call, so it's opt-in.
        string leaveField = _opts.EnableLeaverDates ? "employeeLeaveDateTime," : "";
        string usersUrl =
            "users?$top=" + _opts.PageSize +
            "&$select=id,userPrincipalName,displayName,accountEnabled,department,jobTitle," +
            "usageLocation,createdDateTime,employeeHireDate," + leaveField +
            "securityIdentifier,onPremisesSecurityIdentifier,licenseAssignmentStates,signInActivity";

        var users = new List<DimUser>();
        var assignments = new List<FactLicenseAssignment>();

        await foreach (GraphUser u in _graph.GetPagedAsync<GraphUsersResponse, GraphUser>(
            usersUrl, GraphJsonContext.Default.GraphUsersResponse,
            p => (p.Value, p.NextLink), ct))
        {
            users.Add(new DimUser(
                UserId: u.Id,
                UserPrincipalName: u.UserPrincipalName,
                DisplayName: u.DisplayName,
                AccountEnabled: u.AccountEnabled ?? false,
                Department: u.Department,
                JobTitle: u.JobTitle,
                UsageLocation: u.UsageLocation,
                CreatedDateTime: u.CreatedDateTime,
                EmployeeHireDate: u.EmployeeHireDate,
                EmployeeLeaveDateTime: _opts.EnableLeaverDates ? u.EmployeeLeaveDateTime : null,  // opt-in: needs User-LifeCycleInfo.Read.All
                LastSignInDateTime: u.SignInActivity?.LastSignInDateTime,
                LastNonInteractiveSignInDateTime: u.SignInActivity?.LastNonInteractiveSignInDateTime,
                LastSuccessfulSignInDateTime: u.SignInActivity?.LastSuccessfulSignInDateTime,
                SecurityIdentifier: u.SecurityIdentifier,
                OnPremisesSecurityIdentifier: u.OnPremisesSecurityIdentifier));

            if (u.LicenseAssignmentStates is { Count: > 0 })
            {
                foreach (GraphLicenseAssignmentState st in u.LicenseAssignmentStates)
                {
                    if (string.IsNullOrEmpty(st.SkuId)) continue;
                    assignments.Add(new FactLicenseAssignment(
                        UserId: u.Id,
                        SkuId: st.SkuId!,
                        SkuPartNumber: skuPartById.GetValueOrDefault(st.SkuId!),
                        AssignedDirectly: string.IsNullOrEmpty(st.AssignedByGroup),
                        AssignedByGroupId: st.AssignedByGroup,
                        State: st.State,
                        LastUpdatedDateTime: st.LastUpdatedDateTime,
                        DisabledServicePlanIds: st.DisabledPlans?.ToArray() ?? []));
                }
            }
        }

        await _sink.WriteAsync("users", users.Select(x => Envelope(x, snapshotUtc)), ct);

        // A user can hold the same SKU through more than one path - assigned
        // directly AND/OR inherited from one or more groups - and Graph returns a
        // separate licenseAssignmentState per path. fact.LicenseAssignment is keyed
        // (UserId, SkuId): one consumed seat per user/SKU, which is what waste
        // analysis cares about. Collapse the paths to a single row, preferring a
        // direct assignment (the per-user-reclaimable signal) when one exists.
        assignments = assignments
            .GroupBy(a => a.UserId + "|" + a.SkuId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(a => a.AssignedDirectly).First())
            .ToList();

        await _sink.WriteAsync("license-assignments", assignments.Select(x => Envelope(x, snapshotUtc)), ct);

        // ---- Summary (a quick at-a-glance waste signal) -----------------
        int ownedSeats = skus.Sum(s => s.PrepaidUnitsEnabled);
        int assignedSeats = skus.Sum(s => s.ConsumedUnits);

        var disabledUserIds = users.Where(u => !u.AccountEnabled).Select(u => u.UserId)
                                   .ToHashSet(StringComparer.OrdinalIgnoreCase);
        int disabledLicensed = assignments.Select(a => a.UserId)
                                          .Where(disabledUserIds.Contains)
                                          .Distinct(StringComparer.OrdinalIgnoreCase).Count();

        // ---- Soft-deleted users that still hold (and bill) licences -------
        // Deleted users keep their assigned SKUs for ~30 days unless explicitly
        // removed — pure waste no active-user query surfaces. Optional.
        if (_opts.IncludeDeletedUserLicenses)
        {
            var deleted = new List<FactDeletedUserLicense>();
            try
            {
                await foreach (GraphDeletedUser du in _graph.GetPagedAsync<GraphDeletedUsersResponse, GraphDeletedUser>(
                    "directory/deletedItems/microsoft.graph.user?$top=" + _opts.PageSize +
                    "&$select=id,userPrincipalName,displayName,deletedDateTime,assignedLicenses",
                    GraphJsonContext.Default.GraphDeletedUsersResponse, p => (p.Value, p.NextLink), ct))
                {
                    if (du.AssignedLicenses is not { Count: > 0 }) continue;
                    foreach (GraphAssignedLicense lic in du.AssignedLicenses)
                        if (!string.IsNullOrEmpty(lic.SkuId))
                            deleted.Add(new FactDeletedUserLicense(
                                UserId: du.Id, UserPrincipalName: du.UserPrincipalName, DisplayName: du.DisplayName,
                                DeletedDateTime: du.DeletedDateTime, SkuId: lic.SkuId!));
                }
                await _sink.WriteAsync("deleted-user-licenses", deleted.Select(x => Envelope(x, snapshotUtc)), ct);
                _log.LogInformation("Deleted-but-licensed: {Rows} licence(s) on {Users} soft-deleted user(s) still billing.",
                    deleted.Count, deleted.Select(d => d.UserId).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            }
            catch (Exception ex)
            {
                _log.LogWarning("Could not read deletedItems users ({Msg}); skipping deleted-user licences.", ex.Message);
            }
        }

        _log.LogInformation(
            "Done: {Users} users, {Skus} SKUs ({Owned} seats owned / {Assigned} assigned, {Idle} unassigned), " +
            "{Assignments} assignments, {DisabledLicensed} licence(s) on disabled accounts.",
            users.Count, skus.Count, ownedSeats, assignedSeats, ownedSeats - assignedSeats,
            assignments.Count, disabledLicensed);
    }

    // Generic instance helper: wraps each entity with source/run/snapshot provenance.
    private EntityEnvelope<T> Envelope<T>(T item, string snapshotUtc) =>
        new EntityEnvelope<T>(SourceName, _runId, snapshotUtc, item);
}
