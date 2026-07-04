// ============================================================
//  M365AppUsageConnector.cs
//  Source: m365.app-usage. Read-only (Reports.Read.All +
//  ReportSettings.Read.All).
//
//  getM365AppUserDetail: per-user last-activity for the DESKTOP/office
//  apps themselves - Word, Excel, PowerPoint, Outlook, OneNote, Teams -
//  plus which PLATFORMS were used (Windows / Mac / Web / Mobile). This
//  is distinct from getOffice365ActiveUserDetail (which is workload-
//  level: Exchange/SharePoint/Teams) and sharpens SHALLOW_USE: "the
//  mailbox is active, but has this user actually opened Word/Excel?"
//
//  Same concealment handling and header-by-name parsing as the
//  service-usage connector.
// ============================================================
using System.Text;
using Prism.Connectors.Graph;
using Prism.Connectors.Json;
using Prism.Warehouse;
using Prism.Warehouse.Model;

namespace Prism.Connectors;

public sealed class M365AppUsageConnector : IConnector
{
    public string Name => "m365.app-usage";

    private readonly GraphClient _graph;
    private readonly IIngestionSink _sink;
    private readonly ConnectorOptions _opts;
    private readonly string _runId;
    private readonly ILogger<M365AppUsageConnector> _log;

    public M365AppUsageConnector(GraphClient graph, IIngestionSink sink, ConnectorOptions opts, string runId, ILogger<M365AppUsageConnector> log)
    {
        _graph = graph;
        _sink = sink;
        _opts = opts;
        _runId = runId;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (!_opts.EnableM365AppUsageConnector)
        {
            _log.LogInformation("M365 Apps usage connector disabled (Prism__EnableM365AppUsageConnector); skipping.");
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
            _log.LogWarning("Report anonymization is ON: per-user M365 Apps activity is MASKED; ingesting concealed=true.");

        string period = string.IsNullOrWhiteSpace(_opts.ServiceUsagePeriod) ? "D30" : _opts.ServiceUsagePeriod.Trim();
        string url = $"reports/getM365AppUserDetail(period='{period}')";
        _log.LogInformation("Downloading getM365AppUserDetail({Period})...", period);

        string tmp = await _graph.DownloadReportToFileAsync(url, ct);
        try
        {
            List<FactM365AppUsage> records = ParseReport(tmp, period, concealed, ct);
            await _sink.WriteAsync("m365-app-usage", records.Select(x => Envelope(x, snapshotUtc)), ct);

            int noApps = records.Count(r => string.IsNullOrEmpty(r.LastActivityAnyDate));
            _log.LogInformation("Done: {Rows} user(s) over {Period} (concealed={Concealed}); {NoApps} with no Office-app activity.",
                records.Count, period, concealed, noApps);
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* best-effort */ }
        }
    }

    private List<FactM365AppUsage> ParseReport(string path, string period, bool concealed, CancellationToken ct)
    {
        string periodDays = period.TrimStart('D', 'd');
        var records = new List<FactM365AppUsage>();

        using var reader = new StreamReader(File.OpenRead(path), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        using IEnumerator<string[]> rows = Csv.ReadRows(reader).GetEnumerator();
        if (!rows.MoveNext()) { _log.LogWarning("Report was empty (no header)."); return records; }

        string[] header = rows.Current;
        var col = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < header.Length; i++) col[header[i].Trim()] = i;

        string? Field(string[] r, string name) =>
            col.TryGetValue(name, out int i) && i < r.Length && !string.IsNullOrWhiteSpace(r[i]) ? r[i].Trim() : null;
        bool Flag(string[] r, string name) => string.Equals(Field(r, name), "True", StringComparison.OrdinalIgnoreCase);

        while (rows.MoveNext())
        {
            ct.ThrowIfCancellationRequested();
            string[] r = rows.Current;
            if (r.Length == 0 || (r.Length == 1 && string.IsNullOrWhiteSpace(r[0]))) continue;

            // Per-app "last activity date" columns (Microsoft's header names).
            string? word = Field(r, "Word Last Activity Date");
            string? excel = Field(r, "Excel Last Activity Date");
            string? ppt = Field(r, "PowerPoint Last Activity Date");
            string? outl = Field(r, "Outlook Last Activity Date");
            string? note = Field(r, "OneNote Last Activity Date");
            string? team = Field(r, "Teams Last Activity Date");

            records.Add(new FactM365AppUsage(
                UserPrincipalName: Field(r, "User Principal Name"),
                DisplayName: Field(r, "Display Name"),
                Concealed: concealed,
                ReportRefreshDate: Field(r, "Report Refresh Date"),
                ReportPeriodDays: periodDays,
                IsDeleted: Flag(r, "Is Deleted"),
                WordLastActivityDate: word,
                ExcelLastActivityDate: excel,
                PowerPointLastActivityDate: ppt,
                OutlookLastActivityDate: outl,
                OneNoteLastActivityDate: note,
                TeamsLastActivityDate: team,
                LastActivityAnyDate: MaxDate(word, excel, ppt, outl, note, team),
                // Platform columns are "Yes"/blank in this report.
                UsedWeb: YesNo(Field(r, "Web")),
                UsedMobile: YesNo(Field(r, "Mobile")),
                UsedWindows: YesNo(Field(r, "Windows")),
                UsedMac: YesNo(Field(r, "Mac"))));
        }
        return records;
    }

    private static bool YesNo(string? v) => string.Equals(v, "Yes", StringComparison.OrdinalIgnoreCase);

    // Pick the latest of several report dates. Parse each (InvariantCulture) and compare as
    // dates rather than lexically, so a non-ISO render (e.g. M/d/yyyy) can't pick the wrong
    // "latest". The original (unparsed) string is returned so downstream parsing is unchanged.
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
            else if (max is null) max = d;   // unparseable: keep first seen rather than drop it
        }
        return max;
    }

    private EntityEnvelope<T> Envelope<T>(T item, string snapshotUtc) =>
        new EntityEnvelope<T>(Name, _runId, snapshotUtc, item);
}
