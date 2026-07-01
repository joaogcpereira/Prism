// ============================================================
//  TeamsActivityConnector.cs
//  Source: m365.teams-activity. Read-only (Reports.Read.All +
//  ReportSettings.Read.All).
//
//  getTeamsUserActivityUserDetail: per-user message / CALL / meeting counts +
//  last activity. The call count is the real usage signal for a Teams Phone
//  (MCOEV) seat — a number that took/made zero calls — which the binary "Teams
//  last activity" in the service-usage report cannot show. Same 302->CSV +
//  concealment handling as the other usage-report connectors.
// ============================================================
using System.Text;
using Prism.Connectors.Graph;
using Prism.Connectors.Json;
using Prism.Warehouse;
using Prism.Warehouse.Model;

namespace Prism.Connectors;

public sealed class TeamsActivityConnector : IConnector
{
    public string Name => "m365.teams-activity";

    private readonly GraphClient _graph;
    private readonly IIngestionSink _sink;
    private readonly ConnectorOptions _opts;
    private readonly string _runId;
    private readonly ILogger<TeamsActivityConnector> _log;

    public TeamsActivityConnector(GraphClient graph, IIngestionSink sink, ConnectorOptions opts, string runId, ILogger<TeamsActivityConnector> log)
    {
        _graph = graph;
        _sink = sink;
        _opts = opts;
        _runId = runId;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (!_opts.EnableTeamsActivityConnector)
        {
            _log.LogInformation("Teams activity connector disabled (Prism__EnableTeamsActivityConnector); skipping.");
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
            _log.LogWarning("Report anonymization is ON: per-user Teams activity is MASKED; ingesting concealed=true.");

        string period = string.IsNullOrWhiteSpace(_opts.ServiceUsagePeriod) ? "D30" : _opts.ServiceUsagePeriod.Trim();
        string url = $"reports/getTeamsUserActivityUserDetail(period='{period}')";
        _log.LogInformation("Downloading getTeamsUserActivityUserDetail({Period})...", period);

        string tmp = await _graph.DownloadReportToFileAsync(url, ct);
        try
        {
            List<FactTeamsActivity> records = ParseReport(tmp, period, concealed, ct);
            await _sink.WriteAsync("teams-activity", records.Select(x => Envelope(x, snapshotUtc)), ct);
            _log.LogInformation("Done: {Rows} Teams user(s) over {Period} (concealed={Concealed}).", records.Count, period, concealed);
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* best-effort */ }
        }
    }

    private List<FactTeamsActivity> ParseReport(string path, string period, bool concealed, CancellationToken ct)
    {
        string periodDays = period.TrimStart('D', 'd');
        var records = new List<FactTeamsActivity>();

        using var reader = new StreamReader(File.OpenRead(path), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        using IEnumerator<string[]> rows = Csv.ReadRows(reader).GetEnumerator();
        if (!rows.MoveNext()) { _log.LogWarning("Teams report was empty (no header)."); return records; }

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

            records.Add(new FactTeamsActivity(
                UserPrincipalName: Field(r, "User Principal Name"),
                Concealed: concealed,
                ReportRefreshDate: Field(r, "Report Refresh Date"),
                ReportPeriodDays: periodDays,
                LastActivityDate: Field(r, "Last Activity Date"),
                TeamChatMessageCount: Field(r, "Team Chat Message Count"),
                PrivateChatMessageCount: Field(r, "Private Chat Message Count"),
                CallCount: Field(r, "Call Count"),
                MeetingCount: Field(r, "Meeting Count")));
        }
        return records;
    }

    private EntityEnvelope<T> Envelope<T>(T item, string snapshotUtc) =>
        new EntityEnvelope<T>(Name, _runId, snapshotUtc, item);
}
