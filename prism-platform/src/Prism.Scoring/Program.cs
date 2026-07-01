// ============================================================
//  Program.cs  (Prism.Scoring)
//  Job-style: read the warehouse -> score every (user, SKU) and
//  every SKU's idle seats -> write verdicts + a run summary -> exit.
//  Drops into a Container Apps Job / scheduled task / timer Function.
// ============================================================
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Prism.Scoring;

var builder = Host.CreateApplicationBuilder(args);
var opts = new ScoringOptions();
builder.Configuration.GetSection(ScoringOptions.SectionName).Bind(opts);

using IHost host = builder.Build();
ILogger log = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Prism.Scoring");

if (string.IsNullOrWhiteSpace(opts.ConnectionString))
{
    log.LogError("No Prism:ConnectionString configured; nothing to score.");
    return 1;
}

string runId = $"{DateTime.UtcNow:yyyyMMddTHHmmss}-{Guid.NewGuid():N}"[..22];
DateTime now = DateTime.UtcNow;
using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
CancellationToken ct = cts.Token;

try
{
    var reader = new Reader(opts.ConnectionString);
    log.LogInformation("Scoring run {RunId}: reading warehouse...", runId);

    Dictionary<string, (decimal Cost, string Currency)> costs = await reader.ReadCostsAsync(ct);
    List<SignalRow> signals = await reader.ReadSignalsAsync(ct);
    Dictionary<string, List<AppRow>> appByUser = await reader.ReadAppUsageAsync(ct);
    Dictionary<string, List<InstallRow>> installsByUser = await reader.ReadInstallsAsync(ct);
    Dictionary<string, InstallCoverageRow> installCoverage = await reader.ReadInstallCoverageAsync(ct);
    Dictionary<string, List<RunRow>> runsByUser = await reader.ReadMdeRunsAsync(ct);
    Dictionary<string, List<SignInRow>> signInsByUser = await reader.ReadAppSignInsAsync(ct);
    Dictionary<string, M365AppRow> officeByUser = await reader.ReadM365AppUsageAsync(ct);
    Dictionary<string, CopilotRow> copilotByUser = await reader.ReadCopilotUsageAsync(ct);
    Dictionary<string, TeamsActivityRow> teamsByUser = await reader.ReadTeamsActivityAsync(ct);
    List<SkuRow> skus = await reader.ReadSkuUtilizationAsync(ct);
    string currency = costs.Values.Select(v => v.Currency).FirstOrDefault() ?? "USD";

    log.LogInformation("Read {Signals} assignment(s), {Skus} SKU(s), {Users} user(s) with app-usage coverage, " +
        "{InstallUsers} user(s) with install inventory, {RunUsers} user(s) with MDE run telemetry, " +
        "{SignInUsers} user(s) with app sign-ins, {OfficeUsers} user(s) with M365 Apps usage, {Costs} priced SKU(s).",
        signals.Count, skus.Count, appByUser.Count, installsByUser.Count, runsByUser.Count,
        signInsByUser.Count, officeByUser.Count, costs.Count);

    // Run-summary savings are accumulated as plain decimals and reported under a single
    // currency label; if ref.SkuCost holds more than one currency the totals are a
    // meaningless mixed-currency sum. Per-verdict currency is always stored correctly, but
    // warn loudly so the operator normalizes the price table (single currency expected).
    var distinctCurrencies = costs.Values.Select(v => v.Currency)
        .Where(c => !string.IsNullOrWhiteSpace(c))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    if (distinctCurrencies.Count > 1)
        log.LogWarning("Cost data spans {Count} currencies ({Currencies}); run-summary savings totals assume a " +
            "single currency and will mis-sum. Per-verdict currency is stored correctly.",
            distinctCurrencies.Count, string.Join(", ", distinctCurrencies));

    // ---- score each (user, SKU) assignment ----
    var assignmentRows = new List<VerdictWriter.AssignmentRow>(signals.Count);
    int keep = 0, review = 0, reclaim = 0;
    decimal reclaimSavings = 0, reviewSavings = 0;

    foreach (SignalRow r in signals)
    {
        (decimal Cost, string Currency)? price = costs.TryGetValue(r.SkuPartNumber ?? "", out var p) ? p : null;
        decimal? unitCost = price?.Cost;
        string cur = price?.Currency ?? currency;

        AppSignal? app = ResolveApp(r, opts, appByUser, now);
        InstallSignal? install = ResolveInstall(r, opts, installsByUser, installCoverage);
        MdeRunSignal? mdeRun = ResolveMdeRun(r, opts, runsByUser, installCoverage, now);
        SignInSignal? webSignIn = ResolveSignIn(r, opts, signInsByUser, now);
        OfficeAppsSignal? office = ResolveOffice(r, officeByUser, now, opts);
        CopilotSignal? copilot = ResolveCopilot(r, opts, copilotByUser, now);
        TeamsActivitySignal? teams = ResolveTeams(r, opts, teamsByUser);
        AssignmentVerdict v = ScoringEngine.ScoreAssignment(r, app, install, mdeRun, webSignIn, office, copilot, teams, unitCost, cur, opts, now);

        switch (v.Decision)
        {
            case Decision.Reclaim: reclaim++; reclaimSavings += v.MonthlySavings ?? 0; break;
            case Decision.Review:  review++;  reviewSavings  += v.MonthlySavings ?? 0; break;
            default:               keep++; break;
        }

        assignmentRows.Add(new VerdictWriter.AssignmentRow(
            r.UserId, r.SkuId, r.UserPrincipalName, r.DisplayName, r.SkuPartNumber, r.SkuName,
            v.Decision, v.Score, v.Confidence, Cap(string.Join(",", v.Reasons), 512), v.InactiveDays,
            v.MonthlySavings, v.MonthlySavings is null ? null : cur, r.Department, r.Country));
    }

    // ---- score SKU idle seats ----
    var skuRows = new List<VerdictWriter.SkuRowOut>(skus.Count);
    decimal idleSavings = 0;
    foreach (SkuRow s in skus)
    {
        decimal? unitCost = costs.TryGetValue(s.SkuPartNumber ?? "", out var p) ? p.Cost : null;
        string cur = costs.TryGetValue(s.SkuPartNumber ?? "", out var p2) ? p2.Currency : currency;
        // Free/viral plans often carry thousands of "idle" seats by design — never waste.
        SkuSeatVerdict sv = ScoringEngine.IsFreeSku(s.SkuPartNumber, s.SkuName, unitCost, opts)
            ? new SkuSeatVerdict(Decision.Keep, [Reason.FreeSku], 0m)
            : ScoringEngine.ScoreSkuSeats(s.SeatsOwned, s.SeatsAssigned, unitCost, opts);
        if (sv.Decision != Decision.Keep) idleSavings += sv.MonthlySavings ?? 0;
        skuRows.Add(new VerdictWriter.SkuRowOut(
            s.SkuId, s.SkuPartNumber, s.SkuName, sv.Decision, s.SeatsOwned, s.SeatsAssigned, s.SeatsIdle,
            Cap(string.Join(",", sv.Reasons), 256), sv.MonthlySavings, sv.MonthlySavings is null ? null : cur));
    }

    // ---- persist ----
    var writer = new VerdictWriter(opts.ConnectionString, log);
    await writer.WriteAssignmentsAsync(assignmentRows, runId, now, ct);
    await writer.WriteSkusAsync(skuRows, runId, now, ct);

    // Guard against clobbering the dashboard KPIs with an all-zero summary when the read
    // returned nothing (a transient warehouse gap). The verdict tables are preserved by
    // ReplaceAsync's empty-guard in that case, so the last good summary should stay too.
    if (signals.Count > 0)
    {
        await writer.WriteRunSummaryAsync(runId, now, signals.Count, keep, review, reclaim,
            reclaimSavings, reviewSavings, idleSavings, currency, ct);
    }
    else
    {
        log.LogWarning("Run {RunId}: no assignment signals were read (transient warehouse gap?); " +
            "skipping run-summary write so the dashboard keeps the last good totals.", runId);
    }

    log.LogInformation(
        "Run {RunId} done: {Keep} keep / {Review} review / {Reclaim} reclaim. " +
        "Potential monthly savings ({Cur}): reclaim {Rec}, review {Rev}, idle seats {Idle}.",
        runId, keep, review, reclaim, currency, reclaimSavings, reviewSavings, idleSavings);
    return 0;
}
catch (Exception ex)
{
    log.LogError(ex, "Scoring run {RunId} failed: {Message}", runId, ex.Message);
    return 1;
}

// Resolve the desktop-app signal for an app-tied SKU (null = not app-tied or no agent coverage).
// Usage is attributed to the licensed user via Intune's device primary user (see
// vw.AppUsageByUser90), so this resolves identically for Entra-joined and hybrid devices.
static AppSignal? ResolveApp(SignalRow r, ScoringOptions o, Dictionary<string, List<AppRow>> appByUser, DateTime now)
{
    if (r.SkuPartNumber is null || !o.AppTiedSkus.TryGetValue(r.SkuPartNumber, out string[]? exes)) return null;
    if (string.IsNullOrEmpty(r.UserId) || !appByUser.TryGetValue(r.UserId, out List<AppRow>? rows)) return null;  // device(s) not covered => unknown

    var matching = rows.Where(x => exes.Contains(FileName(x.ExePath), StringComparer.OrdinalIgnoreCase)).ToList();
    int coverage = rows.Count > 0 ? rows[0].CoverageDays : 0;   // per-user; identical across the user's rows
    bool used = matching.Any(x => x.FgActiveSeconds > 0);
    int? daysSince = null;
    if (used)
    {
        var deltas = matching
            .Where(x => x.FgActiveSeconds > 0 && x.LastDay is not null)
            .Select(x => Math.Max(0, (int)Math.Floor((now - x.LastDay!.Value).TotalDays)))
            .ToList();
        if (deltas.Count > 0) daysSince = deltas.Min();
    }
    return new AppSignal(used, daysSince, coverage);
}

// Resolve Defender Advanced Hunting run telemetry for an app-tied SKU (null = SKU not
// app-tied, no hunting data this run, or the user has no MDE-visible devices — in which
// case "no runs" is missing data, never evidence). Reuses the same SKU->exe mapping the
// agent signal uses, so hunting and the agent always watch identical executables.
static MdeRunSignal? ResolveMdeRun(SignalRow r, ScoringOptions o,
    Dictionary<string, List<RunRow>> runsByUser,
    Dictionary<string, InstallCoverageRow> coverage, DateTime nowUtc)
{
    if (runsByUser.Count == 0) return null;                              // feature inert without data
    if (r.SkuPartNumber is null || !o.AppTiedSkus.TryGetValue(r.SkuPartNumber, out string[]? exes)) return null;
    if (string.IsNullOrEmpty(r.UserId)) return null;

    runsByUser.TryGetValue(r.UserId, out List<RunRow>? rows);
    List<RunRow> hits = rows?
        .Where(x => exes.Any(e => string.Equals(e, x.FileName, StringComparison.OrdinalIgnoreCase)))
        .ToList() ?? [];

    if (hits.Count > 0)
    {
        DateTime? last = hits.Max(h => h.LastRunUtc);
        int? daysSince = last is { } l ? Math.Max(0, (int)(nowUtc - l).TotalDays) : null;
        return new MdeRunSignal(true, daysSince, hits.Max(h => h.RunDays));
    }

    // Zero matching runs: only evidence when MDE actually sees the user's devices
    // (proxy: at least one of their devices appears in the MDE software inventory).
    coverage.TryGetValue(r.UserId, out InstallCoverageRow? cov);
    return cov is { MdeSeenDeviceCount: > 0 } ? new MdeRunSignal(false, null, 0) : null;
}

static string FileName(string path)
{
    try { return Path.GetFileName(path); } catch { return path; }
}

// Keep the comma-joined reason list within its warehouse column width
// (AssignmentVerdict.ReasonCodes nvarchar(512); SkuVerdict.ReasonCodes nvarchar(256)) so a
// future expansion of the reason vocabulary can't fail the whole bulk-copy with a truncation.
static string Cap(string s, int max)
{
    if (s.Length <= max) return s;
    string cut = s[..max];
    int lastComma = cut.LastIndexOf(',');
    return lastComma > 0 ? cut[..lastComma] : cut;   // trim a dangling partial code
}

// Resolve Entra per-app sign-in for a WEB/SERVICE-first SKU (null = SKU not sign-in-mapped
// or no sign-in data this run). "Not signed in" is only evidence when the connector ran
// (the dictionary is non-empty); a SKU-mapped user simply absent from it counts as no-use.
static SignInSignal? ResolveSignIn(SignalRow r, ScoringOptions o,
    Dictionary<string, List<SignInRow>> signInsByUser, DateTime nowUtc)
{
    if (signInsByUser.Count == 0) return null;
    if (r.SkuPartNumber is null || !o.AppTiedSignInApps.TryGetValue(r.SkuPartNumber, out string[]? apps)) return null;
    if (string.IsNullOrEmpty(r.UserId)) return null;

    signInsByUser.TryGetValue(r.UserId, out List<SignInRow>? rows);
    List<SignInRow> hits = rows?
        .Where(x => apps.Any(a => string.Equals(a, x.AppId, StringComparison.OrdinalIgnoreCase)))
        .ToList() ?? [];

    if (hits.Count > 0)
    {
        DateTime? last = hits.Max(h => h.LastSignInUtc);
        int? days = last is { } l ? Math.Max(0, (int)(nowUtc - l).TotalDays) : null;
        return new SignInSignal(true, days, true);
    }
    return new SignInSignal(false, null, true);   // connector ran, user has no sign-in to the SKU's app
}

// Resolve the M365 Apps report signal: did the user touch any core app (Word/Excel/PPT)
// vs mail-only, within the shallow-use window. Null when there's no row for the user.
static OfficeAppsSignal? ResolveOffice(SignalRow r, Dictionary<string, M365AppRow> officeByUser,
    DateTime nowUtc, ScoringOptions o)
{
    if (officeByUser.Count == 0 || string.IsNullOrEmpty(r.UserId)) return null;
    if (!officeByUser.TryGetValue(r.UserId, out M365AppRow? m)) return null;

    bool Recent(DateTime? d) => d is { } x && (nowUtc - x).TotalDays <= o.ShallowUseWindowDays;
    bool core = Recent(m.Word) || Recent(m.Excel) || Recent(m.PowerPoint) || Recent(m.OneNote);
    bool mail = Recent(m.Outlook);
    return new OfficeAppsSignal(UsedCoreApps: core, MailOnly: mail && !core);
}

// Resolve the Microsoft 365 Copilot usage signal (null = SKU isn't a Copilot SKU, or the
// Copilot report didn't run this cycle). When the report ran, a Copilot-SKU user with no
// activity row — or a row with no activity date — is an idle Copilot seat (HasReport=true).
static CopilotSignal? ResolveCopilot(SignalRow r, ScoringOptions o,
    Dictionary<string, CopilotRow> copilotByUser, DateTime nowUtc)
{
    if (copilotByUser.Count == 0) return null;                              // connector didn't run
    if (r.SkuPartNumber is null || !o.CopilotSkus.Contains(r.SkuPartNumber, StringComparer.OrdinalIgnoreCase)) return null;
    if (string.IsNullOrEmpty(r.UserId)) return null;

    if (!copilotByUser.TryGetValue(r.UserId, out CopilotRow? row) || row.LastActivity is null)
        return new CopilotSignal(false, null, true);                       // report ran; no Copilot activity => idle seat

    int days = Math.Max(0, (int)(nowUtc - row.LastActivity.Value).TotalDays);
    return new CopilotSignal(true, days, true);
}

// Resolve the Teams Phone usage signal (null = SKU isn't a Teams Phone SKU, or the Teams report
// didn't run). When the report ran, a phone-SKU user with no activity row counts as zero calls.
static TeamsActivitySignal? ResolveTeams(SignalRow r, ScoringOptions o,
    Dictionary<string, TeamsActivityRow> teamsByUser)
{
    if (teamsByUser.Count == 0) return null;                                // connector didn't run
    if (r.SkuPartNumber is null || !o.TeamsPhoneSkus.Contains(r.SkuPartNumber, StringComparer.OrdinalIgnoreCase)) return null;
    if (string.IsNullOrEmpty(r.UserId)) return null;

    if (!teamsByUser.TryGetValue(r.UserId, out TeamsActivityRow? row))
        return new TeamsActivitySignal(0, 0, null, true);                   // report ran; no Teams activity => zero calls
    return new TeamsActivitySignal(row.CallCount, row.MeetingCount, row.LastActivity, true);
}

// Resolve install evidence for an app-tied SKU (null = SKU not install-mapped, or no
// install inventory data at all this run). Fuses Intune detectedApps and Defender for
// Endpoint rows; "not installed" is only trustworthy when an inventory actually SEES
// the user's devices (vw.UserInstallCoverage), so absence is evidence, not a data gap.
static InstallSignal? ResolveInstall(SignalRow r, ScoringOptions o,
    Dictionary<string, List<InstallRow>> installsByUser,
    Dictionary<string, InstallCoverageRow> coverage)
{
    if (installsByUser.Count == 0 && coverage.Count == 0) return null;   // feature inert without data
    if (r.SkuPartNumber is null || !o.AppTiedSoftwareNames.TryGetValue(r.SkuPartNumber, out string[]? frags)) return null;
    if (string.IsNullOrEmpty(r.UserId)) return null;

    coverage.TryGetValue(r.UserId, out InstallCoverageRow? cov);
    bool absenceTrustworthy = cov is not null && cov.ManagedDeviceCount > 0
        && (cov.IntuneSeenDeviceCount > 0 || cov.MdeSeenDeviceCount > 0);

    installsByUser.TryGetValue(r.UserId, out List<InstallRow>? rows);
    bool Match(string name) =>
        frags.Any(f => !string.IsNullOrEmpty(f) && name.Contains(f, StringComparison.OrdinalIgnoreCase))
        && !o.InstallNegativePatterns.Any(nf => !string.IsNullOrEmpty(nf) && name.Contains(nf, StringComparison.OrdinalIgnoreCase));

    List<InstallRow> hits = rows?.Where(x => Match(x.AppName)).ToList() ?? [];
    int sources = hits.Select(h => h.Source).Distinct(StringComparer.OrdinalIgnoreCase).Count();
    return new InstallSignal(hits.Count > 0, sources, cov?.ManagedDeviceCount ?? 0, absenceTrustworthy);
}
