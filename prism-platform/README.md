# Prism - Production Deployment Guide

This is the complete, from-scratch runbook to stand Prism up in production using **Bicep** and
**Azure DevOps**. It assumes nothing beyond an Azure subscription and an Entra tenant. Everything
Prism does is read-only; the whole deployment lives in one resource group and tears down with one
command.

<<<<<<< HEAD
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
=======
The deployment has three parts:

1. **Schema** - create the warehouse (two SQL files, run once).
2. **Infrastructure** - provision everything with Bicep (one deployment).
3. **Enablement** - turn on the optional connectors you want and run the jobs.
>>>>>>> 78331c20c8456251821a9fc0bdb0410cf36fd66f

---

## 0. Prerequisites

<<<<<<< HEAD
Prism is a self-hosted **Software Asset Management (SAM) / FinOps** platform for Microsoft 365 and Azure. It answers three questions for every licensed seat:

1. **Is this license actually used?**
2. **If not - how confident are we, and what's the evidence?**
3. **What is the waste worth per month?**

Most license tools score "usage" from a single signal - usually last sign-in - which is famously unreliable (it only counts *interactive* logins, and a null value is ambiguous). Prism's core principle is **multi-source reconciliation**: a seat is called waste only when *independent* signals agree, and an **absence of data is never mistaken for non-use**. That's what makes its verdicts trustworthy enough to act on.

It is **read-only by design** - Prism reads from Microsoft 365, writes verdicts only to its own database, and never changes or removes a license itself. A human always decides.

---

## Why it's different

| The question | Signals Prism reconciles |
=======
| You need | Notes |
>>>>>>> 78331c20c8456251821a9fc0bdb0410cf36fd66f
|---|---|
| An Azure subscription + resource group | Everything is created inside it. |
| A user-assigned **managed identity** | Prism's single identity. Grant it the read-only Graph scopes below + **Cost Management Reader** on the billing scope. |
| The managed identity's **client id** and **principal (object) id** | `az identity show -g <rg> -n <mi> --query '{client:clientId,principal:principalId}'` |
| An Entra account to be **SQL admin** | The person who runs the two schema files. Its **object id**: `az ad signed-in-user show --query id -o tsv` |
| Azure CLI (Cloud Shell is easiest) | `https://shell.azure.com` - no local installs needed. |

<<<<<<< HEAD
- 🔎 **Deterministic & explainable.** No black-box AI. Every verdict is a set of reason codes, a 0–100 waste score, a confidence level, and a monthly € figure. You can defend any decision line-by-line.
- 🔐 **Secret-free & in-tenant.** Federated identity → user-assigned managed identity. No app secrets, no passwords, no data leaving your tenant.
- 🧱 **Private-network friendly.** Ships as internal Azure Container Apps with private endpoints and Key Vault - deployable under a locked-down "private application architecture" with no public ingress.
- 🧰 **Cheap to run.** Serverless Azure SQL (auto-pause) + scheduled Container Apps Jobs. It sleeps when idle.
- ✅ **Safe.** Absence of telemetry never auto-reclaims; risky cases are capped at REVIEW; nothing is ever cut automatically.
=======
**Graph application permissions on the managed identity (all read-only):**
`Directory.Read.All`, `Reports.Read.All`, `AuditLog.Read.All`,
`DeviceManagementManagedDevices.Read.All`, `DeviceManagementApps.Read.All`.
Grant admin consent for each. The optional Defender connectors add their own permissions (§4).

> **Report concealment.** If your tenant anonymises usage reports
> (`Reports.Read.All` returns masked names), Prism still runs and flags the affected signals as
> concealed rather than losing them silently. To get per-user M365 activity, turn off
> *"Display concealed user, group, and site names"* in the M365 admin centre's report settings.
>>>>>>> 78331c20c8456251821a9fc0bdb0410cf36fd66f

---

## 1. Create the warehouse schema

<<<<<<< HEAD
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

### Identity lifecycle & mailbox truth (v2)
- **Mailbox `userPurpose`** - the *deterministic* shared / room / equipment discriminator (replaces the name-pattern heuristic), plus auto-reply state (an active out-of-office **blocks** reclaim: leave of absence is the most damaging false positive there is)
- **Auth-method registration** - a holder who never registered MFA/SSPR was never onboarded: positive corroboration for `NEVER_ACTIVE`
- **User type** (Member vs **Guest** - a paid SKU on a guest account is a governance flag) and hybrid-sync provenance

### Calling (v2)
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
- **v2 - every verdict carries its evidence.** Each seat records how many independent sources were consulted (`SignalCount`) and a compact per-signal trail (`EvidenceJson`) the dashboard renders as a case file: what each signal said, and - just as important - which signals were *silent* (absence is never counted as evidence). Verdict history is kept per run (`score.VerdictHistory`) so trends and "what changed since last run" are first-class, and human decisions (keep / snooze-until / approve-reclaim, with a rationale note) live in an append-only audit log that re-scoring can never overwrite.

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
=======
Two files, run in order, against the `prism` database. Use the Portal **Query editor** (browser,
no tools) or `sqlcmd -G` (Entra auth).

```bash
sqlcmd -S <server>.database.windows.net -d prism -G -i schema/schema.sql
sqlcmd -S <server>.database.windows.net -d prism -G -i schema/seed-commercial.sql
>>>>>>> 78331c20c8456251821a9fc0bdb0410cf36fd66f
```

- **`schema/schema.sql`** is the entire warehouse - every schema, table, index, analytic view,
  and the curated product reference data. It is idempotent: safe to re-run any time.
- **`schema/seed-commercial.sql`** loads your negotiated EUR unit prices and EA contract
  quantities. Edit it to match your contract; re-run after edits. (You can instead maintain
  `ref.SkuCost` by hand or wire the pricing connector - see `PRICING.md`.)

**Grant the managed identity database access** (read + write to Prism's own database only - this
grants nothing in Microsoft 365):

```sql
CREATE USER [<managed-identity-name>] FROM EXTERNAL PROVIDER;
ALTER ROLE db_datareader ADD MEMBER [<managed-identity-name>];
ALTER ROLE db_datawriter ADD MEMBER [<managed-identity-name>];
```

**Verify:** `SELECT COUNT(*) FROM sys.tables;` shows the `dim.*`, `fact.*`, `ref.*`, `score.*`
tables, and `SELECT name FROM sys.database_principals WHERE name='<managed-identity-name>';`
returns one `EXTERNAL_USER` row.

---

## 2. Deploy the infrastructure with Bicep

`infra/main.bicep` provisions, in dependency order: the Container Registry (with an `AcrPull`
grant to the managed identity), the serverless Azure SQL server + database, the Container Apps
environment, the two scheduled jobs (`prism-connectors`, `prism-scoring`), the dashboard
Container App, and - when enabled - the gateway.

### 2a. Fill in the parameters

Edit `infra/parameters/prd.bicepparam`. The Contoso values are pre-filled; the one field you must
supply is `sqlAdminObjectId` (the object id from §0).

```bicep
param location                = 'westeurope'
param suffix                  = 'cts01'
param managedIdentityName     = 'id-mi-prism-platform'
param managedIdentityClientId = '<mi client id>'
param sqlAdminUpn             = 'sqladmin@contoso.com'
param sqlAdminObjectId        = '<your object id>'   // <-- fill this in
param tenantId                = '<tenant guid>'
param deployGateway           = false            // set true only for the desktop-agent path (§5)
```

### 2b. Deploy

**Via Azure DevOps (recommended).** The pipeline at `.azuredevops/azure-pipelines.yml` runs three
stages - build all images with `az acr build`, run `what-if` to show the diff, then deploy behind
a manual approval gate. One-time setup:

1. **Service connection** (`prism-azure-sc`) → Azure Resource Manager, scoped to the subscription.
2. **Variable group `prism-prd-infra`**: `RESOURCE_GROUP`, `ACR_NAME` (the `prismacr<suffix>`
   from the parameter file), `SQL_ADMIN_OBJECT_ID`, `BICEP_PARAM_FILE=infra/parameters/prd.bicepparam`.
3. **Variable group `prism-prd-secrets`** (secret): any Defender app ids you wire as secrets.
   (The gateway's device-trust CA certificate is **not** a pipeline secret - the gateway reads it
   at runtime from Key Vault using its managed identity; see §5.)
4. **Environment `prism-prd`** with an Approvals check - this is what gates the deploy stage.

Push to `main` (or run the pipeline) and approve the deploy stage when prompted.

**Or directly from Cloud Shell:**

```bash
az deployment group create \
  --resource-group <rg> \
  --template-file infra/main.bicep \
  --parameters infra/parameters/prd.bicepparam \
  --parameters sqlAdminObjectId="$(az ad signed-in-user show --query id -o tsv)" \
  --parameters imageTag=v1
```

> The container images must exist in the registry before (or as part of) the deploy. The pipeline
> builds them in its first stage; doing it by hand is `az acr build -r <acr> -t prism-connectors:v1
> -f deploy/Dockerfile.connectors .` (repeat for `scoring`, `dashboard`, `gateway`).

The deployment outputs the dashboard URL.

**Lock the dashboard down before sharing it** - it has no auth of its own. Either keep ingress
internal (VPN/private), or enable Entra auth:

```bash
az containerapp auth update -g <rg> -n prism-dashboard \
  --action RequireAuthentication --enable-token-store true
# then add the Microsoft provider in the Portal.
```

---

## 3. First run & verification

The jobs are scheduled (connectors 02:00 UTC, scoring 03:30 UTC). To run immediately:

```bash
az containerapp job start -g <rg> -n prism-connectors
# wait for it to finish (Portal → job → Execution history → Console for logs), then:
az containerapp job start -g <rg> -n prism-scoring
```

**Verify:**

| Check | Expected |
|---|---|
| `SELECT COUNT(*) FROM dim.[User];` | > 0 |
| `SELECT TOP 5 * FROM fact.LicenseAssignment;` | rows |
| `SELECT * FROM vw.SavingsSummary;` | one row, non-zero counts |
| `SELECT TOP 5 * FROM vw.ReviewQueue ORDER BY EstMonthlySavings DESC;` | verdicts |
| dashboard URL | live data, filters work |

---

## 4. Enabling optional connectors

Everything in §1–3 is the core product. The connectors below add depth and are off by default.
Enable each by setting its Bicep parameter (then redeploy) - or for a quick change, set the env
var on the running job with `az containerapp job update -g <rg> -n prism-connectors --set-env-vars …`.

| Connector | Bicep parameter | Adds permission |
|---|---|---|
| Defender for Cloud Apps (shadow IT) | `enableDefenderConnector` (+ `defenderTenantId`, `defenderAppId`) | MDCA app registration |
| Defender for Endpoint inventory | `enableMdeConnector` (+ `mdeTenantId`, `mdeAppId`) | `Software.Read.All` (WindowsDefenderATP) |
| Defender Advanced Hunting (process runs) | `enableMdeHunting` | `AdvancedQuery.Read.All` |
| Entra per-app sign-ins | `enableSignInConnector` | `AuditLog.Read.All` |
| M365 Apps usage | `enableM365AppUsage` | `Reports.Read.All` |
| Deleted-but-licensed users | `includeDeletedUserLics` | `Directory.Read.All` |
| Copilot usage | `enableCopilotConnector` | `Reports.Read.All` |
| Teams activity | `enableTeamsActivity` | `Reports.Read.All` |
| Mailbox/OneDrive/SharePoint detail | `enableServiceDetail` | `Reports.Read.All` |
| Intune app health | `enableAppHealth` | `DeviceManagementApps.Read.All` |
| Intune managed-app installs | `enableMobileApps` | `DeviceManagementApps.Read.All` |
| Enterprise-app sign-ins | `enableSpSignIn` | `AuditLog.Read.All` |
| Leaver dates (offboarding signal) | `enableLeaverDates` | `User.Read.All` |

<<<<<<< HEAD
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
=======
**Defender connectors - app registration (secret-free).** The Defender APIs aren't reachable with
a managed identity directly, so Prism uses a federated app registration that the managed identity
exchanges a token into (no client secret ever exists):

```bash
APP_ID=$(az ad app create --display-name "Prism-MdeConnector" --query appId -o tsv)
az ad sp create --id "$APP_ID"
az ad app federated-credential create --id "$APP_ID" --parameters "{
  \"name\":\"prism-mi\",
  \"issuer\":\"https://login.microsoftonline.com/<tenant>/v2.0\",
  \"subject\":\"<managed-identity-client-id>\",
  \"audiences\":[\"api://AzureADTokenExchange\"]
}"
# Grant Software.Read.All (+ AdvancedQuery.Read.All for hunting) on WindowsDefenderATP
#   appId, Application type, then admin-consent.
>>>>>>> 78331c20c8456251821a9fc0bdb0410cf36fd66f
```

Pass `enableMdeConnector=true`, `mdeAppId=$APP_ID`, `mdeTenantId=<tenant>` (the schema and views
they populate already exist in `schema.sql`). The Defender APIs are regional - set
`mdeApiBaseUrl=https://eu.api.security.microsoft.com` in the EU for lower latency.

After enabling anything, run the connectors job, then the scoring job.

---

## 5. Desktop agent + gateway (optional)

<<<<<<< HEAD
- **Read-only** to Microsoft 365 - the managed identity holds only read scopes plus Cost Management Reader.
- **Secret-free** - federated identity → managed identity; no app secrets or passwords anywhere.
- **In-tenant** - all data stays in your Azure SQL; there is no vendor backend and no telemetry phones home.
- **Private networking** - internal Container Apps, private endpoints, and a Key Vault-sourced mTLS CA. No public ingress required.
- **Human-in-the-loop** - the dashboard records approve/keep/snooze decisions for export; approved reclaims go through your normal change process. Prism never touches a license.
=======
Only needed to corroborate **app-tied** SKUs (Visio / Project / Power BI) with real desktop
foreground-time. Core waste detection does not depend on it.

The gateway authenticates each device over **mutual TLS**: a device presents the client
certificate delivered by its Intune SCEP policy, and the gateway validates that cert against the
**issuing CA** (the CA configured as the *Root Certificate* on the SCEP profile - at Contoso the
"Computers - Device Root CA"). The gateway reads that CA's **public** certificate from Azure
Key Vault at startup using its managed identity - no certificate material is placed in app config,
pipeline secrets, or parameter files.

### 5a. Get the CA public certificate and store it in Key Vault

The CA certificate is **public** (no private key), so it is stored as a Key Vault **secret** whose
value is the PEM. (A public-only cert cannot be imported as a Key Vault *certificate* object -
those require a private key.) You need the same CA you attached as the SCEP *Root Certificate*.

**Export the public cert (.cer) - pick whichever you can reach:**

- **From a managed Windows machine** (the CA is deployed to the Trusted Root store by your Intune
  trusted-cert profile). In `certlm.msc` → *Trusted Root Certification Authorities* → *Certificates*,
  find the device-trust CA → right-click → *All Tasks → Export* → **No, do not export the private key** →
  **Base-64 encoded X.509 (.CER)** → save `device-ca.cer`. Or PowerShell:
  ```powershell
  $ca = Get-ChildItem Cert:\LocalMachine\Root |
        Where-Object { $_.Subject -match 'Contoso' -or $_.FriendlyName -match 'Contoso' } | Select-Object -First 1
  [IO.File]::WriteAllText("device-ca.cer",
    "-----BEGIN CERTIFICATE-----`n" +
    [Convert]::ToBase64String($ca.RawData,'InsertLineBreaks') + "`n-----END CERTIFICATE-----")
  ```
- **From an already-enrolled device** - open an issued PRISM agent cert → *Certification Path* →
  select the CA node → *View Certificate → Details → Copy to File* → **Base-64 X.509 (.CER)**.
- **From Intune (Graph)** - the trusted-cert profile holds the cert: `GET .../deviceManagement/
  deviceConfigurations`, find the trusted-root profile, base64-decode its `trustedRootCertificate`.

> If your PKI is two-tier (a root *and* an issuing CA), put **both** PEM blocks in the same secret
> value (concatenated). The gateway loads every certificate in the PEM as a trust anchor, so the
> device leaf validates whether it was issued by the root or the issuing CA.

**Upload it as a secret** (name it whatever you set in `caCertificateName`, e.g. `prism-device-ca`):
```bash
az keyvault secret set --vault-name contoso-prism-dev --name prism-device-ca --file device-ca.cer
```

### 5b. Wire it up in the apps deployment (`infra/apps/main.bicep` / `main.bicepparam`)

- `keyVaultName` / `keyVaultResourceGroupName` / `caCertificateName` - the existing vault, its
  resource group, and the **secret** name from §5a.
- `grantKeyVaultAccess=true` (default) grants the managed identity the built-in **Key Vault
  Secrets User** role (`4633458b-…`) on the vault.
- The gateway env vars `Gateway__CaCertificateKeyVaultUri`, `Gateway__CaCertificateName` and
  `Gateway__ManagedIdentityClientId` are populated automatically from those parameters.

The agent ships usage over mutual TLS; the gateway writes it straight to `fact.AppUsage`, and
scoring picks up the foreground-time signal automatically. Deploy/upgrade the gateway before the
agent fleet. Rotating the CA secret in Key Vault is picked up on the next gateway revision/restart.
>>>>>>> 78331c20c8456251821a9fc0bdb0410cf36fd66f

---

## 6. Operating

<<<<<<< HEAD
- Shared-mailbox / resource-account detection is **deterministic when the mailbox-settings connector is enabled** (`userPurpose`); tenants that leave it off fall back to the name-pattern + sign-in-shape heuristic.
- The desktop-agent SID↔user join is Entra-joined-only; hybrid/AD-joined devices simply yield no agent corroboration (never a penalty).
- No automatic E5→E3 tier-downgrade recommendations - doing that responsibly needs advanced-feature telemetry Prism doesn't collect. Idle premium seats still surface via inactivity, ranked first by cost.
- Savings are blank until `ref.SkuCost` is populated (Price Sheet connector or manual).
=======
- **Schedules:** connectors 02:00 UTC, scoring 03:30 UTC (a 90-minute gap so scoring never starts
  while ingest is still running). Change with `az containerapp job update --cron-expression`.
- **On demand:** `az containerapp job start -g <rg> -n prism-connectors` (then `prism-scoring`).
- **Logs:** Container Apps → each job → Execution history → Console. For retention, wire a Log
  Analytics workspace to the environment (recommended for production).
- **The connectors job has no artificial timeout** - Graph throttling can legitimately stretch a
  full per-device sweep, so the only ceiling is the job's `replicaTimeout` (24h in the Bicep).
>>>>>>> 78331c20c8456251821a9fc0bdb0410cf36fd66f

---

## 7. Cost

<<<<<<< HEAD
Issues and pull requests are welcome - new connectors (e.g., mailbox `RecipientTypeDetails`), additional reason codes, and dashboard drill-downs especially. Please keep the core principles intact: read-only, deterministic, absence-is-not-evidence, human-in-the-loop.
=======
Azure SQL serverless (auto-pauses when idle) + Container Apps Jobs (scale to zero, two short runs
a day) + one small dashboard replica + Basic ACR ≈ **€30–70 / month** at 2000 device size. The
dashboard replica is the only always-on cost; set its `minReplicas` to 0 if a cold start is fine.
>>>>>>> 78331c20c8456251821a9fc0bdb0410cf36fd66f

---

<<<<<<< HEAD
Released under the **MIT License** - see [`LICENSE`](LICENSE). *(Add a `LICENSE` file with your chosen license before publishing.)*
=======
## 8. Teardown
>>>>>>> 78331c20c8456251821a9fc0bdb0410cf36fd66f

```bash
az group delete -g <rg> --yes --no-wait     # removes SQL, ACR, Container Apps - everything
```

<<<<<<< HEAD
Prism is provided as-is. It reads your Microsoft 365 / Azure data and produces recommendations; **you** own the decision to act on any verdict. Validate reclaim candidates on real data before enabling any auto-reclaim flag. Not affiliated with or endorsed by Microsoft. "Contoso" is Microsoft's standard sample organization name and is used here as a placeholder - replace it with your own values.
=======
The managed identity's Graph permissions are removed separately wherever you granted them.

---

## 9. Troubleshooting

- **Job can't get a token (`DefaultAzureCredential` failed)** → the job's managed identity isn't
  attached, or `AZURE_CLIENT_ID` is wrong. Confirm with
  `az containerapp job show -g <rg> -n prism-connectors --query identity` and check the env var
  equals the managed identity's client id.
- **`Login failed for user '<token-identified principal>'`** → the `CREATE USER … FROM EXTERNAL
  PROVIDER` grant (§1) didn't run, or the client id doesn't match. Re-run the grant.
- **Connector logs a 403 from Graph** → a permission is missing or unconsented; grant + admin-consent it.
- **Dashboard shows sample data, not live** → it couldn't reach the database. Check its
  `Prism__ConnectionString`, that SQL allows Azure services, and that scoring has run.
- **Cost connector returns nothing** → `Prism__CostManagementScope` must be a scope the managed
  identity can read (tenant-root management group for EA; a billing/subscription scope for MCA).
>>>>>>> 78331c20c8456251821a9fc0bdb0410cf36fd66f
