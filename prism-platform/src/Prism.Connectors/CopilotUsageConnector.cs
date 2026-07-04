// ============================================================
//  CopilotUsageConnector.cs
//  Source: m365.copilot-usage. Read-only (Reports.Read.All +
//  ReportSettings.Read.All).
//
//  getMicrosoft365CopilotUsageUserDetail (Graph BETA): per-user last activity
//  for Microsoft 365 Copilot in each host app (Teams / Word / Excel / PowerPoint
//  / Outlook / OneNote / Loop / Copilot Chat). This is the ONLY signal that sees
//  Copilot seat usage - every exe/workload/sign-in signal is blind to it - and
//  Copilot is the priciest per-seat SKU, so an enabled-but-idle seat is the
//  clearest reclaim candidate. Same 302->CSV + concealment handling as the other
//  usage-report connectors.
//
//  The report currently lives only under the BETA endpoint (Microsoft is moving
//  it to the /copilot path); CopilotApiBaseUrl is an ABSOLUTE base that points at
//  it, so this reuses the shared GraphClient without changing its v1.0 base
//  address (GraphClient.SendWithRetryAsync follows absolute URLs as-is).
// ============================================================
using System.Text;
using Prism.Connectors.Graph;
using Prism.Connectors.Json;
using Prism.Warehouse;
using Prism.Warehouse.Model;

namespace Prism.Connectors;

public sealed class CopilotUsageConnector : IConnector
{
    public string Name => "m365.copilot-usage";

    private readonly GraphClient _graph;
    private readonly IIngestionSink _sink;
    private readonly ConnectorOptions _opts;
    private readonly string _runId;
    private readonly ILogger<CopilotUsageConnector> _log;

    public CopilotUsageConnector(GraphClient graph, IIngestionSink sink, ConnectorOptions opts, string runId, ILogger<CopilotUsageConnector> log)
    {
        _graph = graph;
        _sink = sink;
        _opts = opts;
        _runId = runId;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (!_opts.EnableCopilotConnector)
        {
            _log.LogInformation("Copilot usage connector disabled (Prism__EnableCopilotConnector); skipping.");
            return;
        }

        string snapshotUtc = DateTime.UtcNow.ToString("o");

        bool concealed = false;
        try
        {
            ReportSettings? settings = await _graph.GetJsonAsync("admin/reportSettings", GraphJsonContext.Default.ReportSettings, ct);
            concealed = settings?.DisplayConcealedNames ?? false;
        }
        catch (Exception ex)
        {
            _log.LogWarning("Could not read /admin/reportSettings ({Msg}); assuming names are NOT concealed.", ex.Message);
        }
        if (concealed)
            _log.LogWarning("Report anonymization is ON: per-user Copilot activity is MASKED; ingesting concealed=true.");

        string period = string.IsNullOrWhiteSpace(_opts.ServiceUsagePeriod) ? "D30" : _opts.ServiceUsagePeriod.Trim();
        string baseUrl = (_opts.CopilotApiBaseUrl ?? "").TrimEnd('/');
        // Absolute URL: the report is beta-only and the shared GraphClient's base is v1.0.
        string url = $"{baseUrl}/reports/getMicrosoft365CopilotUsageUserDetail(period='{period}')?$format=text/csv";
        _log.LogInformation("Downloading getMicrosoft365CopilotUsageUserDetail({Period}) [beta]...", period);

        string tmp = await _graph.DownloadReportToFileAsync(url, ct);
        try
        {
            List<FactCopilotUsage> records = ParseReport(tmp, period, concealed, ct);
            await _sink.WriteAsync("copilot-usage", records.Select(x => Envelope(x, snapshotUtc)), ct);

            int idle = records.Count(r => string.IsNullOrEmpty(r.LastActivityAnyDate));
            _log.LogInformation("Done: {Rows} Copilot user(s) over {Period} (concealed={Concealed}); {Idle} with no Copilot activity in window.",
                records.Count, period, concealed, idle);
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* best-effort */ }
        }
    }

    private List<FactCopilotUsage> ParseReport(string path, string period, bool concealed, CancellationToken ct)
    {
        string periodDays = period.TrimStart('D', 'd');
        var records = new List<FactCopilotUsage>();

        using var reader = new StreamReader(File.OpenRead(path), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        using IEnumerator<string[]> rows = Csv.ReadRows(reader).GetEnumerator();
        if (!rows.MoveNext()) { _log.LogWarning("Copilot report was empty (no header)."); return records; }

        string[] header = rows.Current;
        var col = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < header.Length; i++) col[header[i].Trim()] = i;

        string? Field(string[] r, string name) =>
            col.TryGetValue(name, out int i) && i < r.Length && !string.IsNullOrWhiteSpace(r[i]) ? r[i].Trim() : null;

        while (rows.MoveNext())
        {
            ct.ThrowIfCancellationRequested();
            string[] r = rows.Current;
            if (r.Length == 0 || (r.Length == 1 && string.IsNullOrWhiteSpace(r[0]))) continue;

            // Microsoft's header names for getMicrosoft365CopilotUsageUserDetail.
            string? teams = Field(r, "Microsoft Teams Copilot Last Activity Date");
            string? word  = Field(r, "Word Copilot Last Activity Date");
            string? excel = Field(r, "Excel Copilot Last Activity Date");
            string? ppt   = Field(r, "PowerPoint Copilot Last Activity Date");
            string? outl  = Field(r, "Outlook Copilot Last Activity Date");
            string? note  = Field(r, "OneNote Copilot Last Activity Date");
            string? loop  = Field(r, "Loop Copilot Last Activity Date");
            string? chat  = Field(r, "Copilot Chat Last Activity Date");
            string? last  = Field(r, "Last Activity Date");

            records.Add(new FactCopilotUsage(
                UserPrincipalName: Field(r, "User Principal Name"),
                DisplayName: Field(r, "Display Name"),
                Concealed: concealed,
                ReportRefreshDate: Field(r, "Report Refresh Date"),
                ReportPeriodDays: periodDays,
                LastActivityDate: last,
                TeamsLastActivityDate: teams,
                WordLastActivityDate: word,
                ExcelLastActivityDate: excel,
                PowerPointLastActivityDate: ppt,
                OutlookLastActivityDate: outl,
                OneNoteLastActivityDate: note,
                LoopLastActivityDate: loop,
                ChatLastActivityDate: chat,
                LastActivityAnyDate: MaxDate(last, teams, word, excel, ppt, outl, note, loop, chat)));
        }
        return records;
    }

    // Latest of several report dates, parsed (InvariantCulture) and compared as dates so a
    // non-ISO render can't pick the wrong "latest"; the original string is returned unchanged.
    private static string? MaxDate(params string?[] dates)
    {
        string? max = null;
        DateTime maxParsed = DateTime.MinValue;
        foreach (string? d in dates)
        {
            if (string.IsNullOrEmpty(d)) continue;
            if (DateTime.TryParse(d, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out DateTime parsed))
            {
                if (max is null || parsed > maxParsed) { max = d; maxParsed = parsed; }
            }
            else if (max is null) max = d;
        }
        return max;
    }

    private EntityEnvelope<T> Envelope<T>(T item, string snapshotUtc) =>
        new EntityEnvelope<T>(Name, _runId, snapshotUtc, item);
}
