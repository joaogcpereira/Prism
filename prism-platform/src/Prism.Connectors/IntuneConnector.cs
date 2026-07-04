// ============================================================
//  IntuneConnector.cs
//  Source: intune.devices. Read-only (DeviceManagementManagedDevices.Read.All).
//
//  Two pulls:
//   * /deviceManagement/managedDevices  -> device inventory (compliance,
//     owner, last check-in) = DimDevice.
//   * /deviceManagement/detectedApps    -> fleet-wide install presence
//     (which apps, on how many devices) = FactDetectedApp.
//
//  This corroborates the agent's Win32 usage signal and covers devices
//  that don't run the agent (install-presence, not active usage).
// ============================================================
using Prism.Connectors.Graph;
using Prism.Connectors.Json;
using Prism.Warehouse;
using Prism.Warehouse.Model;

namespace Prism.Connectors;

public sealed class IntuneConnector : IConnector
{
    public string Name => "intune.devices";

    private readonly GraphClient _graph;
    private readonly IIngestionSink _sink;
    private readonly ConnectorOptions _opts;
    private readonly string _runId;
    private readonly ILogger<IntuneConnector> _log;

    public IntuneConnector(GraphClient graph, IIngestionSink sink, ConnectorOptions opts, string runId, ILogger<IntuneConnector> log)
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

        // ---- 1. Managed devices ----------------------------------------
        _log.LogInformation("Fetching Intune managed devices...");
        const string deviceUrl =
            "deviceManagement/managedDevices?$select=id,deviceName,userId,userPrincipalName,operatingSystem," +
            "osVersion,complianceState,managedDeviceOwnerType,managementState,enrolledDateTime,lastSyncDateTime," +
            "model,manufacturer,serialNumber,isEncrypted";

        var devices = new List<DimDevice>();
        await foreach (GraphManagedDevice d in _graph.GetPagedAsync<GraphDevicesResponse, GraphManagedDevice>(
            deviceUrl, GraphJsonContext.Default.GraphDevicesResponse, p => (p.Value, p.NextLink), ct))
        {
            // Item 6: Prism covers Windows workstations and servers only. Exclude
            // Android, iOS, macOS, Windows Phone/Mobile, Holographic, etc. at the
            // source so every downstream view, drill, and report is Windows-scoped.
            if (!IsWindowsEndpoint(d.OperatingSystem)) continue;
            devices.Add(new DimDevice(
                DeviceId: d.Id,
                DeviceName: d.DeviceName,
                UserId: d.UserId,
                UserPrincipalName: d.UserPrincipalName,
                OperatingSystem: d.OperatingSystem,
                OsVersion: d.OsVersion,
                ComplianceState: d.ComplianceState,
                OwnerType: d.ManagedDeviceOwnerType,
                ManagementState: d.ManagementState,
                EnrolledDateTime: d.EnrolledDateTime,
                LastSyncDateTime: d.LastSyncDateTime,
                Model: d.Model,
                Manufacturer: d.Manufacturer,
                SerialNumber: d.SerialNumber,
                IsEncrypted: d.IsEncrypted));
        }
        await _sink.WriteAsync("devices", devices.Select(x => Envelope(x, snapshotUtc)), ct);

        // ---- 2. Detected apps (fleet-wide install presence) ------------
        _log.LogInformation("Fetching Intune detected apps...");
        var apps = new List<FactDetectedApp>();
        await foreach (GraphDetectedApp a in _graph.GetPagedAsync<GraphDetectedAppsResponse, GraphDetectedApp>(
            "deviceManagement/detectedApps", GraphJsonContext.Default.GraphDetectedAppsResponse,
            p => (p.Value, p.NextLink), ct))
        {
            // Item 6: keep Windows (and unknown, in a Windows-only fleet) detected
            // apps; drop iOS/macOS/Android/Windows-Mobile/Phone/Holographic inventory.
            if (!IsWindowsPlatformApp(a.Platform)) continue;
            apps.Add(new FactDetectedApp(
                AppId: a.Id,
                DisplayName: a.DisplayName,
                Version: a.Version,
                Publisher: a.Publisher,
                Platform: a.Platform,
                DeviceCount: a.DeviceCount,
                SizeInByte: a.SizeInByte));
        }
        await _sink.WriteAsync("detected-apps", apps.Select(x => Envelope(x, snapshotUtc)), ct);

        // ---- 3. Per-device install visibility (item 4, extended) -------
        // Licensing-relevant apps (InstallVisibilityPatterns) are expanded first and
        // unconditionally; with ExpandAllInstalls the remaining inventory follows,
        // most-installed first, until the MaxInstallExpansions budget (one Graph call
        // per app) is spent. Store/UWP identity names ("Publisher.Package": a dot, no
        // spaces - mirrors ref.AppExclusion) and zero-install rows are skipped, so the
        // budget is spent on apps the dashboard actually shows.
        string[] patterns = _opts.InstallVisibilityPatterns ?? [];
        bool Watched(FactDetectedApp a) => patterns.Any(p =>
            !string.IsNullOrEmpty(p) && a.DisplayName!.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0);

        // Intune's detected-app inventory is PER (app, version) - "Google Chrome" is
        // dozens of rows, each with its own AppId. Selection therefore works per
        // APPLICATION NAME: an app is either fully expanded (every version-row) or
        // not expanded at all. Partially expanded apps made the dashboard's
        // per-version device lists inconsistent with the inventory counts.
        var candidates = apps
            .Where(a => !string.IsNullOrEmpty(a.DisplayName) && !string.IsNullOrEmpty(a.AppId) && a.DeviceCount > 0)
            .Where(a => Watched(a) || (_opts.ExpandAllInstalls && !IsStoreIdentityName(a.DisplayName!)))
            .ToList();
        var groups = candidates
            .GroupBy(a => a.DisplayName!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Any(Watched))                       // watched apps never dropped by the cap
            .ThenByDescending(g => g.Sum(a => (long)a.DeviceCount))
            .ToList();
        int cap = Math.Max(0, _opts.MaxInstallExpansions);
        var expandable = new List<FactDetectedApp>();
        int includedNames = 0;
        foreach (var g in groups)
        {
            int size = g.Count();
            if (expandable.Count + size > cap && expandable.Count > 0) continue;   // app doesn't fit whole - skip it (smaller apps may still fit)
            expandable.AddRange(g);
            includedNames++;
            if (expandable.Count >= cap) break;
        }
        if (includedNames < groups.Count)
            _log.LogWarning("Install expansion cap ({Cap} version-rows) covers {In} of {All} application(s); " +
                "raise Prism__MaxInstallExpansions to include the rest.", cap, includedNames, groups.Count);
        // Cadence gate: the full sweep can run weekly (e.g. Prism__ExpandInstallsDayOfWeek=Sunday).
        // On other days the phase is skipped and fact.AppInstall keeps its last snapshot -
        // the sink only REPLACEs when we actually write.
        string expandDay = (_opts.ExpandInstallsDayOfWeek ?? "").Trim();
        bool expandToday = expandDay.Length == 0
                           || expandDay.Equals("any", StringComparison.OrdinalIgnoreCase)
                           || DateTime.UtcNow.DayOfWeek.ToString().Equals(expandDay, StringComparison.OrdinalIgnoreCase);
        if (!expandToday)
        {
            _log.LogInformation("Per-device install expansion skipped (runs on {Day}); fact.AppInstall keeps its previous snapshot.", expandDay);
        }
        else if (expandable.Count > 0)
        {
            // Graph JSON $batch, 20 sub-requests per call. ONE work queue carries both
            // first pages and continuation pages (nextLink rewritten to a batch-relative
            // URL), so paging is batched too and nothing is skipped silently. Inner
            // per-item 429s honor the service's Retry-After and re-queue the item.
            _log.LogInformation("Fetching per-device installs: {Rows} app-version request(s) for {Apps} application(s) via $batch (budget {Secs}s)...",
                expandable.Count, includedNames, _opts.InstallExpansionTimeBudgetSeconds);
            var installs = new List<FactAppInstall>();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool unlimited = _opts.InstallExpansionTimeBudgetSeconds <= 0;          // 0 = run to completion
            var pending = new Queue<(FactDetectedApp App, string Url, int Attempts)>(
                expandable.Select(a => (a,
                    $"/deviceManagement/detectedApps/{a.AppId}/managedDevices?$select=id,deviceName,userPrincipalName", 0)));
            int pages = 0, batches = 0, dropped = 0;
            int baseDelay = Math.Max(0, _opts.InstallExpansionDelayMs);
            int maxDelay  = Math.Max(baseDelay, _opts.InstallExpansionMaxDelayMs);
            int curDelay  = baseDelay;          // adaptive inter-batch pace (AIMD)
            const int hardAttemptCap = 100;     // ONLY non-throttle failures (5xx / network) count toward this

            while (pending.Count > 0)
            {
                if (!unlimited && sw.Elapsed.TotalSeconds > _opts.InstallExpansionTimeBudgetSeconds)
                {
                    _log.LogWarning("Install expansion time budget ({Secs}s) reached with {Left} request(s) left - " +
                        "writing what was gathered. Clear Prism__InstallExpansionTimeBudgetSeconds (0) to always run to completion.",
                        _opts.InstallExpansionTimeBudgetSeconds, pending.Count);
                    break;
                }

                // Positional batch ids ("0".."19") - robust regardless of how Graph
                // echoes ids - mapped back to the dequeued items by index.
                var meta = new List<(FactDetectedApp App, string Url, int Attempts)>(20);
                var chunk = new List<(string Id, string Url)>(20);
                while (chunk.Count < 20 && pending.Count > 0)
                {
                    var item = pending.Dequeue();
                    chunk.Add((meta.Count.ToString(), item.Url));
                    meta.Add(item);
                }
                batches++;

                System.Text.Json.JsonDocument doc;
                try { doc = await _graph.PostBatchAsync(chunk, ct); }
                catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException)
                {
                    // The whole $batch POST failed even after GraphClient's throttle-aware
                    // retries (network blip, or throttling beyond the ceiling). Don't drop:
                    // widen the pace, re-queue everything, and cool off before trying again.
                    curDelay = Math.Min(maxDelay, Math.Max(1000, curDelay * 2));
                    foreach (var item in meta)
                        if (item.Attempts < hardAttemptCap) pending.Enqueue((item.App, item.Url, item.Attempts + 1)); else dropped++;
                    _log.LogWarning("$batch POST failed ({Message}); re-queued {N} request(s), pace now {Delay}ms.", ex.Message, meta.Count, curDelay);
                    await Task.Delay(curDelay, ct).ConfigureAwait(false);
                    continue;
                }

                double waitSec = 0; bool throttled = false; int ok = 0;
                using (doc)
                {
                    foreach (System.Text.Json.JsonElement r in doc.RootElement.GetProperty("responses").EnumerateArray())
                    {
                        if (!r.TryGetProperty("id", out var idEl) || !int.TryParse(idEl.GetString(), out int idx)
                            || idx < 0 || idx >= meta.Count) continue;
                        (FactDetectedApp app, string url, int tries) = meta[idx];
                        int status = r.TryGetProperty("status", out var stEl) ? stEl.GetInt32() : 0;

                        if (status == 200 && r.TryGetProperty("body", out System.Text.Json.JsonElement body))
                        {
                            if (body.TryGetProperty("value", out System.Text.Json.JsonElement val))
                                foreach (System.Text.Json.JsonElement d in val.EnumerateArray())
                                    installs.Add(new FactAppInstall(
                                        AppId: app.AppId,
                                        DisplayName: app.DisplayName,
                                        DeviceId: d.TryGetProperty("id", out var p1) ? p1.GetString() : null,
                                        DeviceName: d.TryGetProperty("deviceName", out var p2) ? p2.GetString() : null,
                                        UserPrincipalName: d.TryGetProperty("userPrincipalName", out var p3) ? p3.GetString() : null));
                            pages++; ok++;
                            // Continuation page? Back into the SAME batched queue.
                            if (body.TryGetProperty("@odata.nextLink", out var nl) && nl.GetString() is { Length: > 0 } link)
                                pending.Enqueue((app, ToBatchUrl(link), 0));
                        }
                        else if (status == 429)
                        {
                            // THROTTLED - never dropped. Re-queue (attempts unchanged) and honor
                            // this sub-response's Retry-After exactly; the pace widens below.
                            throttled = true;
                            pending.Enqueue((app, url, tries));
                            waitSec = Math.Max(waitSec, ReadRetryAfter(r, 5));
                        }
                        else if (status >= 500)
                        {
                            if (tries < hardAttemptCap) pending.Enqueue((app, url, tries + 1)); else dropped++;
                            waitSec = Math.Max(waitSec, ReadRetryAfter(r, 2));
                        }
                        // other 4xx: app vanished between inventory and expansion - no rows, not retried.
                    }
                }

                // AIMD: double the pace on ANY throttle signal (up to the ceiling); ease it
                // back down only when a batch came back entirely clean.
                if (throttled) curDelay = Math.Min(maxDelay, Math.Max(1000, curDelay * 2));
                else if (ok == meta.Count && curDelay > baseDelay) curDelay = Math.Max(baseDelay, curDelay - 250);

                // Wait the larger of the honored Retry-After and the current adaptive pace.
                int waitMs = Math.Max((int)(Math.Min(_opts.MaxRetryAfterSeconds, waitSec) * 1000), curDelay);
                if (throttled)
                    _log.LogWarning("Inner 429 in $batch; honoring Retry-After, waiting {S}s (pace {Delay}ms).", waitMs / 1000, curDelay);
                if (waitMs > 0) await Task.Delay(waitMs, ct).ConfigureAwait(false);

                if (batches % 10 == 0)
                    _log.LogInformation("  ...batch {B}: {Pages} page(s), {Rows} install row(s), {Left} request(s) queued, pace {Delay}ms, {Secs:F0}s elapsed.",
                        batches, pages, installs.Count, pending.Count, curDelay, sw.Elapsed.TotalSeconds);
            }

            await _sink.WriteAsync("app-installs", installs.Select(x => Envelope(x, snapshotUtc)), ct);
            if (pending.Count > 0 || dropped > 0)
                _log.LogWarning("Install expansion incomplete: {Rows} row(s) from {Pages} page(s) in {Batches} call(s); " +
                    "{Left} request(s) unfinished, {Dropped} dropped after retries. Raise Prism__InstallExpansionTimeBudgetSeconds for full coverage.",
                    installs.Count, pages, batches, pending.Count, dropped);
            else
                _log.LogInformation("Per-device installs complete: {Rows} row(s) from {Pages} page(s) across {Apps} application(s) in {Batches} $batch call(s), {Secs:F0}s.",
                    installs.Count, pages, includedNames, batches, sw.Elapsed.TotalSeconds);
        }

        // ---- Summary ----------------------------------------------------
        int noncompliant = devices.Count(d => string.Equals(d.ComplianceState, "noncompliant", StringComparison.OrdinalIgnoreCase));
        int stale = devices.Count(d => IsStale(d.LastSyncDateTime, 30));
        _log.LogInformation(
            "Done: {Devices} device(s) ({NonCompliant} noncompliant, {Stale} not synced in 30d); {Apps} detected app(s).",
            devices.Count, noncompliant, stale, apps.Count);
    }

    private static bool IsStale(string? lastSync, int days) =>
        DateTimeOffset.TryParse(lastSync, out DateTimeOffset t) && t < DateTimeOffset.UtcNow.AddDays(-days);

    // Store/UWP/MSIX package identities surface as "Publisher.Package" (a dot, no
    // spaces). vw.AppEstate excludes them (ref.AppExclusion), so spending install-
    // expansion budget on them would be wasted.
    private static bool IsStoreIdentityName(string name) =>
        name.Contains('.') && !name.Contains(' ');

    // Read a $batch sub-response's Retry-After (seconds). Graph serializes header values
    // as strings, but tolerate a JSON number too, and match the header name case-insensitively.
    private static double ReadRetryAfter(System.Text.Json.JsonElement subResponse, double dflt)
    {
        if (subResponse.TryGetProperty("headers", out var h) && h.ValueKind == System.Text.Json.JsonValueKind.Object)
            foreach (var prop in h.EnumerateObject())
            {
                if (!string.Equals(prop.Name, "Retry-After", StringComparison.OrdinalIgnoreCase)) continue;
                if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.String
                    && double.TryParse(prop.Value.GetString(), System.Globalization.NumberStyles.Float,
                           System.Globalization.CultureInfo.InvariantCulture, out double s)) return s;
                if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.Number
                    && prop.Value.TryGetDouble(out double n)) return n;
            }
        return dflt;
    }

    // Rewrite an absolute @odata.nextLink to the version-relative form a $batch
    // sub-request expects ("/deviceManagement/...?...&$skiptoken=..."). Strips whichever
    // API-version segment is present (/v1.0/ or /beta/) so it works regardless of the
    // configured GraphBaseUrl, keeping the leading slash the $batch url field requires.
    private static string ToBatchUrl(string absoluteNextLink)
    {
        var u = new Uri(absoluteNextLink);
        string pq = u.PathAndQuery;
        string[] versionSegments = ["/v1.0/", "/beta/"];
        foreach (string seg in versionSegments)
        {
            int i = pq.IndexOf(seg, StringComparison.OrdinalIgnoreCase);
            if (i >= 0) return pq[(i + seg.Length - 1)..];   // keep the segment's trailing slash
        }
        return pq;
    }

    // Item 6 - Windows-only scope. Intune reports operatingSystem = "Windows" for
    // desktops, laptops, and servers alike, so a Windows prefix (minus the mobile
    // variants) is the right inclusion test.
    private static bool IsWindowsEndpoint(string? os)
    {
        if (string.IsNullOrWhiteSpace(os)) return false;
        if (!os.StartsWith("Windows", StringComparison.OrdinalIgnoreCase)) return false;
        return os.IndexOf("Phone", StringComparison.OrdinalIgnoreCase) < 0
            && os.IndexOf("Mobile", StringComparison.OrdinalIgnoreCase) < 0
            && os.IndexOf("Holographic", StringComparison.OrdinalIgnoreCase) < 0;
    }

    // detectedApp.platform is an enum; deny-list the non-Windows values and keep
    // windows + unknown/blank (unknown is common and safe to keep given the device
    // filter above already restricts the estate to Windows endpoints).
    private static bool IsWindowsPlatformApp(string? platform)
    {
        if (string.IsNullOrWhiteSpace(platform)) return true;
        string p = platform.Trim();
        return !(p.Equals("ios", StringComparison.OrdinalIgnoreCase)
              || p.Equals("macOS", StringComparison.OrdinalIgnoreCase)
              || p.Equals("chromeOS", StringComparison.OrdinalIgnoreCase)
              || p.StartsWith("android", StringComparison.OrdinalIgnoreCase)
              || p.Equals("windowsMobile", StringComparison.OrdinalIgnoreCase)
              || p.Equals("windowsPhone", StringComparison.OrdinalIgnoreCase)
              || p.Equals("windowsHolographic", StringComparison.OrdinalIgnoreCase));
    }

    private EntityEnvelope<T> Envelope<T>(T item, string snapshotUtc) =>
        new EntityEnvelope<T>(Name, _runId, snapshotUtc, item);
}
