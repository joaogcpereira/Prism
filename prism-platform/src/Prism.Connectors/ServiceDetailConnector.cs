// ============================================================
//  ServiceDetailConnector.cs
//  Source: m365.service-detail. Read-only (Reports.Read.All +
//  ReportSettings.Read.All).
//
//  Consolidates three usage-detail reports into one fact, tagged by service:
//    getMailboxUsageDetail          (storage + item count + last activity)
//    getOneDriveActivityUserDetail  (viewed/edited/synced/shared file counts)
//    getSharePointActivityUserDetail(+ visited page count)
//
//  Adds INTENSITY beyond the workload last-activity dates already in
//  fact.ServiceUsage — e.g. "has a OneDrive licence but edited zero files".
//  All three reports are written in ONE WriteAsync call (a REPLACE deletes by
//  Source, so separate calls would clobber each other).
// ============================================================
using System.Text;
using Prism.Connectors.Graph;
using Prism.Connectors.Json;
using Prism.Warehouse;
using Prism.Warehouse.Model;

namespace Prism.Connectors;

public sealed class ServiceDetailConnector : IConnector
{
    public string Name => "m365.service-detail";

    private readonly GraphClient _graph;
    private readonly IIngestionSink _sink;
    private readonly ConnectorOptions _opts;
    private readonly string _runId;
    private readonly ILogger<ServiceDetailConnector> _log;

    public ServiceDetailConnector(GraphClient graph, IIngestionSink sink, ConnectorOptions opts, string runId, ILogger<ServiceDetailConnector> log)
    {
        _graph = graph;
        _sink = sink;
        _opts = opts;
        _runId = runId;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (!_opts.EnableServiceDetailConnector)
        {
            _log.LogInformation("M365 service-detail connector disabled (Prism__EnableServiceDetailConnector); skipping.");
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
            _log.LogWarning("Report anonymization is ON: per-user service activity is MASKED; ingesting concealed=true.");

        string period = string.IsNullOrWhiteSpace(_opts.ServiceUsagePeriod) ? "D30" : _opts.ServiceUsagePeriod.Trim();
        string periodDays = period.TrimStart('D', 'd');

        var all = new List<FactServiceActivityDetail>();
        all.AddRange(await PullAsync("mailbox",    $"reports/getMailboxUsageDetail(period='{period}')",          concealed, periodDays, ct));
        all.AddRange(await PullAsync("onedrive",   $"reports/getOneDriveActivityUserDetail(period='{period}')",  concealed, periodDays, ct));
        all.AddRange(await PullAsync("sharepoint", $"reports/getSharePointActivityUserDetail(period='{period}')",concealed, periodDays, ct));

        await _sink.WriteAsync("service-detail", all.Select(x => Envelope(x, snapshotUtc)), ct);
        _log.LogInformation("Done: {Rows} service-detail row(s) over {Period} (concealed={Concealed}).", all.Count, period, concealed);
    }

    // Download + parse one report into service-tagged rows. A single report failing (e.g. a
    // service not licensed in the tenant) is logged and skipped so the others still load.
    private async Task<List<FactServiceActivityDetail>> PullAsync(string service, string url, bool concealed, string periodDays, CancellationToken ct)
    {
        var records = new List<FactServiceActivityDetail>();
        string tmp;
        try { tmp = await _graph.DownloadReportToFileAsync(url, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning("service-detail: {Service} report failed ({Msg}); skipping it.", service, ex.Message);
            return records;
        }

        try
        {
            using var reader = new StreamReader(File.OpenRead(tmp), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            using IEnumerator<string[]> rows = Csv.ReadRows(reader).GetEnumerator();
            if (!rows.MoveNext()) { _log.LogWarning("service-detail: {Service} report empty.", service); return records; }

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

                // Union of columns across the three reports; absent columns return null.
                records.Add(new FactServiceActivityDetail(
                    Service: service,
                    UserPrincipalName: Field(r, "User Principal Name"),
                    Concealed: concealed,
                    ReportRefreshDate: Field(r, "Report Refresh Date"),
                    ReportPeriodDays: periodDays,
                    LastActivityDate: Field(r, "Last Activity Date"),
                    ViewedOrEditedFileCount: Field(r, "Viewed Or Edited File Count"),
                    SyncedFileCount: Field(r, "Synced File Count"),
                    SharedInternallyFileCount: Field(r, "Shared Internally File Count"),
                    SharedExternallyFileCount: Field(r, "Shared Externally File Count"),
                    VisitedPageCount: Field(r, "Visited Page Count"),
                    StorageUsedBytes: Field(r, "Storage Used (Byte)"),
                    ItemCount: Field(r, "Item Count")));
            }
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* best-effort */ }
        }
        return records;
    }

    private EntityEnvelope<T> Envelope<T>(T item, string snapshotUtc) =>
        new EntityEnvelope<T>(Name, _runId, snapshotUtc, item);
}
