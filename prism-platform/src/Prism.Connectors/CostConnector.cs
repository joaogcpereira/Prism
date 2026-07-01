// ============================================================
//  CostConnector.cs
//  Source: azure.cost. Read-only (Cost Management Reader at the
//  configured scope - default the tenant-root management group).
//
//  Queries the Cost Management Query API for daily cost grouped by
//  service and resource group, and normalizes to FactAzureCost.
//
//  Caveats:
//   * Management-group scope supports EA/legacy subscriptions only,
//     NOT MCA. For an MCA subscription scope, set CostColumn="Cost".
//   * The API is heavily throttled; the client honors 429 Retry-After.
// ============================================================
using System.Text.Json;
using Prism.Connectors.Cost;
using Prism.Connectors.Json;
using Prism.Warehouse;
using Prism.Warehouse.Model;

namespace Prism.Connectors;

public sealed class CostConnector : IConnector
{
    public string Name => "azure.cost";

    private readonly CostManagementClient _cost;
    private readonly IIngestionSink _sink;
    private readonly ConnectorOptions _opts;
    private readonly string _runId;
    private readonly ILogger<CostConnector> _log;

    public CostConnector(CostManagementClient cost, IIngestionSink sink, ConnectorOptions opts, string runId, ILogger<CostConnector> log)
    {
        _cost = cost;
        _sink = sink;
        _opts = opts;
        _runId = runId;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        string snapshotUtc = DateTime.UtcNow.ToString("o");
        string scope = _opts.CostManagementScope?.Trim() ?? "";

        if (string.IsNullOrEmpty(scope))
        {
            _log.LogWarning("CostManagementScope is not set; skipping the Azure cost connector.");
            return;
        }

        // Resolve the scope(s). The sentinel "subscriptions:*" (also "*" or
        // "all-subscriptions") enumerates every enabled subscription the managed
        // identity can see and unions their cost - the right choice for a flat
        // tenant with many subscriptions and no rollup management group.
        bool allSubs = scope is "subscriptions:*" or "*" or "all-subscriptions";
        List<string> scopes;
        if (allSubs)
        {
            var ids = await _cost.ListEnabledSubscriptionIdsAsync(GraphJsonContext.Default.SubscriptionList, ct);
            scopes = ids.Select(id => $"/subscriptions/{id}").ToList();
            _log.LogInformation("Cost: enumerating {Count} enabled subscription(s).", scopes.Count);
            if (scopes.Count == 0)
                _log.LogWarning("Cost: the managed identity sees no enabled subscriptions. " +
                                "Grant it 'Cost Management Reader' on each subscription (or a covering scope).");
        }
        else
        {
            scopes = [scope];
        }

        // The cost column depends on billing type (EA/MG => PreTaxCost, MCA => Cost).
        // Try the configured column first, then the alternate, so a mixed or unknown
        // estate doesn't fail. An invalid column returns 400, which we catch and retry.
        string primary = string.IsNullOrWhiteSpace(_opts.CostColumn) ? "Cost" : _opts.CostColumn.Trim();
        string alternate = primary.Equals("Cost", StringComparison.OrdinalIgnoreCase) ? "PreTaxCost" : "Cost";
        string[] columnsToTry = [primary, alternate];

        var records = new List<FactAzureCost>();
        decimal grandTotal = 0;
        int okScopes = 0;

        foreach (string sc in scopes)
        {
            List<FactAzureCost>? got = null;
            string? usedCol = null;
            Exception? lastErr = null;

            foreach (string colName in columnsToTry)
            {
                try { got = await QueryScopeAsync(sc, colName, ct); usedCol = colName; break; }
                catch (Exception ex)
                {
                    lastErr = ex;
                    _log.LogWarning("Cost scope {Scope} with column '{Col}' failed: {Msg}", sc, colName, ex.Message);
                }
            }

            if (got is null)
            {
                // One bad scope must not abort the rest (or the whole connector run).
                _log.LogWarning("Cost scope {Scope}: all attempts failed; skipping. Last error: {Msg}", sc, lastErr?.Message);
                continue;
            }

            okScopes++;
            decimal subTotal = got.Sum(r => r.Cost);
            grandTotal += subTotal;
            records.AddRange(got);
            _log.LogInformation("Cost scope {Scope}: {Rows} row(s), {Total:0.00} (column '{Col}').",
                sc, got.Count, subTotal, usedCol);
        }

        // Empty 'records' is fine: the sink guards against wiping the table with a no-op.
        await _sink.WriteAsync("azure-cost", records.Select(x => Envelope(x, snapshotUtc)), ct);

        int services = records.Select(r => r.ServiceName).Where(s => s is not null).Distinct().Count();
        _log.LogInformation("Done: {Rows} cost row(s) across {Services} service(s) from {Ok}/{Total} scope(s); total {Grand:0.00}.",
            records.Count, services, okScopes, scopes.Count, grandTotal);
    }

    // Query a single scope with one cost-column name; map rows into a fresh list.
    // Throws on HTTP failure so the caller can try the alternate column or skip the scope.
    private async Task<List<FactAzureCost>> QueryScopeAsync(string scope, string costColumn, CancellationToken ct)
    {
        var query = new CostQuery
        {
            Type = string.IsNullOrWhiteSpace(_opts.CostType) ? "ActualCost" : _opts.CostType,
            Timeframe = string.IsNullOrWhiteSpace(_opts.CostTimeframe) ? "MonthToDate" : _opts.CostTimeframe,
            Dataset = new CostDataset
            {
                Granularity = "Daily",
                Aggregation = new() { ["totalCost"] = new CostAggregation { Name = costColumn, Function = "Sum" } },
                Grouping =
                [
                    new CostGrouping { Type = "Dimension", Name = "ServiceName" },
                    new CostGrouping { Type = "Dimension", Name = "ResourceGroup" },
                ]
            }
        };

        var local = new List<FactAzureCost>();

        await foreach (CostQueryResult page in _cost.QueryAsync(
            scope, query, GraphJsonContext.Default.CostQuery, GraphJsonContext.Default.CostQueryResult,
            p => p.Properties?.NextLink, ct))
        {
            CostQueryProperties? props = page.Properties;
            if (props is null || props.Rows.Count == 0) continue;

            // Map column name -> index (order varies by query; never assume position).
            var col = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < props.Columns.Count; i++) col[props.Columns[i].Name] = i;

            int ciCost = col.GetValueOrDefault(costColumn, -1);
            int ciDate = col.GetValueOrDefault("UsageDate", -1);
            int ciSvc = col.GetValueOrDefault("ServiceName", -1);
            int ciRg = col.GetValueOrDefault("ResourceGroup", -1);
            int ciCur = col.GetValueOrDefault("Currency", -1);

            foreach (List<JsonElement> row in props.Rows)
            {
                decimal cost = ciCost >= 0 && ciCost < row.Count ? AsDecimal(row[ciCost]) : 0;
                local.Add(new FactAzureCost(
                    Scope: scope,
                    UsageDate: ciDate >= 0 && ciDate < row.Count ? AsDate(row[ciDate]) : null,
                    Cost: cost,
                    Currency: ciCur >= 0 && ciCur < row.Count ? AsString(row[ciCur]) : null,
                    ServiceName: ciSvc >= 0 && ciSvc < row.Count ? AsString(row[ciSvc]) : null,
                    ResourceGroup: ciRg >= 0 && ciRg < row.Count ? AsString(row[ciRg]) : null));
            }
        }
        return local;
    }

    private static decimal AsDecimal(JsonElement e) =>
        e.ValueKind == JsonValueKind.Number && e.TryGetDecimal(out decimal d) ? d
        : decimal.TryParse(e.ValueKind == JsonValueKind.String ? e.GetString() : null, out decimal s) ? s : 0m;

    private static string? AsString(JsonElement e) =>
        e.ValueKind == JsonValueKind.String ? e.GetString()
        : e.ValueKind == JsonValueKind.Number ? e.GetRawText() : null;

    private static string? AsDate(JsonElement e)
    {
        // Daily granularity returns UsageDate as an integer yyyymmdd (e.g. 20260601).
        int v = e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out int n) ? n
              : int.TryParse(e.ValueKind == JsonValueKind.String ? e.GetString() : null, out int p) ? p : 0;
        if (v < 1_0000101) return AsString(e);
        return $"{v / 10000:0000}-{v / 100 % 100:00}-{v % 100:00}";
    }

    private EntityEnvelope<T> Envelope<T>(T item, string snapshotUtc) =>
        new EntityEnvelope<T>(Name, _runId, snapshotUtc, item);
}
