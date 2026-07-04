# Changelog

All notable changes to the Prism platform. Format follows [Keep a Changelog](https://keepachangelog.com);
versioning follows [SemVer](https://semver.org).

## [1.0.0-rc.1] - 2026-07-04

First release candidate. Prism reconciles entitlements × installs × usage across **19
independent Microsoft signals** into per-seat KEEP / REVIEW / RECLAIM verdicts - with the
evidence attached and every decision left to a human.

### Signals & ingestion
- **Mailbox-purpose connector** (`graph.mailboxsettings`, off by default): `userPurpose`
  makes shared / room / equipment detection a *fact* instead of a name-pattern guess, and
  auto-reply state powers the new leave guard. Batched via Graph `$batch` with per-item
  throttle handling. Requires `MailboxSettings.Read`.
- **PSTN calling connector** (`graph.pstn`, off by default): real Teams Phone call-detail
  records aggregated per user - the authoritative phone-seat usage signal. Requires the
  narrow `CallRecord-PstnCalls.Read.All` scope.
- **Auth-methods connector** (`graph.authmethods`, off by default): MFA/SSPR registration
  state; a never-registered holder was never onboarded. Reuses `AuditLog.Read.All`.
- **User enrichment**: `userType` (guest detection) and `onPremisesSyncEnabled` (hybrid
  provenance) now flow into the warehouse.
- **Bounded-parallel connector host** (`Prism__MaxConcurrentConnectors`, default 3) -
  roughly halves a full run while honouring Graph throttling.
- **Throttle-hardening across every client**: jittered exponential backoff, `Retry-After`
  honoured on 429 *and* 5xx, one-shot 401 token refresh in all clients (previously
  Graph-only), consecutive-failure circuit breaker, invariant-culture numeric parsing
  (EU-locale containers previously mis-parsed cost decimals).

### Evaluation engine
- **Evidence trail on every verdict**: `SignalCount` (independent sources consulted) and
  `EvidenceJson` (compact per-signal day-deltas/flags). Signals that stayed silent are
  recorded as *absent*, never as evidence - the doctrine, now auditable.
- **Leave guard**: an active auto-reply blocks the RECLAIM band (`ON_LEAVE_SUSPECTED`).
- **Deterministic mailbox rules**: `SHARED_MAILBOX` / `RESOURCE_MAILBOX` cap at REVIEW; a
  deterministic `user` purpose overrides a suspicious-looking display name.
- **PSTN corroboration**: `PSTN_NO_CALLS` strengthens (and `PHONE_IN_USE` can be proven
  by) real call records.
- **Onboarding evidence**: `MFA_NEVER_REGISTERED` ranks never-onboarded seats at the top
  of the review band - still never auto-reclaimed on absence alone.
- **Guest governance**: `GUEST_WITH_PAID_SKU` tags every paid guest seat; inactive ones
  escalate to REVIEW.
- **Copilot depth**: `COPILOT_SINGLE_SURFACE` flags single-host-app usage for an adoption
  conversation.
- **Verdict history** (`score.VerdictHistory`, retained 400 days) powers trends and the
  run-over-run delta; **decisions** (keep / snooze-until / reclaim + rationale note) are
  kept in `score.Decision` with an append-only `score.DecisionLog` audit trail - re-scoring
  refreshes verdicts but can never overwrite a human's call, and expired snoozes resurface.

### Warehouse
- **One consolidated, self-upgrading schema** (`schema/schema.sql`); preview databases
  upgrade with a single companion pass (`schema/migrate-v2.sql`).
- **Index overhaul**: ~20 covering / filtered indexes matched to the actual read paths
  (scoring signal join, review queue, drills, palette search, agent correlation).
- `vw.AppEstate` now serves the estate search and watched-apps endpoints across the whole
  inventory, `vw.ReviewQueue` is decision-aware, and `vw.DataFreshness` exposes per-feed
  load health.
- **Transient-fault retry with jitter** on every warehouse load (a paused serverless
  database resuming no longer fails a run), `TABLOCK` bulk replaces, materialised
  `DisabledPlanCount`, and bounded retention for history/log tables.

### Dashboard
- **Evidence drawer**: the full case file behind any queue row - per-signal panels,
  silent-signal disclosure, verdict history, and the standing human decision.
- **Decision workflow**: keep / snooze (with date) / approve-reclaim, each with an
  audit-logged note; handled seats dim and expired snoozes return automatically.
- **Data & Decisions view**: connector freshness ledger and the append-only decision log,
  with CSV exports (`/api/export/review.csv`, `/api/export/decisions.csv`).
- **"Since the last run"**: escalations, new arrivals and relaxations at a glance.
- New read-only API: `/api/trends`, `/api/changes`, `/api/evidence`, `/api/departments`,
  `/api/health`, `/api/decision-log`; `/api/decision` accepts `snoozeUntil` + `note`.

### Gateway
- **Per-device rate limiting** (fixed window keyed on certificate identity) with honest
  `Retry-After`; `/healthz` (liveness) and `/readyz` (dependency probe) endpoints.
- **Input hardening at the trust boundary**: string clamping to column widths, negative
  counter clamps, implausible-date rejection, security headers on every response.

### Security
- No secrets anywhere (federated identity → managed identity throughout); parameterised
  SQL everywhere; decision vocabulary enforced by CHECK constraint; release security
  sweep clean (no TODO debt, no non-TLS endpoints, safe configuration defaults).

### Upgrade notes
1. Back up, then run `schema/schema.sql` (idempotent). Preview databases also run
   `schema/migrate-v2.sql` once.
2. Rebuild and deploy the four containers.
3. New connectors stay **off** until you grant their scopes and enable them - see
   `infra/scripts/grant-prism-identity.ps1` and `DEPLOYMENT.md`.
