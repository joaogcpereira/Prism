// ============================================================
//  ServiceUsageConnector.cs
//  Source: m365.service-usage. Read-only (Reports.Read.All +
//  ReportSettings.Read.All).
//
//  The strongest "is this licence actually used" signal: per-user
//  last-activity dates per workload from getOffice365ActiveUserDetail.
//
//  Flow:
//   1. Read /admin/reportSettings -> displayConcealedNames. If on,
//      the report's UPN/Display Name are masked and cannot be joined
//      to identity; we flag every record Concealed=true and warn.
//   2. GET getOffice365ActiveUserDetail(period) -> 302 -> download CSV.
//   3. Parse by HEADER NAME (not position - Microsoft has reordered
//      columns before) and normalize to FactServiceUsage.
// ============================================================
using System.Text;
using Prism.Connectors.Graph;
using Prism.Connectors.Json;
using Prism.Warehouse;
using Prism.Warehouse.Model;

namespace Prism.Connectors;

public sealed class ServiceUsageConnector : IConnector
{
    public string Name => "m365.service-usage";

    private readonly GraphClient _graph;
    private readonly IIngestionSink _sink;
    private readonly ConnectorOptions _opts;
    private readonly string _runId;
    private readonly ILogger<ServiceUsageConnector> _log;

    public ServiceUsageConnector(GraphClient graph, IIngestionSink sink, ConnectorOptions opts, string runId, ILogger<ServiceUsageConnector> log)
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

        // ---- 1. Concealment state --------------------------------------
        bool concealed = false;
        try
        {
            ReportSettings? settings = await _graph.GetJsonAsync(
                "admin/reportSettings", GraphJsonContext.Default.ReportSettings, ct);
            concealed = settings?.DisplayConcealedNames ?? false;
        }
        catch (Exception ex)
        {
            _log.LogWarning("Could not read /admin/reportSettings ({Msg}); assuming names are NOT concealed.", ex.Message);
        }

        if (concealed)
            _log.LogWarning("Report anonymization (displayConcealedNames) is ON: per-user M365 activity is MASKED and " +
                            "cannot be joined to identity. Ingesting with concealed=true - downstream should treat per-user " +
                            "M365 usage as unavailable and rely on sign-in activity + the agent's Win32 signal instead.");
        else
            _log.LogInformation("Report anonymization is OFF: report UPNs are real and join to users.");

        // ---- 2. Download the activity report ---------------------------
        string period = string.IsNullOrWhiteSpace(_opts.ServiceUsagePeriod) ? "D30" : _opts.ServiceUsagePeriod.Trim();
        string url = $"reports/getOffice365ActiveUserDetail(period='{period}')";
        _log.LogInformation("Downloading getOffice365ActiveUserDetail({Period})...", period);

        string tmp = await _graph.DownloadReportToFileAsync(url, ct);
        try
        {
            // ---- 3. Parse + normalize ----------------------------------
            List<FactServiceUsage> records = ParseReport(tmp, period, concealed, ct);

            await _sink.WriteAsync("service-usage", records.Select(x => Envelope(x, snapshotUtc)), ct);

            int noActivity = records.Count(r => string.IsNullOrEmpty(r.LastActivityAnyDate));
            int teamsIdle = records.Count(r => r.HasTeamsLicense && string.IsNullOrEmpty(r.TeamsLastActivityDate));
            _log.LogInformation(
                "Done: {Rows} user(s) over {Period} (concealed={Concealed}); {NoAct} with no activity in any workload; " +
                "{TeamsIdle} with a Teams licence but no Teams activity.",
                records.Count, period, concealed, noActivity, teamsIdle);
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* best-effort temp cleanup */ }
        }
    }

    private List<FactServiceUsage> ParseReport(string path, string period, bool concealed, CancellationToken ct)
    {
        string periodDays = period.TrimStart('D', 'd');
        var records = new List<FactServiceUsage>();

        using var reader = new StreamReader(File.OpenRead(path), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        using IEnumerator<string[]> rows = Csv.ReadRows(reader).GetEnumerator();

        if (!rows.MoveNext()) { _log.LogWarning("Report was empty (no header)."); return records; }

        // Header name -> column index (tolerant of reordering / added columns).
        string[] header = rows.Current;
        var col = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < header.Length; i++) col[header[i].Trim()] = i;

        string? Field(string[] r, string name) =>
            col.TryGetValue(name, out int i) && i < r.Length && !string.IsNullOrWhiteSpace(r[i]) ? r[i].Trim() : null;
        bool Flag(string[] r, string name) =>
            string.Equals(Field(r, name), "True", StringComparison.OrdinalIgnoreCase);

        while (rows.MoveNext())
        {
            ct.ThrowIfCancellationRequested();
            string[] r = rows.Current;
            if (r.Length == 0 || (r.Length == 1 && string.IsNullOrWhiteSpace(r[0]))) continue;

            string? ex = Field(r, "Exchange Last Activity Date");
            string? od = Field(r, "OneDrive Last Activity Date");
            string? sp = Field(r, "SharePoint Last Activity Date");
            string? tm = Field(r, "Teams Last Activity Date");
            string? ym = Field(r, "Yammer Last Activity Date");
            string? sk = Field(r, "Skype For Business Last Activity Date");

            records.Add(new FactServiceUsage(
                UserPrincipalName: Field(r, "User Principal Name"),
                DisplayName: Field(r, "Display Name"),
                Concealed: concealed,
                ReportRefreshDate: Field(r, "Report Refresh Date"),
                ReportPeriodDays: periodDays,
                IsDeleted: Flag(r, "Is Deleted"),
                HasExchangeLicense: Flag(r, "Has Exchange License"),
                HasOneDriveLicense: Flag(r, "Has OneDrive License"),
                HasSharePointLicense: Flag(r, "Has SharePoint License"),
                HasTeamsLicense: Flag(r, "Has Teams License"),
                HasYammerLicense: Flag(r, "Has Yammer License"),
                HasSkypeLicense: Flag(r, "Has Skype For Business License"),
                ExchangeLastActivityDate: ex,
                OneDriveLastActivityDate: od,
                SharePointLastActivityDate: sp,
                TeamsLastActivityDate: tm,
                YammerLastActivityDate: ym,
                SkypeLastActivityDate: sk,
                LastActivityAnyDate: MaxDate(ex, od, sp, tm, ym, sk),   // chronological max (format-robust)
                AssignedProducts: Field(r, "Assigned Products")));
        }
        return records;
    }

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
