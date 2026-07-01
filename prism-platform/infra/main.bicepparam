using './main.bicep'

// =============================================================================
// Prism (AppID 0001) — consolidated parameters for the orchestrated deployment.
// Values carried verbatim from containerregistry.bicepparam + sqldatabase.bicepparam.
// Deploy target: a resource group whose name STARTS WITH 'dev-' (so the modules'
// environment token resolves to 'dev'). The pipeline enforces this.
// =============================================================================

// location / tags default to the resource group's own location & tags.

// ---- Container Registry ----------------------------------------------------
// NOTE: the module appends the env token, so 'contosoprismacrdev' becomes
// 'contosoprismacrdevdev'. If you want the login server 'contosoprismacrdev.azurecr.io',
// set this to 'contosoprismacr' instead (see the chat note). Kept verbatim here.
param acrName = 'contosoprismacr'

param acrPrivateEndpointSubnetResourceId = '/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-network-internal-non-prd-001/providers/Microsoft.Network/virtualNetworks/vnet-internal-non-prd/subnets/dev-prism-svc'

// AcrPull for the Container Apps' shared UAMI. Paste the UAMI's OBJECT (principal)
// id — NOT its client id. Leave commented to deploy the registry without the grant.
// param acrPullPrincipalId = '33333333-3333-3333-3333-333333333333'

// ---- Container Apps environment --------------------------------------------
param containerEnvName = 'contosoPrismEnvDev'

param caeInfrastructureSubnetResourceId = '/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-network-internal-non-prd-001/providers/Microsoft.Network/virtualNetworks/vnet-internal-non-prd/subnets/dev-prism-cae'

// Empty = no app-log streaming. To enable, set the FULL Log Analytics resource ID,
// e.g. /subscriptions/<subId>/resourceGroups/dev-0001/providers/Microsoft.OperationalInsights/workspaces/we-dev-0001-loganalytics-001
param logAnalyticsWorkspaceResourceId = '/subscriptions/00000000-0000-0000-0000-000000000000/resourcegroups/dev-prism/providers/microsoft.operationalinsights/workspaces/contoso-prism-law-dev'

// ---- SQL -------------------------------------------------------------------
param sqlName = 'contoso-prism-sql'
param sqlAadAdminLogin = 'sqladmin@contoso.com'
param sqlAadAdminObjectId = '22222222-2222-2222-2222-222222222222'
param sqlAadAdminTenantId = '11111111-1111-1111-1111-111111111111'
// 'sqladmin@contoso.com' is a UPN -> 'User'. Change to 'Group' if it is an Entra group.
param sqlAadAdminPrincipalType = 'User'
param sqlPrivateEndpointSubnetResourceId = '/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-network-internal-non-prd-001/providers/Microsoft.Network/virtualNetworks/vnet-internal-non-prd/subnets/dev-prism-svc'
