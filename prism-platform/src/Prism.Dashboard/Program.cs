// ============================================================
//  Program.cs  (Prism.Dashboard)
//  Tiny read-only API + static host for the Prism dashboard.
//  Serves dashboard/index.html and exposes /api/* over the
//  warehouse views. Column names are camelCased to match the UI.
//  If no connection string is set, /api/* returns 503 and the UI
//  falls back to its built-in sample data (so it always renders).
//
//  Read-only to Microsoft 365: the /api GETs are SELECTs over warehouse
//  views. The only write is POST /api/decision, which records a human's
//  decision in Prism's OWN score.Decision table - it never calls or
//  modifies Microsoft 365, Entra, Intune or Azure in any way.
// ============================================================
using System.Data;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<JsonOptions>(o => o.SerializerOptions.PropertyNamingPolicy = null); // keys are already camelCase

string? cs = builder.Configuration["Prism:ConnectionString"];
var app = builder.Build();

// --- security headers (defence in depth; pair with Container Apps Entra auth) ---
// CSP: the UI is a single self-contained file (inline CSS/JS, data: logo, Google fonts);
// everything else - external scripts, frames, foreign connect targets - is refused, so
// even an injected tag cannot load code from, or exfiltrate to, another origin.
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"]        = "DENY";
    h["Referrer-Policy"]        = "no-referrer";
    h["Content-Security-Policy"] =
        "default-src 'self'; base-uri 'self'; frame-ancestors 'none'; form-action 'self'; " +
        "img-src 'self' data:; style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; " +
        "font-src https://fonts.gstatic.com; script-src 'self' 'unsafe-inline'; connect-src 'self'; object-src 'none'";
    await next();
});

// --- static UI: resolve the dashboard folder for local OR container layouts ---
string[] candidates =
{
    builder.Configuration["Prism:DashboardDir"] ?? "",
    Path.Combine(builder.Environment.ContentRootPath, "wwwroot"),                 // container
    Path.Combine(builder.Environment.ContentRootPath, "..", "..", "dashboard"),   // dotnet run from src/Prism.Dashboard
    Path.Combine(builder.Environment.ContentRootPath, "dashboard"),
};
string? dashDir = candidates
    .Where(d => !string.IsNullOrEmpty(d))
    .Select(Path.GetFullPath)
    .FirstOrDefault(d => File.Exists(Path.Combine(d, "index.html")));
if (dashDir is not null)
{
    var fp = new PhysicalFileProvider(dashDir);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = fp });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = fp });
}

// --- read-only API over the warehouse views ---
app.MapGet("/api/summary",      () => Single("SELECT * FROM vw.SavingsSummary"));
app.MapGet("/api/review-queue", () => Many(
    "SELECT UserId, SkuId, UserPrincipalName, DisplayName, SkuPartNumber, SkuName, Verdict, WasteScore, " +
    "Confidence, ReasonCodes, EffectiveInactiveDays, EstMonthlySavings, Currency, Department, Country FROM vw.ReviewQueue"));
app.MapGet("/api/history", () => Many(
    "SELECT TOP 24 RunId, ScoredUtc, ReclaimMonthlySavings, ReviewMonthlySavings, IdleSeatMonthlySavings, Currency " +
    "FROM score.RunSummary ORDER BY ScoredUtc DESC"));
app.MapGet("/api/skus", () => Many(
    "SELECT v.SkuId, v.SkuPartNumber, v.SkuName, v.Verdict, v.SeatsOwned, v.SeatsAssigned, v.SeatsIdle, " +
    "v.EstMonthlySavings, v.ReasonCodes, c.MonthlyUnitCost AS Unit " +
    "FROM score.SkuVerdict v LEFT JOIN ref.SkuCost c ON c.SkuPartNumber = v.SkuPartNumber"));
app.MapGet("/api/unpriced", () => Many("SELECT SkuPartNumber, SkuName, SeatsOwned FROM vw.UnpricedSkus"));

// ---- global entity search (command palette): finds ANY user / device / app /
// SKU / department in the warehouse and returns a drill key for each. Read-only,
// parameterised, LIKE-escaped; TOP-bounded per entity so it stays instant. ----
app.MapGet("/api/search", async (string? q) =>
{
    if (string.IsNullOrWhiteSpace(cs)) return Results.StatusCode(503);
    q = (q ?? "").Trim();
    if (q.Length < 2 || q.Length > 100) return Results.Json(Array.Empty<object>());
    string pattern = "%" + q.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[") + "%";
    const string sql = @"
        SELECT * FROM (SELECT TOP 8 'user' AS Kind, UserId AS [Key], DisplayName AS Label, UserPrincipalName AS Meta
                       FROM dim.[User]
                       WHERE DisplayName LIKE @p ESCAPE '\' OR UserPrincipalName LIKE @p ESCAPE '\'
                       ORDER BY DisplayName) u
        UNION ALL
        SELECT * FROM (SELECT TOP 8 'device', DeviceName, DeviceName, OperatingSystem
                       FROM dim.Device WHERE DeviceName LIKE @p ESCAPE '\' ORDER BY DeviceName) d
        UNION ALL
        SELECT * FROM (SELECT TOP 8 'app', Name, Name, Vendor
                       FROM vw.AppEstate
                       WHERE Name LIKE @p ESCAPE '\' OR Vendor LIKE @p ESCAPE '\'
                       ORDER BY InstallDeviceCount DESC) a
        UNION ALL
        SELECT * FROM (SELECT TOP 8 'sku', SkuPartNumber, ISNULL(DisplayName, SkuPartNumber), SkuPartNumber
                       FROM dim.Sku
                       WHERE DisplayName LIKE @p ESCAPE '\' OR SkuPartNumber LIKE @p ESCAPE '\'
                       ORDER BY DisplayName) s
        UNION ALL
        SELECT * FROM (SELECT TOP 8 'department', Department, Department, CAST(COUNT(*) AS nvarchar(16)) + N' people'
                       FROM dim.[User]
                       WHERE Department LIKE @p ESCAPE '\'
                       GROUP BY Department ORDER BY COUNT(*) DESC) dep;";
    try
    {
        var rows = new List<object>();
        await using var c = new SqlConnection(cs);
        await c.OpenAsync();
        await using var cmd = new SqlCommand(sql, c) { CommandTimeout = 15 };
        cmd.Parameters.AddWithValue("@p", pattern);
        await using SqlDataReader r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            rows.Add(new
            {
                kind  = r.IsDBNull(0) ? null : r.GetString(0),
                key   = r.IsDBNull(1) ? null : r.GetValue(1).ToString(),
                label = r.IsDBNull(2) ? null : r.GetValue(2).ToString(),
                meta  = r.IsDBNull(3) ? null : r.GetValue(3).ToString(),
            });
        return Results.Json(rows);
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "/api/search failed");
        return Results.Problem("Search failed.");
    }
});
app.MapGet("/api/watched-apps", () => Many(
    "SELECT Name, Vendor, LicenseModel, InstallDeviceCount, AnnualUnitCost, AnnualExposure, CostConfidence " +
    "FROM vw.AppEstate ORDER BY CASE WHEN AnnualExposure IS NULL THEN 1 ELSE 0 END, AnnualExposure DESC, InstallDeviceCount DESC"));

// ---- decision log (writes to Prism's OWN score.Decision table; never to Microsoft 365) ----
app.MapGet("/api/decisions", () => Many("SELECT UserId, SkuId, Decision, DecidedUtc, DecidedBy FROM score.Decision"));

app.MapPost("/api/decision", async (HttpContext ctx) =>
{
    if (string.IsNullOrWhiteSpace(cs)) return Results.StatusCode(503);
    string? userId = null, skuId = null, part = null, decision = null, runId = null;
    try
    {
        using var doc = await System.Text.Json.JsonDocument.ParseAsync(ctx.Request.Body);
        var root = doc.RootElement;
        userId   = root.TryGetProperty("userId", out var u) ? u.GetString() : null;
        skuId    = root.TryGetProperty("skuId", out var s) ? s.GetString() : null;
        part     = root.TryGetProperty("skuPartNumber", out var p) ? p.GetString() : null;
        decision = root.TryGetProperty("decision", out var d) ? d.GetString() : null;
        runId    = root.TryGetProperty("runId", out var r) ? r.GetString() : null;
    }
    catch { return Results.BadRequest(new { error = "invalid body" }); }
    if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(skuId) || string.IsNullOrEmpty(decision))
        return Results.BadRequest(new { error = "userId, skuId and decision are required" });
    // Controlled vocabulary - anything else is rejected rather than stored.
    if (decision is not ("reclaim" or "keep" or "snooze"))
        return Results.BadRequest(new { error = "decision must be reclaim | keep | snooze" });

    var byRaw = ctx.Request.Headers["X-MS-CLIENT-PRINCIPAL-NAME"].ToString();
    var by = string.IsNullOrEmpty(byRaw) ? "dashboard" : byRaw;

    const string sql = @"
        IF OBJECT_ID('score.Decision') IS NULL
        CREATE TABLE score.Decision(
            UserId nvarchar(64) NOT NULL, SkuId nvarchar(64) NOT NULL,
            SkuPartNumber nvarchar(128) NULL, Decision nvarchar(20) NOT NULL,
            DecidedBy nvarchar(256) NULL, DecidedUtc datetime2(3) NOT NULL CONSTRAINT DF_score_Decision_ts DEFAULT sysutcdatetime(),
            RunId nvarchar(64) NULL,
            CONSTRAINT PK_score_Decision PRIMARY KEY (UserId, SkuId));
        MERGE score.Decision AS t
        USING (SELECT @u AS UserId, @s AS SkuId) src ON t.UserId = src.UserId AND t.SkuId = src.SkuId
        WHEN MATCHED THEN UPDATE SET Decision=@d, SkuPartNumber=@p, DecidedBy=@by, DecidedUtc=sysutcdatetime(), RunId=@r
        WHEN NOT MATCHED THEN INSERT (UserId, SkuId, SkuPartNumber, Decision, DecidedBy, RunId)
            VALUES (@u, @s, @p, @d, @by, @r);";
    try
    {
        await using var c = new SqlConnection(cs);
        await c.OpenAsync();
        await using var cmd = new SqlCommand(sql, c) { CommandTimeout = 30 };
        cmd.Parameters.AddWithValue("@u", userId);
        cmd.Parameters.AddWithValue("@s", skuId);
        cmd.Parameters.AddWithValue("@p", (object?)part ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@d", decision);
        cmd.Parameters.AddWithValue("@by", by);
        cmd.Parameters.AddWithValue("@r", (object?)runId ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
        return Results.Ok(new { ok = true });
    }
    catch (Exception ex) { app.Logger.LogError(ex, "/api/decision failed"); return Results.Problem("Could not record the decision."); }
});

// ---- Wave 2: optimisation views (read-only SELECTs over the analytical views) ----
app.MapGet("/api/rightsize", () => Many(
    "SELECT UserPrincipalName, DisplayName, Department, FromSku, FromName, FromCost, ToSku, ToName, ToCost, Path, MonthlyDelta, EffectiveInactiveDays, Confidence " +
    "FROM vw.RightSize ORDER BY MonthlyDelta DESC"));
app.MapGet("/api/overlap", () => Many(
    "SELECT UserPrincipalName, DisplayName, Department, RedundantPart, RedundantName, MonthlyWaste, CoveredByPart, CoveredByName, Note " +
    "FROM vw.SkuOverlap ORDER BY CASE WHEN MonthlyWaste IS NULL THEN 1 ELSE 0 END, MonthlyWaste DESC"));
app.MapGet("/api/renewals", () => Many(
    "SELECT SkuPartNumber, SkuName, RenewalDate, Term, QuantityOwned, DaysToRenewal, SeatsAssigned, SeatsIdle, IdleMonthly, ReclaimSeats, ReclaimMonthly, RecoverableMonthly, Notes " +
    "FROM vw.RenewalExposure ORDER BY CASE WHEN DaysToRenewal IS NULL THEN 1 ELSE 0 END, DaysToRenewal"));
app.MapGet("/api/reallocation", () => Many(
    "SELECT SkuPartNumber, SkuName, IdleSeats, ReclaimableSeats, RecoverablePool, MonthlyUnitCost, PoolMonthlyValue " +
    "FROM vw.Reallocation ORDER BY PoolMonthlyValue DESC"));
// Wave 3.1: licenses held but not actually used (measured by the agent).
// NB: the route is American-spelled for the UI; the underlying SQL view keeps
// its original name (vw.LicenceUsage) - do not "fix" that reference.
app.MapGet("/api/license-usage", () => Many(
    "SELECT UserPrincipalName, DisplayName, Department, SkuName, SkuPartNumber, FgActiveHours90, ActiveDays90, LastUsedDate, UsageVerdict, ReclaimableMonthly " +
    "FROM vw.LicenceUsage WHERE UsageVerdict='Unused' ORDER BY ReclaimableMonthly DESC"));

// Item 9: governance - licences assigned DIRECTLY (not via a group). Group
// assignment is the normal model and is not surfaced as an anomaly.
app.MapGet("/api/direct-assignments", () => Many(
    "SELECT UserId, UserPrincipalName, DisplayName, Department, SkuName, SkuPartNumber, MonthlyUnitCost, Currency, State " +
    "FROM vw.DirectAssignments ORDER BY DisplayName, SkuName"));
app.MapGet("/api/direct-assignment-summary", () => Many(
    "SELECT Department, DirectAssignments, Users, MonthlyCost FROM vw.DirectAssignmentSummary ORDER BY DirectAssignments DESC"));

// ---- correlated drill-down: /api/drill?type=user|sku|vendor|department|app|device&key=... ----
async Task<List<Dictionary<string, object?>>> Q(string sql, params (string, object?)[] ps)
{
    var list = new List<Dictionary<string, object?>>();
    await using var c = new SqlConnection(cs);
    await c.OpenAsync();
    await using var cmd = new SqlCommand(sql, c) { CommandTimeout = 30 };
    foreach (var p in ps) cmd.Parameters.AddWithValue(p.Item1, (object?)p.Item2 ?? DBNull.Value);
    await using var r = await cmd.ExecuteReaderAsync();
    while (await r.ReadAsync())
    {
        var d = new Dictionary<string, object?>();
        for (int i = 0; i < r.FieldCount; i++) d[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i);
        list.Add(d);
    }
    return list;
}
// Like Q, but never throws: used for the agent app-usage views, which may not
// exist yet (wave3-usage.sql not run) or be empty (no agent data) - a missing
// view must degrade a section gracefully, not 500 the whole drill.
async Task<List<Dictionary<string, object?>>> Qsafe(string sql, params (string, object?)[] ps)
{
    try { return await Q(sql, ps); } catch { return new List<Dictionary<string, object?>>(); }
}
object Fact(string label, object? value, string? lt = null, string? lk = null) => new { kind = "fact", label, value = value?.ToString() ?? "-", linkType = lt, linkKey = lk };
object TableSection(string heading, string[] cols, IEnumerable<object> rows) => new { heading, kind = "table", cols, rows };
object Row(object?[] cells, string? lt = null, string? lk = null) => new { cells = cells.Select(x => x?.ToString() ?? "-").ToArray(), linkType = lt, linkKey = lk };

app.MapGet("/api/drill", async (string type, string key) =>
{
    if (string.IsNullOrWhiteSpace(cs)) return Results.StatusCode(503);
    if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(key)) return Results.BadRequest();
    try
    {
        var sections = new List<object>();
        string title = key, subtitle = "";
        if (type == "user")
        {
            var u = (await Q("SELECT TOP 1 * FROM dim.[User] WHERE UserId=@k OR UserPrincipalName=@k OR DisplayName=@k", ("@k", key))).FirstOrDefault();
            if (u != null)
            {
                title = u.GetValueOrDefault("DisplayName")?.ToString() ?? key;
                subtitle = u.GetValueOrDefault("UserPrincipalName")?.ToString() ?? "";
                key = u.GetValueOrDefault("UserId")?.ToString() ?? key;
                sections.Add(new { heading = "Profile", kind = "facts", rows = new[] {
                    Fact("Email", u.GetValueOrDefault("UserPrincipalName")),
                    Fact("Department", u.GetValueOrDefault("Department"), u.GetValueOrDefault("Department")==null?null:"department", u.GetValueOrDefault("Department")?.ToString()),
                    Fact("Job title", u.GetValueOrDefault("JobTitle")),
                    Fact("Account enabled", (u.GetValueOrDefault("AccountEnabled") as bool?)==true?"Yes":"No"),
                    Fact("Country", u.GetValueOrDefault("UsageLocation")),
                    Fact("Last sign-in", u.GetValueOrDefault("LastSignInDateTime")),
                    Fact("Hire date", u.GetValueOrDefault("EmployeeHireDate")) } });
            }
            var lic = await Q("SELECT SkuName, SkuPartNumber, Verdict, EffectiveInactiveDays, EstMonthlySavings FROM score.AssignmentVerdict WHERE UserId=@k ORDER BY CASE Verdict WHEN 'RECLAIM' THEN 0 WHEN 'REVIEW' THEN 1 ELSE 2 END", ("@k", key));
            sections.Add(TableSection("Licenses (" + lic.Count + ")", new[] { "License", "Status", "Idle days", "€/mo" },
                lic.Select(x => Row(new object?[] { x["SkuName"], x["Verdict"], x["EffectiveInactiveDays"], x["EstMonthlySavings"] }, "sku", x["SkuPartNumber"]?.ToString()))));
            var dev = await Q("SELECT DeviceName, OperatingSystem, OsVersion, ComplianceState, LastSyncDateTime FROM dim.Device WHERE UserId=@k ORDER BY LastSyncDateTime DESC", ("@k", key));
            sections.Add(TableSection("Devices (" + dev.Count + ")", new[] { "Device", "OS", "Version", "Compliance", "Last sync" },
                dev.Select(x => Row(new object?[] { x["DeviceName"], x["OperatingSystem"], x["OsVersion"], x["ComplianceState"], x["LastSyncDateTime"] }, "device", x["DeviceName"]?.ToString()))));
            if (!string.IsNullOrEmpty(subtitle))
            {
                var ua = await Qsafe("SELECT AppName, FgActiveHours90, ActiveDays90, UsageState FROM vw.AppUsageCorrelated WHERE UserPrincipalName=@u ORDER BY FgActiveSeconds90 DESC", ("@u", subtitle));
                if (ua.Count > 0)
                    sections.Add(TableSection("Applications used (" + ua.Count + ", agent 90d)", new[] { "Application", "Hrs 90d", "Days", "State" },
                        ua.Select(x => Row(new object?[] { x["AppName"], x["FgActiveHours90"], x["ActiveDays90"], x["UsageState"] }, "app", x["AppName"]?.ToString()))));
            }
        }
        else if (type == "sku")
        {
            // Accept either the SkuPartNumber or the SkuName as the key (the UI links by either),
            // resolving to the part number so every sub-query below keys off it.
            key = (await Qsafe("SELECT TOP 1 SkuPartNumber FROM score.SkuVerdict WHERE SkuPartNumber=@k OR SkuName=@k", ("@k", key)))
                  .FirstOrDefault()?.GetValueOrDefault("SkuPartNumber")?.ToString() ?? key;
            // Curated one-paragraph description (ref.SkuDescription, maintained offline).
            var desc = (await Qsafe("SELECT Description FROM ref.SkuDescription WHERE SkuPartNumber=@k", ("@k", key))).FirstOrDefault();
            if (desc?.GetValueOrDefault("Description") is { } d0 && d0.ToString() is { Length: > 0 } dTxt)
                sections.Add(new { heading = "What this license grants", kind = "note", text = dTxt });

            var sv = (await Q("SELECT sv.*, c.MonthlyUnitCost, c.Currency AS CostCurrency FROM score.SkuVerdict sv LEFT JOIN ref.SkuCost c ON c.SkuPartNumber=sv.SkuPartNumber WHERE sv.SkuPartNumber=@k", ("@k", key))).FirstOrDefault();
            if (sv != null)
            {
                title = sv.GetValueOrDefault("SkuName")?.ToString() ?? key;
                subtitle = key;
                decimal unit = sv.GetValueOrDefault("MonthlyUnitCost") is { } mu && decimal.TryParse(mu.ToString(), out var muv) ? muv : 0m;
                int idle = sv.GetValueOrDefault("SeatsIdle") is { } si && int.TryParse(si.ToString(), out var siv) ? siv : 0;
                int owned = sv.GetValueOrDefault("SeatsOwned") is { } so && int.TryParse(so.ToString(), out var sov) ? sov : 0;
                int assigned = sv.GetValueOrDefault("SeatsAssigned") is { } sa && int.TryParse(sa.ToString(), out var sav) ? sav : 0;
                sections.Add(new { heading = "Utilisation", kind = "facts", rows = new[] {
                    Fact("Seats owned", owned),
                    Fact("Assigned", assigned),
                    Fact("Idle (open)", idle),
                    Fact("Utilisation", owned > 0 ? $"{Math.Round(100.0 * assigned / owned)}%" : "-") } });
                // NB: score.SkuVerdict is the IDLE-SEAT recommendation, not a judgment
                // of the license itself - phrase it that way.
                string seatAction = sv.GetValueOrDefault("Verdict")?.ToString() switch
                {
                    "RECLAIM" => "Drop the idle seats at renewal (no user impact)",
                    "REVIEW"  => "Review idle seats at renewal (within buffer)",
                    _         => "None - no idle seats"
                };
                sections.Add(new { heading = "Cost & savings", kind = "facts", rows = new[] {
                    Fact("Unit / mo", unit > 0 ? unit.ToString("0.##") : "unpriced"),
                    Fact("Idle cost / mo", unit > 0 ? (idle * unit).ToString("0.##") : "n/a"),
                    Fact("Annual if idle reclaimed", unit > 0 ? (idle * unit * 12).ToString("0.##") : "n/a"),
                    Fact("Idle-seat action", seatAction) } });
            }
            var us = await Q("SELECT UserId, DisplayName, UserPrincipalName, Department, Verdict, EffectiveInactiveDays, EstMonthlySavings FROM score.AssignmentVerdict WHERE SkuPartNumber=@k ORDER BY CASE Verdict WHEN 'RECLAIM' THEN 0 WHEN 'REVIEW' THEN 1 ELSE 2 END, EffectiveInactiveDays DESC", ("@k", key));
            sections.Add(TableSection("Assigned users (" + us.Count + ")", new[] { "User", "Department", "Status", "Idle days", "€/mo at stake" },
                us.Select(x => Row(new object?[] { x["DisplayName"], x["Department"], x["Verdict"], x["EffectiveInactiveDays"], x["EstMonthlySavings"] }, "user", x["UserId"]?.ToString()))));
            // departments + countries holding this license (cross-reference rollups)
            var skd = await Q(@"SELECT Department, COUNT(*) AS Holders,
                       SUM(CASE WHEN Verdict<>'KEEP' THEN ISNULL(EstMonthlySavings,0) ELSE 0 END) AS FlaggedEur
                FROM score.AssignmentVerdict WHERE SkuPartNumber=@k GROUP BY Department ORDER BY COUNT(*) DESC", ("@k", key));
            sections.Add(TableSection("Departments (" + skd.Count + ")", new[] { "Department", "Holders", "Flagged €/mo" },
                skd.Select(x => Row(new object?[] { x["Department"], x["Holders"], x["FlaggedEur"] }, "department", x["Department"]?.ToString()))));
            var skc = await Q(@"SELECT Country, COUNT(*) AS Holders,
                       SUM(CASE WHEN Verdict<>'KEEP' THEN ISNULL(EstMonthlySavings,0) ELSE 0 END) AS FlaggedEur
                FROM score.AssignmentVerdict WHERE SkuPartNumber=@k AND Country IS NOT NULL GROUP BY Country ORDER BY COUNT(*) DESC", ("@k", key));
            sections.Add(TableSection("Countries (" + skc.Count + ")", new[] { "Country", "Holders", "Flagged €/mo" },
                skc.Select(x => Row(new object?[] { x["Country"], x["Holders"], x["FlaggedEur"] }, "region", x["Country"]?.ToString()))));
        }
        else if (type == "region")
        {
            subtitle = "Region / usage location";
            var summary = (await Q(@"SELECT COUNT(DISTINCT UserId) AS Users,
                       SUM(CASE WHEN Verdict<>'KEEP' THEN 1 ELSE 0 END) AS Flagged,
                       SUM(CASE WHEN Verdict<>'KEEP' THEN ISNULL(EstMonthlySavings,0) ELSE 0 END) AS FlaggedEur
                FROM score.AssignmentVerdict WHERE Country=@k", ("@k", key))).FirstOrDefault();
            if (summary != null)
                sections.Add(new { heading = "Summary", kind = "facts", rows = new[] {
                    Fact("Licensed users", summary.GetValueOrDefault("Users")),
                    Fact("Flagged assignments", summary.GetValueOrDefault("Flagged")),
                    Fact("Recoverable / mo", summary.GetValueOrDefault("FlaggedEur")) } });
            var lic = await Q(@"SELECT SkuName, SkuPartNumber, COUNT(*) AS Holders,
                       SUM(CASE WHEN Verdict<>'KEEP' THEN 1 ELSE 0 END) AS Flagged,
                       SUM(CASE WHEN Verdict<>'KEEP' THEN ISNULL(EstMonthlySavings,0) ELSE 0 END) AS FlaggedEur
                FROM score.AssignmentVerdict WHERE Country=@k
                GROUP BY SkuName, SkuPartNumber ORDER BY COUNT(*) DESC", ("@k", key));
            sections.Add(TableSection("Licenses (" + lic.Count + ")", new[] { "License", "Holders", "Flagged", "Flagged €/mo" },
                lic.Select(x => Row(new object?[] { x["SkuName"], x["Holders"], x["Flagged"], x["FlaggedEur"] }, "sku", x["SkuPartNumber"]?.ToString()))));
            var us = await Q(@"SELECT TOP 200 u.UserId, u.DisplayName, u.Department, u.JobTitle, u.LastSignInDateTime
                FROM dim.[User] u WHERE u.UsageLocation=@k ORDER BY u.DisplayName", ("@k", key));
            sections.Add(TableSection("People (" + us.Count + (us.Count == 200 ? "+" : "") + ")", new[] { "User", "Department", "Job title", "Last sign-in" },
                us.Select(x => Row(new object?[] { x["DisplayName"], x["Department"], x["JobTitle"], x["LastSignInDateTime"] }, "user", x["UserId"]?.ToString()))));
            var dev = await Q(@"SELECT TOP 200 d.DeviceName, d.OperatingSystem, d.ComplianceState, u.DisplayName AS Owner
                FROM dim.Device d JOIN dim.[User] u ON u.UserId = d.UserId
                WHERE u.UsageLocation=@k ORDER BY d.DeviceName", ("@k", key));
            sections.Add(TableSection("Devices (" + dev.Count + (dev.Count == 200 ? "+" : "") + ")", new[] { "Device", "OS", "Compliance", "Owner" },
                dev.Select(x => Row(new object?[] { x["DeviceName"], x["OperatingSystem"], x["ComplianceState"], x["Owner"] }, "device", x["DeviceName"]?.ToString()))));
            var rdep = await Q(@"SELECT Department, COUNT(*) AS Holders,
                       SUM(CASE WHEN Verdict<>'KEEP' THEN ISNULL(EstMonthlySavings,0) ELSE 0 END) AS FlaggedEur
                FROM score.AssignmentVerdict WHERE Country=@k GROUP BY Department ORDER BY COUNT(*) DESC", ("@k", key));
            sections.Add(TableSection("Departments (" + rdep.Count + ")", new[] { "Department", "Holders", "Flagged €/mo" },
                rdep.Select(x => Row(new object?[] { x["Department"], x["Holders"], x["FlaggedEur"] }, "department", x["Department"]?.ToString()))));
            var rsw = await Qsafe(@"SELECT TOP 15 uc.AppName, COUNT(DISTINCT uc.MachineName) AS Devices
                FROM vw.AppUsageCorrelated uc JOIN dim.[User] u ON u.UserPrincipalName = uc.UserPrincipalName
                WHERE u.UsageLocation=@k GROUP BY uc.AppName ORDER BY COUNT(DISTINCT uc.MachineName) DESC", ("@k", key));
            if (rsw.Count > 0)
                sections.Add(TableSection("Top software used (agent 90d)", new[] { "Application", "Devices" },
                    rsw.Select(x => Row(new object?[] { x["AppName"], x["Devices"] }, "app", x["AppName"]?.ToString()))));
        }
        else if (type == "vendor")
        {
            subtitle = "Applications by this vendor";
            var ap = await Q("SELECT Name, InstallDeviceCount, AnnualExposure FROM vw.AppEstate WHERE Vendor=@k ORDER BY InstallDeviceCount DESC", ("@k", key));
            sections.Add(TableSection("Applications (" + ap.Count + ")", new[] { "Application", "Installs", "Annual exposure" },
                ap.Select(x => Row(new object?[] { x["Name"], x["InstallDeviceCount"], x["AnnualExposure"] }, "app", x["Name"]?.ToString()))));
        }
        else if (type == "department")
        {
            subtitle = "Department";
            // "Unattributed" is the synthetic label for users with no department on
            // record (Department IS NULL/''), so it must not be matched literally.
            bool unattr = string.Equals(key, "Unattributed", StringComparison.OrdinalIgnoreCase);
            string dep  = unattr ? "(Department IS NULL OR Department=N'')"     : "Department=@k";
            string depU = unattr ? "(u.Department IS NULL OR u.Department=N'')" : "u.Department=@k";
            var us = await Q($"SELECT UserId, DisplayName, JobTitle, LastSignInDateTime FROM dim.[User] WHERE {dep} ORDER BY DisplayName", ("@k", key));
            sections.Add(TableSection("People (" + us.Count + ")", new[] { "User", "Job title", "Last sign-in" },
                us.Select(x => Row(new object?[] { x["DisplayName"], x["JobTitle"], x["LastSignInDateTime"] }, "user", x["UserId"]?.ToString()))));
            var lic = await Q($"SELECT SkuName, SkuPartNumber, COUNT(*) AS Holders, SUM(CASE WHEN Verdict<>'KEEP' THEN EstMonthlySavings ELSE 0 END) AS Flagged FROM score.AssignmentVerdict WHERE {dep} GROUP BY SkuName, SkuPartNumber ORDER BY COUNT(*) DESC", ("@k", key));
            sections.Add(TableSection("Licenses (" + lic.Count + ")", new[] { "License", "Holders", "Flagged €/mo" },
                lic.Select(x => Row(new object?[] { x["SkuName"], x["Holders"], x["Flagged"] }, "sku", x["SkuPartNumber"]?.ToString()))));
            var dev = await Q($"SELECT d.DeviceName, d.OperatingSystem, d.ComplianceState, u.DisplayName AS Owner FROM dim.Device d JOIN dim.[User] u ON u.UserId=d.UserId WHERE {depU} ORDER BY d.DeviceName", ("@k", key));
            sections.Add(TableSection("Devices (" + dev.Count + ")", new[] { "Device", "OS", "Compliance", "Owner" },
                dev.Select(x => Row(new object?[] { x["DeviceName"], x["OperatingSystem"], x["ComplianceState"], x["Owner"] }, "device", x["DeviceName"]?.ToString()))));
            var da = await Qsafe($"SELECT TOP 15 AppName, COUNT(DISTINCT MachineName) AS Devices, CAST(SUM(FgActiveSeconds90)/3600.0 AS decimal(14,1)) AS Hrs FROM vw.AppUsageCorrelated WHERE {dep} GROUP BY AppName ORDER BY SUM(FgActiveSeconds90) DESC", ("@k", key));
            if (da.Count > 0)
                sections.Add(TableSection("Top applications used (agent 90d)", new[] { "Application", "Devices", "Hrs 90d" },
                    da.Select(x => Row(new object?[] { x["AppName"], x["Devices"], x["Hrs"] }, "app", x["AppName"]?.ToString()))));
            var dcty = await Q($"SELECT Country, COUNT(*) AS Seats, SUM(CASE WHEN Verdict<>'KEEP' THEN ISNULL(EstMonthlySavings,0) ELSE 0 END) AS FlaggedEur FROM score.AssignmentVerdict WHERE {dep} AND Country IS NOT NULL GROUP BY Country ORDER BY COUNT(*) DESC", ("@k", key));
            if (dcty.Count > 0)
                sections.Add(TableSection("Countries (" + dcty.Count + ")", new[] { "Country", "Seats", "Flagged €/mo" },
                    dcty.Select(x => Row(new object?[] { x["Country"], x["Seats"], x["FlaggedEur"] }, "region", x["Country"]?.ToString()))));
        }
        else if (type == "app")
        {
            var a = (await Q("SELECT TOP 1 * FROM vw.AppEstate WHERE Name=@k", ("@k", key))).FirstOrDefault();
            if (a != null)
            {
                title = key; subtitle = a.GetValueOrDefault("Vendor")?.ToString() ?? "";
                sections.Add(new { heading = "Application", kind = "facts", rows = new[] {
                    Fact("Vendor", a.GetValueOrDefault("Vendor"), "vendor", a.GetValueOrDefault("Vendor")?.ToString()),
                    Fact("License model", a.GetValueOrDefault("LicenseModel")),
                    Fact("Installs (devices)", a.GetValueOrDefault("InstallDeviceCount")),
                    Fact("Unit / yr", a.GetValueOrDefault("AnnualUnitCost")),
                    Fact("Annual exposure", a.GetValueOrDefault("AnnualExposure")),
                    Fact("Price confidence", a.GetValueOrDefault("CostConfidence")) } });
            }
            var ver = await Q("SELECT [Version], Platform, DeviceCount FROM fact.DetectedApp WHERE DisplayName=@k ORDER BY DeviceCount DESC", ("@k", key));
            sections.Add(TableSection("Versions (" + ver.Count + ")", new[] { "Version", "Platform", "Devices" },
                ver.Select(x => Row(new object?[] { x["Version"], x["Platform"], x["DeviceCount"] }))));
            var byApp = (await Qsafe("SELECT TOP 1 * FROM vw.AppUsageByApp WHERE AppName=@k", ("@k", key))).FirstOrDefault();
            if (byApp != null)
                sections.Add(new { heading = "Measured usage (agent, last 90d)", kind = "facts", rows = new[] {
                    Fact("Devices using", byApp.GetValueOrDefault("Devices")),
                    Fact("Users using", byApp.GetValueOrDefault("Users")),
                    Fact("Foreground hours", byApp.GetValueOrDefault("FgActiveHours90")),
                    Fact("Used installs", byApp.GetValueOrDefault("UsedInstalls")),
                    Fact("Open-only / dormant", (byApp.GetValueOrDefault("OpenOnlyInstalls")?.ToString() ?? "0") + " / " + (byApp.GetValueOrDefault("DormantInstalls")?.ToString() ?? "0")),
                    Fact("Last used", byApp.GetValueOrDefault("LastUsedDate")) } });
            // Match the exe's friendly name OR its version-resource product name -
            // Intune's inventory name and the binary's product name often differ.
            var uc = await Qsafe("SELECT UserDisplayName, UserPrincipalName, MachineName, Department, FgActiveHours90, ActiveDays90, UsageState FROM vw.AppUsageCorrelated WHERE AppName=@k OR ProductName=@k ORDER BY FgActiveSeconds90 DESC", ("@k", key));
            if (uc.Count > 0)
                sections.Add(TableSection("Where it's used (" + uc.Count + ")", new[] { "User", "Device", "Dept", "Hrs 90d", "Days", "State" },
                    uc.Select(x => Row(new object?[] { x["UserDisplayName"] ?? x["UserPrincipalName"], x["MachineName"], x["Department"], x["FgActiveHours90"], x["ActiveDays90"], x["UsageState"] }, "user", x["UserPrincipalName"]?.ToString()))));
            // Item 4: exactly which devices have it installed (Intune), each joined with
            // the Prism agent's measured usage ON THAT DEVICE (- = device not reporting).
            var inst = await Qsafe(@"SELECT TOP 2000 ai.DeviceName, ai.UserDisplayName, ai.UserPrincipalName, ai.Department,
                       ai.OperatingSystem, ai.ComplianceState, ai.AppVersion,
                       u.FgActiveHours90, u.UsageState, CONVERT(varchar(10), u.LastUsedDate, 23) AS LastUsedDate
                FROM vw.AppInstall ai
                OUTER APPLY (
                    SELECT CAST(SUM(uc.FgActiveSeconds90) / 3600.0 AS decimal(12,1)) AS FgActiveHours90,
                           MAX(uc.LastUsedDate) AS LastUsedDate,
                           CASE MIN(CASE uc.UsageState WHEN 'Active' THEN 1 WHEN 'Light' THEN 2 WHEN 'Open-only' THEN 3 WHEN 'Dormant' THEN 4 END)
                                WHEN 1 THEN 'Active' WHEN 2 THEN 'Light' WHEN 3 THEN 'Open-only' WHEN 4 THEN 'Dormant' END AS UsageState
                    FROM vw.AppUsageCorrelated uc
                    WHERE uc.MachineName = ai.DeviceName
                      AND (uc.AppName = ai.DisplayName OR uc.ProductName = ai.DisplayName)
                ) u
                WHERE ai.DisplayName = @k
                ORDER BY CASE WHEN u.FgActiveHours90 IS NULL THEN 1 ELSE 0 END, u.FgActiveHours90 DESC, ai.DeviceName", ("@k", key));
            // If the agent views aren't deployed yet (wave3-usage.sql), the joined query
            // throws inside Qsafe; fall back to the plain install list (usage columns NULL).
            if (inst.Count == 0)
                inst = await Qsafe(@"SELECT TOP 2000 DeviceName, UserDisplayName, UserPrincipalName, Department,
                           OperatingSystem, ComplianceState, AppVersion,
                           CAST(NULL AS decimal(12,1)) AS FgActiveHours90,
                           CAST(NULL AS varchar(16))   AS UsageState,
                           CAST(NULL AS varchar(10))   AS LastUsedDate
                    FROM vw.AppInstall WHERE DisplayName=@k ORDER BY DeviceName", ("@k", key));
            if (inst.Count > 0)
            {
                // Total from the estate aggregate so a truncated list is visible as "2000 of 5614".
                string totalSuffix = a?.GetValueOrDefault("InstallDeviceCount")?.ToString() is { } tot && tot != inst.Count.ToString()
                    ? inst.Count + " of " + tot : inst.Count.ToString();
                sections.Add(TableSection("Installed on devices (" + totalSuffix + ") - click a version above to filter; usage from the Prism agent",
                    new[] { "Device", "Version", "OS", "User", "Hrs 90d", "State", "Last used" },
                    inst.Select(x => Row(new object?[] { x["DeviceName"], x["AppVersion"], x["OperatingSystem"], x["UserDisplayName"] ?? x["UserPrincipalName"], x["FgActiveHours90"], x["UsageState"], x["LastUsedDate"] }, "device", x["DeviceName"]?.ToString()))));
                var iu = inst.Where(x => x.GetValueOrDefault("UserPrincipalName") != null)
                             .GroupBy(x => x["UserPrincipalName"]!.ToString())
                             .Select(g => g.First()).ToList();
                if (iu.Count > 0)
                    sections.Add(TableSection("Installed for users (" + iu.Count + ")", new[] { "User", "Department" },
                        iu.Select(x => Row(new object?[] { x["UserDisplayName"] ?? x["UserPrincipalName"], x["Department"] }, "user", x["UserPrincipalName"]?.ToString()))));
            }
            else
                sections.Add(new { heading = "Installed on devices", kind = "note", text = "No per-device install rows from Intune for this application yet. The connectors job expands the device list for the whole inventory within a per-run budget (Prism:ExpandAllInstalls / Prism:MaxInstallExpansions) - this fills in after the next prism-connectors run. Any agent-measured usage still shows under “Where it's used” above." });
        }
        else if (type == "device")
        {
            var d = (await Q("SELECT TOP 1 * FROM dim.Device WHERE DeviceName=@k OR DeviceId=@k", ("@k", key))).FirstOrDefault();
            if (d != null)
            {
                title = d.GetValueOrDefault("DeviceName")?.ToString() ?? key;
                subtitle = d.GetValueOrDefault("OperatingSystem")?.ToString() ?? "Device";
                key = d.GetValueOrDefault("DeviceName")?.ToString() ?? key;
                sections.Add(new { heading = "Device", kind = "facts", rows = new[] {
                    Fact("Primary user", d.GetValueOrDefault("UserPrincipalName"), d.GetValueOrDefault("UserId")==null?null:"user", d.GetValueOrDefault("UserId")?.ToString()),
                    Fact("OS", d.GetValueOrDefault("OperatingSystem")),
                    Fact("OS version", d.GetValueOrDefault("OsVersion")),
                    Fact("Compliance", d.GetValueOrDefault("ComplianceState")),
                    Fact("Manufacturer", d.GetValueOrDefault("Manufacturer")),
                    Fact("Model", d.GetValueOrDefault("Model")),
                    Fact("Last sync", d.GetValueOrDefault("LastSyncDateTime")) } });
            }
            // owner department + country (joined from the device's primary user) — both drillable
            if (d?.GetValueOrDefault("UserId")?.ToString() is { Length: > 0 } devUser)
            {
                var ou = (await Qsafe("SELECT Department, UsageLocation FROM dim.[User] WHERE UserId=@k", ("@k", devUser))).FirstOrDefault();
                if (ou != null)
                    sections.Add(new { heading = "Owner", kind = "facts", rows = new[] {
                        Fact("Department", ou.GetValueOrDefault("Department"), ou.GetValueOrDefault("Department")==null?null:"department", ou.GetValueOrDefault("Department")?.ToString()),
                        Fact("Country", ou.GetValueOrDefault("UsageLocation"), ou.GetValueOrDefault("UsageLocation")==null?null:"region", ou.GetValueOrDefault("UsageLocation")?.ToString()) } });
            }
            var ap = await Qsafe("SELECT AppName, FgActiveHours90, ActiveDays90, Launches, UsageState FROM vw.AppUsageCorrelated WHERE MachineName=@k ORDER BY FgActiveSeconds90 DESC", ("@k", key));
            if (ap.Count > 0)
                sections.Add(TableSection("Applications on this device (" + ap.Count + ", agent 90d)", new[] { "Application", "Hrs 90d", "Days", "Launches", "State" },
                    ap.Select(x => Row(new object?[] { x["AppName"], x["FgActiveHours90"], x["ActiveDays90"], x["Launches"], x["UsageState"] }, "app", x["AppName"]?.ToString()))));
            else
                sections.Add(new { heading = "Applications (agent)", kind = "note", text = "No agent-measured usage for this device yet - it reports once the Prism agent is running on it." });
            // installed software on this device (Intune inventory) — each app drillable
            var dsw = await Qsafe("SELECT DisplayName, AppVersion FROM vw.AppInstall WHERE DeviceName=@k ORDER BY DisplayName", ("@k", key));
            if (dsw.Count > 0)
                sections.Add(TableSection("Installed software (Intune, " + dsw.Count + ")", new[] { "Application", "Version" },
                    dsw.Select(x => Row(new object?[] { x["DisplayName"], x["AppVersion"] }, "app", x["DisplayName"]?.ToString()))));
        }
        else return Results.BadRequest();
        return Results.Json(new { title, subtitle, sections });
    }
    catch (Exception ex) { app.Logger.LogError(ex, "/api/drill failed for {Type}", type); return Results.Problem("Drill query failed."); }
});

app.Run();

// ---- helpers ----
// Error hygiene: SQL exception text (object names, column names, server paths) is an
// internals leak on an anonymous endpoint - log it server-side, return a generic 500.
// The UI only branches on response.ok, so behaviour is unchanged.
async Task<IResult> Many(string sql)
{
    if (string.IsNullOrWhiteSpace(cs)) return Results.StatusCode(503);
    try { return Results.Json(await ReadAsync(cs!, sql)); }
    catch (Exception ex) { app.Logger.LogError(ex, "API query failed: {Sql}", sql); return Results.Problem("A database query failed."); }
}
async Task<IResult> Single(string sql)
{
    if (string.IsNullOrWhiteSpace(cs)) return Results.StatusCode(503);
    try { var l = await ReadAsync(cs!, sql); return Results.Json(l.Count > 0 ? l[0] : new Dictionary<string, object?>()); }
    catch (Exception ex) { app.Logger.LogError(ex, "API query failed: {Sql}", sql); return Results.Problem("A database query failed."); }
}
static async Task<List<Dictionary<string, object?>>> ReadAsync(string cs, string sql)
{
    var rows = new List<Dictionary<string, object?>>();
    await using var c = new SqlConnection(cs);
    await c.OpenAsync();
    await using var cmd = new SqlCommand(sql, c) { CommandTimeout = 60 };
    await using SqlDataReader r = await cmd.ExecuteReaderAsync();
    while (await r.ReadAsync())
    {
        var row = new Dictionary<string, object?>(r.FieldCount);
        for (int i = 0; i < r.FieldCount; i++)
            row[Camel(r.GetName(i))] = r.IsDBNull(i) ? null : Norm(r.GetValue(i));
        rows.Add(row);
    }
    return rows;
}
static string Camel(string s) => string.IsNullOrEmpty(s) ? s : char.ToLowerInvariant(s[0]) + s[1..];

// Every warehouse datetime is UTC by convention (sysutcdatetime() defaults; the jobs write
// DateTime.UtcNow), but SqlDataReader returns Kind=Unspecified, which System.Text.Json
// serializes WITHOUT the trailing 'Z'. Browsers parse a no-offset ISO string as LOCAL time,
// so every timestamp shifted by the viewer's UTC offset (e.g. "Scored on 14:47 CEST" for a
// run that happened at 14:47 UTC = 16:47 CEST). Stamping Kind=Utc makes the JSON carry 'Z',
// and new Date(...) in the UI then converts to the viewer's zone correctly. Date-only columns
// also gain 'Z' but the UI string-slices those (and midnight UTC is the same calendar day
// anywhere at/east of UTC), so they render unchanged.
static object Norm(object v) =>
    v is DateTime { Kind: DateTimeKind.Unspecified } dt ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : v;
