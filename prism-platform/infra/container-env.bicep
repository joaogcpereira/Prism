// =============================================================================
// modules/container-env.bicep  -  Prism Container Apps managed environment
// -----------------------------------------------------------------------------
// PAA (Private Application Architecture) compliant:
//   * VNet-injected into the dedicated "cae" subnet (delegated to
//     Microsoft.App/environments) provided by the platform team.
//   * internal = true  -> the environment's ingress uses an INTERNAL load
//     balancer (private VNet IP); there is no public inbound IP. With an
//     internal environment, apps with ingress.external = true are reachable
//     only from inside the VNet / peered networks, never the public internet.
//   * publicNetworkAccess = 'Disabled'.
// =============================================================================

// =============== //
// Parameters      //
// =============== //

@description('Required. The name of the Container Apps managed environment.')
param envName string = 'contosoPrismEnvDev'

@description('Optional. Location for all resources. Default is the resource group location.')
param location string = resourceGroup().location

@description('Optional. Tags of the resource. Default is the resource group tags.')
param tags object = resourceGroup().tags

@description('Required. Resource ID of the delegated "cae" subnet (Microsoft.App/environments) the environment is VNet-injected into.')
param caeInfrastructureSubnetResourceId string = '/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-network-internal-non-prd-001/providers/Microsoft.Network/virtualNetworks/vnet-internal-non-prd/subnets/dev-prism-cae'

@description('Optional. Whether the environment uses an internal-only load balancer (private VNet IP, no public ingress IP). PAA default is true.')
param internal bool = true

@description('Optional. Whether public network access to the environment is allowed. PAA default is "Disabled".')
@allowed([
  'Enabled'
  'Disabled'
])
param publicNetworkAccess string = 'Disabled'

@description('Optional. Whether the environment is zone redundant. Default is false.')
param zoneRedundant bool = false

@description('Optional. Resource ID of a Log Analytics workspace for app log streaming. If empty, app log collection is not configured (Application Insights does not require a Private Endpoint).')
param logAnalyticsWorkspaceResourceId string = 'xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx'

// =============== //
// Variables       //
// =============== //

var collectLogs = !empty(logAnalyticsWorkspaceResourceId)

// =============== //
// Dependencies    //
// =============== //

// Optional cross-RG reference to the Log Analytics workspace (only when provided).
resource laws 'Microsoft.OperationalInsights/workspaces@2022-10-01' existing = if (collectLogs) {
  name: last(split(logAnalyticsWorkspaceResourceId, '/'))
  scope: resourceGroup(split(logAnalyticsWorkspaceResourceId, '/')[2], split(logAnalyticsWorkspaceResourceId, '/')[4])
}

resource env 'Microsoft.App/managedEnvironments@2024-10-02-preview' = {
  name: envName
  location: location
  tags: tags
  properties: {
    // PAA: private, VNet-injected, no public inbound IP.
    vnetConfiguration: {
      infrastructureSubnetId: caeInfrastructureSubnetResourceId
      internal: internal
    }
    publicNetworkAccess: publicNetworkAccess
    zoneRedundant: zoneRedundant
    appLogsConfiguration: collectLogs
      ? {
          destination: 'log-analytics'
          logAnalyticsConfiguration: {
            customerId: laws.properties.customerId
            sharedKey: laws.listKeys().primarySharedKey
          }
        }
      : null
  }
}

// =============== //
// Outputs         //
// =============== //

@description('The resource ID of the managed environment.')
output envId string = env.id

@description('The name of the managed environment.')
output envName string = env.name

@description('The default domain of the environment (used to build app FQDNs).')
output defaultDomain string = env.properties.defaultDomain

@description('The static private IP of the internal load balancer. Send this + the default domain to the platform team so the network team can create the wildcard A record in the Private DNS Zone.')
output staticIp string = env.properties.staticIp
