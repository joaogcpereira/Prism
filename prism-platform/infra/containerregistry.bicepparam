using 'containerregistry.bicep'

// =============================================================================
// Prism (AppID 0001) — Azure Container Registry parameters (PAA)
// =============================================================================

// Required parameters
// -----------------------------------------------------------------------------
// The template appends the environment token (e.g. 'dev') to this name, so
// 'contosoPrism' becomes 'contosoPrismdev'. ACR names are alphanumeric-only and the
// login server is always lower-cased (-> contosoprismdev.azurecr.io).
param name = 'contosoprismacrdev'

// PAA: Premium SKU required for Private Endpoint support
param acrSku = 'Premium'

// PAA: Disable public network access
param publicNetworkAccess = 'Disabled'

// PAA: Private Endpoint on the dedicated "svc" data subnet (provided by the platform team).
// NOTE: DNS A record is created by the network team via the change-management process after PE deploy.
//       Do NOT include privateDnsZoneGroup. After deploy, send the network team the registry
//       FQDN (contosoprismdev.azurecr.io) + the PE private IP.
param privateEndpoints = [
  {
    subnetResourceId: '/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-network-internal-non-prd-001/providers/Microsoft.Network/virtualNetworks/vnet-internal-non-prd/subnets/dev-prism-svc'
  }
]

// Registry's own identity (kept from the catalog sample).
param managedIdentities = { systemAssigned: true }

// AcrPull grant for the Container Apps' shared user-assigned managed identity.
// >>> Replace with the object (principal) ID of that UAMI from your main.bicep. <<<
// Leave commented until you have the value; the registry deploys fine without it
// and you can add the assignment on a later run.
// param acrPullPrincipalId = '33333333-3333-3333-3333-333333333333'
