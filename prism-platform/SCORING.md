# Prism scoring methodology

How Prism decides **KEEP / REVIEW / RECLAIM** for every Microsoft licence, and why the
rules are shaped the way they are. The design follows established Software Asset Management
(SAM) / FinOps reclamation practice rather than a single in-house heuristic - sources are
listed at the end.

## Philosophy

Prism **surfaces and explains; a human decides.** Nothing is ever reclaimed automatically.
Every verdict carries reason codes and a confidence so a reviewer can trust - or overrule -
it in seconds. The guiding principle from the field is *"don't cut blindly"*: a wrong
reclaim (locking out a real user) costs far more trust than a missed one, so the engine is
deliberately conservative and biases toward REVIEW whenever evidence is thin.

## What gets scored

Two units:

1. **Each (user, SKU) assignment** → KEEP / REVIEW / RECLAIM, a waste score (0–100), a
   confidence (LOW/MEDIUM/HIGH), reason codes, and the monthly seat cost at stake.
2. **Each SKU's idle seats** (owned − assigned) → the simplest, clearest waste; unassigned
   seats are pure spend.

## The signals - and why we never trust just one

The single most-repeated lesson in practitioner guidance and Microsoft's own community
threads is that **last interactive sign-in is not a reliable sole indicator**:

- `signInActivity.lastSignInDateTime` reflects **interactive** sign-ins only - a user living
  in cached Outlook or mobile apps can look "dormant" while working daily.
- A **null** last-sign-in is *ambiguous*: Microsoft documents it as "never signed in **or**
  last sign-in predates April 2020," and the property needs Entra **P1/P2** to populate.

So Prism combines four independent signals and treats a user as active if **any** of them is
recent (the most-recent activity wins):

- interactive sign-in (`lastSignInDateTime`),
- M365 workload activity (Exchange/Teams/OneDrive/SharePoint last-activity),
- desktop app usage of the licensed app (via the agent - see "App corroboration"),
- and, for service-account *detection only*, non-interactive sign-in.

`effectiveInactiveDays = min(days-since interactive, days-since M365, days-since app)`.

## The 30/60/90 framework

The industry-standard cadence (NinjaOne, Zylo, Surveil, Redress, CloudNuro all converge on
it) maps onto the score bands:

| Inactivity | Score | Default band |
|-----------|-------|--------------|
| < 30 days  | ≤ 20  | KEEP |
| 30–59 days | 45    | REVIEW (warn) |
| 60–89 days | 70    | REVIEW |
| ≥ 90 days  | 85+   | RECLAIM *(if HIGH confidence)* |

Thresholds are configurable per cohort (contractors tighter at 7–14 days, executives looser
at 60–90 is common). Microsoft's own guidance calls 90–180 days a "reasonable window" and
explicitly warns to account for vacation/leave - which is exactly what the guards below do.

## Guards against the classic false positives

- **New-hire grace** - created/hired within 30 days (or a future hire date) ⇒ KEEP. New
  starters haven't generated activity yet.
- **Recently-assigned grace** - licence assigned within 30 days ⇒ KEEP. A fresh assignment
  hasn't had time to be used.
- **Disabled account** ⇒ RECLAIM (HIGH) - a disabled-but-licensed account is the textbook
  clear waste, tagged `CHECK_RETENTION_OR_SHARED` because litigation hold / shared-mailbox
  conversion may apply.
- **Offboarded** (`employeeLeaveDateTime` in the past) ⇒ RECLAIM (HIGH).
- **Shared mailbox / service account** ⇒ capped at **REVIEW**, never RECLAIM. These are the
  most-cited trap: shared mailboxes have sign-in enabled by default and are often (non-
  compliantly) licensed, and service accounts show only non-interactive sign-ins. Prism
  flags them (`SERVICE_ACCOUNT_SUSPECTED`, `POSSIBLE_SHARED_OR_SERVICE`) for a human to
  convert/right-size rather than cut.

## Confidence - why missing data never means RECLAIM

RECLAIM requires **HIGH** confidence, which only comes from *evidence*: a real, stale
activity date, or a deterministic state (disabled/offboarded). When there's simply **no**
telemetry (null sign-in *and* no workload activity), the verdict is `NEVER_ACTIVE` at
**MEDIUM** confidence → REVIEW, not RECLAIM - precisely because a null sign-in is ambiguous.
Concealed usage reports (tenant `displayConcealedNames`) lower confidence further.

## App corroboration (agent ↔ licence join)

For app-tied SKUs (Visio, Project, Power BI), the agent's foreground-usage telemetry is a
direct signal: did the licensed executable actually run? The agent reports by Windows **SID**
while licences key on the Entra **object id**; for Entra-joined devices the SID is a
deterministic encoding of the object GUID, so Prism converts and joins them. If the app ran
recently ⇒ KEEP (`APP_IN_USE`); if the device is covered by the agent but the app never ran
while the user is otherwise inactive ⇒ extra weight (`APP_UNUSED`). No agent coverage ⇒ no
penalty (the signal is simply absent).

## Hardened heuristics

A second pass aligned the engine with the practices the top commercial SAM/FinOps
platforms (Flexera One, Snow, ServiceNow SAM Pro, Zylo, Productiv) converge on - all
implemented against data the warehouse already ingests:

- **Free plans are never waste** - €0-priced / "(free)" / viral-trial SKUs score KEEP
  (`FREE_SKU`) and are excluded from direct-assignment governance.
- **Leavers pipeline** - a *future* `employeeLeaveDateTime` within
  `OffboardingHorizonDays` (default 30) ⇒ REVIEW (`OFFBOARDING_SCHEDULED`): the reclaim
  is planned for the leave date instead of rediscovered by inactivity months later.
- **Never used since assignment** - zero recorded activity on *any* signal while the
  assignment is at least `NeverUsedAssignmentAgeDays` old (default 90) ⇒ score 92
  (`NEVER_USED_SINCE_ASSIGNMENT`), top of the review band. Still MEDIUM confidence:
  absence of telemetry never auto-reclaims.
- **Depth of use, not just recency** - per-workload last-activity (Teams / Exchange /
  OneDrive / SharePoint) now flows into scoring. An otherwise-active holder of a
  HighValue SKU with at most one active workload in `ShallowUseWindowDays` ⇒ REVIEW
  (`SHALLOW_USE`): a right-size conversation, never a reclaim, no savings claimed.
- **Multi-signal corroboration** - if the *only* activity evidence is the UPN-joined
  usage report (no Entra sign-in dates, no agent), confidence caps at MEDIUM
  (`LIMITED_TELEMETRY`) - the fuzzy join can't carry a reclaim alone.
- **Continuous score curve** - 30→90 days ramps 45→79 linearly (ranking within the
  review band reflects real staleness); crossing into RECLAIM remains strictly a
  ≥ 90-day event.
- **Proportional seat buffer** - idle-seat tolerance is max(`SeatBuffer`,
  `SeatBufferPercent`% of owned, default 2%), so churn headroom on large SKUs isn't
  flagged while small SKUs keep the tight absolute buffer.

All thresholds are options (`Prism__*` environment variables) and every new rule
carries its own reason code, so verdict changes stay explainable seat by seat.

## Release 1.0 evaluations - deterministic truth over heuristics

The release pass added signals that convert the engine's weakest guesses into facts, plus
a uniform evidence trail. All are conservative by construction:

- **Mailbox purpose (deterministic).** `userPurpose = shared | room | equipment` from the
  mailbox-settings connector replaces the name heuristic: `SHARED_MAILBOX` /
  `RESOURCE_MAILBOX` cap at REVIEW (convert properly, never cut), and a deterministic
  `user` purpose *overrides* a suspicious-looking name - fewer false flags both ways.
- **Leave guard.** An enabled auto-reply (`alwaysEnabled`/`scheduled`) **blocks the reclaim
  band** (`ON_LEAVE_SUSPECTED`): reclaiming a parental-leave / sabbatical seat is the most
  trust-destroying false positive there is. Microsoft's own guidance says to account for
  leave; now the engine provably does. Review is still allowed - a human can confirm the
  leave and snooze until return.
- **PSTN corroboration.** Real call-detail records now judge Teams Phone seats. PSTN calls
  ⇒ `PHONE_IN_USE` (decisive). Teams report *and* PSTN both zero ⇒ `PHONE_NO_CALLS` +
  `PSTN_NO_CALLS`, scored higher - two independent sources agree.
- **Never-onboarded evidence.** `MFA_NEVER_REGISTERED` (auth-methods report) is *positive*
  evidence an account was never onboarded - it tops the review band at score 95 but keeps
  MEDIUM confidence: absence alone still never auto-reclaims.
- **Guest governance.** `GUEST_WITH_PAID_SKU`: paid seats on guest accounts are tagged
  always and escalated to REVIEW when inactive-ish.
- **Copilot depth.** `COPILOT_SINGLE_SURFACE` marks a used-but-single-host-app Copilot
  seat for an adoption conversation (kept either way).
- **Evidence trail.** Every verdict now carries `SignalCount` (independent sources
  consulted) and `EvidenceJson` (compact per-signal day-deltas/flags). Present-and-negative
  is visibly different from absent - the dashboard renders both, and the audit export
  can replay any decision months later.
- **Verdict history + decisions.** Each run appends to `score.VerdictHistory` (trends,
  `vw.VerdictDelta` "what changed"); human decisions (keep / snooze-until / reclaim, with a
  note) upsert `score.Decision` and append to `score.DecisionLog` - re-scoring refreshes
  verdicts but can never overwrite a human's call, and expired snoozes resurface
  automatically.

## Reason-code glossary

`DISABLED_ACCOUNT`, `OFFBOARDED`, `OFFBOARDING_SCHEDULED`, `CHECK_RETENTION_OR_SHARED`,
`NEW_HIRE_GRACE`, `RECENTLY_ASSIGNED`, `SERVICE_ACCOUNT_SUSPECTED`,
`POSSIBLE_SHARED_OR_SERVICE`, `SHARED_MAILBOX`, `RESOURCE_MAILBOX`, `ON_LEAVE_SUSPECTED`,
`GUEST_WITH_PAID_SKU`, `MFA_NEVER_REGISTERED`, `APP_IN_USE`, `APP_UNUSED`, `NEVER_ACTIVE`,
`NEVER_USED_SINCE_ASSIGNMENT`, `NO_ACTIVITY_30D/60D/90D`, `SHALLOW_USE`,
`ACTIVITY_CONCEALED`, `LIMITED_TELEMETRY`, `HIGH_VALUE` (premium SKU, prioritised),
`FREE_SKU`, `GROUP_ASSIGNED` (reclaim requires a group change, not a direct unassign),
`UNASSIGNED_SEATS`, `SEAT_BUFFER`, `APP_NOT_INSTALLED`, `APP_INSTALLED`,
`INSTALL_CORROBORATED`, `INSTALLED_NO_USAGE_TELEMETRY`, `APP_IN_USE_MDE`, `MDE_NO_RUN_30D`,
`APP_IN_USE_WEB`, `WEB_NO_SIGNIN`, `OFFICE_APPS_UNUSED`, `COPILOT_IN_USE`,
`COPILOT_UNUSED`, `COPILOT_SINGLE_SURFACE`, `PHONE_IN_USE`, `PHONE_NO_CALLS`,
`PSTN_NO_CALLS`, `DIRECT_ASSIGNMENT`, `ASSIGNMENT_ERROR`, `SERVICE_PLANS_DISABLED`.

## Savings

Per-seat monthly cost comes from the `ref.SkuCost` table, which is **not hardcoded** - it's
loaded from your negotiated **Price Sheet API** (current month, your billing currency) by the
`pricing.skucost` connector, or maintained manually, with provenance (`Origin`, `Currency`,
`AsOfDate`) so stale or list-price figures are visible. There is no public M365 price-by-
region API for a customer, so see **PRICING.md** for the real options per agreement type.
Idle-seat savings = idle × unit cost. Reclaim/review savings = the seat cost that *could* be
recovered (potential, pending human action). A SKU with no price still gets a verdict; it just
shows no savings figure and appears in `vw.UnpricedSkus`.

## Honest limitations

- **Shared-mailbox / resource detection is deterministic only when the mailbox-settings
  connector is enabled** (`userPurpose`, needs `MailboxSettings.Read`). With it off, the
  name-pattern + sign-in-shape heuristic remains the fallback.
- **The SID↔user join is Entra-joined-only.** Hybrid/AD-joined device SIDs derive from
  on-prem AD; those simply yield no app corroboration (never a penalty).
- **No tier-downgrade (E5→E3) recommendations.** Doing this responsibly needs advanced-
  feature telemetry (Defender, compliance, Power BI, audio-conferencing use) Prism doesn't
  collect. Idle premium licences still surface via inactivity, ranked first by their higher
  cost. Blanket "downgrade every active E5" flags were deliberately *not* added - they'd bury
  the actionable items.
- **Prices are your responsibility to wire.** Savings are blank until `ref.SkuCost` is loaded
  (Price Sheet connector or manual). See PRICING.md - there is no public M365 price API.

## Sources

Practitioner / vendor guidance: NinjaOne (30-day default, cohort thresholds), Zylo and
Surveil (30/60/90 framework), Redress and CloudNuro (waste taxonomy, 15–25% typical savings,
convert-to-shared-mailbox), AdminDroid / o365reports (shared-mailbox sign-in-enabled
compliance trap). Microsoft Learn / Entra docs and Tech Community (interactive-only
`lastSignInDateTime`, null ambiguity, P1/P2 requirement, 90–180-day window, block-sign-in
for shared mailboxes). Pricing: Microsoft's December 2025 pricing announcement and partner
summaries (list prices and the 1 July 2026 changes).
