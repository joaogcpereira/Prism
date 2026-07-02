# Prism - Production Deployment Guide

This is the complete, from-scratch runbook to stand Prism up in production using **Bicep** and
**Azure DevOps**. It assumes nothing beyond an Azure subscription and an Entra tenant. Everything
Prism does is read-only; the whole deployment lives in one resource group and tears down with one
command.

The deployment has three parts:

1. **Schema** - create the warehouse (two SQL files, run once).
2. **Infrastructure** - provision everything with Bicep (one deployment).
3. **Enablement** - turn on the optional connectors you want and run the jobs.

---

## 0. Prerequisites

| You need | Notes |
|---|---|
| An Azure subscription + resource group | Everything is created inside it. |
| A user-assigned **managed identity** | Prism's single identity. Grant it the read-only Graph scopes below + **Cost Management Reader** on the billing scope. |
| The managed identity's **client id** and **principal (object) id** | `az identity show -g <rg> -n <mi> --query '{client:clientId,principal:principalId}'` |
| An Entra account to be **SQL admin** | The person who runs the two schema files. Its **object id**: `az ad signed-in-user show --query id -o tsv` |
| Azure CLI (Cloud Shell is easiest) | `https://shell.azure.com` - no local installs needed. |

**Graph application permissions on the managed identity (all read-only):**
`Directory.Read.All`, `Reports.Read.All`, `AuditLog.Read.All`,
`DeviceManagementManagedDevices.Read.All`, `DeviceManagementApps.Read.All`.
Grant admin consent for each. The optional Defender connectors add their own permissions (§4).

> **Report concealment.** If your tenant anonymises usage reports
> (`Reports.Read.All` returns masked names), Prism still runs and flags the affected signals as
> concealed rather than losing them silently. To get per-user M365 activity, turn off
> *"Display concealed user, group, and site names"* in the M365 admin centre's report settings.

---

## 1. Create the warehouse schema

Two files, run in order, against the `prism` database. Use the Portal **Query editor** (browser,
no tools) or `sqlcmd -G` (Entra auth).

```bash
sqlcmd -S <server>.database.windows.net -d prism -G -i schema/schema.sql
sqlcmd -S <server>.database.windows.net -d prism -G -i schema/seed-commercial.sql
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
```

Pass `enableMdeConnector=true`, `mdeAppId=$APP_ID`, `mdeTenantId=<tenant>` (the schema and views
they populate already exist in `schema.sql`). The Defender APIs are regional - set
`mdeApiBaseUrl=https://eu.api.security.microsoft.com` in the EU for lower latency.

After enabling anything, run the connectors job, then the scoring job.

---

## 5. Desktop agent + gateway (optional)

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
  resource group, and the **secret** name from §5a. At Contoso the vault is `contoso-prism-dev` in the
  **shared `dev-prism-shr`** RG while the apps are in `dev-prism`, so `keyVaultResourceGroupName` is
  set to `dev-prism-shr`. The role grant is applied through a module (`keyvault-role.bicep`) scoped
  to that RG, since a role assignment is created in the RG it targets.
- `grantKeyVaultAccess=true` (default) grants the managed identity the built-in **Key Vault
  Secrets User** role (`4633458b-…`) on the vault. Because the vault sits in a **shared** RG, the
  deploy service principal may not be allowed to create role assignments there - if the deploy
  fails on authorization, set `grantKeyVaultAccess=false` and ask the vault owner to grant **Key
  Vault Secrets User** to `id-im-prism-platform` once. (If the vault uses the legacy access-policy
  model rather than Azure RBAC, do the same and add a `secrets: get` access policy instead.)
- The gateway env vars `Gateway__CaCertificateKeyVaultUri`, `Gateway__CaCertificateName` and
  `Gateway__ManagedIdentityClientId` are populated automatically from those parameters. Cross-RG
  RBAC is fine at runtime - the identity reads the secret regardless of which RG the vault is in.
  (For local dev only, `Gateway:CaCertificatePem` / `Gateway:CaCertificatePath` remain fallbacks.)

The agent ships usage over mutual TLS; the gateway writes it straight to `fact.AppUsage`, and
scoring picks up the foreground-time signal automatically. Deploy/upgrade the gateway before the
agent fleet. Rotating the CA secret in Key Vault is picked up on the next gateway revision/restart.

---

## 6. Operating

- **Schedules:** connectors 02:00 UTC, scoring 03:30 UTC (a 90-minute gap so scoring never starts
  while ingest is still running). Change with `az containerapp job update --cron-expression`.
- **On demand:** `az containerapp job start -g <rg> -n prism-connectors` (then `prism-scoring`).
- **Logs:** Container Apps → each job → Execution history → Console. For retention, wire a Log
  Analytics workspace to the environment (recommended for production).
- **New version:** the pipeline rebuilds and redeploys on push. By hand: `az acr build … -t
  prism-connectors:v2 …` then `az containerapp job update --image …`.
- **The connectors job has no artificial timeout** - Graph throttling can legitimately stretch a
  full per-device sweep, so the only ceiling is the job's `replicaTimeout` (24h in the Bicep).

---

## 7. Cost

Azure SQL serverless (auto-pauses when idle) + Container Apps Jobs (scale to zero, two short runs
a day) + one small dashboard replica + Basic ACR ≈ **€30–70 / month** at Contoso's size. The
dashboard replica is the only always-on cost; set its `minReplicas` to 0 if a cold start is fine.

---

## 8. Teardown

```bash
az group delete -g <rg> --yes --no-wait     # removes SQL, ACR, Container Apps - everything
```

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
