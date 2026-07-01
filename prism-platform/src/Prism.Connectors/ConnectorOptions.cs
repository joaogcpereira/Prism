// ============================================================
//  ConnectorOptions.cs
//  Bound from the "Prism" section of appsettings.json / env vars.
// ============================================================
namespace Prism.Connectors;

public sealed class ConnectorOptions
{
    public const string SectionName = "Prism";

    // Microsoft Graph base (national clouds can override, e.g. graph.microsoft.us).
    public string GraphBaseUrl { get; set; } = "https://graph.microsoft.com/v1.0";

    // Client id of the user-assigned managed identity (id-prism-platform). When set,
    // DefaultAzureCredential targets THIS identity (a VM/Container can have several).
    // Leave null locally to fall back to `az login` / Visual Studio sign-in.
    public string? ManagedIdentityClientId { get; set; }

    // Where normalized NDJSON snapshots are written (one folder per run).
    public string LandingDirectory { get; set; } = "./data/m365";

    // Page size for Graph list calls (max 999 for /users).
    public int PageSize { get; set; } = 999;

    // Item 4 — per-device install visibility. For each detected app whose
    // DisplayName contains one of these (case-insensitive) fragments, the Intune
    // connector expands detectedApps/{id}/managedDevices into fact.AppInstall so the
    // dashboard can show exactly which devices/users have it installed. These
    // licensing-relevant apps are always expanded FIRST (before the general budget).
    public string[] InstallVisibilityPatterns { get; set; } =
        ["Visio", "Project", "Power BI", "Acrobat", "Photoshop", "AutoCAD", "Project Professional"];

    // When true, the connector ALSO expands per-device installs for the REST of the
    // detected-app inventory (Store/UWP identity-style names and zero-install rows
    // skipped), most-installed first, so the Applications drill can show the device
    // list for any app — not just the watched ones. Each app costs one Graph call,
    // so the total is hard-capped by MaxInstallExpansions per run (watched apps
    // count against the cap last, i.e. they are never the ones dropped).
    public bool ExpandAllInstalls { get; set; } = true;
    public int MaxInstallExpansions { get; set; } = 2500;
    // Soft time budget for the expansion phase. 0 (default) = NO budget: expand the
    // whole inventory however long Graph throttling makes it take. Because the run no
    // longer has an artificial deadline and the job's --replica-timeout should be set
    // generously, the sweep simply runs to completion. A POSITIVE value re-enables the
    // old behaviour (stop expanding in time to still write what was gathered before a
    // finite replica timeout kills the replica) — keep it under --replica-timeout.
    public int InstallExpansionTimeBudgetSeconds { get; set; } = 0;
    // Base pacing between expansion $batch calls (the floor of the adaptive pace).
    // Intune's managedDevices endpoint is throttled far more aggressively than general
    // Graph (observed live); the connector also honors each Retry-After exactly.
    public int InstallExpansionDelayMs { get; set; } = 250;
    // Adaptive pacing ceiling (AIMD). The inter-batch delay starts at
    // InstallExpansionDelayMs and DOUBLES whenever Graph signals throttling (up to this
    // ceiling), then eases back down as batches succeed — so the sweep settles just
    // under Graph's tolerance instead of triggering 429 after 429.
    public int InstallExpansionMaxDelayMs { get; set; } = 15000;

    // Cadence gate for the (expensive) install expansion. Empty / "any" = every run.
    // Set e.g. "Sunday" to do the full per-device sweep weekly: on other days the
    // phase is skipped entirely and fact.AppInstall simply keeps its last snapshot —
    // the agent + scoring keep the dashboard's usage data fresh in between.
    public string ExpandInstallsDayOfWeek { get; set; } = "";

    // Transient-failure retry budget for 5xx (exponential backoff).
    public int MaxRetries { get; set; } = 5;

    // Throttle (429) retry budget — separate from, and far higher than, MaxRetries.
    // We honor the service's Retry-After exactly and would rather WAIT than lose data,
    // so a single Graph call (or $batch POST) waits-and-retries up to this many times.
    public int ThrottleMaxRetries { get; set; } = 100;

    // Upper bound (seconds) on a single honored Retry-After, so a pathological header
    // can't park the run for hours. Intune's Retry-After is normally seconds–tens.
    public int MaxRetryAfterSeconds { get; set; } = 300;

    // Overall run deadline in minutes. 0 (default) = NO artificial deadline: the run
    // continues until every connector finishes, however long throttling makes it take.
    // SIGTERM (Container Apps stop) and SIGINT (Ctrl+C) always cancel cooperatively.
    // A positive value is a safety cap. Pair 0 with a generous job --replica-timeout.
    public int OverallTimeoutMinutes { get; set; } = 0;

    // Aggregation window for the M365 service-usage report: D7 | D30 | D90 | D180.
    public string ServiceUsagePeriod { get; set; } = "D30";

    // ---- Azure cost connector ------------------------------------------
    // Scope to query, e.g. tenant-root MG: /providers/Microsoft.Management/managementGroups/<tenant-guid>
    // Empty => the cost connector is skipped.
    public string? CostManagementScope { get; set; }
    public string CostApiVersion { get; set; } = "2025-03-01";
    public string CostType { get; set; } = "ActualCost";   // ActualCost | AmortizedCost | Usage
    public string CostTimeframe { get; set; } = "MonthToDate";
    // EA / management-group scopes use "PreTaxCost"; an MCA subscription scope uses "Cost".
    public string CostColumn { get; set; } = "PreTaxCost";

    // ---- Output sink ---------------------------------------------------
    // "file" (default): NDJSON under LandingDirectory (local/dev).
    // "sql": write straight to the Azure SQL warehouse (production).
    public string Sink { get; set; } = "file";
    // Required when Sink = "sql". Secret-free Entra auth, e.g.:
    //   Server=tcp:prism-sql.database.windows.net,1433;Database=prism;Authentication=Active Directory Default;Encrypt=True;
    public string? ConnectionString { get; set; }

    // ---- Defender for Cloud Apps (optional shadow-IT discovery) --------
    public bool EnableDefenderConnector { get; set; } = false;
    // Tenant-specific portal URL, e.g. https://<tenant>.<region>.portal.cloudappsecurity.com (not derivable).
    public string? DefenderApiBaseUrl { get; set; }
    public string? DefenderTenantId { get; set; }              // tenant GUID (for the app-reg token)
    public string? DefenderAppId { get; set; }                 // Prism-DfCAConnector client id (FIC -> MI)
    // Defender for Cloud Apps API resource ("Microsoft Cloud App Security" app).
    public string DefenderApiScope { get; set; } = "05a65629-4c1b-48c1-a78b-804c4abdd4af/.default";
    public string? DefenderStreamId { get; set; }              // optional; auto-resolved if unset
    public int DefenderTimeframeDays { get; set; } = 90;

    // ---- Defender for Endpoint / Vulnerability Management --------------
    // Org-wide software inventory (GET /api/Software) + optional per-device
    // expansion. Same FIC->MI auth as the MDCA connector but a SEPARATE app
    // registration (Prism-MdeConnector) holding Software.Read.All on
    // WindowsDefenderATP, and Bearer (not "Token") auth.
    public bool EnableMdeConnector { get; set; } = false;
    // Defender for Endpoint API host. Global default; regional hosts are faster,
    // e.g. https://eu.api.security.microsoft.com (EU), us/uk/au also exist.
    public string MdeApiBaseUrl { get; set; } = "https://api.security.microsoft.com";
    public string? MdeTenantId { get; set; }                   // tenant GUID (for the app-reg token)
    public string? MdeAppId { get; set; }                      // Prism-MdeConnector client id (FIC -> MI)
    // App-token scope for the Defender for Endpoint API (WindowsDefenderATP resource).
    public string MdeApiScope { get; set; } = "https://api.securitycenter.microsoft.com/.default";
    // $top page size for the inventory list (API max 10000).
    public int MdePageSize { get; set; } = 1000;
    // When true, ALSO expand machineReferences per title into fact.SoftwareInstall.
    // Off by default: the inventory (with ExposedMachines counts) is the cheap,
    // high-value pull; the expansion is one extra call per title and is capped.
    public bool ExpandSoftwareMachines { get; set; } = false;
    public int MaxSoftwareExpansions { get; set; } = 500;
    // Base inter-title pace for the expansion (AIMD floor; ceiling reuses
    // InstallExpansionMaxDelayMs). MdeClient additionally honors each Retry-After.
    public int SoftwareExpansionDelayMs { get; set; } = 200;

    // ---- Defender for Endpoint Advanced Hunting (process-run telemetry) -----
    // One summarized KQL query over DeviceProcessEvents (max 30-day lookback):
    // which licensing-relevant executables actually STARTED, on which device, by
    // which account. True usage telemetry fleet-wide without the agent. Requires
    // the AdvancedQuery.Read.All application permission on the same app
    // registration; shares EnableMdeConnector's client settings (base URL,
    // tenant, app id). Gate separately so inventory can run without hunting.
    public bool EnableMdeHunting { get; set; } = false;
    public int MdeHuntingLookbackDays { get; set; } = 30;
    // Executables to hunt — keep aligned with the scoring AppTiedSkus mapping.
    public string[] MdeHuntingExecutables { get; set; } = ["visio.exe", "winproj.exe", "pbidesktop.exe"];

    // ---- Entra per-application sign-ins (entra.app-signins) ----------------
    // The authoritative usage signal for WEB/SERVICE-first SKUs (Power BI service,
    // Project web) that exe- and workload-based signals can't see. Reads
    // /auditLogs/signIns (AuditLog.Read.All). Filter to the licence-relevant apps
    // to keep volume small; empty = all apps (heavier). Default app ids are the
    // well-known first-party Power BI + Project/Visio web service principals.
    public bool EnableSignInConnector { get; set; } = false;
    public int SignInLookbackDays { get; set; } = 30;          // Entra retains ~30d
    public string[] SignInAppIds { get; set; } =
    [
        "00000009-0000-0000-c000-000000000000",   // Power BI Service
        "00000004-0000-0ff1-ce00-000000000000",   // Project / Office web (Project Online)
    ];
    // Also count NON-interactive sign-ins (a user's client silently redeeming a token to
    // reach the licensed service = real usage). High volume, so it only runs when SignInAppIds
    // bounds the query to specific apps. Both passes count successful sign-ins only.
    public bool SignInIncludeNonInteractive { get; set; } = true;

    // ---- Microsoft 365 Copilot usage (m365.copilot-usage) ------------------
    // getMicrosoft365CopilotUsageUserDetail (beta): the only signal that sees Copilot seat
    // usage. Reuses ServiceUsagePeriod for the window. CopilotApiBaseUrl is the ABSOLUTE base
    // for the beta report (Microsoft is migrating it to the /copilot path); the connector
    // builds an absolute URL so the shared GraphClient's v1.0 base address is untouched.
    public bool EnableCopilotConnector { get; set; } = false;
    public string CopilotApiBaseUrl { get; set; } = "https://graph.microsoft.com/beta";

    // Leaver pipeline: pull employeeLeaveDateTime (activates OFFBOARDED / OFFBOARDING_SCHEDULED
    // scoring). employeeLeaveDateTime is a PROTECTED property — selecting it without
    // User-LifeCycleInfo.Read.All 403s the whole /users call — so it stays OFF until the scope
    // is granted. Flip Prism__EnableLeaverDates=true after granting it.
    public bool EnableLeaverDates { get; set; } = false;

    // ---- Additional usage connectors (wave 10) -----------------------------
    public bool EnableTeamsActivityConnector { get; set; } = false;   // getTeamsUserActivityUserDetail (calls/meetings)
    public bool EnableServiceDetailConnector { get; set; } = false;   // mailbox + OneDrive + SharePoint detail
    public bool EnableAppHealthConnector     { get; set; } = false;   // Intune Endpoint Analytics App Health
    public bool EnableMobileAppsConnector    { get; set; } = false;   // Intune managed-app install summary
    public bool EnableSpSignInConnector      { get; set; } = false;   // Entra service-principal (enterprise app) sign-ins
    // Absolute base for the beta-only Graph endpoints used above (service-principal sign-ins).
    public string GraphBetaBaseUrl { get; set; } = "https://graph.microsoft.com/beta";

    // ---- Microsoft 365 Apps usage (m365.app-usage) -------------------------
    // getM365AppUserDetail: per-user Word/Excel/PowerPoint/Outlook/OneNote/Teams
    // last-activity + platform mix. Sharpens SHALLOW_USE. Reads Reports.Read.All;
    // honors report-name concealment like the service-usage connector.
    public bool EnableM365AppUsageConnector { get; set; } = false;

    // ---- Deleted-but-licensed users (in entra.license) ---------------------
    // Pull /directory/deletedItems users and their still-assigned (still-billing)
    // licences. Reads Directory.Read.All. Off by default.
    public bool IncludeDeletedUserLicenses { get; set; } = false;

    // ---- Pricing (ref.SkuCost via the negotiated Price Sheet API) ------
    // Prices are loaded from your Price Sheet, not hardcoded. Leave BillingAccountName
    // empty to skip (and maintain ref.SkuCost by hand). See PRICING.md.
    public string PricingAgreementType { get; set; } = "MCA";        // MCA | EA
    public string? BillingAccountName { get; set; }
    public string? BillingProfileName { get; set; }                  // MCA/MPA
    public string? BillingPeriodName { get; set; }                   // EA, e.g. 202606; empty = current
    public string PriceSheetApiVersion { get; set; } = "2023-09-01";
    // Maps a substring of the price-sheet product name to a Graph skuPartNumber.
    // Tune to YOUR price sheet's product naming. Unmapped rows are logged, not guessed.
    public Dictionary<string, string> PriceSheetProductMap { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft 365 E5"] = "SPE_E5",
        ["Microsoft 365 E3"] = "SPE_E3",
        ["Office 365 E3"] = "ENTERPRISEPACK",
        ["Office 365 E1"] = "STANDARDPACK",
        ["Microsoft 365 Business Premium"] = "SPB",
        ["Microsoft 365 Business Standard"] = "O365_BUSINESS_PREMIUM",
        ["Microsoft 365 Business Basic"] = "O365_BUSINESS_ESSENTIALS",
        ["Microsoft 365 F3"] = "SPE_F1",
        ["Microsoft 365 Copilot"] = "Microsoft_365_Copilot",
        ["Power BI Pro"] = "POWER_BI_PRO",
        ["Project Plan 3"] = "PROJECTPROFESSIONAL",
        ["Visio Plan 2"] = "VISIOCLIENT",
    };

    // Which connectors to run (by Name). Empty/null => run all registered connectors.
    public string[] Enabled { get; set; } = [];
}
