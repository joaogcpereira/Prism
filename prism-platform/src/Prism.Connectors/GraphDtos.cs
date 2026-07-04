// ============================================================
//  GraphDtos.cs
//  Minimal DTOs for the exact Graph fields we $select. Kept tiny
//  on purpose: we don't take a dependency on the full Graph SDK,
//  we just model the read-only slices we need.
// ============================================================
using System.Text.Json.Serialization;

namespace Prism.Connectors.Graph;

// ---- /users (paged) -------------------------------------------------------
public sealed class GraphUsersResponse
{
    [JsonPropertyName("value")] public List<GraphUser> Value { get; set; } = [];
    [JsonPropertyName("@odata.nextLink")] public string? NextLink { get; set; }
}

public sealed class GraphUser
{
    public string Id { get; set; } = "";
    public string? UserPrincipalName { get; set; }
    public string? DisplayName { get; set; }
    public bool? AccountEnabled { get; set; }
    public string? Department { get; set; }
    public string? JobTitle { get; set; }
    public string? UsageLocation { get; set; }
    public string? CreatedDateTime { get; set; }
    public string? EmployeeHireDate { get; set; }
    public string? EmployeeLeaveDateTime { get; set; }           // requires User-LifeCycleInfo.Read.All
    public string? SecurityIdentifier { get; set; }              // Entra/cloud SID, S-1-12-1-… (Entra-joined sessions)
    public string? OnPremisesSecurityIdentifier { get; set; }    // on-prem AD SID, S-1-5-21-… (hybrid sessions)
    public string? UserType { get; set; }                        // Member | Guest (paid SKU on a Guest = governance flag)
    public bool? OnPremisesSyncEnabled { get; set; }             // hybrid-synced account (null = cloud-only)
    public List<GraphLicenseAssignmentState>? LicenseAssignmentStates { get; set; }
    public GraphSignInActivity? SignInActivity { get; set; }
}

public sealed class GraphLicenseAssignmentState
{
    public string? SkuId { get; set; }
    public string? AssignedByGroup { get; set; }   // group object id, or null when assigned directly
    public string? State { get; set; }
    public string? LastUpdatedDateTime { get; set; }
    public List<string>? DisabledPlans { get; set; }
}

public sealed class GraphSignInActivity
{
    public string? LastSignInDateTime { get; set; }
    public string? LastNonInteractiveSignInDateTime { get; set; }
    public string? LastSuccessfulSignInDateTime { get; set; }
}

// ---- /subscribedSkus (paged) ---------------------------------------------
public sealed class GraphSkusResponse
{
    [JsonPropertyName("value")] public List<GraphSubscribedSku> Value { get; set; } = [];
    [JsonPropertyName("@odata.nextLink")] public string? NextLink { get; set; }
}

public sealed class GraphSubscribedSku
{
    public string SkuId { get; set; } = "";
    public string? SkuPartNumber { get; set; }
    public string? CapabilityStatus { get; set; }
    public int ConsumedUnits { get; set; }
    public GraphPrepaidUnits? PrepaidUnits { get; set; }
}

public sealed class GraphPrepaidUnits
{
    public int Enabled { get; set; }
    public int Warning { get; set; }
    public int Suspended { get; set; }
}

// ---- /admin/reportSettings -----------------------------------------------
public sealed class ReportSettings
{
    // When true, Microsoft masks UPN/Display Name in usage reports (GDPR / works-council).
    public bool DisplayConcealedNames { get; set; }
}

// ---- /deviceManagement/managedDevices (paged) ----------------------------
public sealed class GraphDevicesResponse
{
    [JsonPropertyName("value")] public List<GraphManagedDevice> Value { get; set; } = [];
    [JsonPropertyName("@odata.nextLink")] public string? NextLink { get; set; }
}

public sealed class GraphManagedDevice
{
    public string Id { get; set; } = "";
    public string? DeviceName { get; set; }
    public string? UserId { get; set; }
    public string? UserPrincipalName { get; set; }
    public string? OperatingSystem { get; set; }
    public string? OsVersion { get; set; }
    public string? ComplianceState { get; set; }
    public string? ManagedDeviceOwnerType { get; set; }
    public string? ManagementState { get; set; }
    public string? EnrolledDateTime { get; set; }
    public string? LastSyncDateTime { get; set; }
    public string? Model { get; set; }
    public string? Manufacturer { get; set; }
    public string? SerialNumber { get; set; }
    public bool? IsEncrypted { get; set; }
}

// ---- /deviceManagement/detectedApps (paged) ------------------------------
public sealed class GraphDetectedAppsResponse
{
    [JsonPropertyName("value")] public List<GraphDetectedApp> Value { get; set; } = [];
    [JsonPropertyName("@odata.nextLink")] public string? NextLink { get; set; }
}

public sealed class GraphDetectedApp
{
    public string Id { get; set; } = "";
    public string? DisplayName { get; set; }
    public string? Version { get; set; }
    public string? Publisher { get; set; }
    public string? Platform { get; set; }
    public int DeviceCount { get; set; }
    public long SizeInByte { get; set; }
}

// Item 4: detectedApps/{id}/managedDevices projection (which devices have the app).
public sealed class GraphManagedDevicesResponse
{
    [JsonPropertyName("value")] public List<GraphManagedDeviceRef> Value { get; set; } = [];
    [JsonPropertyName("@odata.nextLink")] public string? NextLink { get; set; }
}

// ---- /auditLogs/signIns (paged, beta-grade fields on v1.0) ----------------
// Per-(user, application) interactive + non-interactive sign-ins. The connector
// aggregates these to "last time user U touched app A" - the authoritative usage
// signal for WEB/SERVICE-first SKUs (Power BI service, Project web) that no exe-
// or workload-based signal can see.
public sealed class GraphSignInsResponse
{
    [JsonPropertyName("value")] public List<GraphSignIn> Value { get; set; } = [];
    [JsonPropertyName("@odata.nextLink")] public string? NextLink { get; set; }
}

public sealed class GraphSignIn
{
    public string? UserId { get; set; }
    public string? UserPrincipalName { get; set; }
    public string? AppId { get; set; }
    public string? AppDisplayName { get; set; }
    public string? CreatedDateTime { get; set; }
}

// ---- Intune Endpoint Analytics App Health (paged) ------------------------
public sealed class AppHealthResponse
{
    [JsonPropertyName("value")] public List<AppHealthPerf> Value { get; set; } = [];
    [JsonPropertyName("@odata.nextLink")] public string? NextLink { get; set; }
}
public sealed class AppHealthPerf
{
    public string? AppName { get; set; }
    public string? AppDisplayName { get; set; }
    public string? AppPublisher { get; set; }
    public long? AppUsageDuration { get; set; }
    public int? ActiveDeviceCount { get; set; }
    public int? AppCrashCount { get; set; }
    public int? AppHangCount { get; set; }
    public double? AppHealthScore { get; set; }
    public double? MeanTimeToFailureInMinutes { get; set; }
}

// ---- Intune managed apps + install summary (paged, $expand=installSummary) -
public sealed class MobileAppsResponse
{
    [JsonPropertyName("value")] public List<MobileApp> Value { get; set; } = [];
    [JsonPropertyName("@odata.nextLink")] public string? NextLink { get; set; }
}
public sealed class MobileApp
{
    public string? Id { get; set; }
    public string? DisplayName { get; set; }
    public string? Publisher { get; set; }
    [JsonPropertyName("@odata.type")] public string? ODataType { get; set; }   // app type ~ platform
    public MobileAppInstallSummary? InstallSummary { get; set; }
}
public sealed class MobileAppInstallSummary
{
    public int? InstalledDeviceCount { get; set; }
    public int? FailedDeviceCount { get; set; }
    public int? NotInstalledDeviceCount { get; set; }
    public int? PendingInstallDeviceCount { get; set; }
}

// ---- Entra service-principal sign-in activity (paged, beta) ---------------
public sealed class SpSignInResponse
{
    [JsonPropertyName("value")] public List<SpSignIn> Value { get; set; } = [];
    [JsonPropertyName("@odata.nextLink")] public string? NextLink { get; set; }
}
public sealed class SpSignIn
{
    public string? AppId { get; set; }
    public SpSignInActivity? LastSignInActivity { get; set; }
}
public sealed class SpSignInActivity
{
    public string? LastSignInDateTime { get; set; }
}

// ---- /directory/deletedItems/microsoft.graph.user (paged) -----------------
// Soft-deleted users keep their licences (and keep billing) for ~30 days.
public sealed class GraphDeletedUsersResponse
{
    [JsonPropertyName("value")] public List<GraphDeletedUser> Value { get; set; } = [];
    [JsonPropertyName("@odata.nextLink")] public string? NextLink { get; set; }
}

public sealed class GraphDeletedUser
{
    public string Id { get; set; } = "";
    public string? UserPrincipalName { get; set; }
    public string? DisplayName { get; set; }
    public string? DeletedDateTime { get; set; }
    public List<GraphAssignedLicense>? AssignedLicenses { get; set; }
}

public sealed class GraphAssignedLicense
{
    public string? SkuId { get; set; }
}

public sealed class GraphManagedDeviceRef
{
    public string Id { get; set; } = "";
    public string? DeviceName { get; set; }
    public string? UserPrincipalName { get; set; }
}

// ---- v2: licensed-user id page (mailbox-settings enumeration) --------------
public sealed class GraphUserIdsResponse
{
    [JsonPropertyName("value")] public List<GraphUserId> Value { get; set; } = [];
    [JsonPropertyName("@odata.nextLink")] public string? NextLink { get; set; }
}
public sealed class GraphUserId
{
    public string Id { get; set; } = "";
    public string? UserPrincipalName { get; set; }
}

// ---- v2: /users/{id}/mailboxSettings ($batch sub-response body) ------------
public sealed class GraphMailboxSettings
{
    public string? UserPurpose { get; set; }                     // user | shared | room | equipment | linked | others
    public string? TimeZone { get; set; }
    public GraphAutomaticReplies? AutomaticRepliesSetting { get; set; }
}
public sealed class GraphAutomaticReplies
{
    public string? Status { get; set; }                          // disabled | alwaysEnabled | scheduled
}

// ---- v2: /communications/callRecords/getPstnCalls (paged) ------------------
public sealed class PstnCallsResponse
{
    [JsonPropertyName("value")] public List<PstnCall> Value { get; set; } = [];
    [JsonPropertyName("@odata.nextLink")] public string? NextLink { get; set; }
}
public sealed class PstnCall
{
    public string? UserId { get; set; }
    public string? UserPrincipalName { get; set; }
    public string? StartDateTime { get; set; }
    public int? Duration { get; set; }                           // seconds
}

// ---- v2: /reports/authenticationMethods/userRegistrationDetails (paged) ----
public sealed class AuthRegistrationResponse
{
    [JsonPropertyName("value")] public List<AuthRegistrationDetail> Value { get; set; } = [];
    [JsonPropertyName("@odata.nextLink")] public string? NextLink { get; set; }
}
public sealed class AuthRegistrationDetail
{
    public string Id { get; set; } = "";                         // the user's object id
    public string? UserPrincipalName { get; set; }
    public bool? IsAdmin { get; set; }
    public bool? IsMfaRegistered { get; set; }
    public bool? IsMfaCapable { get; set; }
    public bool? IsPasswordlessCapable { get; set; }
    public bool? IsSsprRegistered { get; set; }
    public bool? IsSsprEnabled { get; set; }
    public bool? IsSsprCapable { get; set; }
    public List<string>? MethodsRegistered { get; set; }
    public string? UserPreferredMethodForSecondaryAuthentication { get; set; }
    public string? LastUpdatedDateTime { get; set; }
}
