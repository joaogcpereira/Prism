# Prism Win32 Usage-Metering Agent — Single Executable

One deployable binary, `prism-agent.exe` (plus a shared Contracts library that
compiles into it). Now includes a real Windows Service host, a per-session
launcher, service install/uninstall, local upload spooling, and silent
Intune deployment scripts.

## Two projects

| Folder | Project | Kind | Produces |
|---|---|---|---|
| `src/Prism.Agent.Contracts` | class library (net10.0) | compiled in | — |
| `src/Prism.Agent` | executable (net10.0-windows, AOT) | **`prism-agent.exe`** | the single binary you deploy |

## Modes (one binary)

```
prism-agent.exe                -> service mode (real Windows Service if launched by the SCM;
                                   console/dev host otherwise)
prism-agent.exe --service      -> same, explicit
prism-agent.exe --session      -> per-session usage tracker (the service launches this for you)
prism-agent.exe --install      -> register + start the Windows service (run elevated)
prism-agent.exe --uninstall    -> stop + remove the service (run elevated)
prism-agent.exe --help
```

The service runs as LocalSystem in session 0 and **cannot** see windows in user
sessions, so it launches **this same exe** with `--session` into each interactive
session via `CreateProcessAsUser`. One file, two contexts.

## Privacy posture (data minimisation)

This is a **licence-metering** agent, not a surveillance tool, and the data model
enforces that. A shipped `UsageRollup` records only *which executable* ran and
*how many seconds* it spent in each visibility state (foreground-active,
foreground-idle, visible-background, minimised, tray) on a given day, plus
file-version metadata and launch count. It deliberately carries **no window
titles, document names, URLs, command lines, keystrokes, or screen content** —
exactly what licence true-up / reclaim needs and nothing more. User attribution
is the OS-proven SID of the connecting session (derived by the service, not
self-reported in the payload), so usage maps to a licence holder without the
payload being able to spoof identity.

## Source layout (src/Prism.Agent)

- `Program.cs` — argument dispatch
- `ServiceMode.cs` — runs as a service if SCM-launched, else console host
- `ServiceHost.cs` — Windows Service control plumbing (P/Invoke, AOT-clean) incl. session-change
- `AgentService.cs` — orchestrates pipe server + session launcher + watchdog
- `SessionLauncher.cs` — `WTSQueryUserToken` + `CreateProcessAsUser` into each session
- `SessionMode.cs` — the tracker host (pump thread + ACK-gated ship loop)
- `InstallMode.cs` — `--install` / `--uninstall` via the service-control API
- `LocalSink.cs` — local upload spool + quarantine (self-bounding), and the
  Windows Event Log (lifecycle + warnings only; no per-batch logging)
- `UsageTracker.cs`, `UsageModels.cs`, `WindowNative.cs` — the measurement engine
- `UsagePipeServer.cs`, `ProcessNative.cs` — service-side pipe + OS-derived client SID
- `UsagePipeClient.cs` — session-side pipe client
- `Uploader.cs` — mTLS uploader (device-cert auth, drains the spool to the gateway)

(Five native-interop classes — `WindowNative`, `ProcessNative`, `ServiceNative`,
`SessionNative`, `ScmNative` — coexist cleanly in one assembly.)

## Build / run / publish

```bash
dotnet build Prism.Agent.slnx -c Release

# local/dev (console): same exe, two terminals
dotnet run --project src/Prism.Agent -- --service     # pipe server (console host)
dotnet run --project src/Prism.Agent -- --session     # tracker

# the single deployable native exe
dotnet publish src/Prism.Agent -c Release -r win-x64   /p:PublishAot=true
dotnet publish src/Prism.Agent -c Release -r win-arm64 /p:PublishAot=true
```

## Install on a device

Manual (elevated):
```
prism-agent.exe --install      # registers auto-start LocalSystem service + starts it
prism-agent.exe --uninstall    # stops + removes it
```

Fleet (Intune, silent): see `deploy/INTUNE.md`. Scripts: `deploy/install.ps1`,
`deploy/uninstall.ps1`.

## Where to see that it's working

- **Upload spool (service):** `C:\ProgramData\Prism\Agent\spool\*.json`
  — one file per received batch, awaiting upload. The uploader deletes each on
  delivery, so in a healthy fleet this stays near-empty; permanently rejected
  batches land in `quarantine\`. (There is intentionally no permanent local
  audit file — it would grow without bound; the warehouse is the system of record.)
- **Event Log:** Event Viewer -> Windows Logs -> Application -> source `ContosoPrismAgent`.
- **Session tracker (console/dev):** prints a startup line + a per-ship summary.

## Status / what's next

Done: measurement engine, named-pipe IPC, single-exe merge, Windows Service host,
per-session launcher, install/uninstall, self-bounding local spool, **mTLS uploader
(device-cert auth, spool/retry/quarantine)**, and Intune scripts.

The agent is feature-complete for v1. Remaining work is the **gateway/server
side** (receive the POSTed batches, authenticate by client cert, land them in the
warehouse) and the rest of the Prism platform (attribution scoring, dashboards).

**Honest status:** review-ready; the console paths are tested. The
service-hosted path (SCM control + `CreateProcessAsUser` into sessions), the
service-control install, and the mTLS upload (cert selection from
`LocalMachine\My`, client-auth handshake) should all get a first run on a
**pilot device** before fleet rollout. Sign the exe before packaging. Build on
Windows with the .NET 10 SDK.
