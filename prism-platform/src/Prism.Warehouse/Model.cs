// ============================================================
//  Model.cs  (Prism.Warehouse)
//  The canonical, store-agnostic Prism entities - the shared home
//  for these types. Connectors and the gateway map onto these; the
//  warehouse persists them; the scoring engine reads them. Field
//  names match the wire shape the connectors/gateway emit.
// ============================================================
namespace Prism.Warehouse.Model;

public sealed record EntityEnvelope<T>(string Source, string RunId, string SnapshotUtc, T Data);

public sealed record DimUser(
    string  UserId, string? UserPrincipalName, string? DisplayName, bool AccountEnabled,
    string? Department, string? JobTitle, string? UsageLocation, string? CreatedDateTime,
    string? EmployeeHireDate, string? EmployeeLeaveDateTime,
    string? LastSignInDateTime, string? LastNonInteractiveSignInDateTime, string? LastSuccessfulSignInDateTime,
    string? SecurityIdentifier, string? OnPremisesSecurityIdentifier,
    // v2 enrichment: Member vs Guest (a paid SKU on a guest is a governance flag) and
    // hybrid-sync provenance (on-prem-synced accounts have AD-derived SIDs - explains
    // why agent SID correlation may be absent; never a penalty).
    string? UserType = null, bool? OnPremisesSyncEnabled = null);

public sealed record DimSku(
    string SkuId, string? SkuPartNumber, string? DisplayName, string? CapabilityStatus,
    int PrepaidUnitsEnabled, int PrepaidUnitsWarning, int PrepaidUnitsSuspended, int ConsumedUnits);

public sealed record FactLicenseAssignment(
    string UserId, string SkuId, string? SkuPartNumber, bool AssignedDirectly,
    string? AssignedByGroupId, string? State, string? LastUpdatedDateTime, string[] DisabledServicePlanIds);

public sealed record FactServiceUsage(
    string? UserPrincipalName, string? DisplayName, bool Concealed, string? ReportRefreshDate,
    string? ReportPeriodDays, bool IsDeleted,
    bool HasExchangeLicense, bool HasOneDriveLicense, bool HasSharePointLicense,
    bool HasTeamsLicense, bool HasYammerLicense, bool HasSkypeLicense,
    string? ExchangeLastActivityDate, string? OneDriveLastActivityDate, string? SharePointLastActivityDate,
    string? TeamsLastActivityDate, string? YammerLastActivityDate, string? SkypeLastActivityDate,
    string? LastActivityAnyDate, string? AssignedProducts);

public sealed record FactAzureCost(
    string Scope, string? UsageDate, decimal Cost, string? Currency, string? ServiceName, string? ResourceGroup);

public sealed record FactAppUsage(
    string Date, string? DeviceThumbprint, string? MachineName, string? UserSid,
    string? ExePath, string? DisplayName, string? ProductName, string? Description, string? Company, string? FileVersion,
    int Launches, string? FirstSeenUtc, string? LastSeenUtc,
    long ForegroundActiveSeconds, long ForegroundIdleSeconds, long VisibleBackgroundSeconds,
    long MinimizedSeconds, long TraySeconds, int UtcOffsetMinutes, string? AgentVersion, string? ReceiveId);

public sealed record DimDevice(
    string DeviceId, string? DeviceName, string? UserId, string? UserPrincipalName,
    string? OperatingSystem, string? OsVersion, string? ComplianceState, string? OwnerType,
    string? ManagementState, string? EnrolledDateTime, string? LastSyncDateTime,
    string? Model, string? Manufacturer, string? SerialNumber, bool? IsEncrypted);

public sealed record FactDetectedApp(
    string AppId, string? DisplayName, string? Version, string? Publisher, string? Platform,
    int DeviceCount, long SizeInByte);

public sealed record FactAppInstall(
    string? AppId, string? DisplayName, string? DeviceId, string? DeviceName, string? UserPrincipalName);

public sealed record FactDiscoveredApp(
    string? AppName, string? Category, double? RiskScore, long? UserCount,
    long? UploadedBytes, long? DownloadedBytes, long? TrafficTotalBytes, long? TransactionCount,
    string? LastSeen, string? Tags);

// Microsoft Defender for Endpoint / Defender Vulnerability Management:
// org-wide software inventory (GET /api/Software). One row per software TITLE
// (id = "vendor-_-name"), with the count of machines on which it is present
// (ExposedMachines) - a tenant-wide install/usage footprint that corroborates
// the Intune detectedApps pull and the agent's active-usage signal.
public sealed record FactSoftwareInventory(
    string SoftwareId, string? Name, string? Vendor, long? Weaknesses,
    bool? PublicExploit, bool? ActiveAlert, long? ExposedMachines, double? ImpactScore);

// Per-device expansion (GET /api/Software/{id}/machineReferences), optional and
// capped - one row per (software, device). Lets the dashboard show exactly which
// machines carry a licensed title, the way fact.AppInstall does for Intune.
public sealed record FactSoftwareInstall(
    string SoftwareId, string? SoftwareName, string? Vendor,
    string? MachineId, string? ComputerDnsName, string? OsPlatform);

// Defender for Endpoint Advanced Hunting (DeviceProcessEvents, last 30 days):
// one row per (device, executable, account) with launch statistics. TRUE usage
// telemetry - "the exe actually started" - fleet-wide without the agent. Lower
// fidelity than the agent (launches, not foreground time; 30d, not 90d) but it
// covers every onboarded device, including those with no agent installed.
public sealed record FactSoftwareRun(
    string FileName, string? DeviceId, string? DeviceName, string? AccountUpn,
    string? LastRunUtc, long RunCount, int RunDays);

// Entra per-application sign-in activity (auditLogs/signIns, aggregated). One row
// per (user, application) with last interactive/non-interactive sign-in and a count.
// The authoritative usage signal for WEB/SERVICE-first SKUs (Power BI service,
// Project web) invisible to exe- and workload-based signals.
public sealed record FactAppSignIn(
    string? UserId, string? UserPrincipalName, string? AppId, string? AppDisplayName,
    string? LastSignInUtc, long SignInCount, int WindowDays);

// Microsoft 365 Apps usage (getM365AppUserDetail): per-user last activity per desktop
// app (Word/Excel/PowerPoint/Outlook/OneNote/Teams) across platforms. Sharpens
// SHALLOW_USE - "has the SKU's core apps actually been touched" - beyond workload activity.
public sealed record FactM365AppUsage(
    string? UserPrincipalName, string? DisplayName, bool Concealed,
    string? ReportRefreshDate, string? ReportPeriodDays, bool IsDeleted,
    string? WordLastActivityDate, string? ExcelLastActivityDate, string? PowerPointLastActivityDate,
    string? OutlookLastActivityDate, string? OneNoteLastActivityDate, string? TeamsLastActivityDate,
    string? LastActivityAnyDate, bool UsedWeb, bool UsedMobile, bool UsedWindows, bool UsedMac);

// Microsoft 365 Copilot usage (getMicrosoft365CopilotUsageUserDetail, beta): per-user last
// activity for Copilot in each host app (Teams/Word/Excel/PowerPoint/Outlook/OneNote/Loop/Chat).
// The ONLY signal that sees Copilot seat usage - invisible to every exe/workload/sign-in signal,
// and the priciest per-seat SKU, so an enabled-but-idle Copilot seat is the clearest reclaim.
public sealed record FactCopilotUsage(
    string? UserPrincipalName, string? DisplayName, bool Concealed,
    string? ReportRefreshDate, string? ReportPeriodDays, string? LastActivityDate,
    string? TeamsLastActivityDate, string? WordLastActivityDate, string? ExcelLastActivityDate,
    string? PowerPointLastActivityDate, string? OutlookLastActivityDate, string? OneNoteLastActivityDate,
    string? LoopLastActivityDate, string? ChatLastActivityDate, string? LastActivityAnyDate);

// Microsoft Teams user activity (getTeamsUserActivityUserDetail): per-user message/call/meeting
// counts + last activity. The call count is the real usage signal for a Teams Phone (MCOEV)
// seat - "has a number, made/took zero calls" - that the binary Teams last-activity can't show.
public sealed record FactTeamsActivity(
    string? UserPrincipalName, bool Concealed, string? ReportRefreshDate, string? ReportPeriodDays,
    string? LastActivityDate, string? TeamChatMessageCount, string? PrivateChatMessageCount,
    string? CallCount, string? MeetingCount);

// Consolidated per-user M365 service activity detail (getMailboxUsageDetail +
// getOneDriveActivityUserDetail + getSharePointActivityUserDetail), one row per (service, user).
// Adds INTENSITY (file/page counts, storage) beyond the workload last-activity dates already in
// fact.ServiceUsage - e.g. "has a OneDrive licence but edited zero files".
public sealed record FactServiceActivityDetail(
    string Service, string? UserPrincipalName, bool Concealed, string? ReportRefreshDate,
    string? ReportPeriodDays, string? LastActivityDate,
    string? ViewedOrEditedFileCount, string? SyncedFileCount,
    string? SharedInternallyFileCount, string? SharedExternallyFileCount, string? VisitedPageCount,
    string? StorageUsedBytes, string? ItemCount);

// Intune Endpoint Analytics - App Health (userExperienceAnalyticsAppHealthApplicationPerformance).
// Tenant/app-level (NOT per-user): app usage duration + active-device count + crash/hang stats,
// agent-independent. Corroborates whether an app is used in the org at all.
public sealed record FactAppHealth(
    string? AppName, string? AppDisplayName, string? AppPublisher,
    long? AppUsageDuration, int? ActiveDeviceCount, int? AppCrashCount, int? AppHangCount,
    double? AppHealthScore, double? MeanTimeToFailureInMinutes);

// Intune managed-app deployment status (deviceAppManagement/mobileApps + installSummary).
// Per-app (NOT per-user): how many devices have the app installed / failed / pending. Complements
// the discovered/detectedApps INVENTORY with assigned-deployment status for managed LOB apps.
public sealed record FactMobileAppInstall(
    string? AppId, string? DisplayName, string? Publisher, string? Platform,
    int? InstalledDeviceCount, int? FailedDeviceCount, int? NotInstalledDeviceCount, int? PendingInstallDeviceCount);

// Entra service-principal (enterprise-app) sign-in activity (reports/servicePrincipalSignInActivities).
// Per-app (NOT per-user): last sign-in to/by each enterprise app - flags entirely-unused licensed services.
public sealed record FactServicePrincipalSignIn(
    string? AppId, string? DisplayName, string? LastSignInUtc);

// Soft-deleted users that still hold (and bill) licences within the 30-day window.
public sealed record FactDeletedUserLicense(
    string UserId, string? UserPrincipalName, string? DisplayName,
    string? DeletedDateTime, string SkuId);

// Mailbox settings (GET /users/{id}/mailboxSettings, batched): userPurpose is the
// DETERMINISTIC shared/room/equipment discriminator that replaces the name-pattern
// heuristic for shared-mailbox detection. A licensed 'shared' mailbox <50 GB usually
// needs no license at all - the classic compliance trap, now evidence-backed.
public sealed record FactMailbox(
    string UserId, string? UserPrincipalName, string? UserPurpose,
    string? AutomaticRepliesStatus, string? TimeZone);

// Teams PSTN calling usage (getPstnCalls, aggregated per user over the window).
// REAL call-detail records - the authoritative signal for a Teams Phone / calling-plan
// seat, far stronger than the Teams activity report's coarse CallCount.
public sealed record FactPstnUsage(
    string? UserId, string? UserPrincipalName, int CallCount, long TotalDurationSeconds,
    string? LastCallDateTime, int WindowDays);

// Authentication-method registration (reports/authenticationMethods/userRegistrationDetails).
// A seat whose holder never even registered MFA/SSPR is positive evidence the account was
// never onboarded - sharpens NEVER_ACTIVE (still never auto-reclaims on absence alone).
public sealed record FactAuthMethod(
    string UserId, string? UserPrincipalName, bool? IsAdmin,
    bool? IsMfaRegistered, bool? IsMfaCapable, bool? IsPasswordlessCapable,
    bool? IsSsprRegistered, bool? IsSsprEnabled, bool? IsSsprCapable,
    string? MethodsRegistered, string? DefaultMethod, string? LastUpdatedDateTime);
