<#
.SYNOPSIS
    Publishes the Prism agent as a single Native-AOT executable (prism-agent.exe),
    optionally Authenticode-signed.

.DESCRIPTION
    Wraps `dotnet publish`. When a signing certificate is supplied, the csproj's
    SignPrismAgent target signs the published exe automatically (AfterTargets=Publish).
    Two signing modes are supported:
      * Thumbprint  - certificate already installed in a Windows certificate store.
      * PFX file    - path to a .pfx plus its password.
    Signing requires signtool.exe on PATH (install the Windows SDK).

    The custom icon (item 8) is embedded automatically when
    src\Prism.Agent\prism-agent.ico exists - no parameter needed.

.PARAMETER Rid
    Runtime identifier: win-x64 (default) or win-arm64.

.EXAMPLE
    .\publish.ps1 -Rid win-x64
    Unsigned build.

.EXAMPLE
    .\publish.ps1 -Rid win-x64 -SigningThumbprint 9F86D081884C7D659A2FEAA0C55AD015A3BF4F1B
    Signs using a cert already in the LocalMachine/CurrentUser store.

.EXAMPLE
    .\publish.ps1 -Rid win-x64 -SigningCertPath C:\certs\prism-codesign.pfx -SigningCertPassword 'P@ssw0rd!'
    Signs using a PFX file.
#>
[CmdletBinding()]
param(
    [ValidateSet('win-x64','win-arm64')] [string]$Rid = 'win-x64',
    [string]$Configuration = 'Release',
    [string]$SigningThumbprint='ab270beb5badfe2a2e2cad27b69c83678417e513',
    [string]$SigningCertPath,
    [string]$SigningCertPassword,
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
$proj = Join-Path $PSScriptRoot '..\src\Prism.Agent\Prism.Agent.csproj' | Resolve-Path

$args = @(
    'publish', $proj.Path,
    '-c', $Configuration,
    '-r', $Rid,
    '/p:PublishAot=true',
    "/p:TimestampUrl=$TimestampUrl"
)
if ($SigningThumbprint)  { $args += "/p:SigningThumbprint=$SigningThumbprint" }
if ($SigningCertPath)    { $args += "/p:SigningCertPath=$SigningCertPath" }
if ($SigningCertPassword){ $args += "/p:SigningCertPassword=$SigningCertPassword" }

if ($SigningThumbprint -or $SigningCertPath) {
    if (-not (Test-Path "C:\Program Files (x86)\Windows Kits\10\bin\10.0.22621.0\x64\signtool.exe" -ErrorAction SilentlyContinue)) {
        throw "signtool.exe not found on PATH. Install the Windows SDK or open a Developer prompt."
    }
    Write-Host "Publishing $Rid (signed)..." -ForegroundColor Cyan
} else {
    Write-Host "Publishing $Rid (unsigned)..." -ForegroundColor Cyan
}

dotnet @args
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }

Write-Host "Done. Output under src\Prism.Agent\bin\$Configuration\net10.0-windows\$Rid\publish\prism-agent.exe" -ForegroundColor Green
