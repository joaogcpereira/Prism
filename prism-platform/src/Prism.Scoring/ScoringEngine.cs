// ============================================================
//  ScoringEngine.cs  (Prism.Scoring)
//  Pure, side-effect-free scoring. Given the signals for one
//  (user, SKU) assignment, decide KEEP / REVIEW / RECLAIM with a
//  waste score (0..100), a confidence, reason codes, and the
//  monthly seat cost at stake.
//
//  Design (grounded in SAM/FinOps practice - see README):
//   * 30/60/90 inactivity framework: 30d warn, 60d review, 90d reclaim.
//   * Never trust one signal. "Active in ANY of {interactive sign-in,
//     non-interactive sign-in, M365 workload, the app itself}" = active.
//     We take the MOST RECENT activity across signals.
//   * RECLAIM only on EVIDENCE (a real, stale activity date, or a
//     deterministic state like disabled/offboarded) at HIGH confidence.
//     Absence of telemetry => REVIEW, never RECLAIM.
//   * Known false positives (shared mailboxes, service accounts) are
//     capped at REVIEW, never auto-reclaimed.
//   * A human always decides - this only triages and explains.
// ============================================================
namespace Prism.Scoring;

public static class Decision { public const string Keep = "KEEP", Review = "REVIEW", Reclaim = "RECLAIM"; }
public static class Conf     { public const string Low = "LOW", Medium = "MEDIUM", High = "HIGH"; }

public static class Reason
{
    public const string DisabledAccount         = "DISABLED_ACCOUNT";
    public const string Offboarded              = "OFFBOARDED";
    public const string CheckRetentionOrShared  = "CHECK_RETENTION_OR_SHARED";
    public const string NewHireGrace            = "NEW_HIRE_GRACE";
    public const string RecentlyAssigned        = "RECENTLY_ASSIGNED";
    public const string ServiceAccountSuspected = "SERVICE_ACCOUNT_SUSPECTED";
    public const string PossibleSharedOrService = "POSSIBLE_SHARED_OR_SERVICE";
    public const string AppInUse                = "APP_IN_USE";
    public const string AppUnused               = "APP_UNUSED";
    public const string AppUnused90             = "APP_UNUSED_90D";
    public const string NeverActive             = "NEVER_ACTIVE";
    public const string NoActivity30            = "NO_ACTIVITY_30D";
    public const string NoActivity60            = "NO_ACTIVITY_60D";
    public const string NoActivity90            = "NO_ACTIVITY_90D";
    public const string ActivityConcealed       = "ACTIVITY_CONCEALED";
    public const string HighValue               = "HIGH_VALUE";
    public const string GroupAssigned           = "GROUP_ASSIGNED";   // retained for back-compat; no longer emitted
    public const string DirectAssignment        = "DIRECT_ASSIGNMENT";
    public const string AssignmentError         = "ASSIGNMENT_ERROR"; // licence assigned but provisioning failed
    public const string ServicePlansDisabled    = "SERVICE_PLANS_DISABLED"; // one or more plans in the SKU are turned off (often deliberate)
    public const string UnassignedSeats         = "UNASSIGNED_SEATS";
    public const string SeatBuffer              = "SEAT_BUFFER";
    public const string FreeSku                 = "FREE_SKU";
    public const string OffboardingScheduled    = "OFFBOARDING_SCHEDULED";
    public const string NeverUsedSinceAssignment= "NEVER_USED_SINCE_ASSIGNMENT";
    public const string ShallowUse              = "SHALLOW_USE";
    public const string LimitedTelemetry        = "LIMITED_TELEMETRY";
    // Wave 6: install evidence (Intune detectedApps + Defender for Endpoint).
    public const string AppNotInstalled         = "APP_NOT_INSTALLED";
    public const string AppInstalled            = "APP_INSTALLED";
    public const string InstallCorroborated     = "INSTALL_CORROBORATED";
    public const string InstalledNoUsageTelemetry = "INSTALLED_NO_USAGE_TELEMETRY";
    // Wave 7: Defender Advanced Hunting process-run telemetry (30-day window).
    public const string AppInUseMde             = "APP_IN_USE_MDE";
    public const string MdeNoRun30              = "MDE_NO_RUN_30D";
    // Wave 8: Entra per-app sign-ins (web/service usage) + M365 Apps report.
    public const string AppInUseWeb             = "APP_IN_USE_WEB";
    public const string WebNoSignIn             = "WEB_NO_SIGNIN";
    public const string OfficeAppsUnused        = "OFFICE_APPS_UNUSED";
    // Wave 9: Microsoft 365 Copilot usage report.
    public const string CopilotInUse            = "COPILOT_IN_USE";
    public const string CopilotUnused           = "COPILOT_UNUSED";
    public const string CopilotSingleSurface    = "COPILOT_SINGLE_SURFACE"; // used, but in one host app only - adoption/right-size conversation
    // Wave 10: Teams Phone calling usage.
    public const string PhoneInUse              = "PHONE_IN_USE";
    public const string PhoneNoCalls            = "PHONE_NO_CALLS";
    // v2: deterministic mailbox purpose (replaces the name heuristic when present).
    public const string SharedMailbox           = "SHARED_MAILBOX";          // userPurpose=shared - convert, don't cut
    public const string ResourceMailbox         = "RESOURCE_MAILBOX";        // userPurpose=room/equipment
    // v2: real PSTN call-detail records corroborate the Teams Phone verdict.
    public const string PstnNoCalls             = "PSTN_NO_CALLS";
    // v2: identity-lifecycle evidence.
    public const string GuestWithPaidSku        = "GUEST_WITH_PAID_SKU";     // paid seat on a guest account - governance review
    public const string MfaNeverRegistered      = "MFA_NEVER_REGISTERED";    // never onboarded: positive corroboration for NEVER_ACTIVE
    public const string OnLeaveSuspected        = "ON_LEAVE_SUSPECTED";      // auto-reply enabled - do NOT reclaim a leave-of-absence seat
}

/// <summary>Resolved desktop-app signal for the SKU's mapped executables (null if not app-tied or no agent coverage).</summary>
public sealed record AppSignal(bool Used, int? DaysSinceLast, int CoverageDays);

/// <summary>Resolved install evidence for the SKU's mapped software titles, fused from
/// Intune detectedApps and Defender for Endpoint (null if not app-tied or no inventory data).
/// <paramref name="AbsenceTrustworthy"/> is true only when an inventory actually sees the
/// user's devices - without it, "not installed" is missing data, not evidence.</summary>
public sealed record InstallSignal(bool Installed, int Sources, int DeviceCount, bool AbsenceTrustworthy);

/// <summary>Resolved Defender Advanced Hunting run telemetry for the SKU's mapped
/// executables (30-day window; null if not app-tied, no hunting data, or the user has
/// no MDE-visible devices - in which case "no runs" would be missing data, not evidence).</summary>
public sealed record MdeRunSignal(bool Ran, int? DaysSinceLast, int RunDays);

/// <summary>Resolved Entra per-app sign-in signal for a WEB/SERVICE-first SKU (null if the
/// SKU isn't sign-in-mapped or there's no sign-in data). Direct evidence of service usage.</summary>
public sealed record SignInSignal(bool SignedIn, int? DaysSinceLast, bool HasCoverage);

/// <summary>Resolved M365 Apps report signal: did the user touch any of Word/Excel/PowerPoint
/// (not just mail) recently (null if no non-concealed report row for the user).</summary>
public sealed record OfficeAppsSignal(bool UsedCoreApps, bool MailOnly);

/// <summary>Resolved Microsoft 365 Copilot usage signal (null if the SKU isn't a Copilot SKU
/// or the Copilot report didn't run). HasReport=true means the report ran and is authoritative,
/// so Used=false is real evidence of an idle Copilot seat - the clearest, priciest reclaim.
/// <paramref name="Surfaces"/> counts the distinct Copilot host apps touched (depth of use).</summary>
public sealed record CopilotSignal(bool Used, int? DaysSinceLast, bool HasReport, int Surfaces = 0);

/// <summary>Resolved Teams Phone usage signal (null if the SKU isn't a Teams Phone SKU or the
/// Teams report didn't run). CallCount in the window is the authoritative phone-seat usage.</summary>
public sealed record TeamsActivitySignal(int CallCount, int MeetingCount, DateTime? LastActivity, bool HasReport);

/// <summary>v2: resolved PSTN call-detail signal for a Teams Phone SKU (null if the SKU isn't
/// phone-tied or the PSTN connector didn't run). These are REAL calls to/from the public
/// network - stronger than the Teams report's CallCount, which also counts VoIP.</summary>
public sealed record PstnSignal(int CallCount, long TotalSeconds, int? DaysSinceLast, bool HasData);

public sealed record AssignmentVerdict(
    string Decision, int Score, string Confidence, List<string> Reasons,
    int? InactiveDays, decimal? MonthlySavings, int SignalCount = 0, string? EvidenceJson = null);

public sealed record SkuSeatVerdict(string Decision, List<string> Reasons, decimal? MonthlySavings);

public static class ScoringEngine
{
    /// <summary>
    /// Score one (user, SKU) seat. v2: the verdict now carries its own EVIDENCE TRAIL -
    /// a compact JSON of every signal consulted plus the count of independent sources -
    /// so a reviewer can see exactly WHY in one glance, and an auditor can replay the
    /// decision months later. The trail is attached uniformly to every verdict path.
    /// </summary>
    public static AssignmentVerdict ScoreAssignment(
        SignalRow r, AppSignal? app, InstallSignal? install, MdeRunSignal? mdeRun,
        SignInSignal? webSignIn, OfficeAppsSignal? office, CopilotSignal? copilot, TeamsActivitySignal? teams,
        PstnSignal? pstn, decimal? unitCost, string currency, ScoringOptions o, DateTime nowUtc)
    {
        AssignmentVerdict v = Core(r, app, install, mdeRun, webSignIn, office, copilot, teams, pstn, unitCost, currency, o, nowUtc);
        return v with
        {
            SignalCount = CountSignals(r, app, install, mdeRun, webSignIn, copilot, teams, pstn),
            EvidenceJson = BuildEvidence(r, app, install, mdeRun, webSignIn, copilot, teams, pstn, nowUtc),
        };
    }

    /// <summary>How many INDEPENDENT sources actually contributed data for this seat.
    /// Confidence should scale with corroboration, not with the loudness of one feed.</summary>
    private static int CountSignals(SignalRow r, AppSignal? app, InstallSignal? install, MdeRunSignal? mdeRun,
        SignInSignal? webSignIn, CopilotSignal? copilot, TeamsActivitySignal? teams, PstnSignal? pstn)
    {
        int n = 0;
        if (r.LastSignInDateTime is not null || r.LastNonInteractiveSignInDateTime is not null
            || r.LastSuccessfulSignInDateTime is not null) n++;                     // Entra sign-in activity
        if (r.M365ReportRefreshDate is not null) n++;                               // M365 usage report row
        if (app is not null) n++;                                                   // desktop agent
        if (install is not null) n++;                                               // install inventory (Intune/MDE)
        if (mdeRun is not null) n++;                                                // Defender hunting
        if (webSignIn is not null) n++;                                             // per-app sign-ins
        if (copilot is { HasReport: true }) n++;                                    // Copilot report
        if (teams is { HasReport: true }) n++;                                      // Teams activity report
        if (pstn is { HasData: true }) n++;                                         // PSTN call records
        if (r.MailboxPurpose is not null) n++;                                      // mailbox settings
        if (r.IsMfaRegistered is not null) n++;                                     // auth-method registration
        return n;
    }

    /// <summary>Compact JSON evidence trail (dashboard drawer + audit export). Values are
    /// day-deltas / flags, not raw payloads; sections with no data are omitted entirely so
    /// "absent" is visibly different from "present and negative".</summary>
    private static string BuildEvidence(SignalRow r, AppSignal? app, InstallSignal? install, MdeRunSignal? mdeRun,
        SignInSignal? webSignIn, CopilotSignal? copilot, TeamsActivitySignal? teams, PstnSignal? pstn, DateTime nowUtc)
    {
        var sb = new System.Text.StringBuilder(256);
        sb.Append('{');
        int? di = DaysSince(r.LastSignInDateTime, nowUtc);
        int? dn = DaysSince(r.LastNonInteractiveSignInDateTime, nowUtc);
        int? ds = DaysSince(r.LastSuccessfulSignInDateTime, nowUtc);
        if (di is not null || dn is not null || ds is not null)
            Section("signin").Append("{\"i\":").Append(N(di)).Append(",\"ni\":").Append(N(dn)).Append(",\"ok\":").Append(N(ds)).Append('}');
        if (r.M365ReportRefreshDate is not null)
            Section("m365").Append("{\"any\":").Append(N(DaysSince(r.M365LastActivityDate, nowUtc)))
              .Append(",\"teams\":").Append(N(DaysSince(r.TeamsLastActivityDate, nowUtc)))
              .Append(",\"mail\":").Append(N(DaysSince(r.ExchangeLastActivityDate, nowUtc)))
              .Append(",\"od\":").Append(N(DaysSince(r.OneDriveLastActivityDate, nowUtc)))
              .Append(",\"sp\":").Append(N(DaysSince(r.SharePointLastActivityDate, nowUtc))).Append('}');
        if (app is not null)
            Section("app").Append("{\"used\":").Append(B(app.Used)).Append(",\"d\":").Append(N(app.DaysSinceLast))
              .Append(",\"cov\":").Append(app.CoverageDays).Append('}');
        if (install is not null)
            Section("install").Append("{\"has\":").Append(B(install.Installed)).Append(",\"src\":").Append(install.Sources)
              .Append(",\"trust\":").Append(B(install.AbsenceTrustworthy)).Append('}');
        if (mdeRun is not null)
            Section("mde").Append("{\"ran\":").Append(B(mdeRun.Ran)).Append(",\"d\":").Append(N(mdeRun.DaysSinceLast)).Append('}');
        if (webSignIn is not null)
            Section("web").Append("{\"in\":").Append(B(webSignIn.SignedIn)).Append(",\"d\":").Append(N(webSignIn.DaysSinceLast)).Append('}');
        if (copilot is { HasReport: true })
            Section("copilot").Append("{\"used\":").Append(B(copilot.Used)).Append(",\"d\":").Append(N(copilot.DaysSinceLast))
              .Append(",\"surf\":").Append(copilot.Surfaces).Append('}');
        if (teams is { HasReport: true })
            Section("phone").Append("{\"calls\":").Append(teams.CallCount).Append(",\"meet\":").Append(teams.MeetingCount).Append('}');
        if (pstn is { HasData: true })
            Section("pstn").Append("{\"calls\":").Append(pstn.CallCount).Append(",\"d\":").Append(N(pstn.DaysSinceLast)).Append('}');
        if (r.MailboxPurpose is not null) Section("mbx").Append('"').Append(San(r.MailboxPurpose)).Append('"');
        if (r.MailboxAutoReply is not null and not "disabled") Section("oof").Append('"').Append(San(r.MailboxAutoReply)).Append('"');
        if (r.IsMfaRegistered is not null) Section("mfa").Append(B(r.IsMfaRegistered.Value));
        if (string.Equals(r.UserType, "Guest", StringComparison.OrdinalIgnoreCase)) Section("guest").Append("true");
        if (r.DisabledPlanCount > 0) Section("plansOff").Append(r.DisabledPlanCount);
        sb.Append('}');
        string json = sb.ToString();
        return json.Length <= 2000 ? json : json[..2000];   // column-width guard (valid-prefix truncation acceptable for display)

        System.Text.StringBuilder Section(string name)
        {
            if (sb.Length > 1) sb.Append(',');
            return sb.Append('"').Append(name).Append("\":");
        }
        static string N(int? v) => v?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null";
        static string B(bool v) => v ? "true" : "false";
        static string San(string s) => s.Replace("\\", "").Replace("\"", "");
    }

    private static AssignmentVerdict Core(
        SignalRow r, AppSignal? app, InstallSignal? install, MdeRunSignal? mdeRun,
        SignInSignal? webSignIn, OfficeAppsSignal? office, CopilotSignal? copilot, TeamsActivitySignal? teams,
        PstnSignal? pstn, decimal? unitCost, string currency, ScoringOptions o, DateTime nowUtc)
    {
        var reasons = new List<string>();

        // Free / viral / trial plans cost nothing - reclaiming one saves nothing, so
        // flagging it is pure queue noise. KEEP at high confidence with an explaining
        // reason, before any other signal is considered.
        if (IsFreeSku(r.SkuPartNumber, r.SkuName, unitCost, o))
        {
            reasons.Add(Reason.FreeSku);
            if (r.AssignedDirectly == true) reasons.Add(Reason.DirectAssignment);
            return new(Decision.Keep, 0, Conf.High, reasons, null, null);
        }

        // Group-based licensing is Contoso's standard operating model, so it is NOT an
        // anomaly and must not be flagged or scored. The deviation worth surfacing is a
        // DIRECT assignment (bypassing group membership) - recorded as an informational
        // reason here and surfaced in its own governance widget (vw.DirectAssignments).
        // It does not alter the waste score or verdict.
        if (r.AssignedDirectly == true) reasons.Add(Reason.DirectAssignment);

        // ---- deterministic states (not telemetry) => high confidence ----
        if (r.AccountEnabled == false)
        {
            reasons.Add(Reason.DisabledAccount);
            reasons.Add(Reason.CheckRetentionOrShared);
            AddHighValue(r, o, reasons);
            return new(Decision.Reclaim, 90, Conf.High, reasons, null, unitCost);
        }
        if (r.EmployeeLeaveDateTime is { } leave && leave < nowUtc)
        {
            reasons.Add(Reason.Offboarded);
            reasons.Add(Reason.CheckRetentionOrShared);
            AddHighValue(r, o, reasons);
            return new(Decision.Reclaim, 95, Conf.High, reasons, DaysSince(leave, nowUtc), unitCost);
        }
        // Leavers pipeline: a scheduled leave date inside the horizon makes the seat a
        // PLANNED reclaim - review it now so the licence is recovered on the leave date
        // instead of being rediscovered 90 days later. Checked before the grace guards:
        // a short-term contractor can be both recently assigned AND about to leave.
        if (r.EmployeeLeaveDateTime is { } leaving && leaving >= nowUtc && leaving <= nowUtc.AddDays(o.OffboardingHorizonDays))
        {
            reasons.Add(Reason.OffboardingScheduled);
            AddHighValue(r, o, reasons);
            return new(Decision.Review, 75, Conf.High, reasons, null, unitCost);
        }

        // ---- suppressors: don't flag the freshly-started or freshly-licensed ----
        bool hireFuture  = r.EmployeeHireDate is { } h0 && h0 > nowUtc;
        bool hireRecent  = r.EmployeeHireDate is { } h1 && h1 > nowUtc.AddDays(-o.NewHireGraceDays);
        bool createdNew  = r.CreatedDateTime  is { } c  && c  > nowUtc.AddDays(-o.NewHireGraceDays);
        if (hireFuture || hireRecent || createdNew)
        {
            reasons.Add(Reason.NewHireGrace);
            return new(Decision.Keep, 5, Conf.Medium, reasons, null, null);
        }
        if (r.AssignmentLastUpdatedDateTime is { } a && a > nowUtc.AddDays(-o.RecentAssignmentGraceDays))
        {
            reasons.Add(Reason.RecentlyAssigned);
            return new(Decision.Keep, 10, Conf.Medium, reasons, null, null);
        }

        // ---- activity signals (most-recent wins) ----
        int? dInteractive = DaysSince(r.LastSignInDateTime, nowUtc);
        int? dNonInter    = DaysSince(r.LastNonInteractiveSignInDateTime, nowUtc);
        int? dSuccessful  = DaysSince(r.LastSuccessfulSignInDateTime, nowUtc);   // last successful auth of any kind
        int? dM365        = DaysSince(r.M365LastActivityDate, nowUtc);
        int? dApp         = app?.DaysSinceLast;

        // "Active in ANY of {interactive, non-interactive, M365 workload, app}" (most-recent
        // wins). A non-interactive sign-in is the user's own client (Outlook, Teams, a browser
        // session, a mobile app) silently accessing a licensed service on their behalf, so it
        // is real evidence the licence is in use and must lower the waste score. Suspected
        // shared/service accounts are still capped to REVIEW below (serviceSuspect), and
        // app-tied add-ons are still judged by the app itself, not general sign-in.
        int? eff = Min(dInteractive, dNonInter, dSuccessful, dM365, dApp); // interactive + non-interactive + successful + workload + app
        bool concealed = r.M365ActivityConcealed == true;

        // A licence assignment in an error state (provisioning failed - service-plan conflict,
        // insufficient seats, etc.) consumes the seat without delivering the service.
        // "ActiveWithError" = some plans provisioned, some failed. Record the reason now (after
        // the new-hire/recent-assignment grace, so a still-provisioning seat isn't flagged) so
        // it surfaces on EVERY downstream path, including the active-keep branches below; the
        // verdict is only escalated to REVIEW further down, and only if the seat is still KEEP.
        bool assignmentErrored =
            r.AssignmentState is { } astate &&
            (astate.Equals("Error", StringComparison.OrdinalIgnoreCase)
             || astate.Equals("ActiveWithError", StringComparison.OrdinalIgnoreCase));
        if (assignmentErrored) reasons.Add(Reason.AssignmentError);
        if (r.DisabledPlanCount > 0) reasons.Add(Reason.ServicePlansDisabled);   // informational tag; does not change the verdict

        // App-tied SKU actively used => strong keep (the licensed thing is running).
        if (app is { Used: true } && dApp is { } da && da < o.WarnDays)
        {
            reasons.Add(Reason.AppInUse);
            AddHighValue(r, o, reasons);
            return new(Decision.Keep, 15, Conf.High, reasons, eff, null);
        }

        // Wave 7: no agent signal, but Defender Advanced Hunting saw the licensed exe
        // START recently on the user's devices => the seat is in use. A launch is
        // weaker evidence of ENGAGEMENT than the agent's foreground time, but it is
        // direct evidence of USE - industry-standard "launch = usage" (Flexera/Snow).
        // Agent evidence outranks hunting, so this only fires when the agent has no
        // coverage (app is null) for this user.
        if (app is null && mdeRun is { Ran: true, DaysSinceLast: { } dr } && dr < o.WarnDays)
        {
            reasons.Add(Reason.AppInUseMde);
            AddHighValue(r, o, reasons);
            return new(Decision.Keep, 18, Conf.High, reasons, eff, null);
        }

        // Wave 8: WEB/SERVICE-first SKU used in the browser. For Power BI Pro / Project
        // web the desktop signals are structurally blind, so a recent sign-in to the
        // SKU's service application is the PRIMARY usage evidence => KEEP. This is what
        // prevents false reclaims of heavy browser-only users.
        if (app is not { Used: true } && webSignIn is { SignedIn: true, DaysSinceLast: { } dw } && dw < o.WarnDays)
        {
            reasons.Add(Reason.AppInUseWeb);
            AddHighValue(r, o, reasons);
            return new(Decision.Keep, 18, Conf.High, reasons, eff, null);
        }

        // Wave 9: Microsoft 365 Copilot actively used (any host app) => the (expensive) seat is
        // in use. The Copilot usage report is the only signal that sees this, so it's authoritative.
        // v2 depth: used in a SINGLE host app only => tag for an adoption/right-size conversation
        // (the seat is kept either way; the tag guides enablement, not reclamation).
        if (copilot is { Used: true, DaysSinceLast: { } dcop } && dcop < o.WarnDays)
        {
            reasons.Add(Reason.CopilotInUse);
            if (copilot.Surfaces == 1) reasons.Add(Reason.CopilotSingleSurface);
            AddHighValue(r, o, reasons);
            return new(Decision.Keep, 15 + (copilot.Surfaces <= 1 ? 5 : 0), Conf.High, reasons, eff, null);
        }

        // Wave 10: Teams Phone seat with calls in the window => the number is in use => KEEP.
        // v2: REAL PSTN call-detail records count too (and outrank the Teams report, which
        // also counts VoIP-only usage a phone SKU isn't needed for - so PSTN>0 is decisive,
        // while TeamsReport>0 with PSTN=0 keeps the seat but stays informative).
        if (teams is { CallCount: > 0 } || pstn is { CallCount: > 0 })
        {
            reasons.Add(Reason.PhoneInUse);
            AddHighValue(r, o, reasons);
            return new(Decision.Keep, 18, Conf.High, reasons, eff, null);
        }

        // Service / shared-account handling. v2: when the mailbox connector ran, userPurpose
        // is DETERMINISTIC - a 'shared'/'room'/'equipment' mailbox is a fact, not a name-shape
        // guess. These are conversion candidates (block sign-in, drop the license, convert to
        // a proper shared/resource mailbox), never blind reclaims => cap at REVIEW.
        bool sharedMailbox   = string.Equals(r.MailboxPurpose, "shared", StringComparison.OrdinalIgnoreCase);
        bool resourceMailbox = r.MailboxPurpose is { } mp
                               && (mp.Equals("room", StringComparison.OrdinalIgnoreCase)
                                   || mp.Equals("equipment", StringComparison.OrdinalIgnoreCase));
        bool serviceSuspect = (dInteractive is null || dInteractive >= o.ReviewDays)
                              && dNonInter is { } ni && ni < o.WarnDays;     // app/non-interactive only
        // The name heuristic stays as the fallback for tenants without the mailbox connector,
        // but a deterministic 'user' purpose OVERRIDES a suspicious-looking name (fewer false flags).
        bool nameSuspect = !string.Equals(r.MailboxPurpose, "user", StringComparison.OrdinalIgnoreCase)
                          && (MatchesAny(r.UserPrincipalName, o.ServiceAccountPatterns)
                              || MatchesAny(r.DisplayName, o.ServiceAccountPatterns));
        if (sharedMailbox)   reasons.Add(Reason.SharedMailbox);
        if (resourceMailbox) reasons.Add(Reason.ResourceMailbox);
        if (serviceSuspect) reasons.Add(Reason.ServiceAccountSuspected);
        if (nameSuspect && !sharedMailbox && !resourceMailbox) reasons.Add(Reason.PossibleSharedOrService);
        bool capReview = serviceSuspect || nameSuspect || sharedMailbox || resourceMailbox;

        // v2 leave guard: an enabled auto-reply is how real people signal absence (parental
        // leave, sabbatical, long sick leave). Reclaiming a leave-of-absence seat is the most
        // damaging false positive there is - Microsoft's own guidance says to account for
        // leave - so an active OOF BLOCKS the reclaim band (review is still fine: a human can
        // confirm the leave and snooze until return).
        bool onLeave = r.MailboxAutoReply is { } ar
                       && (ar.Equals("alwaysEnabled", StringComparison.OrdinalIgnoreCase)
                           || ar.Equals("scheduled", StringComparison.OrdinalIgnoreCase));

        // ---- Wave 6: assigned but NOT INSTALLED on any inventoried device ----
        // The strongest waste signal in SAM practice: the seat is paid for, yet the
        // software is absent from every device either inventory (Intune detectedApps,
        // Defender for Endpoint) can see for this user - you can't use what isn't there.
        // Gates: absence must be TRUSTWORTHY (an inventory actually sees the user's
        // devices), and the agent must not have seen the app run (usage outranks
        // inventory, which can lag installs by a day). Pilot-first: REVIEW at the top
        // of the band until EnableNotInstalledReclaim is switched on.
        if (install is { Installed: false, AbsenceTrustworthy: true } && app is not { Used: true }
            && mdeRun is not { Ran: true } && webSignIn is not { SignedIn: true })   // any observed use contradicts absence
        {
            reasons.Add(Reason.AppNotInstalled);
            AddHighValue(r, o, reasons);
            return (o.EnableNotInstalledReclaim && !capReview)
                ? new(Decision.Reclaim, 95, Conf.High,   reasons, null, unitCost)
                : new(Decision.Review,  88, Conf.Medium, reasons, null, unitCost);
        }

        // ---- Wave 3.2: app-tied add-on provably unused on a reliably-reporting device ----
        // The licensed desktop app never ran in the 90-day window even though the agent
        // reported this device on enough distinct days to trust that (coverage gate). The
        // licensed thing itself is the usage truth, so reclaim THIS add-on seat even if the
        // user is otherwise active - general sign-in is not the same as using Visio/Project.
        // Suspected shared/service accounts are still only flagged for review, never reclaimed.
        // Wave 6: when an install inventory ALSO shows the title present, two independent
        // sources agree (installed + never runs) => corroborated, score nudged up.
        if (o.EnableAppUnusedReclaim && app is { Used: false } && app.CoverageDays >= o.AppUnusedMinCoverageDays
            && mdeRun is not { Ran: true } && webSignIn is not { SignedIn: true })   // no desktop OR web use seen
        {
            reasons.Add(Reason.AppUnused);
            reasons.Add(Reason.AppUnused90);
            bool corroborated = install is { Installed: true };
            if (corroborated) reasons.Add(Reason.InstallCorroborated);
            if (mdeRun is { Ran: false }) reasons.Add(Reason.MdeNoRun30);   // 3rd independent source agrees
            if (webSignIn is { SignedIn: false }) reasons.Add(Reason.WebNoSignIn);
            AddHighValue(r, o, reasons);
            return capReview
                ? new(Decision.Review,  72, Conf.Medium, reasons, null, unitCost)
                : new(Decision.Reclaim, corroborated ? 95 : 92, Conf.High, reasons, null, unitCost);
        }

        // ---- Wave 9: Copilot seat with the usage report present but NO Copilot activity ----
        // The clearest, highest-value reclaim signal (Copilot is the priciest per-seat SKU).
        // The report is authoritative, so absence of activity IS evidence. Pilot-first: REVIEW
        // until EnableCopilotReclaim is on; suspected shared/service accounts stay REVIEW.
        // v2: an on-leave holder (active OOF) is never auto-reclaimed either.
        if (copilot is { Used: false, HasReport: true })
        {
            reasons.Add(Reason.CopilotUnused);
            if (onLeave) reasons.Add(Reason.OnLeaveSuspected);
            AddHighValue(r, o, reasons);
            return (o.EnableCopilotReclaim && !capReview && !onLeave)
                ? new(Decision.Reclaim, 95, Conf.High,   reasons, eff, unitCost)
                : new(Decision.Review,  85, Conf.Medium, reasons, eff, unitCost);
        }

        // ---- Wave 10: Teams Phone seat with the report present but ZERO calls ----
        // An unused calling-plan seat. Pilot-first; suspected shared/service accounts stay REVIEW.
        // v2: when the PSTN connector ALSO ran and shows zero real calls, two independent
        // sources agree - the corroborated case scores higher and (once the reclaim flag is
        // on) carries HIGH confidence; the Teams-report-only case keeps the old shape.
        if (teams is { CallCount: 0, HasReport: true } || (teams is null && pstn is { HasData: true, CallCount: 0 }))
        {
            reasons.Add(Reason.PhoneNoCalls);
            bool pstnAgrees = pstn is { HasData: true, CallCount: 0 };
            if (pstnAgrees) reasons.Add(Reason.PstnNoCalls);
            if (onLeave) reasons.Add(Reason.OnLeaveSuspected);
            AddHighValue(r, o, reasons);
            return (o.EnableTeamsPhoneReclaim && !capReview && !onLeave)
                ? new(Decision.Reclaim, pstnAgrees ? 93 : 90, Conf.High,   reasons, eff, unitCost)
                : new(Decision.Review,  pstnAgrees ? 83 : 80, Conf.Medium, reasons, eff, unitCost);
        }

        // ---- inactivity score ----
        int score;
        string conf;
        bool inactivityDriven;
        if (eff is null)
        {
            // No positive activity evidence. Could be a genuinely dormant seat OR
            // missing telemetry (sign-in data needs P1 + post-2020 sign-ins). Be
            // conservative: flag, but only MEDIUM confidence => REVIEW, not RECLAIM.
            // HOWEVER, entitlement age is itself evidence: an assignment older than
            // NeverUsedAssignmentAgeDays that has NEVER shown activity on any signal
            // is ranked near the top of the review band (still never auto-reclaimed).
            bool longAssigned = r.AssignmentLastUpdatedDateTime is { } aged
                                && aged <= nowUtc.AddDays(-o.NeverUsedAssignmentAgeDays);
            score = longAssigned ? 92 : 88;
            conf = Conf.Medium;
            inactivityDriven = true;
            reasons.Add(Reason.NeverActive);
            if (longAssigned) reasons.Add(Reason.NeverUsedSinceAssignment);
            // v2: never-registered MFA/SSPR is POSITIVE evidence the account was never
            // onboarded (not merely un-telemetried) - it ranks the seat at the very top of
            // the review band. Doctrine holds: absence still never auto-reclaims, so the
            // confidence stays MEDIUM and the band stays REVIEW.
            if (r.IsMfaRegistered == false)
            {
                reasons.Add(Reason.MfaNeverRegistered);
                score = Math.Max(score, 95);
            }
        }
        else
        {
            score = ScoreFromDays(eff.Value, o);
            conf = Conf.High;
            inactivityDriven = score >= o.ReviewScore;
            if (eff.Value >= o.ReclaimDays) reasons.Add(Reason.NoActivity90);
            else if (eff.Value >= o.ReviewDays) reasons.Add(Reason.NoActivity60);
            else if (eff.Value >= o.WarnDays) reasons.Add(Reason.NoActivity30);

            // Multi-signal corroboration: when the ONLY activity evidence is the M365
            // usage report (no Entra sign-in dates at all, no agent), the UPN-joined
            // report is too weak to carry a HIGH-confidence reclaim on its own.
            if (inactivityDriven && dInteractive is null && r.LastNonInteractiveSignInDateTime is null && dApp is null)
            {
                reasons.Add(Reason.LimitedTelemetry);
                if (conf == Conf.High) conf = Conf.Medium;
            }
        }

        // App-tied but unused while otherwise inactive => extra weight.
        if (app is { Used: false } && eff is { } e2 && e2 >= o.ReviewDays)
        {
            score = Math.Min(100, score + 10);
            reasons.Add(Reason.AppUnused);
            inactivityDriven = true;
        }

        // Wave 6 enrichment: the title IS installed (per Intune/MDE) but the agent has no
        // coverage of this user's devices, and the seat is otherwise inactivity-flagged.
        // Purely informational - tells the reviewer "the software is deployed; the missing
        // piece is usage telemetry", which directs the follow-up (check with the user /
        // extend agent coverage) without changing the verdict.
        if (install is { Installed: true } && app is null && inactivityDriven)
        {
            reasons.Add(Reason.AppInstalled);
            reasons.Add(Reason.InstalledNoUsageTelemetry);
            // Wave 7: hunting fills exactly this gap - the title is installed, the agent
            // can't see the device, but Defender CAN, and it saw zero launches in 30 days.
            // Escalate within REVIEW (a 30d window never auto-reclaims under the 90d doctrine).
            if (mdeRun is { Ran: false })
            {
                reasons.Add(Reason.MdeNoRun30);
                score = Math.Min(100, score + 8);
            }
        }

        // Concealed M365 names weaken our certainty.
        if (concealed)
        {
            reasons.Add(Reason.ActivityConcealed);
            if (dInteractive is null && dApp is null) conf = Conf.Low;
            else if (conf == Conf.High) conf = Conf.Medium;
        }

        // Tag premium SKUs for prioritisation (an idle E5 outranks an idle E1 by savings).
        // Note: we deliberately do NOT blanket-flag active premium users for downgrade -
        // a tier-downgrade decision needs advanced-feature telemetry we don't collect, and
        // over-flagging would bury the genuinely actionable items.
        AddHighValue(r, o, reasons);

        // v2 leave guard applied to the inactivity path: an active auto-reply blocks the
        // reclaim band (see the flag's rationale above) and is surfaced as a reason so the
        // reviewer immediately sees WHY an otherwise-reclaimable seat is only REVIEW.
        if (onLeave && inactivityDriven && !reasons.Contains(Reason.OnLeaveSuspected))
            reasons.Add(Reason.OnLeaveSuspected);

        // ---- banding ----
        string decision =
            (score >= o.ReclaimScore && conf == Conf.High && !capReview && !onLeave) ? Decision.Reclaim
            : (score >= o.ReviewScore || capReview)                                  ? Decision.Review
            : Decision.Keep;

        // Depth-of-use (right-size, not reclaim): an otherwise-ACTIVE holder of a
        // premium SKU whose usage report shows at most one active workload in the
        // window is paying suite money for single-workload use - surface for a
        // downgrade conversation. Needs a present, non-concealed report to judge.
        if (o.EnableShallowUseFlag && decision == Decision.Keep
            && eff is { } e3 && e3 < o.WarnDays
            && !concealed && r.M365ReportRefreshDate is not null
            && o.HighValueSkus.Contains(r.SkuPartNumber ?? "", StringComparer.OrdinalIgnoreCase)
            && CountRecent(nowUtc, o.ShallowUseWindowDays,
                   r.TeamsLastActivityDate, r.ExchangeLastActivityDate,
                   r.OneDriveLastActivityDate, r.SharePointLastActivityDate) <= 1)
        {
            reasons.Add(Reason.ShallowUse);
            decision = Decision.Review;
            score = Math.Max(score, 50);
            if (conf == Conf.High) conf = Conf.Medium;   // downgrade hint, verify with the user
        }

        // Wave 8 refinement: the M365 Apps report shows this suite user touched ONLY mail
        // (Outlook) and none of Word/Excel/PowerPoint in the window - concrete corroboration
        // that an E3/E5 productivity suite is being used like a mailbox. Strengthens the
        // downgrade case (adds the reason; nudges score) without auto-reclaiming.
        if (o.EnableM365AppShallowUse && decision != Decision.Keep
            && office is { MailOnly: true }
            && o.HighValueSkus.Contains(r.SkuPartNumber ?? "", StringComparer.OrdinalIgnoreCase))
        {
            if (!reasons.Contains(Reason.OfficeAppsUnused)) reasons.Add(Reason.OfficeAppsUnused);
            score = Math.Min(100, score + 6);
        }

        // v2 governance: a PAID seat on a GUEST account deserves a look regardless of
        // activity shape - guests routinely keep licenses long after an engagement ends,
        // and most guest scenarios need no paid seat at all. Active guests are only
        // tagged; inactive-ish ones are escalated into the review queue.
        if (string.Equals(r.UserType, "Guest", StringComparison.OrdinalIgnoreCase))
        {
            if (!reasons.Contains(Reason.GuestWithPaidSku)) reasons.Add(Reason.GuestWithPaidSku);
            if (decision == Decision.Keep && (eff is null || eff >= o.WarnDays))
            {
                decision = Decision.Review;
                score = Math.Max(score, 50);
                if (conf == Conf.High) conf = Conf.Medium;
            }
        }

        // Escalate a still-KEEP verdict to REVIEW when the assignment is errored (the reason was
        // already recorded above, so it also surfaces on the active-keep paths that return
        // early). A broken-but-billed seat is recoverable spend, so savings reflects the cost.
        if (assignmentErrored && decision == Decision.Keep)
        {
            decision = Decision.Review;
            score = Math.Max(score, 50);
            if (conf == Conf.High) conf = Conf.Medium;   // a config issue to verify, not a clean reclaim
        }

        decimal? savings = decision != Decision.Keep && (inactivityDriven || capReview || assignmentErrored) ? unitCost : null;
        return new(decision, score, conf, reasons, eff, savings);
    }

    /// <summary>Tenant-level: unassigned (idle) seats are the clearest waste. The
    /// buffer scales with the SKU (max of an absolute floor and a % of owned), so
    /// normal churn headroom on a 2,000-seat SKU isn't flagged as waste.</summary>
    public static SkuSeatVerdict ScoreSkuSeats(int seatsOwned, int seatsAssigned, decimal? unitCost, ScoringOptions o)
    {
        int idle = Math.Max(0, seatsOwned - seatsAssigned);
        var reasons = new List<string>();
        if (idle == 0) return new(Decision.Keep, reasons, 0m);

        decimal? savings = unitCost is { } c ? idle * c : null;
        int buffer = Math.Max(o.SeatBuffer, (int)Math.Floor(seatsOwned * o.SeatBufferPercent / 100.0));
        if (idle <= buffer)
        {
            reasons.Add(Reason.SeatBuffer);
            return new(Decision.Review, reasons, savings);
        }
        reasons.Add(Reason.UnassignedSeats);
        return new(Decision.Reclaim, reasons, savings);
    }

    // ---- helpers ----
    /// <summary>A SKU is treated as free when there is nothing to save by reclaiming it:
    /// its price on file is zero (0 / 0.0 / 0.00 - all equal as decimal) OR there is no
    /// price on file at all (null), its display name says "(free)", or its part number
    /// matches a free/viral/trial pattern. Mirrored by the SQL filter on vw.DirectAssignments
    /// (wave4-remediation.sql). NOTE: treating an UNPRICED SKU (null) as free means it is no
    /// longer flagged on activity - price every chargeable SKU in ref.SkuCost to score it.</summary>
    public static bool IsFreeSku(string? skuPartNumber, string? skuName, decimal? unitCost, ScoringOptions o)
    {
        if (unitCost is null or 0m) return true;   // no cost on file, or zero in any representation => nothing to save
        if (skuName?.Contains("(free)", StringComparison.OrdinalIgnoreCase) == true) return true;
        string part = skuPartNumber ?? "";
        foreach (string p in o.FreeSkuPatterns)
            if (!string.IsNullOrEmpty(p) && part.Contains(p, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static void AddHighValue(SignalRow r, ScoringOptions o, List<string> reasons)
    {
        if (o.HighValueSkus.Contains(r.SkuPartNumber ?? "", StringComparer.OrdinalIgnoreCase)
            && !reasons.Contains(Reason.HighValue))
            reasons.Add(Reason.HighValue);
    }

    // Continuous curve instead of 45/70 steps: ranks candidates WITHIN the review band
    // by how stale they actually are. Capped at ReclaimScore-1 below ReclaimDays so
    // crossing into RECLAIM remains strictly a >= ReclaimDays event.
    private static int ScoreFromDays(int days, ScoringOptions o)
    {
        if (days >= o.ReclaimDays) return Math.Min(100, 85 + (days - o.ReclaimDays) / 9);
        if (days >= o.WarnDays)
        {
            int ramp = 45 + (int)Math.Round((days - o.WarnDays) * 40.0 / Math.Max(1, o.ReclaimDays - o.WarnDays));
            return Math.Min(o.ReclaimScore - 1, ramp);
        }
        return Math.Min(20, days * 20 / Math.Max(1, o.WarnDays));
    }

    /// <summary>How many of the given activity dates fall inside the last <paramref name="windowDays"/> days.</summary>
    private static int CountRecent(DateTime nowUtc, int windowDays, params DateTime?[] dates)
    {
        DateTime cutoff = nowUtc.AddDays(-windowDays);
        int n = 0;
        foreach (DateTime? d in dates) if (d is { } v && v >= cutoff) n++;
        return n;
    }

    private static int? DaysSince(DateTime? d, DateTime now)
    {
        if (d is null) return null;
        int v = (int)Math.Floor((now - d.Value).TotalDays);
        return v < 0 ? 0 : v;
    }

    private static int? Min(params int?[] xs)
    {
        int? m = null;
        foreach (int? x in xs) if (x is { } v && (m is null || v < m)) m = v;
        return m;
    }

    private static bool MatchesAny(string? text, string[] patterns)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (string p in patterns)
            if (text.Contains(p, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
