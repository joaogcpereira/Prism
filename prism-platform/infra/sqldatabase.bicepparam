using 'sqldatabase.bicep'

// =============================================================================
// Prism (AppID 0001) - Azure SQL parameters (PAA / Private Application Arch.)
// =============================================================================

// Required parameters
// -----------------------------------------------------------------------------
// The template appends "-${environment}" to this name (environment is derived
// from the leading token of the resource group name, e.g. "dev-prism-..." -> "dev").
// So 'contoso-prism' becomes the target server/db name 'contoso-prism-dev'.
// >>> Verify your RG name resolves the environment token to 'dev' before deploy. <<<
param name = 'contoso-prism'

// Authentication - Entra-only (no SQL admin login / password).
// -----------------------------------------------------------------------------
// Leaving administratorLogin / administratorLoginPassword unset keeps the server
// Microsoft Entra-only, matching Prism's original "no SQL passwords" design.
param aadAdminLogin = 'sqladmin@contoso.com'
param aadAdminObjectId = '22222222-2222-2222-2222-222222222222'
param aadAdminTenantId = '11111111-1111-1111-1111-111111111111'
param administrators = {
  azureADOnlyAuthentication: true // Entra-only - no SQL password path
  login: aadAdminLogin!
  // 'sqladmin@contoso.com' is a UPN -> principalType 'User'. If this is actually
  // an Entra *group* used for PAM/DBA access, change this to 'Group'.
  principalType: 'User'
  sid: aadAdminObjectId!
  tenantId: aadAdminTenantId
  administratorType: 'ActiveDirectory'
}

// PAA: Disable public network access
param publicNetworkAccess = 'Disabled'

// PAA: NO firewall rules - all access through the Private Endpoint only.
// Do NOT add firewallRules (no AllowAzureServices / AllowAllWindowsAzureIps).

// PAA: Private Endpoint on the dedicated "svc" data subnet (provided by the platform team).
// NOTE: DNS records are created manually by the network team via the change-management process after the
//       PE is deployed. Do NOT include privateDnsZoneGroup (the platform team has no Private DNS
//       Zone Contributor role). After deploy, send the network team the FQDN + PE private IP.
param privateEndpoints = [
  {
    subnetResourceId: '/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-network-internal-non-prd-001/providers/Microsoft.Network/virtualNetworks/vnet-internal-non-prd/subnets/dev-prism-svc'
  }
]

// Audit settings (server-level, to the Log Analytics target).
param auditSettings = {
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

// Contoso security defaults
param minimalTlsVersion = '1.2'
param isIPv6Enabled = 'Disabled'
param connectionPolicy = 'Default'

// The serverless database (GP_S_Gen5_2, auto-pause 60 min, min 0.5 vCore) is
// defined as the default in sqldatabase.bicep. Leave `databases` unset to use it.
// Override here only if you need a different SKU/topology.
