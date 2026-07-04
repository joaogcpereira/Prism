// ============================================================
//  MailboxSettingsConnector.cs
//  Source: graph.mailboxsettings. Read-only. OFF by default
//  (Prism__EnableMailboxSettings=true + MailboxSettings.Read).
//
//  Pulls /users/{id}/mailboxSettings for every LICENSED user via
//  Graph $batch (20 sub-requests per POST) and lands one row per
//  mailbox: userPurpose (user | shared | room | equipment | ...),
//  auto-reply status, time zone.
//
//  WHY: userPurpose is the DETERMINISTIC discriminator that
//  replaces the name-pattern heuristic for shared-mailbox /
//  resource-account detection. A licensed 'shared' mailbox under
//  50 GB usually needs no license at all - the classic compliance
//  trap - and the engine can now prove it instead of guessing.
//
//  Resilience: the licensed-user enumeration uses the advanced
//  $filter (assignedLicenses/$count ne 0, ConsistencyLevel:
//  eventual) so we never batch the unlicensed majority. Per-item
//  404s (no mailbox: unlicensed-for-Exchange, guest) are expected
//  and skipped; per-item 429s honor that item's Retry-After and
//  re-queue; systemic failures trip the consecutive-failure
//  breaker instead of hammering on.
// ============================================================
using System.Text.Json;
using Prism.Connectors.Graph;
using Prism.Connectors.Json;
using Prism.Warehouse;
using Prism.Warehouse.Model;

namespace Prism.Connectors;

public sealed class MailboxSettingsConnector : IConnector
{
    private const string SourceName = "graph.mailboxsettings";
    public string Name => SourceName;

    private readonly GraphClient _graph;
    private readonly IIngestionSink _sink;
    private readonly ConnectorOptions _opts;
    private readonly string _runId;
    private readonly ILogger<MailboxSettingsConnector> _log;

    public MailboxSettingsConnector(GraphClient graph, IIngestionSink sink, ConnectorOptions opts, string runId,
        ILogger<MailboxSettingsConnector> log)
    {
        _graph = graph; _sink = sink; _opts = opts; _runId = runId; _log = log;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (!_opts.EnableMailboxSettings)
        {
            _log.LogInformation("Mailbox-settings connector disabled (Prism__EnableMailboxSettings=false); skipping.");
            return;
        }

        string snapshotUtc = DateTime.UtcNow.ToString("o");

        // ---- 1. Enumerate LICENSED users only (advanced query needs the
        //         ConsistencyLevel header + $count=true to activate $filter on
        //         assignedLicenses). Unlicensed accounts have no seat to score. ----
        _log.LogInformation("Enumerating licensed users for mailbox settings...");
        var targets = new List<GraphUserId>();
        string url = "users?$top=" + _opts.PageSize +
                     "&$select=id,userPrincipalName&$count=true&$filter=assignedLicenses/$count ne 0";
        await foreach (GraphUserId u in _graph.GetPagedAsync<GraphUserIdsResponse, GraphUserId>(
            url, GraphJsonContext.Default.GraphUserIdsResponse, p => (p.Value, p.NextLink), ct,
            headers: new Dictionary<string, string> { ["ConsistencyLevel"] = "eventual" }))
        {
            if (!string.IsNullOrEmpty(u.Id)) targets.Add(u);
        }
        _log.LogInformation("Fetching mailboxSettings for {Count} licensed user(s) via $batch...", targets.Count);

        // ---- 2. $batch the mailboxSettings GETs, 20 at a time -------------------
        var rows = new List<FactMailbox>(targets.Count);
        var breaker = new ConsecutiveFailureBreaker(_opts.CircuitBreakerFailures, SourceName + " $batch sweep");
        var upnById = targets.ToDictionary(t => t.Id, t => t.UserPrincipalName, StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<GraphUserId>(targets);
        int noMailbox = 0, otherErrors = 0;
        var retryCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var slice = new List<(string Id, string Url)>(20);
            while (slice.Count < 20 && queue.Count > 0)
            {
                GraphUserId u = queue.Dequeue();
                slice.Add((u.Id, $"/users/{u.Id}/mailboxSettings?$select=userPurpose,automaticRepliesSetting,timeZone"));
            }

            JsonDocument doc;
            try { doc = await _graph.PostBatchAsync(slice, ct); breaker.RecordSuccess(); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                breaker.RecordFailure(ex.Message);
                _log.LogWarning("mailboxSettings $batch failed ({Message}); re-queueing {Count} user(s).", ex.Message, slice.Count);
                foreach ((string id, _) in slice) Requeue(id);
                continue;
            }

            double maxRetryAfter = 0;
            using (doc)
            {
                if (!doc.RootElement.TryGetProperty("responses", out JsonElement responses)) continue;
                foreach (JsonElement r in responses.EnumerateArray())
                {
                    string id = r.TryGetProperty("id", out JsonElement idEl) ? idEl.GetString() ?? "" : "";
                    int status = r.TryGetProperty("status", out JsonElement stEl) ? stEl.GetInt32() : 0;

                    if (status == 200 && r.TryGetProperty("body", out JsonElement body))
                    {
                        GraphMailboxSettings? mb = JsonSerializer.Deserialize(body.GetRawText(),
                            GraphJsonContext.Default.GraphMailboxSettings);
                        rows.Add(new FactMailbox(
                            UserId: id,
                            UserPrincipalName: upnById.GetValueOrDefault(id),
                            UserPurpose: mb?.UserPurpose,
                            AutomaticRepliesStatus: mb?.AutomaticRepliesSetting?.Status,
                            TimeZone: mb?.TimeZone));
                    }
                    else if (status == 429)
                    {
                        // Honor the ITEM's Retry-After (the outer POST succeeded) and try again.
                        maxRetryAfter = Math.Max(maxRetryAfter, ReadRetryAfter(r, 5));
                        Requeue(id);
                    }
                    else if (status == 404 || status == 403)
                    {
                        noMailbox++;   // no Exchange mailbox behind this account (guest / unlicensed-for-EXO)
                    }
                    else
                    {
                        otherErrors++;
                        if (otherErrors <= 5)
                            _log.LogWarning("mailboxSettings for {User}: HTTP {Status}.", id, status);
                    }
                }
            }
            if (maxRetryAfter > 0)
            {
                TimeSpan wait = TimeSpan.FromSeconds(Math.Min(maxRetryAfter, _opts.MaxRetryAfterSeconds))
                                + TimeSpan.FromMilliseconds(Random.Shared.Next(100, 750));
                _log.LogInformation("Per-item throttling: waiting {Sec}s before the next $batch.", (int)wait.TotalSeconds);
                await Task.Delay(wait, ct);
            }
        }

        _log.LogInformation("Mailbox settings: {Rows} row(s); {NoMailbox} account(s) without a mailbox; {Errors} error(s).",
            rows.Count, noMailbox, otherErrors);
        await _sink.WriteAsync("mailbox-settings", rows.Select(x => Envelope(x, snapshotUtc)), ct);
        return;

        // Re-queue an item at most ThrottleMaxRetries times; then drop it with a warning
        // (a single stuck mailbox must not spin the sweep forever).
        void Requeue(string id)
        {
            int n = retryCounts.GetValueOrDefault(id);
            if (n >= _opts.ThrottleMaxRetries)
            {
                otherErrors++;
                _log.LogWarning("mailboxSettings for {User}: giving up after {N} throttled attempts.", id, n);
                return;
            }
            retryCounts[id] = n + 1;
            queue.Enqueue(new GraphUserId { Id = id, UserPrincipalName = upnById.GetValueOrDefault(id) });
        }
    }

    private static double ReadRetryAfter(JsonElement subResponse, double dflt)
    {
        if (subResponse.TryGetProperty("headers", out JsonElement h) && h.ValueKind == JsonValueKind.Object)
            foreach (JsonProperty prop in h.EnumerateObject())
                if (string.Equals(prop.Name, "Retry-After", StringComparison.OrdinalIgnoreCase)
                    && prop.Value.ValueKind == JsonValueKind.String
                    && double.TryParse(prop.Value.GetString(), System.Globalization.NumberStyles.Float,
                           System.Globalization.CultureInfo.InvariantCulture, out double s))
                    return s;
        return dflt;
    }

    private EntityEnvelope<T> Envelope<T>(T item, string snapshotUtc) => new(Name, _runId, snapshotUtc, item);
}
