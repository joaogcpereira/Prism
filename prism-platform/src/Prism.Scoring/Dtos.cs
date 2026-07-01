// ============================================================
//  Dtos.cs  (Prism.Scoring)  — rows read from the warehouse views.
// ============================================================
namespace Prism.Scoring;

/// <summary>One row of vw.LicenseSignals: a (user, assigned SKU) pair + raw signals.</summary>
public sealed class SignalRow
{
    public string UserId = "";
    public string? UserPrincipalName;
    public string? DisplayName;
    public bool? AccountEnabled;
    public string? Department;
    public string? Country;
    public DateTime? EmployeeHireDate;
    public DateTime? CreatedDateTime;
    public DateTime? EmployeeLeaveDateTime;
    public DateTime? LastSignInDateTime;
    public DateTime? LastNonInteractiveSignInDateTime;
    public string SkuId = "";
    public string? SkuPartNumber;
    public string? SkuName;
    public bool? AssignedDirectly;
    public string? AssignmentState;
    public DateTime? AssignmentLastUpdatedDateTime;
    public bool? M365ActivityConcealed;
    public DateTime? M365LastActivityDate;
    // Per-workload last activity (depth-of-use signal) + report presence.
    public DateTime? TeamsLastActivityDate;
    public DateTime? ExchangeLastActivityDate;
    public DateTime? OneDriveLastActivityDate;
    public DateTime? SharePointLastActivityDate;
    public DateTime? M365ReportRefreshDate;
    public DateTime? LastSuccessfulSignInDateTime;
    public int DisabledPlanCount;
}

/// <summary>One row of vw.AppUsageByUser90: per (Entra user, exe) foreground usage + per-user coverage.</summary>
public sealed class AppRow
{
    public string UserId = "";
    public string ExePath = "";
    public long FgActiveSeconds;
    public int ActiveDays;
    public DateTime? LastDay;
    public int CoverageDays;   // distinct days this user's device(s) reported ANY app (trust gate)
}

/// <summary>One row of vw.SkuUtilization.</summary>
public sealed class SkuRow
{
    public string SkuId = "";
    public string? SkuPartNumber;
    public string? SkuName;
    public int SeatsOwned;
    public int SeatsAssigned;
    public int SeatsIdle;
}

/// <summary>One row of vw.SoftwareInstallByUser: an app installed on a user's device,
/// from either install inventory (intune = Intune detectedApps, mde = Defender for Endpoint).</summary>
public sealed class InstallRow
{
    public string UserId = "";
    public string AppName = "";
    public string Source = "";     // "intune" | "mde"
}

/// <summary>One row of vw.UserInstallCoverage: whether the install inventories actually
/// see this user's devices. Absence of installs is only evidence when they do.</summary>
public sealed class InstallCoverageRow
{
    public int ManagedDeviceCount;
    public int IntuneSeenDeviceCount;
    public int MdeSeenDeviceCount;
}

/// <summary>One row of vw.SoftwareRunByUser: Defender Advanced Hunting process-run
/// telemetry attributed to a user (last 30 days, per executable).</summary>
public sealed class RunRow
{
    public string UserId = "";
    public string FileName = "";
    public DateTime? LastRunUtc;
    public long RunCount;
    public int RunDays;
}

/// <summary>One row of vw.AppSignInByUser: last Entra sign-in to an application by a user.
/// The authoritative WEB/SERVICE usage signal (Power BI service, Project web).</summary>
public sealed class SignInRow
{
    public string UserId = "";
    public string AppId = "";
    public DateTime? LastSignInUtc;
    public long SignInCount;
}

/// <summary>One row of vw.M365AppUsageByUser: per-user Office desktop-app last activity.</summary>
public sealed class M365AppRow
{
    public string UserId = "";
    public DateTime? Word, Excel, PowerPoint, Outlook, OneNote, Teams, AnyApp;
}

/// <summary>One row of vw.CopilotUsageByUser: per-user last Copilot activity (any host app).
/// The authoritative usage signal for the Microsoft 365 Copilot SKU.</summary>
public sealed class CopilotRow
{
    public string UserId = "";
    public DateTime? LastActivity;
}

/// <summary>One row of vw.TeamsActivityByUser: per-user Teams call/meeting counts. The call
/// count is the authoritative usage signal for a Teams Phone (MCOEV) seat.</summary>
public sealed class TeamsActivityRow
{
    public string UserId = "";
    public int CallCount;
    public int MeetingCount;
    public DateTime? LastActivity;
}
