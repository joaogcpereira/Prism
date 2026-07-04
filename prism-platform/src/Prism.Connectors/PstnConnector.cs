// ============================================================
//  PstnConnector.cs
//  Source: graph.pstn. Read-only. OFF by default
//  (Prism__EnablePstn=true + CallRecord-PstnCalls.Read.All -
//  the NARROW report permission; CallRecords.Read.All also works
//  but grants full call-record content and should be avoided).
//
//  Pulls the tenant's PSTN call log (getPstnCalls) for the
//  trailing PstnWindowDays and aggregates it per user: call
//  count, total seconds, most recent call. These are REAL
//  call-detail records - the authoritative usage signal for a
//  Teams Phone (MCOEV) seat, far stronger than the Teams
//  activity report's coarse CallCount (which also counts VoIP).
//  "Zero PSTN calls in the window" is hard evidence the paid
//  number is idle.
// ============================================================
using System.Globalization;
using Prism.Connectors.Graph;
using Prism.Connectors.Json;
using Prism.Warehouse;
using Prism.Warehouse.Model;

namespace Prism.Connectors;

public sealed class PstnConnector : IConnector
{
    private const string SourceName = "graph.pstn";
    public string Name => SourceName;

    private readonly GraphClient _graph;
    private readonly IIngestionSink _sink;
    private readonly ConnectorOptions _opts;
    private readonly string _runId;
    private readonly ILogger<PstnConnector> _log;

    public PstnConnector(GraphClient graph, IIngestionSink sink, ConnectorOptions opts, string runId,
        ILogger<PstnConnector> log)
    {
        _graph = graph; _sink = sink; _opts = opts; _runId = runId; _log = log;
    }

    private sealed class Agg
    {
        public string? Upn;
        public int Calls;
        public long Seconds;
        public DateTime? Last;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (!_opts.EnablePstn)
        {
            _log.LogInformation("PSTN connector disabled (Prism__EnablePstn=false); skipping.");
            return;
        }

        string snapshotUtc = DateTime.UtcNow.ToString("o");
        int window = Math.Clamp(_opts.PstnWindowDays, 1, 90);   // Graph caps one range at 90 days
        DateTime toUtc = DateTime.UtcNow;
        DateTime fromUtc = toUtc.AddDays(-window);

        // Function parameters are ISO-8601 UTC; invariant formatting is deliberate.
        string url = "communications/callRecords/getPstnCalls(fromDateTime=" +
                     fromUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) +
                     ",toDateTime=" + toUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) + ")";

        _log.LogInformation("Fetching PSTN calls for the last {Days} day(s)...", window);
        var byUser = new Dictionary<string, Agg>(StringComparer.OrdinalIgnoreCase);
        long total = 0;

        await foreach (PstnCall call in _graph.GetPagedAsync<PstnCallsResponse, PstnCall>(
            url, GraphJsonContext.Default.PstnCallsResponse, p => (p.Value, p.NextLink), ct))
        {
            total++;
            // Key by object id when present; some rows (resource accounts, ported
            // numbers mid-migration) only carry a UPN - keep those under a UPN key
            // so vw.PstnUsageByUser can still resolve them.
            string key = !string.IsNullOrEmpty(call.UserId) ? call.UserId! : ("upn:" + (call.UserPrincipalName ?? ""));
            if (key == "upn:") continue;   // unattributable (service/bot legs)

            if (!byUser.TryGetValue(key, out Agg? a)) byUser[key] = a = new Agg();
            a.Upn ??= call.UserPrincipalName;
            a.Calls++;
            a.Seconds += Math.Max(0, call.Duration ?? 0);
            if (DateTime.TryParse(call.StartDateTime, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime start)
                && (a.Last is null || start > a.Last))
                a.Last = start;
        }

        var rows = byUser.Select(kv => new FactPstnUsage(
            UserId: kv.Key.StartsWith("upn:", StringComparison.Ordinal) ? null : kv.Key,
            UserPrincipalName: kv.Value.Upn,
            CallCount: kv.Value.Calls,
            TotalDurationSeconds: kv.Value.Seconds,
            LastCallDateTime: kv.Value.Last?.ToString("o"),
            WindowDays: window)).ToList();

        _log.LogInformation("PSTN: {Calls} call leg(s) across {Users} user(s) in {Days}d.", total, rows.Count, window);
        await _sink.WriteAsync("pstn-usage", rows.Select(x => Envelope(x, snapshotUtc)), ct);
    }

    private EntityEnvelope<T> Envelope<T>(T item, string snapshotUtc) => new(Name, _runId, snapshotUtc, item);
}
