// =============================================================================
// apps/main.bicep  —  Prism (AppID 0001) PHASE 2 application deployment
// -----------------------------------------------------------------------------
// Publishes the four Prism workloads INTO the resources created in Phase 1:
//   1. prism-gateway    Container App  (HTTP ingress :8080, device-agent mTLS)
//   2. prism-dashboard  Container App  (HTTP ingress :8080, read-only UI + /api)
//   3. prism-connectors Container Apps Job (scheduled ETL into SQL)
//   4. prism-scoring    Container Apps Job (scheduled verdict pass)
//
// This template does NOT create the environment / ACR / SQL / managed identity.
// Those already exist from Phase 1 (infra/main.bicep). Here we only REFERENCE
// them (by name) so the apps land in the existing internal VLAN-injected
// environment, pull from the existing private ACR, and reach SQL over the
// existing Private Endpoint — all with the shared user-assigned managed
// identity (secret-free Entra auth; no passwords, no registry credentials).
//
// DEPLOY TARGET: the SAME resource group as Phase 1 (e.g. 'dev-prism'), which is
// where the environment, ACR and SQL live. (The "dev-" env-token rule from
// Phase 1 does not apply here — these names are explicit, not token-derived.)
//
// PREREQUISITE (one-time, not expressible in Bicep): the shared managed
// identity must have a contained DB user in the Prism database with
// db_datareader + db_datawriter (and EXECUTE on the scoring procs). See
// DEPLOYMENT.md step A3:
//   CREATE USER [id-im-prism-platform] FROM EXTERNAL PROVIDER;
//   ALTER ROLE db_datareader ADD MEMBER [id-im-prism-platform];
//   ALTER ROLE db_datawriter ADD MEMBER [id-im-prism-platform];
// =============================================================================

targetScope = 'resourceGroup'

// ============================================================================ //
//  Shared parameters                                                           //
// ============================================================================ //

@description('Optional. Location for all resources. Default is the resource group location.')
param location string = resourceGroup().location

@description('Optional. Tags applied to all resources. Default is the resource group tags.')
param tags object = resourceGroup().tags

// ---- Existing Phase 1 resources (referenced, not created) ------------------ //

@description('Required. Name of the EXISTING Phase 1 Container Apps managed environment.')
param containerEnvName string = 'contosoPrismEnvDev'

@description('Required. Name of the EXISTING Phase 1 Azure Container Registry (token-resolved name, e.g. "contosoprismacrdev").')
param acrName string = 'contosoprismacrdev'

@description('Required. Name of the EXISTING shared user-assigned managed identity used for ACR pull + SQL/Graph auth.')
param uamiName string = 'id-im-prism-platform'

@description('Optional. Resource group of the shared managed identity. Defaults to this resource group.')
param uamiResourceGroupName string = resourceGroup().name

// ---- SQL warehouse (Phase 1 output) ---------------------------------------- //

@description('Required. Fully qualified domain name of the Phase 1 SQL server (Phase 1 output "sqlFqdn"), e.g. "contoso-prism-sql-dev.database.windows.net".')
param sqlServerFqdn string = 'contoso-prism-sql-dev.database.windows.net'

@description('Required. Name of the Prism database on that server. With the Phase 1 naming this is "contoso-prism-sql-dev".')
param sqlDatabaseName string = 'contoso-prism-sql-dev'

// ---- Images ---------------------------------------------------------------- //

@description('Required. Tag applied to all four images by the build pipeline (e.g. the Build.BuildId). The repo names are fixed: prism-gateway / prism-dashboard / prism-connectors / prism-scoring.')
param imageTag string = 'latest'

// ---- Gateway: device-trust CA certificate (from Key Vault) ----------------- //
// The gateway validates device client certs against the SCEP issuing/root CA.
// That CA PUBLIC certificate is stored as a SECRET (PEM) in an EXISTING Key Vault
// (the shared "dev-prism-shr" RG); the gateway reads it at startup using the shared
// managed identity. No certificate material is passed through Bicep / app config.

@description('Required. Name of the EXISTING Key Vault holding the device-trust CA certificate.')
param keyVaultName string = 'contoso-prism-dev'

@description('Optional. Resource group of the Key Vault. The Prism vault lives in the shared "dev-prism-shr" RG, separate from the apps. Defaults to this resource group.')
param keyVaultResourceGroupName string = resourceGroup().name

@description('Required. Name of the Key Vault SECRET holding the CA public certificate (PEM). It may contain the root and the issuing CA concatenated.')
param caCertificateName string = 'prism-device-ca'

@description('Optional. Create the "Key Vault Secrets User" role assignment for the managed identity on the vault. Idempotent; set false if access is managed out-of-band or the vault uses the legacy access-policy model.')
param grantKeyVaultAccess bool = true

@description('Optional. If set, the gateway additionally pins the device-cert issuer to this certificate thumbprint. Empty = no issuer pin.')
param gatewayExpectedIssuerThumbprint string = ''

// ---- Job schedules (UTC) --------------------------------------------------- //

@description('Optional. Cron (UTC) for the connectors ETL job. Default 02:00 daily.')
param connectorsCron string = '0 2 * * *'

@description('Optional. Cron (UTC) for the scoring job. Default 03:30 daily (after connectors).')
param scoringCron string = '30 3 * * *'

// ---- Optional extra env (feature toggles) ---------------------------------- //

@description('Optional. Extra env vars appended to the connectors job, e.g. enable flags ({ name: "Prism__EnableSignInConnector", value: "true" }). Default [].')
param connectorsExtraEnv array = []

@description('Optional. Extra env vars appended to the scoring job, e.g. ({ name: "Prism__EnableCopilotReclaim", value: "true" }). Default [].')
param scoringExtraEnv array = []

// ---- Role assignment ------------------------------------------------------- //

@description('Optional. Create the AcrPull role assignment for the managed identity on the ACR. Safe to leave true (idempotent); set false if Phase 1 already granted it.')
param grantAcrPull bool = true

// ============================================================================ //
//  Existing resources                                                          //
// ============================================================================ //

resource cae 'Microsoft.App/managedEnvironments@2024-10-02-preview' existing = {
  name: containerEnvName
}

resource acr 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = {
  name: acrName
}

resource uami 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = {
  name: uamiName
  scope: resourceGroup(uamiResourceGroupName)
}

// ============================================================================ //
//  Variables                                                                   //
// ============================================================================ //

var acrLoginServer = acr.properties.loginServer
var uamiClientId = uami.properties.clientId

// Secret-free SQL connection string: user-assigned managed identity, no password.
// User Id pins the exact UAMI (a container can carry several identities).
var sqlConnectionString = 'Server=tcp:${sqlServerFqdn},1433;Database=${sqlDatabaseName};Authentication=Active Directory Managed Identity;User Id=${uamiClientId};Encrypt=True;TrustServerCertificate=False;'

// Also surface the identity to DefaultAzureCredential (Graph/MDE token path).
var azureClientIdEnv = {
  name: 'AZURE_CLIENT_ID'
  value: uamiClientId
}

// --- Gateway env ------------------------------------------------------------ //
// The device-trust CA certificate is read at runtime FROM KEY VAULT by the gateway
// itself (managed identity); no certificate material is passed through here.
var gatewayEnv = concat(
  [
    { name: 'Gateway__ConnectionString', value: sqlConnectionString }
    { name: 'Gateway__Sink', value: 'warehouse' }
    // Behind Container Apps ingress: gateway serves plain HTTP and reads the
    // device cert from the X-Forwarded-Client-Cert header (clientCertificateMode
    // = 'accept' on the ingress below forwards it).
    { name: 'Gateway__BehindIngress', value: 'true' }
    { name: 'Gateway__Port', value: '8080' }
    // Device-trust CA certificate source: the gateway fetches the public cert from
    // this Key Vault SECRET (PEM) at startup using the managed identity below.
    { name: 'Gateway__CaCertificateKeyVaultUri', value: 'https://${keyVaultName}${environment().suffixes.keyvaultDns}/' }
    { name: 'Gateway__CaCertificateName', value: caCertificateName }
    { name: 'Gateway__ManagedIdentityClientId', value: uamiClientId }
    azureClientIdEnv
  ],
  empty(gatewayExpectedIssuerThumbprint) ? [] : [ { name: 'Gateway__ExpectedIssuerThumbprint', value: gatewayExpectedIssuerThumbprint } ]
)

// --- Dashboard env ---------------------------------------------------------- //
// ASPNETCORE_URLS=http://+:8080 is baked into the image (Dockerfile.dashboard).
var dashboardEnv = [
  { name: 'Prism__ConnectionString', value: sqlConnectionString }
  azureClientIdEnv
]

// --- Connectors job env ----------------------------------------------------- //
var connectorsEnv = concat(
  [
    { name: 'Prism__ConnectionString', value: sqlConnectionString }
    { name: 'Prism__Sink', value: 'sql' }
    { name: 'Prism__ManagedIdentityClientId', value: uamiClientId }
    azureClientIdEnv
  ],
  connectorsExtraEnv
)

// --- Scoring job env -------------------------------------------------------- //
var scoringEnv = concat(
  [
    { name: 'Prism__ConnectionString', value: sqlConnectionString }
    azureClientIdEnv
  ],
  scoringExtraEnv
)

// ============================================================================ //
//  AcrPull for the shared managed identity (so the apps/jobs can pull images)  //
// ============================================================================ //

// Built-in AcrPull role.
var acrPullRoleDefinitionId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')

resource acrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (grantAcrPull) {
  name: guid(acr.id, uami.id, acrPullRoleDefinitionId)
  scope: acr
  properties: {
    roleDefinitionId: acrPullRoleDefinitionId
    principalId: uami.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// Built-in "Key Vault Secrets User" (read secret contents) so the gateway's managed
// identity can read the device-trust CA certificate, which is stored as a Key Vault
// SECRET (PEM). Requires the vault to use the Azure RBAC permission model; for an
// access-policy vault set grantKeyVaultAccess = false and add a secrets(get) access
// policy for the identity out-of-band.
//
// The grant goes through a MODULE scoped to the vault's resource group because the
// vault (contoso-prism-dev) lives in a DIFFERENT resource group (dev-prism-shr) than the
// apps (dev-prism): a role assignment is created in the scope it targets, so it must be
// deployed into the vault's RG, not this one.
var keyVaultSecretsUserRoleDefinitionId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')

module keyVaultSecretsUser './keyvault-role.bicep' = if (grantKeyVaultAccess) {
  name: 'prism-kv-secrets-user'
  scope: resourceGroup(keyVaultResourceGroupName)
  params: {
    keyVaultName: keyVaultName
    principalId: uami.properties.principalId
    roleDefinitionId: keyVaultSecretsUserRoleDefinitionId
  }
}

// ============================================================================ //
//  Modules — the four Prism objects                                            //
// ============================================================================ //

// --- 1. prism-gateway (Container App, mTLS ingestion) ----------------------- //
module gateway './container-app.bicep' = {
  name: 'prism-gateway'
  params: {
    name: 'prism-gateway'
    location: location
    tags: tags
    environmentResourceId: cae.id
    uamiResourceId: uami.id
    acrLoginServer: acrLoginServer
    image: 'prism-gateway:${imageTag}'
    targetPort: 8080
    external: true // internal environment => private VNet IP only
    clientCertificateMode: 'accept' // forward device cert to the app via XFCC
    env: gatewayEnv
    minReplicas: 1
    maxReplicas: 3
  }
  dependsOn: [
    acrPull
    keyVaultSecretsUser
  ]
}

// --- 2. prism-dashboard (Container App, read-only UI) ----------------------- //
module dashboard './container-app.bicep' = {
  name: 'prism-dashboard'
  params: {
    name: 'prism-dashboard'
    location: location
    tags: tags
    environmentResourceId: cae.id
    uamiResourceId: uami.id
    acrLoginServer: acrLoginServer
    image: 'prism-dashboard:${imageTag}'
    targetPort: 8080
    external: true // internal environment => private VNet IP only
    clientCertificateMode: 'ignore'
    env: dashboardEnv
    minReplicas: 1
    maxReplicas: 3
  }
  dependsOn: [
    acrPull
  ]
}

// --- 3. prism-connectors (Container Apps Job, scheduled ETL) ----------------- //
module connectors './container-job.bicep' = {
  name: 'prism-connectors'
  params: {
    name: 'prism-connectors'
    location: location
    tags: tags
    environmentResourceId: cae.id
    uamiResourceId: uami.id
    acrLoginServer: acrLoginServer
    image: 'prism-connectors:${imageTag}'
    cronExpression: connectorsCron
    env: connectorsEnv
    cpu: '1.0'
    memory: '2Gi'
    replicaTimeout: 10800 // 3h: the install-expansion sweep can be long under throttling
  }
  dependsOn: [
    acrPull
  ]
}

// --- 4. prism-scoring (Container Apps Job, scheduled verdicts) --------------- //
module scoring './container-job.bicep' = {
  name: 'prism-scoring'
  params: {
    name: 'prism-scoring'
    location: location
    tags: tags
    environmentResourceId: cae.id
    uamiResourceId: uami.id
    acrLoginServer: acrLoginServer
    image: 'prism-scoring:${imageTag}'
    cronExpression: scoringCron
    env: scoringEnv
    cpu: '0.5'
    memory: '1Gi'
    replicaTimeout: 3600 // 1h
  }
  dependsOn: [
    acrPull
  ]
}

// ============================================================================ //
//  Outputs                                                                     //
// ============================================================================ //

@description('Private FQDN of the gateway (on the internal environment domain). Hand to the platform team if a custom DNS record is required for the device agents.')
output gatewayFqdn string = gateway.outputs.fqdn

@description('Private FQDN of the dashboard (on the internal environment domain).')
output dashboardFqdn string = dashboard.outputs.fqdn

@description('Default domain of the existing Container Apps environment.')
output environmentDefaultDomain string = cae.properties.defaultDomain

@description('Resource ID of the connectors job.')
output connectorsJobResourceId string = connectors.outputs.resourceId

@description('Resource ID of the scoring job.')
output scoringJobResourceId string = scoring.outputs.resourceId

@description('ACR login server the images are pulled from.')
output acrLoginServer string = acrLoginServer
