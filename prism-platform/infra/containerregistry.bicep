// =============================================================================
// containerregistry.bicep  -  Prism (AppID 0001) Azure Container Registry
// =============================================================================

// =============== //
// Parameters      //
// =============== //

@description('Required. Name of the Container Registry. Must be globally unique and alphanumeric only.')
param name string = ''

@description('Optional Custom instance name for Container Registry. If you do not provide this name, the template will generate the name based on the Cloud Platform naming convention. We do not recommend deviating from our naming convention. We recommend that you use this only if you have existing resources named based on a different convention. Custom default value is null.')
param customInstanceName string?

@description('Optional. Location for all resources. AVM Bicep default value is "resourceGroup().location".')
param location string = resourceGroup().location

@description('Optional. Tier of your Azure container registry. Premium is required for Private Endpoint support. AVM Bicep default value is "Premium".')
@metadata({
  example: '''
  'Basic'
  'Standard'
  'Premium'
  '''
})
param acrSku string = 'Premium'

@description('Optional. Whether or not public network access is allowed for this resource. For security reasons it should be disabled. PAA default value is "Disabled".')
@metadata({
  example: '''
  ''
  'Enabled'
  'Disabled'
  '''
})
param publicNetworkAccess string = 'Disabled'

@description('Optional. Whether or not zone redundancy is enabled for this container registry. AVM Bicep default value is "Disabled".')
param zoneRedundancy string?

@description('Optional. The value that indicates whether the admin user is enabled. AVM Bicep default value is false.')
param acrAdminUserEnabled bool?

@description('Optional. The value that indicates whether the export policy is enabled. AVM Bicep default value is "enabled".')
param exportPolicyStatus string?

@description('Optional. The value that indicates whether the quarantine policy is enabled. AVM Bicep default value is "disabled".')
param quarantinePolicyStatus string?

@description('Optional. The value that indicates whether the trust policy is enabled. AVM Bicep default value is "disabled".')
param trustPolicyStatus string?

@description('Optional. The value that indicates whether the retention policy is enabled. AVM Bicep default value is "enabled".')
param retentionPolicyStatus string?

@description('Optional. The number of days to retain an untagged manifest after which it gets purged. AVM Bicep default value is 15.')
param retentionPolicyDays int?

@description('Optional. The managed identity definition for this resource. AVM Bicep default value is null.')
import { managedIdentityAllType } from 'br/public:avm/utl/types/avm-common-types:0.6.1'
param managedIdentities managedIdentityAllType?

// Import "avm-common-types" module for using the commonly used defined types
import { lockType, roleAssignmentType, privateEndpointSingleServiceType, diagnosticSettingFullType } from 'br/public:avm/utl/types/avm-common-types:0.6.1'

@description('Optional. The lock settings of the service. AVM Bicep default value is null.')
param lock lockType?

@description('Optional. Array of role assignments to create. AVM Bicep default value is null.')
param roleAssignments roleAssignmentType[]?

@description('Optional. Principal ID (object ID) of the user-assigned managed identity that the Container Apps use to pull images. When provided, an AcrPull role assignment is created on this registry. AVM Bicep default value is null.')
param acrPullPrincipalId string?

@description('Optional. Configuration details for private endpoints. For security reasons, it is recommended to use private endpoints whenever possible. AVM Bicep default value is null.')
param privateEndpoints privateEndpointSingleServiceType[]?

@description('Optional. Tags of the resource. AVM Bicep default value is null.')
param tags object = resourceGroup().tags

@description('Optional. The diagnostic settings of the service. AVM Bicep default value is null.')
param diagnosticSettings diagnosticSettingFullType[]?

@description('Optional. Enable/Disable usage telemetry for module. AVM Bicep default value is true.')
param enableTelemetry bool?

// =============== //
// Variables       //
// =============== //

var environment = substring(resourceGroup().name, 0, indexOf(resourceGroup().name, '-'))

var registryName = empty(customInstanceName) ? '${name}${environment}' : customInstanceName

// AcrPull (built-in role 7f951dda-...). Merge any caller-supplied role assignments
// with an AcrPull grant for the Container Apps' managed identity when supplied.
var acrPullRoleDefinitionId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')

var effectiveRoleAssignments = concat(
  roleAssignments ?? [],
  empty(acrPullPrincipalId)
    ? []
    : [
        {
          principalId: acrPullPrincipalId!
          roleDefinitionIdOrName: acrPullRoleDefinitionId
          principalType: 'ServicePrincipal'
        }
      ]
)

// =============== //
// Dependencies    //
// =============== //

module containerRegistry 'br/public:avm/res/container-registry/registry:0.10.0' = {
  name: 'containerRegistryAVMDeployment'
  params: {
    name: registryName!
    location: location
    acrSku: acrSku
    publicNetworkAccess: publicNetworkAccess
    zoneRedundancy: zoneRedundancy
    acrAdminUserEnabled: acrAdminUserEnabled
    exportPolicyStatus: exportPolicyStatus
    quarantinePolicyStatus: quarantinePolicyStatus
    trustPolicyStatus: trustPolicyStatus
    retentionPolicyStatus: retentionPolicyStatus
    retentionPolicyDays: retentionPolicyDays
    managedIdentities: managedIdentities
    lock: lock
    tags: tags
    enableTelemetry: enableTelemetry
    privateEndpoints: privateEndpoints
    diagnosticSettings: diagnosticSettings
    roleAssignments: empty(effectiveRoleAssignments) ? null : effectiveRoleAssignments
  }
}

// =============== //
// Outputs         //
// =============== //

@description('The resource ID of the Container Registry.')
output resourceId string = containerRegistry.outputs.resourceId

@description('The name of the Container Registry.')
output name string = containerRegistry.outputs.name

@description('The name of the resource group the Container Registry was created in.')
output resourceGroupName string = containerRegistry.outputs.resourceGroupName

@description('The login server URL of the Container Registry.')
output loginServer string = containerRegistry.outputs.loginServer

@description('The location the resource was deployed into.')
output location string = containerRegistry.outputs.location
