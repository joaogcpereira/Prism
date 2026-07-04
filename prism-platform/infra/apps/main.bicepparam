using './main.bicep'

// =============================================================================
// Prism (AppID 0001) - PHASE 2 application parameters.
// Publishes prism-gateway / prism-dashboard / prism-connectors / prism-scoring
// into the EXISTING Phase 1 environment (contosoPrismEnvDev) / ACR (contosoprismacrdev)
// / SQL (contoso-prism-sql-dev) using the shared UAMI (id-prism-platform).
//
// >>> BEFORE DEPLOY, confirm the four "existing resource" values below against
//     your Phase 1 deployment OUTPUTS (az deployment group show ... outputs):
//       containerEnvName  -> the deployed managedEnvironments name
//       acrName           -> the token-resolved registry name (loginServer minus
//                            ".azurecr.io"); Phase 1 'contosoprismacr' + 'dev' = 'contosoprismacrdev'
//       sqlServerFqdn     -> Phase 1 output  sqlFqdn
//       sqlDatabaseName   -> Phase 1 DB name (name '${sqlName}-${env}' = 'contoso-prism-sql-dev')
// =============================================================================

// location / tags default to the resource group's own location & tags.

// ---- Existing Phase 1 resources (referenced, not created) ------------------
param containerEnvName = 'contosoPrismEnvDev'
param acrName = 'contosoprismacrdev'
param uamiName = 'id-im-prism-platform'
// UAMI lives in the same RG as the apps by default; set if it is elsewhere.
// param uamiResourceGroupName = 'dev-prism'

// ---- SQL warehouse (from Phase 1 output) ----------------------------------
param sqlServerFqdn = 'contoso-prism-sql-dev.database.windows.net'
param sqlDatabaseName = 'contoso-prism-sql-dev'

// ---- Images ----------------------------------------------------------------
// The pipeline overrides this with the build id: --parameters imageTag=$(tag).
param imageTag = 'latest'

// ---- Gateway: device-trust CA certificate (from Key Vault) ----------------
// The gateway reads the SCEP/issuing CA PUBLIC certificate from an EXISTING Key
// Vault SECRET (PEM value) at startup, using the managed identity (no cert in config).
// >>> Confirm the vault name against your resource group. <<<
param keyVaultName = 'contoso-prism-dev'
// The assigned vault lives in the SHARED resource group, separate from the apps (dev-prism):
param keyVaultResourceGroupName = 'dev-prism-shr'
// >>> Set this to the SECRET name you create in the vault holding the CA public-cert
//     PEM (see DEPLOYMENT.md §5 for how to export the device-trust CA and upload it). <<<
param caCertificateName = 'prism-device-ca'
// Idempotent "Key Vault Secrets User" grant for the managed identity. Set false
// if the vault uses the legacy access-policy model (then add a secrets(get)
// access policy for id-prism-platform out-of-band).
param grantKeyVaultAccess = true
// Optional issuer pin (thumbprint). Empty = no pin.
param gatewayExpectedIssuerThumbprint = ''

// ---- Job schedules (UTC) ---------------------------------------------------
param connectorsCron = '0 2 * * *'   // 02:00 UTC daily
param scoringCron = '30 3 * * *'     // 03:30 UTC daily (after connectors)

// ---- Connector feature toggles (all OFF by default in code) ----------------
// Turn connectors on here without touching the bicep. Start small, verify on the
// pilot data, then widen. Examples (uncomment what you need):
param connectorsExtraEnv = [
  // { name: 'Prism__EnableSignInConnector', value: 'true' }        // Entra per-app sign-ins
  // { name: 'Prism__EnableCopilotConnector', value: 'true' }       // M365 Copilot usage
  // { name: 'Prism__EnableTeamsActivityConnector', value: 'true' } // Teams calls/meetings
  // { name: 'Prism__EnableServiceDetailConnector', value: 'true' } // mailbox + OneDrive + SharePoint
  // { name: 'Prism__EnableM365AppUsageConnector', value: 'true' }  // Office desktop app usage
  // { name: 'Prism__EnableAppHealthConnector', value: 'true' }     // Intune Endpoint Analytics app health
  // { name: 'Prism__EnableMobileAppsConnector', value: 'true' }    // Intune managed-app install summary
  // { name: 'Prism__EnableSpSignInConnector', value: 'true' }      // enterprise-app (SP) sign-ins
  // { name: 'Prism__EnableMdeConnector', value: 'true' }           // Defender for Endpoint software inventory
  // { name: 'Prism__EnableMdeHunting', value: 'true' }             // Defender Advanced Hunting (process runs)
  // { name: 'Prism__EnableLeaverDates', value: 'true' }            // employeeLeaveDateTime (needs User-LifeCycleInfo.Read.All)
  // { name: 'Prism__CostManagementScope', value: '/providers/Microsoft.Management/managementGroups/<tenant-guid>' }
]

// ---- Scoring feature toggles (all OFF by default in code) -------------------
// While off, the corresponding signals surface as top-of-band REVIEW; turning a
// flag on lets that signal auto-RECLAIM. Flip on after eyeballing real candidates.
param scoringExtraEnv = [
  // { name: 'Prism__EnableCopilotReclaim', value: 'true' }
  // { name: 'Prism__EnableTeamsPhoneReclaim', value: 'true' }
  // { name: 'Prism__EnableNotInstalledReclaim', value: 'true' }
  // { name: 'Prism__EnableAppUnusedReclaim', value: 'true' }
]

// ---- Role assignment -------------------------------------------------------
// Idempotent AcrPull grant for the UAMI on the ACR. Leave true; set false only if
// you already granted it in Phase 1 and your SPN lacks Owner/UAA on the registry.
param grantAcrPull = true
