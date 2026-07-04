// =============================================================================
// apps/keyvault-role.bicep  -  grant a built-in role on an EXISTING Key Vault
// -----------------------------------------------------------------------------
// Deployed as a MODULE so the role assignment lands in the VAULT'S resource
// group, which may differ from the apps' resource group. (A role assignment is
// created at the scope it targets; a resource-group deployment can only write
// into its own RG, so a cross-RG grant must go through a module scoped to that
// other resource group - see main.bicep, which sets scope: resourceGroup(...).)
//
// In Prism's case the apps live in 'dev-prism' but the assigned Key Vault
// (contoso-prism-dev) lives in the shared 'dev-prism-shr'.
// =============================================================================

targetScope = 'resourceGroup'

@description('Required. Name of the existing Key Vault in THIS module\'s resource group.')
param keyVaultName string

@description('Required. Principal (object) id to grant the role to (the managed identity).')
param principalId string

@description('Required. Full resource id of the role definition to assign.')
param roleDefinitionId string

@description('Optional. Principal type. Default ServicePrincipal (managed identity).')
param principalType string = 'ServicePrincipal'

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: keyVaultName
}

resource roleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, principalId, roleDefinitionId)
  scope: keyVault
  properties: {
    roleDefinitionId: roleDefinitionId
    principalId: principalId
    principalType: principalType
  }
}

@description('Resource id of the created role assignment.')
output roleAssignmentId string = roleAssignment.id
