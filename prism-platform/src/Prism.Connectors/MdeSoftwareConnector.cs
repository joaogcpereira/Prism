// ============================================================
//  MdeSoftwareConnector.cs
//  Source: defender.software. OPTIONAL (behind EnableMdeConnector).
//
//  Microsoft Defender for Endpoint / Defender Vulnerability Management:
//   1. GET /api/Software            -> org-wide software inventory
//      (one row per title, with ExposedMachines = install footprint)
//      => fact.SoftwareInventory.
//   2. (optional) GET /api/Software/{id}/machineReferences for licensing-
//      relevant titles, capped & adaptively paced => fact.SoftwareInstall
//      (which devices carry the title).
//
//  This corroborates Intune detectedApps (a second, independent install
//  signal) and feeds license-waste analysis for desktop software that has
//  no M365 SKU. Skipped unless enabled AND base URL / tenant / app id are
//  configured. The MDE API is OData V4 and rate-limited; MdeClient honors
//  Retry-After and this connector paces the (expensive) expansion phase.
// ============================================================
using System.Text.Json;
using Prism.Connectors.Defender;
using Prism.Warehouse;
using Prism.Warehouse.Model;

namespace Prism.Connectors;

public sealed class MdeSoftwareConnector : IConnector
{
    public string Name => "defender.software";

    private readonly MdeClient? _client;
    private readonly IIngestionSink _sink;
    private readonly ConnectorOptions _opts;
    private readonly string _runId;
    private readonly ILogger<MdeSoftwareConnector> _log;

    public MdeSoftwareConnector(MdeClient? client, IIngestionSink sink, ConnectorOptions opts, string runId, ILogger<MdeSoftwareConnector> log)
    {
        _client = client;
        _sink = sink;
        _opts = opts;
        _runId = runId;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (!_opts.EnableMdeConnector || _client is null)
        {
            _log.LogInformation("Defender for Endpoint connector disabled or not configured; skipping.");
            return;
        }

        string snapshotUtc = DateTime.UtcNow.ToString("o");

        // ---- 1. Org-wide software inventory ----------------------------
        _log.LogInformation("Fetching Defender for Endpoint software inventory...");
        var inventory = new List<FactSoftwareInventory>();
        int top = Math.Clamp(_opts.MdePageSize, 1, 10000);   // API max $top = 10000
        await _client.GetPagedAsync($"api/Software?$top={top}", item =>
        {
            string? id = Str(item, "id");
            if (string.IsNullOrEmpty(id)) return;
            inventory.Add(new FactSoftwareInventory(
                SoftwareId: id,
                Name: Str(item, "name"),
                Vendor: Str(item, "vendor"),
                Weaknesses: Int(item, "weaknesses"),
                PublicExploit: Bool(item, "publicExploit"),
                ActiveAlert: Bool(item, "activeAlert"),
                ExposedMachines: Int(item, "exposedMachines"),
                ImpactScore: Num(item, "impactScore")));
        }, ct).ConfigureAwait(false);

        await _sink.WriteAsync("software-inventory", inventory.Select(x => Envelope(x, snapshotUtc)), ct);
        _log.LogInformation("Software inventory: {Count} title(s).", inventory.Count);

        // ---- 2. Per-device expansion (optional) ------------------------
        if (!_opts.ExpandSoftwareMachines)
        {
            _log.LogInformation("Per-device software expansion disabled (Prism__ExpandSoftwareMachines=false); inventory only.");
            return;
        }

        // Licensing-relevant titles first (same patterns as Intune watched apps),
        // then the rest by footprint, until the cap is spent. One Graph-equivalent
        // call per title (plus continuation pages).
        string[] patterns = _opts.InstallVisibilityPatterns ?? [];
        bool Watched(FactSoftwareInventory s) => patterns.Any(p =>
            !string.IsNullOrEmpty(p) && (s.Name ?? "").IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0);

        var selected = inventory
            .Where(s => (s.ExposedMachines ?? 0) > 0)
            .OrderByDescending(Watched)
            .ThenByDescending(s => s.ExposedMachines ?? 0)
            .Take(Math.Max(0, _opts.MaxSoftwareExpansions))
            .ToList();

        if (selected.Count == 0) { _log.LogInformation("No titles to expand."); return; }

        _log.LogInformation("Expanding per-device installs for {N} title(s) (adaptive pacing)...", selected.Count);
        var installs = new List<FactSoftwareInstall>();
        int baseDelay = Math.Max(0, _opts.SoftwareExpansionDelayMs);
        int maxDelay  = Math.Max(baseDelay, _opts.InstallExpansionMaxDelayMs);
        int curDelay  = baseDelay;
        int done = 0;

        foreach (FactSoftwareInventory sw in selected)
        {
            ct.ThrowIfCancellationRequested();
            long throttleBefore = _client.ThrottleEvents;

            try
            {
                string idSeg = Uri.EscapeDataString(sw.SoftwareId);
                await _client.GetPagedAsync($"api/Software/{idSeg}/machineReferences", m =>
                {
                    installs.Add(new FactSoftwareInstall(
                        SoftwareId: sw.SoftwareId,
                        SoftwareName: sw.Name,
                        Vendor: sw.Vendor,
                        MachineId: Str(m, "id"),
                        ComputerDnsName: Str(m, "computerDnsName"),
                        OsPlatform: Str(m, "osPlatform")));
                }, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException)
            {
                // A title that errors out (e.g. transient 5xx beyond the ceiling) is
                // logged and skipped — never aborts the whole sweep.
                _log.LogWarning("machineReferences failed for {Id} ({Msg}); skipping that title.", sw.SoftwareId, ex.Message);
            }

            // AIMD: if MdeClient had to wait on a 429 during this title, widen the pace;
            // otherwise ease it back toward the floor.
            if (_client.ThrottleEvents > throttleBefore) curDelay = Math.Min(maxDelay, Math.Max(1000, curDelay * 2));
            else if (curDelay > baseDelay) curDelay = Math.Max(baseDelay, curDelay - 250);

            if (curDelay > 0) await Task.Delay(curDelay, ct).ConfigureAwait(false);

            if (++done % 25 == 0)
                _log.LogInformation("  ...{Done}/{Total} title(s), {Rows} install row(s), pace {Delay}ms.",
                    done, selected.Count, installs.Count, curDelay);
        }

        await _sink.WriteAsync("software-installs", installs.Select(x => Envelope(x, snapshotUtc)), ct);
        _log.LogInformation("Per-device software installs: {Rows} row(s) across {Titles} title(s).", installs.Count, selected.Count);
    }

    // ---- defensive JSON reads ------------------------------------------
    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d) ? d : null;

    private static long? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n) ? n : null;

    private static bool? Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
            ? v.GetBoolean() : null;

    private EntityEnvelope<T> Envelope<T>(T item, string snapshotUtc) =>
        new EntityEnvelope<T>(Name, _runId, snapshotUtc, item);
}
