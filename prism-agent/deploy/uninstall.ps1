#Requires -RunAsAdministrator
<#
  uninstall.ps1 - silent uninstall for the Contoso Prism Agent.

  Intune uninstall command:
    powershell.exe -ExecutionPolicy Bypass -WindowStyle Hidden -File uninstall.ps1
#>
param(
    [string]$InstallDir = 'C:\Program Files\Prism\Agent',
    # Privacy: spooled usage batches are removed on uninstall by default.
    [bool]$PurgeData = $true
)
$ErrorActionPreference = 'SilentlyContinue'
$ServiceName = 'ContosoPrismAgent'
$DataDir     = 'C:\ProgramData\Prism\Agent'
$exe = Join-Path $InstallDir 'prism-agent.exe'

if (Test-Path $exe) {
    # The exe handles it natively: stop, disable, delete the service, and kill any
    # per-session tracker processes it spawned.
    & $exe --uninstall
} else {
    # Fallback if the exe is already gone: disable first so it can't auto-start again,
    # then stop and delete.
    & sc.exe config $ServiceName start= disabled | Out-Null
    & sc.exe stop   $ServiceName | Out-Null
    & sc.exe delete $ServiceName | Out-Null
}

# Safety net: terminate any lingering agent processes. The per-session trackers are
# independent of the service and survive its stop; an orphan keeps the exe image open
# and would block removal of the install directory below.
Get-Process -Name 'prism-agent' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

Start-Sleep -Seconds 2
Remove-Item -Recurse -Force $InstallDir -ErrorAction SilentlyContinue
if ($PurgeData) { Remove-Item -Recurse -Force $DataDir -ErrorAction SilentlyContinue }

# v2: best-effort removal of the Event Log source so an uninstall leaves nothing behind.
try { [System.Diagnostics.EventLog]::DeleteEventSource('ContosoPrismAgent') } catch { }

Write-Host "Contoso Prism Agent uninstalled."
exit 0
