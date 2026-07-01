// ============================================================
//  DefenderConnector.cs
//  Source: defender.discovered-apps. OPTIONAL (behind EnableDefenderConnector).
//
//  Pulls Defender for Cloud Apps cloud-discovery "discovered apps"
//  (shadow IT) -> FactDiscoveredApp. Flow:
//    1. Resolve a discovery stream id (configured, else first from
//       /api/v1/discovery/streams/).
//    2. POST /api/v1/discovery/discovered_apps/ with skip/limit paging.
//    3. Parse each item defensively (schema varies by tenant/version).
//
//  Skipped unless enabled AND the tenant base URL / app id / tenant id
//  are configured. The MDCA discovery API is partly legacy and
//  tenant/region-specific; verify field names against your tenant.
// ============================================================
using System.Text.Json;
using Prism.Connectors.Defender;
using Prism.Connectors.Json;
using Prism.Warehouse;
using Prism.Warehouse.Model;

namespace Prism.Connectors;

public sealed class DefenderConnector : IConnector
{
    public string Name => "defender.discovered-apps";

    private const int PageLimit = 100;

    private readonly DefenderClient? _client;
    private readonly IIngestionSink _sink;
    private readonly ConnectorOptions _opts;
    private readonly string _runId;
    private readonly ILogger<DefenderConnector> _log;

    public DefenderConnector(DefenderClient? client, IIngestionSink sink, ConnectorOptions opts, string runId, ILogger<DefenderConnector> log)
    {
        _client = client;
        _sink = sink;
        _opts = opts;
        _runId = runId;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (!_opts.EnableDefenderConnector || _client is null)
        {
            _log.LogInformation("Defender connector disabled or not configured; skipping.");
            return;
        }

        string snapshotUtc = DateTime.UtcNow.ToString("o");

        // ---- 1. Resolve a stream id ------------------------------------
        string? streamId = string.IsNullOrWhiteSpace(_opts.DefenderStreamId) ? null : _opts.DefenderStreamId.Trim();
        if (streamId is null)
        {
            try { streamId = await ResolveStreamAsync(ct); }
            catch (Exception ex) { _log.LogWarning("Could not list discovery streams ({Msg}); querying without a stream filter.", ex.Message); }
        }

        // ---- 2. Page through discovered apps ---------------------------
        var records = new List<FactDiscoveredApp>();
        int skip = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            string body = BuildQuery(streamId, _opts.DefenderTimeframeDays, skip, PageLimit);

            using JsonDocument doc = await _client!.PostAsync("api/v1/discovery/discovered_apps/", body, ct);
            JsonElement root = doc.RootElement;

            if (!root.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
                break;

            int countThisPage = 0;
            foreach (JsonElement app in data.EnumerateArray())
            {
                records.Add(MapApp(app));
                countThisPage++;
            }

            bool hasNext = root.TryGetProperty("hasNext", out JsonElement hn) && hn.ValueKind == JsonValueKind.True;
            skip += countThisPage;
            // Stop on an empty/short page. Continue while the API says there's more OR it
            // returned a full page (some tenants omit "hasNext" and signal "more" only by
            // returning a full page); the empty-page check below still guarantees termination.
            bool fullPage = countThisPage >= PageLimit;
            if (countThisPage == 0 || (!hasNext && !fullPage)) break;
        }

        await _sink.WriteAsync("discovered-apps", records.Select(x => Envelope(x, snapshotUtc)), ct);

        long users = records.Sum(r => r.UserCount ?? 0);
        _log.LogInformation("Done: {Apps} discovered app(s) (stream {Stream}, {Days}d), ~{Users} user-app associations.",
            records.Count, streamId ?? "(none)", _opts.DefenderTimeframeDays, users);
    }

    private async Task<string?> ResolveStreamAsync(CancellationToken ct)
    {
        using JsonDocument doc = await _client!.GetAsync("api/v1/discovery/streams/", ct);
        JsonElement root = doc.RootElement;
        // streams may come back as a bare array or wrapped in { data: [...] }
        JsonElement arr = root.ValueKind == JsonValueKind.Array ? root
                        : root.TryGetProperty("data", out JsonElement d) ? d : default;
        if (arr.ValueKind != JsonValueKind.Array) return null;

        string? first = null, global = null;
        foreach (JsonElement s in arr.EnumerateArray())
        {
            string? id = Str(s, "_id") ?? Str(s, "id");
            if (id is null) continue;
            first ??= id;
            string? name = Str(s, "displayName") ?? Str(s, "name");
            if (name is not null && name.Contains("Global", StringComparison.OrdinalIgnoreCase)) global = id;
        }
        return global ?? first;
    }

    private static string BuildQuery(string? streamId, int timeframeDays, int skip, int limit)
    {
        var filters = new Dictionary<string, object?> { ["timeframe"] = timeframeDays };
        if (streamId is not null) filters["streamId"] = streamId;
        var query = new Dictionary<string, object?>
        {
            ["skip"] = skip,
            ["limit"] = limit,
            ["filters"] = filters,
            ["sortField"] = "trafficTotalBytes",
            ["sortDirection"] = "desc",
        };
        return JsonSerializer.Serialize(query, DefenderQueryJson.Options);
    }

    // ---- Defensive field reads (schema varies across tenants/versions) ----
    private static FactDiscoveredApp MapApp(JsonElement a) => new(
        AppName: Str(a, "name") ?? Str(a, "appName"),
        Category: Str(a, "category") ?? Str(a, "categoryName"),
        RiskScore: Num(a, "score") ?? Num(a, "riskScore"),
        UserCount: Int(a, "users") ?? Int(a, "usersCount") ?? Int(a, "userCount"),
        UploadedBytes: Int(a, "uploadedBytes") ?? Int(a, "trafficUploadedBytes"),
        DownloadedBytes: Int(a, "downloadedBytes") ?? Int(a, "trafficDownloadedBytes"),
        TrafficTotalBytes: Int(a, "trafficTotalBytes") ?? Int(a, "totalBytes"),
        TransactionCount: Int(a, "transactionsCount") ?? Int(a, "transactions"),
        LastSeen: Str(a, "lastSeen") ?? Str(a, "lastUsed"),
        Tags: Str(a, "sanctionState") ?? Str(a, "tags"));

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d) ? d : null;

    private static long? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n) ? n : null;

    private EntityEnvelope<T> Envelope<T>(T item, string snapshotUtc) =>
        new EntityEnvelope<T>(Name, _runId, snapshotUtc, item);
}

internal static class DefenderQueryJson
{
    public static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
}
