<#
  grant-prism-identity.ps1  —  Prism (AppID 0001)
  -----------------------------------------------------------------------------
  Grants the Prism user-assigned managed identity the permissions the platform
  needs that are NOT created by the Phase 2 Bicep deployment:

    1. Key Vault   — get on secrets (the gateway reads the device-trust CA cert)   [az]
    2. Microsoft Graph — application permissions for the connectors                [Microsoft.Graph]
    3. (optional) Cost Management Reader — pass -CostManagementScope               [az]
    4. (optional) ACR pull                                                         [az, commented]

  NOT handled here:
    - ACR pull is normally created by the Bicep (grantAcrPull=true). Only use the
      optional block below if the deploy service principal can't create that role
      assignment (then also set grantAcrPull=false in main.bicepparam).
    - SQL access is a T-SQL step — run grant-prism-sql.sql against the Prism DB.

  Prerequisites:
    - Azure CLI signed in:            az login
    - Microsoft Graph PowerShell:     Install-Module Microsoft.Graph -Scope CurrentUser
    - The Graph grants require a GLOBAL ADMINISTRATOR or PRIVILEGED ROLE ADMINISTRATOR
      (assigning application permissions / admin consent).

  Usage:
    pwsh ./grant-prism-identity.ps1
    pwsh ./grant-prism-identity.ps1 -CostManagementScope "/providers/Microsoft.Management/managementGroups/<tenant-guid>"
#>

[CmdletBinding()]
param(
  [string]   $SubscriptionId = '00000000-0000-0000-0000-000000000000',
  [string]   $IdentityName   = 'id-im-prism-platform',
  [string]   $IdentityRg     = 'dev-prism',
  [string]   $KeyVaultName   = 'contoso-prism-dev',
  # Pass the scope the cost connector queries (a management group or subscription id) to also
  # grant Cost Management Reader. Leave empty to skip — the cost connector is off unless you set
  # Prism__CostManagementScope on the connectors job.
  [string]   $CostManagementScope = '',
  # Microsoft Graph APPLICATION permissions. Comment out any connector you won't enable.
  [string[]] $GraphPermissions = @(
    'Directory.Read.All',                      # users, subscribed SKUs, license assignments (always needed)
    'Reports.Read.All',                        # M365 usage: apps, services, Copilot, Teams, mailbox/OneDrive/SharePoint
    'AuditLog.Read.All',                       # Entra per-app + service-principal sign-ins
    'DeviceManagementManagedDevices.Read.All', # Intune detected apps / managed devices / endpoint analytics app health
    'DeviceManagementApps.Read.All',           # Intune managed-app install summary (EnableMobileAppsConnector)
    'User-LifeCycleInfo.Read.All'              # employeeLeaveDateTime / leaver dates (EnableLeaverDates) — optional
  )
)

$ErrorActionPreference = 'Stop'

# --- 1. Resolve the identity's principal (object) id + client id -------------
az login --use-device-code
az account set --subscription $SubscriptionId | Out-Null
$principalId = az identity show -n $IdentityName -g $IdentityRg --query principalId -o tsv
$clientId    = az identity show -n $IdentityName -g $IdentityRg --query clientId    -o tsv
if (-not $principalId) { throw "Managed identity '$IdentityName' not found in resource group '$IdentityRg'." }
Write-Host "Identity '$IdentityName'  principalId=$principalId  clientId=$clientId" -ForegroundColor Cyan

# --- 2. Key Vault: grant 'get' on secrets ------------------------------------
# contoso-prism-dev uses the ACCESS-POLICY model -> set-policy (NOT an RBAC role).
# If your vault is RBAC (enableRbacAuthorization=true), replace this with:
#   az role assignment create --assignee-object-id $principalId --assignee-principal-type ServicePrincipal `
#     --role "Key Vault Secrets User" --scope (az keyvault show -n $KeyVaultName --query id -o tsv)
az keyvault set-policy --name $KeyVaultName --object-id $principalId --secret-permissions get | Out-Null
Write-Host "Granted Key Vault secret 'get' on '$KeyVaultName'." -ForegroundColor Green

# --- 3. Microsoft Graph application permissions ------------------------------
Connect-MgGraph -UseDeviceCode -Scopes 'AppRoleAssignment.ReadWrite.All','Application.Read.All'

# The managed identity's service principal: its appId == the identity's clientId.
$miSp    = Get-MgServicePrincipal -Filter "appId eq '$clientId'" | Select-Object -First 1
$graphSp = Get-MgServicePrincipal -Filter "appId eq '00000003-0000-0000-c000-000000000000'" | Select-Object -First 1
if (-not $miSp)    { throw "Service principal for identity clientId '$clientId' not found in Entra." }
if (-not $graphSp) { throw "Microsoft Graph service principal not found in this tenant." }

$existing = Get-MgServicePrincipalAppRoleAssignment -ServicePrincipalId $miSp.Id

foreach ($perm in $GraphPermissions) {
  $role = $graphSp.AppRoles | Where-Object { $_.Value -eq $perm -and $_.AllowedMemberTypes -contains 'Application' }
  if (-not $role) { Write-Warning "Graph app role '$perm' not found — skipped."; continue }
  if ($existing | Where-Object { $_.AppRoleId -eq $role.Id -and $_.ResourceId -eq $graphSp.Id }) {
    Write-Host "Already granted: $perm" -ForegroundColor DarkGray
    continue
  }
  New-MgServicePrincipalAppRoleAssignment -ServicePrincipalId $miSp.Id `
    -PrincipalId $miSp.Id -ResourceId $graphSp.Id -AppRoleId $role.Id | Out-Null
  Write-Host "Granted Graph app permission: $perm" -ForegroundColor Green
}

# --- 4. (optional) ACR pull --------------------------------------------------
# The Phase 2 Bicep grants AcrPull automatically (grantAcrPull=true). Uncomment ONLY
# if the deploy service principal can't create that assignment, and set grantAcrPull=false.
# $acrId = az acr show -n contosoprismacrdev -g dev-prism --query id -o tsv
# az role assignment create --assignee-object-id $principalId --assignee-principal-type ServicePrincipal `
#   --role AcrPull --scope $acrId | Out-Null
# Write-Host "Granted AcrPull on contosoprismacrdev." -ForegroundColor Green

# --- 5. (optional) Cost Management Reader ------------------------------------
# Needed ONLY if you enable the Azure cost connector (Prism__CostManagementScope). Granted
# here only when you pass -CostManagementScope (use the SAME MG/subscription the connector
# queries; the role must be at that scope or a parent).
if ($CostManagementScope) {
  az role assignment create --assignee-object-id $principalId --assignee-principal-type ServicePrincipal `
    --role "Cost Management Reader" --scope $CostManagementScope | Out-Null
  Write-Host "Granted Cost Management Reader at: $CostManagementScope" -ForegroundColor Green
} else {
  Write-Host "Skipped Cost Management Reader (no -CostManagementScope; cost connector is off by default)." -ForegroundColor DarkGray
}

Write-Host "`nDone. Remaining step: run grant-prism-sql.sql against the Prism database (see DEPLOYMENT.md §1)." -ForegroundColor Cyan
