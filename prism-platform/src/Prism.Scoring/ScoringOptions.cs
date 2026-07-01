// ============================================================
//  ScoringOptions.cs  (Prism.Scoring)
//  Bound from the "Prism" section. Every threshold is tunable;
//  the defaults follow the 30/60/90 industry framework.
// ============================================================
namespace Prism.Scoring;

public sealed class ScoringOptions
{
    public const string SectionName = "Prism";

    // Azure SQL warehouse (secret-free Entra auth).
    public string ConnectionString { get; set; } = "";

    // 30/60/90 inactivity framework (days of no activity across all signals).
    public int WarnDays { get; set; } = 30;
    public int ReviewDays { get; set; } = 60;
    public int ReclaimDays { get; set; } = 90;

    // Score bands (0..100): >= ReclaimScore (+HIGH confidence) => RECLAIM; >= ReviewScore => REVIEW.
    public int ReviewScore { get; set; } = 40;
    public int ReclaimScore { get; set; } = 80;

    // Suppressors so we never flag the freshly-started or freshly-licensed.
    public int NewHireGraceDays { get; set; } = 30;
    public int RecentAssignmentGraceDays { get; set; } = 30;

    // Unassigned-seat tolerance: idle seats up to max(SeatBuffer, SeatBufferPercent% of
    // owned) => REVIEW (buffer); more => RECLAIM. The percentage matters at scale: a flat
    // 2-seat buffer on a 2,000-seat SKU would flag normal churn headroom as waste.
    public int SeatBuffer { get; set; } = 2;
    public double SeatBufferPercent { get; set; } = 2.0;

    // Leavers pipeline: a FUTURE employeeLeaveDateTime within this horizon flags the seat
    // for review now (OFFBOARDING_SCHEDULED), so the reclaim is planned, not discovered.
    public int OffboardingHorizonDays { get; set; } = 30;

    // "Never used since assignment": when the assignment is at least this old and NO
    // activity has ever been recorded on any signal, the dormancy evidence is the
    // entitlement age itself (score 92, still REVIEW/MEDIUM — absence of telemetry
    // never auto-reclaims).
    public int NeverUsedAssignmentAgeDays { get; set; } = 90;

    // Depth-of-use: an otherwise-active holder of a HighValue SKU whose M365 report shows
    // activity in at most one workload (of Teams/Exchange/OneDrive/SharePoint) in the
    // window gets SHALLOW_USE — a right-size (downgrade) candidate, never a reclaim.
    public bool EnableShallowUseFlag { get; set; } = true;
    public int ShallowUseWindowDays { get; set; } = 30;

    // Name fragments that hint at shared/service accounts (capped at REVIEW, never reclaimed).
    public string[] ServiceAccountPatterns { get; set; } =
        ["svc", "service", "shared", "noreply", "no-reply", "donotreply", "do-not-reply",
         "mailbox", "room", "equipment", "kiosk", "admin", "test"];

    // Premium SKUs: prioritised, and surfaced for a downgrade review when the user is active.
    public string[] HighValueSkus { get; set; } =
        ["SPE_E5", "ENTERPRISEPREMIUM", "Microsoft_365_Copilot", "PBI_PREMIUM_PER_USER", "PROJECTPREMIUM"];

    // Free / viral / trial plans cost nothing, so they can never be waste: they are
    // scored KEEP (reason FREE_SKU) and excluded from direct-assignment governance
    // (see the mirrored SQL filter in wave4-remediation.sql). Matched case-insensitively
    // as a CONTAINS test on the skuPartNumber; a €0 price in ref.SkuCost and a display
    // name containing "(free)" are treated as free as well.
    public string[] FreeSkuPatterns { get; set; } =
        ["FREE", "VIRAL", "TRIAL", "EXPLORATORY", "POWER_BI_STANDARD"];

    // SKUs whose use can be corroborated by a desktop executable (via the agent).
    public Dictionary<string, string[]> AppTiedSkus { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["VISIOCLIENT"]          = ["visio.exe"],
        ["PROJECTPROFESSIONAL"]  = ["winproj.exe"],
        ["PROJECTPREMIUM"]       = ["winproj.exe"],
        ["POWER_BI_PRO"]         = ["pbidesktop.exe"],
        ["PBI_PREMIUM_PER_USER"] = ["pbidesktop.exe"],
    };

    // ---- Wave 3.2: usage-based reclaim of app-tied add-ons -----------------
    // When the agent has reliably reported a device (>= AppUnusedMinCoverageDays
    // distinct reporting days in the 90-day window) yet the SKU's mapped exe never
    // ran, reclaim that add-on seat regardless of sign-in activity — the licensed
    // app itself is the usage truth ("holds Visio, 0 foreground in 90d").
    //
    // OFF by default: surface the candidates first via vw.LicenceUsage on real
    // pilot data, confirm they look right, then set Prism__EnableAppUnusedReclaim=true.
    public bool EnableAppUnusedReclaim { get; set; } = false;
    // Must match vw.AppUsageLast90's window. Informational unless the view is re-cut.
    public int AppUnusedReclaimDays { get; set; } = 90;
    // Trust gate: require this many distinct reporting days before "unused" => RECLAIM.
    // Below it, the device hasn't reported long enough to be sure (no escalation).
    public int AppUnusedMinCoverageDays { get; set; } = 21;

    // ---- Wave 6: install evidence (Intune detectedApps + Defender for Endpoint) ----
    // Install inventories disambiguate what usage telemetry cannot: the agent only
    // sees apps that RUN, so "no usage" could be installed-but-unused OR not-installed.
    // Standard SAM practice (Flexera/Snow/ServiceNow): reconcile entitlements against
    // installs AND usage. Signals produced:
    //  * APP_NOT_INSTALLED       seat assigned, title absent from ALL the user's
    //                            inventoried devices => strongest reclaim candidate.
    //  * INSTALL_CORROBORATED    installed AND the agent proved it unused — two
    //                            independent sources agree => higher confidence.
    //  * INSTALLED_NO_USAGE_TELEMETRY  installed but no agent coverage => context
    //                            for the review queue.
    // Absence is only treated as evidence when an inventory actually SEES the user's
    // devices (vw.UserInstallCoverage) — absence of telemetry never auto-reclaims.

    // SKU -> software-title fragments matched (contains, case-insensitive) against
    // install names from BOTH sources (Intune DisplayName, e.g. "Microsoft Visio
    // Professional 2021"; MDE name, e.g. "visio").
    public Dictionary<string, string[]> AppTiedSoftwareNames { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["VISIOCLIENT"]          = ["visio"],
        ["PROJECTPROFESSIONAL"]  = ["project"],
        ["PROJECTPREMIUM"]       = ["project"],
        ["POWER_BI_PRO"]         = ["power bi", "power_bi"],
        ["PBI_PREMIUM_PER_USER"] = ["power bi", "power_bi"],
    };

    // Names matching these fragments never count as an install of the licensed product
    // (the free Visio/Project VIEWERS are the classic false positive).
    public string[] InstallNegativePatterns { get; set; } = ["viewer"];

    // OFF by default (same pilot-first doctrine as EnableAppUnusedReclaim): while off,
    // APP_NOT_INSTALLED surfaces as a top-of-band REVIEW; switch on to auto-RECLAIM
    // after the candidates have been eyeballed on real data.
    public bool EnableNotInstalledReclaim { get; set; } = false;

    // ---- Wave 8: web/service usage (Entra per-app sign-ins) ----------------
    // For WEB/SERVICE-first SKUs the desktop signals (agent, MDE, M365-Apps report)
    // are blind — a Power BI Pro user who only opens the browser looks unused to all
    // of them. A recent sign-in to the SKU's service application is direct usage
    // evidence (=> KEEP); its absence on a sign-in-covered user corroborates unused.
    // SKU part number -> the service application id(s) whose sign-ins count as usage.
    public Dictionary<string, string[]> AppTiedSignInApps { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["POWER_BI_PRO"]         = ["00000009-0000-0000-c000-000000000000"],
        ["PBI_PREMIUM_PER_USER"] = ["00000009-0000-0000-c000-000000000000"],
        ["PROJECTPROFESSIONAL"]  = ["00000004-0000-0ff1-ce00-000000000000"],
        ["PROJECTPREMIUM"]       = ["00000004-0000-0ff1-ce00-000000000000"],
    };

    // Wave 8: when the M365 Apps report shows a high-value suite user touched ONLY
    // mail (Outlook) and none of Word/Excel/PowerPoint in the window, that strengthens
    // the SHALLOW_USE downgrade signal beyond the workload-level report.
    public bool EnableM365AppShallowUse { get; set; } = true;

    // ---- Wave 9: Microsoft 365 Copilot usage -------------------------------
    // The Copilot usage report is the only signal that sees Copilot seat use. These are the
    // SKU part numbers whose usage it measures; an enabled seat with no Copilot activity in
    // the window is the clearest, highest-value reclaim. Pilot-first like the other reclaim
    // flags: while EnableCopilotReclaim is off, an unused Copilot seat surfaces as a top-of-
    // band REVIEW; switch it on to auto-RECLAIM after eyeballing the candidates on real data.
    public string[] CopilotSkus { get; set; } = ["Microsoft_365_Copilot"];
    public bool EnableCopilotReclaim { get; set; } = false;

    // ---- Wave 10: Teams Phone usage -----------------------------------------
    // Teams Phone (calling-plan) SKUs are judged by CALL activity: a number that made/took
    // zero calls in the window is an unused phone seat. Pilot-first like the other reclaim
    // flags (EnableTeamsPhoneReclaim off => REVIEW; on => RECLAIM after vetting on real data).
    public string[] TeamsPhoneSkus { get; set; } = ["MCOEV", "PHONESYSTEM_VIRTUALUSER"];
    public bool EnableTeamsPhoneReclaim { get; set; } = false;
}
