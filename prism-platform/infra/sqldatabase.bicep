// =============================================================================
// sqldatabase.bicep  -  Prism (AppID 0001) SQL logical server + database
// -----------------------------------------------------------------------------
// Conformed to the the platform team "blessed" PAA template (sql/server .../using-private-access).
// This REPLACES the old hand-rolled modules/sql.bicep.
//
// Differences from the stock catalog sample (all intentional, all PAA-safe):
//   * customDatabases default rebuilt as a General Purpose *serverless* DB
//     (GP_S_Gen5_2, auto-pause 60 min, min 0.5 vCore) to keep Prism's original
//     cost-control design instead of the sample's fixed S0 Standard DB.
//   * Entra-only authentication is driven from the .bicepparam
//     (azureADOnlyAuthentication: true, no SQL login/password).
//
// REQUIRED sibling file (copy it from the same catalog folder, unchanged):
//   sqldatabaseparamtypes.bicep   <-- imported below; build fails without it.
// =============================================================================

// =============== //
// Parameters      //
// =============== //

@description('Required. The name of the server.')
param name string = 'contoso-prism-sql-dev'

@description('Optional. Custom instance name for server. If you do not provide this name, the template will generate the name based on the Cloud Platform naming convention. We do not recommend deviating from our naming convention. We recommend that you use this only if you have existing resources named based on a different convention. Custom default value is null.')
param customServerInstanceName string?

@description('Optional. Custom instance name for database. If you do not provide this name, the template will generate the name based on the Cloud Platform naming convention. We do not recommend deviating from our naming convention. We recommend that you use this only if you have existing resources named based on a different convention. Custom default value is null.')
param customDatabaseInstanceName string?

@description('Optional. Location for all resources. AVM Bicep default value is "resourceGroup().location".')
param location string = resourceGroup().location

@description('Conditional. The administrator username for the server. Required if no `administrators` object for AAD authentication is provided. AVM Bicep default value is null.')
param administratorLogin string?

@description('Conditional. The administrator login password. Required if no `administrators` object for AAD authentication is provided. AVM Bicep default value is null.')
@secure()
param administratorLoginPassword string?

@description('Optional. Login name of the server administrator. AVM Bicep default value is null.')
param aadAdminLogin string?

@description('Optional. SID (object ID) of the server administrator. AVM Bicep default value is null.')
param aadAdminObjectId string?

@description('Optional. Tenant ID of the administrator. AVM Bicep default value is null.')
param aadAdminTenantId string?

import * as Types from 'sqldatabaseparamtypes.bicep'

@description('Conditional. The Azure Active Directory (AAD) administrator authentication. Required if no `administratorLogin` & `administratorLoginPassword` is provided. AVM Bicep default value is null.')
param administrators Types.serverExternalAdministratorType?

@description('Optional. The Elastic Pools to create in the server. AVM Bicep default value is null.')
param elasticPools Types.elasticPoolType[]?

@description('Optional. The databases to create in the server. AVM Bicep default value is null.')
param databases Types.databaseType[]?

@description('Optional. The firewall rules to create in the server. AVM Bicep default value is null.')
param firewallRules Types.firewallRuleType[]?

@description('Optional. The virtual network rules to create in the server. AVM Bicep default value is null.')
param virtualNetworkRules Types.virtualNetworkRuleType[]?

@description('Optional. The security alert policies to create in the server. AVM Bicep default value is null.')
param securityAlertPolicies Types.securityAlerPolicyType[]?

@description('Optional. The keys to configure. AVM Bicep default value is null.')
param keys Types.keyType[]?

@description('Optional. The vulnerability assessment configuration. AVM Bicep default value is null.')
param vulnerabilityAssessmentsObj Types.vulnerabilityAssessmentType?

@description('Optional. The audit settings configuration. If you want to disable auditing, set the parameter to an empty object. AVM Bicep default value is null.')
param auditSettings Types.auditSettingsType?

@description('Optional. Key vault reference and secret settings for the module\'s secrets export. AVM Bicep default value is null.')
param secretsExportConfiguration Types.secretsExportConfigurationType?

@description('Optional. The failover groups configuration. AVM Bicep default value is null.')
param failoverGroups Types.failoverGroupType[]?

// Import "avm-common-types" module for using the commonly used defined types
import {
  managedIdentityAllType
  lockType
  roleAssignmentType
  customerManagedKeyWithAutoRotateType
  privateEndpointSingleServiceType
} from 'br/public:avm/utl/types/avm-common-types:0.6.1'

@description('Optional. The managed identity definition for this resource. AVM Bicep default value is null.')
param managedIdentities managedIdentityAllType?

@description('Optional. The lock settings of the service. AVM Bicep default value is null.')
param lock lockType?

@description('Optional. Array of role assignments to create. AVM Bicep default value is null.')
param roleAssignments roleAssignmentType[]?

@description('Optional. The customer managed key definition for server TDE. AVM Bicep default value is null.')
param customerManagedKey customerManagedKeyWithAutoRotateType?

@description('Optional. Configuration details for private endpoints. For security reasons, it is recommended to use private endpoints whenever possible. AVM Bicep default value is null.')
param privateEndpoints privateEndpointSingleServiceType[]?

@description('Conditional. The resource ID of a user assigned identity to be used by default. Required if "userAssignedIdentities" is not empty. AVM Bicep default value is null.')
param primaryUserAssignedIdentityResourceId string?

@description('Optional. Tags of the resource. AVM Bicep default value is null.')
param tags object = resourceGroup().tags

@description('Optional. Enable/Disable usage telemetry for module. AVM Bicep default value is true.')
param enableTelemetry bool?

@description('Optional. The Client id used for cross tenant CMK scenario. AVM Bicep default value is null.')
param federatedClientId string?

@metadata({
  example: '''
  '1.0'
  '1.1'
  '1.2'
  '1.3'
  '''
})
@description('Optional. Minimal TLS version allowed. AVM Bicep default value is "1.2".')
param minimalTlsVersion string?

@metadata({
  example: '''
  'Disabled'
  'Enabled'
  '''
})
@description('Optional. Whether or not to enable IPv6 support for this server. AVM Bicep default value is "Disabled".')
param isIPv6Enabled string?

@description('Optional. Whether or not public network access is allowed for this resource. For security reasons it should be disabled. If not specified, it will be disabled by default if private endpoints are set and neither firewall rules nor virtual network rules are set. AVM Bicep default value is empty.')
@metadata({
  example: '''
  ''
  'Enabled'
  'Disabled'
  'SecuredByPerimeter'
  '''
})
param publicNetworkAccess string = 'Disabled'

@description('Optional. Whether or not to restrict outbound network access for this server. AVM Bicep default value is null.')
@metadata({
  example: '''
  'Enabled'
  'Disabled'
  '''
})
param restrictOutboundNetworkAccess string?

@description('Optional. SQL logical server connection policy. AVM Bicep default value is "Default".')
@metadata({
  example: '''
  'Default'
  'Redirect'
  'Proxy'
  '''
})
param connectionPolicy string?

// =============== //
// Variables       //
// =============== //

var environment = substring(resourceGroup().name, 0, indexOf(resourceGroup().name, '-'))

var sqlServerName = empty(customServerInstanceName) ? '${name}-${environment}' : customServerInstanceName

var sqlDatabaseName = empty(customDatabaseInstanceName) ? '${name}-${environment}' : customDatabaseInstanceName

var diagnosticSettingsName = '${sqlDatabaseName}-diagnosticlogging'

var diagnosticsWorkspaceId = '${subscription().id}/resourcegroups/${environment}-0001/providers/microsoft.operationalinsights/workspaces/we-${environment}-0001-loganalytics-001'

// Prism default DB: General Purpose *serverless* (cost-controlled), Entra-only.
// Auto-pauses after 60 min idle, scales down to 0.5 vCore, 2 vCore ceiling.
// NOTE: minCapacity is typed as a *string* by the AVM database type (the module
// converts it with json() internally) - '0.5' is correct, 0.5 will not compile.
var customDatabases = empty(databases)
  ? [
      {
        // Note: databaseType has no `location` property - the AVM module sets each
        // database's location from the server location automatically.
        name: sqlDatabaseName!
        sku: {
          name: 'GP_S_Gen5_2' // General Purpose Serverless Gen5, 2 vCores
          tier: 'GeneralPurpose'
          family: 'Gen5'
          capacity: 2
        }
        autoPauseDelay: 60 // minutes idle before auto-pause (min allowed is 60)
        minCapacity: '0.5' // floor vCores when resumed (string - see note above)
        collation: 'SQL_Latin1_General_CP1_CI_AS'
        createMode: 'Default'
        maxSizeBytes: 268435456000 // 250 GB
        requestedBackupStorageRedundancy: 'Local'
        tags: resourceGroup().tags
        zoneRedundant: false
        availabilityZone: -1
        diagnosticSettings: [
          {
            name: diagnosticSettingsName
            workspaceResourceId: diagnosticsWorkspaceId
            logCategoriesAndGroups: [
              {
                category: 'SQLSecurityAuditEvents'
              }
              {
                category: 'Errors'
              }
              {
                category: 'SQLInsights'
              }
            ]
            metricCategories: [
              {
                category: 'Basic'
              }
            ]
          }
        ]
      }
    ]
  : databases

// =============== //
// Dependencies    //
// =============== //

module sqlServerDatabase 'br/public:avm/res/sql/server:0.21.1' = {
  name: 'sqlServerDatabaseAVMDeployment'
  params: {
    name: sqlServerName!
    location: location
    administratorLogin: administratorLogin
    administratorLoginPassword: administratorLoginPassword
    managedIdentities: managedIdentities
    lock: lock
    roleAssignments: roleAssignments
    customerManagedKey: customerManagedKey
    privateEndpoints: privateEndpoints
    primaryUserAssignedIdentityResourceId: primaryUserAssignedIdentityResourceId
    tags: tags
    enableTelemetry: enableTelemetry
    databases: customDatabases
    elasticPools: elasticPools
    firewallRules: firewallRules
    virtualNetworkRules: virtualNetworkRules
    securityAlertPolicies: securityAlertPolicies
    keys: keys
    administrators: administrators
    federatedClientId: federatedClientId
    minimalTlsVersion: minimalTlsVersion
    isIPv6Enabled: isIPv6Enabled
    publicNetworkAccess: publicNetworkAccess
    restrictOutboundNetworkAccess: restrictOutboundNetworkAccess
    failoverGroups: failoverGroups
    auditSettings: auditSettings
    vulnerabilityAssessmentsObj: vulnerabilityAssessmentsObj
    secretsExportConfiguration: secretsExportConfiguration
    connectionPolicy: connectionPolicy
  }
}

// =============== //
// Outputs         //
// =============== //

@description('The name of the deployed SQL server.')
output name string = sqlServerDatabase.outputs.name

@description('The resource ID of the deployed SQL server.')
output resourceId string = sqlServerDatabase.outputs.resourceId

@description('The fully qualified domain name of the deployed SQL server.')
output fullyQualifiedDomainName string = sqlServerDatabase.outputs.fullyQualifiedDomainName

@description('The resource group of the deployed SQL server.')
output resourceGroupName string = sqlServerDatabase.outputs.resourceGroupName

@description('The principal ID of the system assigned identity.')
output systemAssignedMIPrincipalId string? = sqlServerDatabase.outputs.?systemAssignedMIPrincipalId

@description('The location the resource was deployed into.')
output location string = sqlServerDatabase.outputs.location
