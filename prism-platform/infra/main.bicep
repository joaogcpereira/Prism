// =============================================================================
// main.bicep  —  Prism (AppID 0001) infrastructure orchestrator (PAA-compliant)
// -----------------------------------------------------------------------------
// Deploys, at RESOURCE GROUP scope, exactly the three resources defined in the
// sibling modules:
//   1. containerregistry.bicep  -> Azure Container Registry (Premium, Private
//      Endpoint, public access Disabled)
//   2. container-env.bicep      -> Container Apps managed environment
//      (VNet-injected, internal LB, public access Disabled)
//   3. sqldatabase.bicep        -> Azure SQL logical server + serverless DB
//      (Entra-only auth, Private Endpoint, public access Disabled)
//
// IMPORTANT — environment token:
//   containerregistry.bicep and sqldatabase.bicep derive their `environment`
//   token from the LEADING segment of resourceGroup().name:
//       environment = substring(rgName, 0, indexOf(rgName, '-'))
//   So this template MUST be deployed into a resource group whose name STARTS
//   WITH 'dev-'  (e.g. 'dev-prism-prism')  for names to resolve to '...dev'.
//   The pipeline enforces this with a guard step.
//
// LAYOUT: keep all of these files in the SAME folder (the modules import each
// other and the AVM types by relative/registry path):
//   main.bicep  main.bicepparam
//   containerregistry.bicep  container-env.bicep
//   sqldatabase.bicep  sqldatabaseparamtypes.bicep
// =============================================================================

targetScope = 'resourceGroup'

// ============================================================================ //
//  Shared parameters                                                           //
// ============================================================================ //

@description('Optional. Location for all resources. Default is the resource group location.')
param location string = resourceGroup().location

@description('Optional. Tags applied to all resources. Default is the resource group tags.')
param tags object = resourceGroup().tags

// ============================================================================ //
//  Container Registry parameters                                               //
// ============================================================================ //

@description('Required. Base name for the Container Registry. The module appends the environment token (e.g. "dev"). Alphanumeric only.')
param acrName string

@description('Required. Resource ID of the "svc" subnet for the ACR Private Endpoint.')
param acrPrivateEndpointSubnetResourceId string

@description('Optional. Principal (OBJECT) ID of the Container Apps user-assigned managed identity to grant AcrPull. Leave empty to skip the assignment (registry still deploys).')
param acrPullPrincipalId string = ''

// ---------------------------------------------------------------------------
// OPTIONAL: auto-resolve the AcrPull principal id from an existing UAMI instead
// of pasting it above. Uncomment this block + the wiring in the ACR module call,
// and set acrPullUamiName / acrPullUamiResourceGroupName in main.bicepparam.
//
// @description('Optional. Name of the existing Container Apps UAMI to grant AcrPull.')
// param acrPullUamiName string = ''
// @description('Optional. Resource group of that UAMI. Defaults to this RG.')
// param acrPullUamiResourceGroupName string = resourceGroup().name
//
// resource acrPullUami 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = if (!empty(acrPullUamiName)) {
//   name: acrPullUamiName
//   scope: resourceGroup(acrPullUamiResourceGroupName)
// }
// ---------------------------------------------------------------------------

// ============================================================================ //
//  Container Apps environment parameters                                       //
// ============================================================================ //

@description('Optional. Name of the Container Apps managed environment.')
param containerEnvName string = 'contosoPrismEnvDev'

@description('Required. Resource ID of the delegated "cae" subnet the environment is VNet-injected into.')
param caeInfrastructureSubnetResourceId string

@description('Optional. FULL resource ID of a Log Analytics workspace for Container Apps log streaming. Empty disables app-log collection (no Private Endpoint needed for App Insights).')
param logAnalyticsWorkspaceResourceId string = ''

// ============================================================================ //
//  SQL parameters                                                              //
// ============================================================================ //

@description('Optional. Base name for the SQL server/database. The module appends the environment token (e.g. "dev").')
param sqlName string = 'contoso-prism'

@description('Required. Entra admin login (UPN or group display name) for the SQL server.')
param sqlAadAdminLogin string

@description('Required. Entra admin object ID (SID) for the SQL server.')
param sqlAadAdminObjectId string

@description('Required. Entra tenant ID of the SQL admin.')
param sqlAadAdminTenantId string

@description('Optional. Principal type of the Entra SQL admin. Use "Group" if the login is an Entra group used for PAM/DBA access.')
@allowed([
  'Application'
  'Group'
  'User'
])
param sqlAadAdminPrincipalType string = 'User'

@description('Required. Resource ID of the "svc" subnet for the SQL Private Endpoint.')
param sqlPrivateEndpointSubnetResourceId string

// ============================================================================ //
//  Modules                                                                     //
// ============================================================================ //

// --- 1. Azure Container Registry -------------------------------------------- //
module containerRegistry './containerregistry.bicep' = {
  name: 'prism-acr'
  params: {
    name: acrName
    location: location
    tags: tags
    acrSku: 'Premium' // PAA: Premium required for Private Endpoint
    publicNetworkAccess: 'Disabled' // PAA
    managedIdentities: {
      systemAssigned: true // registry's own identity (kept from catalog sample)
    }
    privateEndpoints: [
      {
        // No privateDnsZoneGroup: the network team creates the A record via the change-management process.
        subnetResourceId: acrPrivateEndpointSubnetResourceId
      }
    ]
    // AcrPull grant for the Container Apps' shared UAMI (skipped when empty).
    acrPullPrincipalId: empty(acrPullPrincipalId) ? null : acrPullPrincipalId
    // To auto-resolve from an existing UAMI instead, replace the line above with:
    // acrPullPrincipalId: empty(acrPullUamiName) ? null : acrPullUami.properties.principalId
  }
}

// --- 2. Container Apps managed environment ---------------------------------- //
module containerEnv './container-env.bicep' = {
  name: 'prism-cae'
  params: {
    envName: containerEnvName
    location: location
    tags: tags
    caeInfrastructureSubnetResourceId: caeInfrastructureSubnetResourceId
    internal: true // PAA: internal-only load balancer (private VNet IP)
    publicNetworkAccess: 'Disabled' // PAA
    zoneRedundant: false
    logAnalyticsWorkspaceResourceId: logAnalyticsWorkspaceResourceId
  }
}

// --- 3. Azure SQL logical server + serverless database ---------------------- //
module sqlDatabase './sqldatabase.bicep' = {
  name: 'prism-sql'
  params: {
    name: sqlName
    location: location
    tags: tags
    // Entra-only administration (no SQL login/password path).
    administrators: {
      azureADOnlyAuthentication: true
      login: sqlAadAdminLogin
      principalType: sqlAadAdminPrincipalType
      sid: sqlAadAdminObjectId
      tenantId: sqlAadAdminTenantId
      administratorType: 'ActiveDirectory'
    }
    publicNetworkAccess: 'Disabled' // PAA
    // PAA: NO firewall rules — all access via the Private Endpoint only.
    privateEndpoints: [
      {
        // No privateDnsZoneGroup: the network team creates DNS via the change-management process.
        subnetResourceId: sqlPrivateEndpointSubnetResourceId
      }
    ]
    // Server-level auditing to the Log Analytics target.
    auditSettings: {
      state: 'Enabled'
      name: 'Default'
      retentionDays: 365
      auditActionsAndGroups: [
        'SUCCESSFUL_DATABASE_AUTHENTICATION_GROUP'
        'DATABASE_LOGOUT_GROUP'
        'USER_CHANGE_PASSWORD_GROUP'
        'BATCH_COMPLETED_GROUP'
        'FAILED_DATABASE_AUTHENTICATION_GROUP'
        'DATABASE_OBJECT_CHANGE_GROUP'
        'DATABASE_OBJECT_PERMISSION_CHANGE_GROUP'
        'DATABASE_PERMISSION_CHANGE_GROUP'
        'DATABASE_PRINCIPAL_CHANGE_GROUP'
        'DATABASE_ROLE_MEMBER_CHANGE_GROUP'
        'SCHEMA_OBJECT_CHANGE_GROUP'
        'SCHEMA_OBJECT_OWNERSHIP_CHANGE_GROUP'
        'SCHEMA_OBJECT_PERMISSION_CHANGE_GROUP'
      ]
      isAzureMonitorTargetEnabled: true
      isStorageSecondaryKeyInUse: false
    }
    // Contoso security defaults.
    minimalTlsVersion: '1.2'
    isIPv6Enabled: 'Disabled'
    connectionPolicy: 'Default'
    // `databases` intentionally left unset -> the module's default GP_S_Gen5_2
    // serverless DB (auto-pause 60 min, min 0.5 vCore) is used.
  }
}

// ============================================================================ //
//  Outputs  (the values to hand to the platform team after deploy)                     //
// ============================================================================ //

@description('Resource ID of the Container Registry.')
output acrResourceId string = containerRegistry.outputs.resourceId

@description('Login server of the Container Registry (send to the network team with the PE private IP).')
output acrLoginServer string = containerRegistry.outputs.loginServer

@description('Resource ID of the Container Apps managed environment.')
output caeResourceId string = containerEnv.outputs.envId

@description('Default domain of the Container Apps environment (used to build app FQDNs).')
output caeDefaultDomain string = containerEnv.outputs.defaultDomain

@description('Static private IP of the environment internal load balancer (send to the platform team with the default domain for the wildcard A record).')
output caeStaticIp string = containerEnv.outputs.staticIp

@description('Resource ID of the SQL logical server.')
output sqlResourceId string = sqlDatabase.outputs.resourceId

@description('Fully qualified domain name of the SQL server (send to the network team with the PE private IP).')
output sqlFqdn string = sqlDatabase.outputs.fullyQualifiedDomainName
