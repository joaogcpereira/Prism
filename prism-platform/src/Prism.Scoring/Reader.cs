// ============================================================
//  Reader.cs  (Prism.Scoring)
//  Reads the warehouse views the engine needs. Connects as the
//  managed identity (Active Directory Default) - no secret.
// ============================================================
using Microsoft.Data.SqlClient;

namespace Prism.Scoring;

public sealed class Reader(string connectionString)
{
    private readonly string _cs = connectionString;

    public async Task<List<SignalRow>> ReadSignalsAsync(CancellationToken ct)
    {
        const string sql = @"SELECT UserId, UserPrincipalName, DisplayName, AccountEnabled, Department,
            EmployeeHireDate, CreatedDateTime, EmployeeLeaveDateTime, LastSignInDateTime,
            LastNonInteractiveSignInDateTime, SkuId, SkuPartNumber, SkuName, AssignedDirectly,
            AssignmentState, AssignmentLastUpdatedDateTime, M365ActivityConcealed, M365LastActivityDate, Country,
            TeamsLastActivityDate, ExchangeLastActivityDate, OneDriveLastActivityDate,
            SharePointLastActivityDate, M365ReportRefreshDate,
            LastSuccessfulSignInDateTime, DisabledPlanCount,
            JobTitle, UserType, OnPremisesSyncEnabled, MailboxPurpose, MailboxAutoReply,
            IsMfaRegistered, AuthMethodsUpdatedDateTime
            FROM vw.LicenseSignals";
        var rows = new List<SignalRow>();
        await using SqlConnection c = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, c) { CommandTimeout = 300 };
        await using SqlDataReader rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            rows.Add(new SignalRow
            {
                UserId = Str(rd, 0) ?? "",
                UserPrincipalName = Str(rd, 1),
                DisplayName = Str(rd, 2),
                AccountEnabled = Bool(rd, 3),
                Department = Str(rd, 4),
                EmployeeHireDate = Dt(rd, 5),
                CreatedDateTime = Dt(rd, 6),
                EmployeeLeaveDateTime = Dt(rd, 7),
                LastSignInDateTime = Dt(rd, 8),
                LastNonInteractiveSignInDateTime = Dt(rd, 9),
                SkuId = Str(rd, 10) ?? "",
                SkuPartNumber = Str(rd, 11),
                SkuName = Str(rd, 12),
                AssignedDirectly = Bool(rd, 13),
                AssignmentState = Str(rd, 14),
                AssignmentLastUpdatedDateTime = Dt(rd, 15),
                M365ActivityConcealed = Bool(rd, 16),
                M365LastActivityDate = Dt(rd, 17),
                Country = Str(rd, 18),
                TeamsLastActivityDate = Dt(rd, 19),
                ExchangeLastActivityDate = Dt(rd, 20),
                OneDriveLastActivityDate = Dt(rd, 21),
                SharePointLastActivityDate = Dt(rd, 22),
                M365ReportRefreshDate = Dt(rd, 23),
                LastSuccessfulSignInDateTime = Dt(rd, 24),
                DisabledPlanCount = rd.IsDBNull(25) ? 0 : Convert.ToInt32(rd.GetValue(25)),
                JobTitle = Str(rd, 26),
                UserType = Str(rd, 27),
                OnPremisesSyncEnabled = Bool(rd, 28),
                MailboxPurpose = Str(rd, 29),
                MailboxAutoReply = Str(rd, 30),
                IsMfaRegistered = Bool(rd, 31),
                AuthMethodsUpdatedDateTime = Dt(rd, 32),
            });
        }
        return rows;
    }

    public async Task<Dictionary<string, List<AppRow>>> ReadAppUsageAsync(CancellationToken ct)
    {
        const string sql = "SELECT UserId, ExePath, FgActiveSeconds, ActiveDays, LastDay, CoverageDays FROM vw.AppUsageByUser90";
        var map = new Dictionary<string, List<AppRow>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await using SqlConnection c = await OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, c) { CommandTimeout = 300 };
            await using SqlDataReader rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                string? uid = Str(rd, 0);
                if (string.IsNullOrEmpty(uid)) continue;
                var row = new AppRow
                {
                    UserId = uid,
                    ExePath = Str(rd, 1) ?? "",
                    FgActiveSeconds = rd.IsDBNull(2) ? 0 : Convert.ToInt64(rd.GetValue(2)),
                    ActiveDays = rd.IsDBNull(3) ? 0 : Convert.ToInt32(rd.GetValue(3)),
                    LastDay = Dt(rd, 4),
                    CoverageDays = rd.IsDBNull(5) ? 0 : Convert.ToInt32(rd.GetValue(5)),
                };
                if (!map.TryGetValue(uid, out List<AppRow>? list)) map[uid] = list = [];
                list.Add(row);
            }
        }
        catch (SqlException)
        {
            // vw.AppUsageByUser90 not present yet (wave3-scoring.sql not run) - app
            // corroboration is optional, so proceed without it rather than failing the run.
        }
        return map;
    }

    public async Task<List<SkuRow>> ReadSkuUtilizationAsync(CancellationToken ct)
    {
        const string sql = "SELECT SkuId, SkuPartNumber, DisplayName, SeatsOwned, SeatsAssigned, SeatsIdle FROM vw.SkuUtilization";
        var rows = new List<SkuRow>();
        await using SqlConnection c = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, c) { CommandTimeout = 300 };
        await using SqlDataReader rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            rows.Add(new SkuRow
            {
                SkuId = Str(rd, 0) ?? "",
                SkuPartNumber = Str(rd, 1),
                SkuName = Str(rd, 2),
                SeatsOwned = rd.IsDBNull(3) ? 0 : Convert.ToInt32(rd.GetValue(3)),
                SeatsAssigned = rd.IsDBNull(4) ? 0 : Convert.ToInt32(rd.GetValue(4)),
                SeatsIdle = rd.IsDBNull(5) ? 0 : Convert.ToInt32(rd.GetValue(5)),
            });
        }
        return rows;
    }

    public async Task<Dictionary<string, (decimal Cost, string Currency)>> ReadCostsAsync(CancellationToken ct)
    {
        const string sql = "SELECT SkuPartNumber, MonthlyUnitCost, Currency FROM ref.SkuCost WHERE MonthlyUnitCost IS NOT NULL";
        var map = new Dictionary<string, (decimal, string)>(StringComparer.OrdinalIgnoreCase);
        await using SqlConnection c = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, c) { CommandTimeout = 120 };
        await using SqlDataReader rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            string? part = Str(rd, 0);
            if (string.IsNullOrEmpty(part)) continue;
            map[part] = (Convert.ToDecimal(rd.GetValue(1)), Str(rd, 2) ?? "USD");
        }
        return map;
    }

    public async Task<Dictionary<string, List<InstallRow>>> ReadInstallsAsync(CancellationToken ct)
    {
        // DISTINCT collapses device multiplicity - the engine only needs "installed
        // somewhere for this user, per source", not per-device rows.
        const string sql = "SELECT DISTINCT UserId, AppName, SourceSystem FROM vw.SoftwareInstallByUser";
        var map = new Dictionary<string, List<InstallRow>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await using SqlConnection c = await OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, c) { CommandTimeout = 300 };
            await using SqlDataReader rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                string? uid = Str(rd, 0); string? app = Str(rd, 1);
                if (string.IsNullOrEmpty(uid) || string.IsNullOrEmpty(app)) continue;
                if (!map.TryGetValue(uid, out List<InstallRow>? list)) map[uid] = list = [];
                list.Add(new InstallRow { UserId = uid, AppName = app, Source = Str(rd, 2) ?? "" });
            }
        }
        catch (SqlException)
        {
            // vw.SoftwareInstallByUser not present (wave6-software-signals.sql not run) -
            // install evidence is optional, so proceed without it rather than failing.
        }
        return map;
    }

    public async Task<Dictionary<string, InstallCoverageRow>> ReadInstallCoverageAsync(CancellationToken ct)
    {
        const string sql = "SELECT UserId, ManagedDeviceCount, IntuneSeenDeviceCount, MdeSeenDeviceCount FROM vw.UserInstallCoverage";
        var map = new Dictionary<string, InstallCoverageRow>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await using SqlConnection c = await OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, c) { CommandTimeout = 300 };
            await using SqlDataReader rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                string? uid = Str(rd, 0);
                if (string.IsNullOrEmpty(uid)) continue;
                map[uid] = new InstallCoverageRow
                {
                    ManagedDeviceCount = rd.IsDBNull(1) ? 0 : Convert.ToInt32(rd.GetValue(1)),
                    IntuneSeenDeviceCount = rd.IsDBNull(2) ? 0 : Convert.ToInt32(rd.GetValue(2)),
                    MdeSeenDeviceCount = rd.IsDBNull(3) ? 0 : Convert.ToInt32(rd.GetValue(3)),
                };
            }
        }
        catch (SqlException)
        {
            // Optional view missing - proceed without coverage (absence evidence disabled).
        }
        return map;
    }

    public async Task<Dictionary<string, List<RunRow>>> ReadMdeRunsAsync(CancellationToken ct)
    {
        const string sql = "SELECT UserId, FileName, LastRunUtc, RunCount, RunDays FROM vw.SoftwareRunByUser";
        var map = new Dictionary<string, List<RunRow>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await using SqlConnection c = await OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, c) { CommandTimeout = 300 };
            await using SqlDataReader rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                string? uid = Str(rd, 0); string? file = Str(rd, 1);
                if (string.IsNullOrEmpty(uid) || string.IsNullOrEmpty(file)) continue;
                if (!map.TryGetValue(uid, out List<RunRow>? list)) map[uid] = list = [];
                list.Add(new RunRow
                {
                    UserId = uid,
                    FileName = file,
                    LastRunUtc = rd.IsDBNull(2) ? null : rd.GetDateTime(2),
                    RunCount = rd.IsDBNull(3) ? 0 : Convert.ToInt64(rd.GetValue(3)),
                    RunDays = rd.IsDBNull(4) ? 0 : Convert.ToInt32(rd.GetValue(4)),
                });
            }
        }
        catch (SqlException)
        {
            // vw.SoftwareRunByUser not present (wave7-mde-hunting.sql not run) -
            // run telemetry is optional, so proceed without it.
        }
        return map;
    }

    public async Task<Dictionary<string, List<SignInRow>>> ReadAppSignInsAsync(CancellationToken ct)
    {
        const string sql = "SELECT UserId, AppId, LastSignInUtc, SignInCount FROM vw.AppSignInByUser";
        var map = new Dictionary<string, List<SignInRow>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await using SqlConnection c = await OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, c) { CommandTimeout = 300 };
            await using SqlDataReader rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                string? uid = Str(rd, 0); string? appId = Str(rd, 1);
                if (string.IsNullOrEmpty(uid) || string.IsNullOrEmpty(appId)) continue;
                if (!map.TryGetValue(uid, out List<SignInRow>? list)) map[uid] = list = [];
                list.Add(new SignInRow
                {
                    UserId = uid, AppId = appId,
                    LastSignInUtc = rd.IsDBNull(2) ? null : rd.GetDateTime(2),
                    SignInCount = rd.IsDBNull(3) ? 0 : Convert.ToInt64(rd.GetValue(3)),
                });
            }
        }
        catch (SqlException) { /* vw.AppSignInByUser absent (wave8 not run) - optional. */ }
        return map;
    }

    public async Task<Dictionary<string, M365AppRow>> ReadM365AppUsageAsync(CancellationToken ct)
    {
        const string sql = "SELECT UserId, WordLastActivityDate, ExcelLastActivityDate, PowerPointLastActivityDate, " +
                           "OutlookLastActivityDate, OneNoteLastActivityDate, TeamsLastActivityDate, LastActivityAnyDate " +
                           "FROM vw.M365AppUsageByUser";
        var map = new Dictionary<string, M365AppRow>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await using SqlConnection c = await OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, c) { CommandTimeout = 300 };
            await using SqlDataReader rd = await cmd.ExecuteReaderAsync(ct);
            DateTime? D(int i) => rd.IsDBNull(i) ? null : rd.GetDateTime(i);
            while (await rd.ReadAsync(ct))
            {
                string? uid = Str(rd, 0);
                if (string.IsNullOrEmpty(uid)) continue;
                map[uid] = new M365AppRow
                {
                    UserId = uid, Word = D(1), Excel = D(2), PowerPoint = D(3),
                    Outlook = D(4), OneNote = D(5), Teams = D(6), AnyApp = D(7),
                };
            }
        }
        catch (SqlException) { /* vw.M365AppUsageByUser absent (wave8 not run) - optional. */ }
        return map;
    }

    public async Task<Dictionary<string, CopilotRow>> ReadCopilotUsageAsync(CancellationToken ct)
    {
        // v2 view carries depth (surfaces used); fall back to the v1 view on older schemas.
        var map = new Dictionary<string, CopilotRow>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await ReadCopilotFrom("SELECT UserId, LastActivityAnyDate, SurfacesUsed FROM vw.CopilotDepthByUser", true, map, ct);
        }
        catch (SqlException)
        {
            try { await ReadCopilotFrom("SELECT UserId, LastActivityAnyDate FROM vw.CopilotUsageByUser", false, map, ct); }
            catch (SqlException) { /* neither view present - Copilot signal optional. */ }
        }
        return map;
    }

    private async Task ReadCopilotFrom(string sql, bool hasDepth, Dictionary<string, CopilotRow> map, CancellationToken ct)
    {
        await using SqlConnection c = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, c) { CommandTimeout = 300 };
        await using SqlDataReader rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            string? uid = Str(rd, 0);
            if (string.IsNullOrEmpty(uid)) continue;
            map[uid] = new CopilotRow
            {
                UserId = uid,
                LastActivity = Dt(rd, 1),
                SurfacesUsed = hasDepth && !rd.IsDBNull(2) ? Convert.ToInt32(rd.GetValue(2)) : 0,
            };
        }
    }

    public async Task<Dictionary<string, PstnRow>> ReadPstnUsageAsync(CancellationToken ct)
    {
        const string sql = "SELECT UserId, CallCount, TotalDurationSeconds, LastCallDateTime, WindowDays FROM vw.PstnUsageByUser";
        var map = new Dictionary<string, PstnRow>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await using SqlConnection c = await OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, c) { CommandTimeout = 300 };
            await using SqlDataReader rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                string? uid = Str(rd, 0);
                if (string.IsNullOrEmpty(uid)) continue;
                map[uid] = new PstnRow
                {
                    UserId = uid,
                    CallCount = rd.IsDBNull(1) ? 0 : Convert.ToInt32(rd.GetValue(1)),
                    TotalDurationSeconds = rd.IsDBNull(2) ? 0 : Convert.ToInt64(rd.GetValue(2)),
                    LastCall = Dt(rd, 3),
                    WindowDays = rd.IsDBNull(4) ? 0 : Convert.ToInt32(rd.GetValue(4)),
                };
            }
        }
        catch (SqlException) { /* vw.PstnUsageByUser absent or connector disabled - optional. */ }
        return map;
    }

    public async Task<Dictionary<string, TeamsActivityRow>> ReadTeamsActivityAsync(CancellationToken ct)
    {
        const string sql = "SELECT UserId, CallCount, MeetingCount, LastActivityDate FROM vw.TeamsActivityByUser";
        var map = new Dictionary<string, TeamsActivityRow>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await using SqlConnection c = await OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, c) { CommandTimeout = 300 };
            await using SqlDataReader rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                string? uid = Str(rd, 0);
                if (string.IsNullOrEmpty(uid)) continue;
                map[uid] = new TeamsActivityRow
                {
                    UserId = uid,
                    CallCount = rd.IsDBNull(1) ? 0 : Convert.ToInt32(rd.GetValue(1)),
                    MeetingCount = rd.IsDBNull(2) ? 0 : Convert.ToInt32(rd.GetValue(2)),
                    LastActivity = Dt(rd, 3),
                };
            }
        }
        catch (SqlException) { /* vw.TeamsActivityByUser absent (wave10-enrichment.sql not run) - optional. */ }
        return map;
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var c = new SqlConnection(_cs);
        await c.OpenAsync(ct);
        return c;
    }

    private static string? Str(SqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetValue(i).ToString();
    private static bool? Bool(SqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetBoolean(i);
    private static DateTime? Dt(SqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDateTime(i);
}
