<div align="center">

# Prism

### Microsoft 365 & Azure License Intelligence - with evidence, not guesswork

**Find the licenses you're paying for that nobody uses - and prove *why* - by reconciling entitlements × installs × usage across 19 independent Microsoft signals into a per-seat verdict (KEEP / REVIEW / RECLAIM) with a euro value and a full evidence trail on every reclaim.**

Runs entirely inside your own Azure tenant. No third-party SaaS. No data egress. No per-seat fee.

![Release: 1.0.0-rc.1](https://img.shields.io/badge/release-1.0.0--rc.1-blue)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)
![IaC: Bicep](https://img.shields.io/badge/IaC-Bicep-0078D4)
![Auth: Managed Identity](https://img.shields.io/badge/auth-secret--free-brightgreen)
![Access: read-only](https://img.shields.io/badge/Microsoft%20365-read--only-brightgreen)
![License: MIT](https://img.shields.io/badge/license-MIT-informational)

**Release candidate 1** - feature-complete for 1.0. What shipped and how to upgrade: [`CHANGELOG.md`](CHANGELOG.md).

</div>

---

## What is Prism?

Prism is a self-hosted **Software Asset Management (SAM) / FinOps** platform for Microsoft 365 and Azure. It answers three questions for every licensed seat:

1. **Is this license actually used?**
2. **If not - how confident are we, and what's the evidence?**
3. **What is the waste worth per month?**

Most license tools score "usage" from a single signal - usually last sign-in - which is famously unreliable (it only counts *interactive* logins, and a null value is ambiguous). Prism's core principle is **multi-source reconciliation**: a seat is called waste only when *independent* signals agree, and an **absence of data is never mistaken for non-use**. That's what makes its verdicts trustworthy enough to act on.

It is **read-only by design** - Prism reads from Microsoft 365, writes verdicts only to its own database, and never changes or removes a license itself. A human always decides.

---

## Why it's different

| The question | Signals Prism reconciles |
|---|---|
| Is the seat assigned, and how? | Entra `licenseAssignmentStates` (direct vs group-inherited) |
| Has the person been active at all? | Entra interactive **and** non-interactive sign-in activity |
| Is the licensed **desktop app** used? | Prism agent foreground-time **and** Defender process-run telemetry |
| Is the licensed **web service** used? | Entra per-application sign-ins (Power BI, Project, …) |
| Are the **Office apps** actually opened? | M365 Apps usage report, Teams activity, mailbox/OneDrive/SharePoint activity |
| Is **Copilot** used? | Microsoft 365 Copilot usage report |
| Is the software even **installed**? | Intune detected apps **and** Defender for Endpoint inventory |
| What does the waste **cost**? | Azure Cost Management + your negotiated price sheet |

- 🔎 **Deterministic & explainable.** No black-box AI. Every verdict is a set of reason codes, a 0–100 waste score, a confidence level, and a monthly € figure. You can defend any decision line-by-line.
- 🔐 **Secret-free & in-tenant.** Federated identity → user-assigned managed identity. No app secrets, no passwords, no data leaving your tenant.
- 🧱 **Private-network friendly.** Ships as internal Azure Container Apps with private endpoints and Key Vault - deployable under a locked-down "private application architecture" with no public ingress.
- 🧰 **Cheap to run.** Serverless Azure SQL (auto-pause) + scheduled Container Apps Jobs. It sleeps when idle.
- ✅ **Safe.** Absence of telemetry never auto-reclaims; risky cases are capped at REVIEW; nothing is ever cut automatically.

---

## The signals it reads

Prism ingests **19 read-only connectors** into an Azure SQL warehouse. Everything below is data you already own - Prism just correlates it.

### Identity & entitlement (Microsoft Entra ID / Graph)
- Users: account enabled/disabled, department, country, hire date, created date, and **leave date** (`employeeLeaveDateTime`) for the offboarding pipeline
- Sign-in activity: **interactive**, **non-interactive**, and **last-successful** sign-in
- License assignments: assigned SKUs, **direct vs group-inherited**, assignment state, and **disabled service plans**
- Subscribed SKUs: seats **owned / assigned / consumed**
- **Deleted-but-still-licensed** users (seats that keep billing after an account is removed)

### Microsoft 365 usage reports (Graph)
- Microsoft 365 Apps: Word / Excel / PowerPoint / Outlook / OneNote / Teams last activity + platform mix
- Office 365 service usage (per-workload last activity)
- Exchange mailbox, OneDrive, and SharePoint file activity
- Teams calls / meetings / messages
- **Microsoft 365 Copilot** per-app activity

### Web / service usage (Entra sign-in logs)
- Per-(user, application) sign-ins - the authoritative signal for browser-first SKUs (Power BI service, Project web) that desktop signals can't see
- Enterprise-app (service principal) sign-ins

### Device & install inventory (Intune + Microsoft Defender)
- Intune managed devices, **detected apps**, and per-device install expansion
- Intune managed-app install status
- Endpoint Analytics app health
- Defender for Endpoint software inventory + per-device installs
- Defender for Endpoint **Advanced Hunting** process-run telemetry (agentless "did this app actually run" evidence)
- Defender for Cloud Apps discovered apps (shadow-IT SaaS)

### Identity lifecycle & mailbox truth
- **Mailbox `userPurpose`** - the *deterministic* shared / room / equipment discriminator (replaces the name-pattern heuristic), plus auto-reply state (an active out-of-office **blocks** reclaim: leave of absence is the most damaging false positive there is)
- **Auth-method registration** - a holder who never registered MFA/SSPR was never onboarded: positive corroboration for `NEVER_ACTIVE`
- **User type** (Member vs **Guest** - a paid SKU on a guest account is a governance flag) and hybrid-sync provenance

### Calling
- **Teams PSTN call records** (`getPstnCalls`) - real public-network calls per user; the authoritative Teams Phone signal, far stronger than the Teams report's coarse call count

### Cost
- Azure Cost Management (actual spend by service)
- Negotiated unit prices via your **Price Sheet** (MCA/EA) - no hardcoded list prices

### Desktop engagement (optional agent)
- A lightweight Native-AOT Windows agent measures **foreground active-time per executable** - true engagement ("used for 3 h this week"), not merely "launched". Ships to Prism over mutual TLS.

---

## Sources (Microsoft APIs)

| Source | Used for |
|---|---|
| Microsoft Graph `v1.0` + `beta` | Users, SKUs, license assignments, sign-in logs, usage reports, Intune, service principals |
| Microsoft Intune (Graph `deviceManagement`) | Managed devices, detected apps, app health, install status |
| Microsoft Entra ID sign-in logs (Graph `auditLogs`) | Interactive / non-interactive / per-app / service-principal sign-ins |
| Microsoft 365 usage reports (Graph `reports`) | App, service, Copilot, Teams, mailbox/OneDrive/SharePoint activity |
| Microsoft Defender for Endpoint API | Software inventory, per-device installs, Advanced Hunting process runs |
| Microsoft Defender for Cloud Apps API | Shadow-IT / discovered SaaS |
| Azure Resource Manager | Cost Management + Price Sheet |
| Prism desktop agent (optional) | Foreground app usage over mutual TLS |

All egress is to Microsoft endpoints only. The connectors share one throttle-hardened HTTP client that honours each API's `Retry-After` and backs off on 5xx; a connector that fails never aborts the rest. Security-scoped connectors (Defender, Copilot, sign-ins) are **off by default** and enabled per deployment.

---

## How verdicts are reached

The scoring engine is deterministic and conservative - *surface and explain; a human decides*.

- **30 / 60 / 90-day** inactivity framework maps to score bands; RECLAIM requires **≥ 90 days AND HIGH confidence**.
- **Effective activity** is the most-recent of interactive sign-in, M365 workload activity, and licensed-app usage - so a user living in cached Outlook doesn't look dormant.
- **Evidence hierarchy:** direct app usage > process launch > install presence > inactivity inference; web sign-ins are primary for browser-first SKUs.
- **Guards** against the classic false positives: new-hire grace, recently-assigned grace, service/shared-account detection (capped at REVIEW), proportional idle-seat buffer.
- **High-value paths:** unused **Copilot** seats, **Teams Phone** numbers with zero calls, **shallow-use** downgrade candidates, **leaver** offboarding, disabled accounts, disabled service plans, and phantom **Visio/Project/Power BI** add-ons.
- **Free / €0 SKUs** are never waste (`FREE_SKU`).
- Every rule carries a **reason code** - 45+ in total - so verdicts stay explainable seat by seat.
- **Every verdict carries its evidence.** Each seat records how many independent sources were consulted (`SignalCount`) and a compact per-signal trail (`EvidenceJson`) the dashboard renders as a case file: what each signal said, and - just as important - which signals were *silent* (absence is never counted as evidence). Verdict history is kept per run (`score.VerdictHistory`) so trends and "what changed since last run" are first-class, and human decisions (keep / snooze-until / approve-reclaim, with a rationale note) live in an append-only audit log that re-scoring can never overwrite.

Full doctrine, thresholds, and the complete reason-code glossary are in **[`SCORING.md`](SCORING.md)**.

---

## Architecture

```
   Microsoft Graph ┐
   Intune          │
   Defender (MDCA / MDE / Hunting) ┼─►  prism-connectors  ─►  Azure SQL  ◄─  prism-scoring  ─►  verdicts
   Entra sign-ins  │                  (Container Apps Job)   (warehouse)   (Container Apps Job)
   M365 reports    │                                             ▲
   Azure Cost      ┘                                             │
                                                         prism-gateway ◄─ prism-agent (mTLS, per-device usage)
                                                                 │
                                                         prism-dashboard (read-only web UI + decision log)
```

Five components, all containers on one Container Apps environment:

| Component | Type | Role |
|---|---|---|
| **prism-connectors** | Container Apps Job | Scheduled read-only ingest (the 16 connectors) |
| **prism-scoring** | Container Apps Job | Deterministic verdict engine over the warehouse |
| **prism-dashboard** | Container App | Read-only web UI + `/api` + human decision log |
| **prism-gateway** | Container App | Optional mTLS endpoint the desktop agent ships usage to |
| **prism-agent** | Windows service (Native-AOT) | Optional foreground-usage telemetry |

**Azure footprint:** Azure SQL (serverless, Entra-only) + Container Apps + Container Registry, with Key Vault (mTLS CA certificate) and Log Analytics. *Not used:* Functions, Logic Apps, Cosmos, Data Factory, Cognitive Services, AI/ML - Prism is deterministic by design.

---

## Getting started

Full runbook: **[`DEPLOYMENT.md`](DEPLOYMENT.md)**. The short version:

1. **Create the warehouse:** run `schema/schema.sql`, then `schema/seed-commercial.sql` (edit prices to taste).
2. **Deploy infrastructure (Bicep):** `infra/` provisions the registry, serverless SQL, and the Container Apps environment; `infra/apps/` publishes the four Prism containers. Pipelines in `.azuredevops/` build the images and deploy behind an approval gate.
3. **Grant the managed identity** its read scopes (Graph app roles, Cost Management Reader, Key Vault) - helper scripts in `infra/scripts/`.
4. The connectors job ingests on a schedule; scoring runs after it; the dashboard shows the result.

> Prices are yours to wire - there is no public per-customer M365 price API. See **[`PRICING.md`](PRICING.md)**.

---

## Repository layout

```
src/
  Prism.Connectors/       16 read-only ingest connectors (scheduled job)
  Prism.Scoring/          deterministic verdict engine (scheduled job)
  Prism.Dashboard/        read-only web UI + decision log
  Prism.Gateway/          mTLS ingest endpoint for the desktop agent
  Prism.Agent.Contracts/  shared agent protocol types
  Prism.Warehouse/        SQL sink, model, and ingestion seam shared by the jobs
schema/
  schema.sql              complete consolidated warehouse schema
  seed-commercial.sql     example negotiated prices + contract quantities (edit)
infra/
  container-env.bicep  containerregistry.bicep  sqldatabase.bicep  main.bicep   (Phase 1: platform)
  apps/                container-app.bicep  container-job.bicep  keyvault-role.bicep  main.bicep   (Phase 2: the 4 apps)
  scripts/             grant-prism-identity.ps1  grant-prism-sql.sql             (managed-identity grants)
.azuredevops/
  azure-pipelines.yml       Phase 1 infrastructure pipeline
  azure-pipelines-apps.yml  Phase 2 build-images → what-if → deploy (approval-gated)
deploy/
  Dockerfile.*          one per container image
dashboard/
  index.html            single-file front end
DEPLOYMENT.md   production deployment runbook (start here)
SCORING.md      the scoring doctrine + reason-code glossary
PRICING.md      how unit costs get into the warehouse
CHANGELOG.md    release notes (1.0.0-rc.1)
```

The optional Windows agent lives in its own repository (**prism-agent**).

---

## Security & privacy

- **Read-only** to Microsoft 365 - the managed identity holds only read scopes plus Cost Management Reader.
- **Secret-free** - federated identity → managed identity; no app secrets or passwords anywhere.
- **In-tenant** - all data stays in your Azure SQL; there is no vendor backend and no telemetry phones home.
- **Private networking** - internal Container Apps, private endpoints, and a Key Vault-sourced mTLS CA. No public ingress required.
- **Human-in-the-loop** - the dashboard records approve/keep/snooze decisions for export; approved reclaims go through your normal change process. Prism never touches a license.

---

## Limitations (honest)

- Shared-mailbox / resource-account detection is **deterministic when the mailbox-settings connector is enabled** (`userPurpose`); tenants that leave it off fall back to the name-pattern + sign-in-shape heuristic.
- The desktop-agent SID↔user join is Entra-joined-only; hybrid/AD-joined devices simply yield no agent corroboration (never a penalty).
- No automatic E5→E3 tier-downgrade recommendations - doing that responsibly needs advanced-feature telemetry Prism doesn't collect. Idle premium seats still surface via inactivity, ranked first by cost.
- Savings are blank until `ref.SkuCost` is populated (Price Sheet connector or manual).

---

## Contributing

Issues and pull requests are welcome - new connectors, additional reason codes, and dashboard drill-downs especially. Please keep the core principles intact: read-only, deterministic, absence-is-not-evidence, human-in-the-loop.

## License

Released under the **MIT License** - see [`LICENSE`](LICENSE).

## Disclaimer

Prism is provided as-is. It reads your Microsoft 365 / Azure data and produces recommendations; **you** own the decision to act on any verdict. Validate reclaim candidates on real data before enabling any auto-reclaim flag.
