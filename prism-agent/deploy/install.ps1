#Requires -RunAsAdministrator
<#
  install.ps1 - silent install for the Contoso Prism Agent.
  - Copies prism-agent.exe to the install dir.
  - Optionally writes the uploader config (gateway URL + device-cert selector).
  - Registers + starts the Windows service (exe --install, CreateService API).
  - Configures crash recovery via sc.exe (no binPath quoting involved here).

  Intune install command:
    powershell.exe -ExecutionPolicy Bypass -WindowStyle Hidden -File install.ps1 -GatewayUrl "https://gateway.contoso.com/api/v1/usage" -CertIssuer "CN=Contoso Device CA"
#>
param(
    [string]$SourceExe  = (Join-Path $PSScriptRoot 'prism-agent.exe'),
    [string]$InstallDir = 'C:\Program Files\Prism\Agent',

    # Uploader config (optional). If -GatewayUrl is omitted, batches spool locally only.
    [string]$GatewayUrl='https://prism-gateway.purplestone-935f9d4a.westeurope.azurecontainerapps.io/api/v1/usage',
    [string]$CertThumbprint='',
    [string]$CertIssuer='Contoso-Device-IssuingCA',
    [string]$ServerCertThumbprint,
    [int]   $UploadIntervalSeconds = 60
)
$ErrorActionPreference = 'Stop'
$ServiceName = 'ContosoPrismAgent'   # display name: "Contoso Prism Agent"
$DataDir     = 'C:\ProgramData\Prism\Agent'

if (-not (Test-Path $SourceExe)) { throw "prism-agent.exe not found next to this script ($SourceExe)." }

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
$target = Join-Path $InstallDir 'prism-agent.exe'
Copy-Item -Force $SourceExe $target

# Guard: pinning the Azure Container Apps managed TLS cert is fragile (Azure rotates it).
if ($ServerCertThumbprint -and $GatewayUrl -like '*azurecontainerapps.io*') {
    Write-Warning "ServerCertThumbprint is set for an *.azurecontainerapps.io gateway. Azure rotates that managed cert, which will break pinning and stop uploads. Leave it empty for the managed domain (the cert is publicly trusted); only pin a custom domain you control."
}

# Write uploader config if a gateway was provided.
if ($GatewayUrl) {
    New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
    $cfg = [ordered]@{
        GatewayUrl            = $GatewayUrl
        CertThumbprint        = $CertThumbprint
        CertIssuer            = $CertIssuer
        ServerCertThumbprint  = $ServerCertThumbprint
        UploadIntervalSeconds = $UploadIntervalSeconds
        MaxBatchesPerCycle    = 50
        CompressUploads       = $true
    }
    ($cfg | ConvertTo-Json) | Set-Content (Join-Path $DataDir 'config.json') -Encoding UTF8
    Write-Host "Wrote uploader config -> $DataDir\config.json"
}

# Register + start the service (idempotent).
& $target --install
if ($LASTEXITCODE -ne 0) { throw "Service registration failed (exit $LASTEXITCODE)." }

# Crash recovery: restart after 60s, three times, counter resets daily.
& sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null

Write-Host "Contoso Prism Agent installed to $InstallDir and service '$ServiceName' registered."
exit 0
