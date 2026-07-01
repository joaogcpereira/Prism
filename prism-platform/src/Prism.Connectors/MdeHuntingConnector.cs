// ============================================================
//  MdeHuntingConnector.cs
//  Source: defender.process-runs. OPTIONAL (behind EnableMdeHunting).
//
//  Defender for Endpoint Advanced Hunting: one summarized KQL query over
//  DeviceProcessEvents (last 30 days) for the licensing-relevant
//  executables -> fact.SoftwareRun. TRUE usage telemetry ("the exe
//  started"), fleet-wide on every onboarded device, no agent required.
//  Complements the prism-agent (which has 90d depth + foreground time
//  but only where deployed) and the install inventories (presence only).
//
//  API constraints respected: 30-day lookback, 100k row / 50MB result cap
//  (the summarize keeps results tiny), 45 calls/min + CPU quotas (one
//  call per run; 429s honored with Retry-After + quota detail logged),
//  200s max query time (client timeout raised accordingly).
// ============================================================
using System.Text.Json;
using Prism.Connectors.Defender;
using Prism.Warehouse;
using Prism.Warehouse.Model;

namespace Prism.Connectors;

public sealed class MdeHuntingConnector : IConnector
{
    public string Name => "defender.process-runs";

    private readonly MdeClient? _client;
    private readonly IIngestionSink _sink;
    private readonly ConnectorOptions _opts;
    private readonly string _runId;
    private readonly ILogger<MdeHuntingConnector> _log;

    public MdeHuntingConnector(MdeClient? client, IIngestionSink sink, ConnectorOptions opts, string runId, ILogger<MdeHuntingConnector> log)
    {
        _client = client;
        _sink = sink;
        _opts = opts;
        _runId = runId;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (!_opts.EnableMdeHunting || _client is null)
        {
            _log.LogInformation("Defender Advanced Hunting connector disabled or not configured; skipping.");
            return;
        }

        string[] exes = (_opts.MdeHuntingExecutables ?? [])
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => e.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (exes.Length == 0)
        {
            _log.LogInformation("No executables configured for hunting (Prism__MdeHuntingExecutables); skipping.");
            return;
        }

        int days = Math.Clamp(_opts.MdeHuntingLookbackDays, 1, 30);   // API max lookback = 30d
        string snapshotUtc = DateTime.UtcNow.ToString("o");

        // Single summarized query: tiny result set (devices x exes x accounts), one API call.
        // Exe names are sanitized (no quotes/backslashes survive) before KQL interpolation.
        string exeList = string.Join(",", exes.Select(e => "'" + e.Replace("\\", "").Replace("'", "") + "'"));
        string kql =
            $"DeviceProcessEvents " +
            $"| where Timestamp > ago({days}d) " +
            $"| where FileName in~ ({exeList}) " +
            $"| summarize LastRun=max(Timestamp), RunCount=count(), RunDays=dcount(bin(Timestamp, 1d)) " +
            $"by DeviceId, DeviceName, FileName, AccountUpn";

        _log.LogInformation("Running advanced-hunting query for {Exes} executable(s), {Days}d lookback...", exes.Length, days);
        string body = JsonSerializer.Serialize(new Dictionary<string, string> { ["Query"] = kql });

        var runs = new List<FactSoftwareRun>();
        using (JsonDocument doc = await _client.PostAsync("api/advancedqueries/run", body, ct).ConfigureAwait(false))
        {
            if (doc.RootElement.TryGetProperty("Results", out JsonElement results) && results.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement row in results.EnumerateArray())
                {
                    string? file = Str(row, "FileName");
                    if (string.IsNullOrEmpty(file)) continue;
                    runs.Add(new FactSoftwareRun(
                        FileName: file,
                        DeviceId: Str(row, "DeviceId"),
                        DeviceName: Str(row, "DeviceName"),
                        AccountUpn: Str(row, "AccountUpn"),
                        LastRunUtc: Str(row, "LastRun"),
                        RunCount: Int(row, "RunCount") ?? 0,
                        RunDays: (int)(Int(row, "RunDays") ?? 0)));
                }
            }
        }

        await _sink.WriteAsync("software-runs", runs.Select(x => Envelope(x, snapshotUtc)), ct);
        _log.LogInformation("Process-run telemetry: {Rows} (device, exe, account) row(s) across {Exes} executable(s), {Days}d window.",
            runs.Count, exes.Length, days);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n) ? n : null;

    private EntityEnvelope<T> Envelope<T>(T item, string snapshotUtc) =>
        new EntityEnvelope<T>(Name, _runId, snapshotUtc, item);
}
