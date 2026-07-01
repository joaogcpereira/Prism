# Deploying the Prism Agent via Intune (Win32 app)

End-to-end, no user interaction. Push it to the whole fleet, the service
registers on each device, and the per-session tracker is launched automatically
at logon.

## 1. Publish the single exe

```powershell
dotnet publish src/Prism.Agent -c Release -r win-x64 /p:PublishAot=true
# output: src/Prism.Agent/bin/Release/net10.0-windows/win-x64/publish/prism-agent.exe
```

Put `prism-agent.exe`, `install.ps1`, and `uninstall.ps1` together in one folder,
e.g. `package\`.

## 2. Wrap as .intunewin

Use Microsoft's **Win32 Content Prep Tool** (`IntuneWinAppUtil.exe`):

```
IntuneWinAppUtil.exe -c .\package -s install.ps1 -o .\out
```

Produces `install.intunewin` for upload.

## 3. Intune app settings

**Program**
- Install command:
  `powershell.exe -ExecutionPolicy Bypass -WindowStyle Hidden -File install.ps1`
- Uninstall command:
  `powershell.exe -ExecutionPolicy Bypass -WindowStyle Hidden -File uninstall.ps1`
- Install behavior: **System**
- Device restart behavior: No specific action

**Requirements**
- OS architecture: x64 (publish an arm64 build and a second app if you have ARM devices)
- Minimum OS: Windows 10 22H2 / Windows 11

**Detection rule** (choose one)
- File: path `C:\Program Files\Prism\Agent`, file `prism-agent.exe`, rule
  "file or folder exists". (Tighten later to a version check.)
- Or registry: the service key
  `HKLM\SYSTEM\CurrentControlSet\Services\ContosoPrismAgent` exists.

**Assignments**: assign to the device group; install context System means no
user needs to be signed in for the service to install.

## 4. What happens on the device

1. Intune runs `install.ps1` as SYSTEM -> copies the exe, runs
   `prism-agent.exe --install` (registers the auto-start LocalSystem service),
   sets crash recovery.
2. The service starts, opens the named pipe, and launches `prism-agent.exe
   --session` into every active user session (and at each subsequent logon).
3. The tracker measures usage and ships rollups to the service over the pipe.
4. The service spools each received batch to
   `C:\ProgramData\Prism\Agent\spool\` for upload and logs lifecycle events and
   warnings to the Windows Event Log (Application source `ContosoPrismAgent`). Routine
   per-batch receipts are not logged.

## 5. Verifying a device

- Service present:  `sc.exe query ContosoPrismAgent`  (or `Get-Service ContosoPrismAgent` — shows as "Contoso Prism Agent")
- Tracker running:  Task Manager -> a `prism-agent.exe` in the user session
- Pending uploads:  `Get-ChildItem C:\ProgramData\Prism\Agent\spool\`  (near-empty when healthy)
- Events:           Event Viewer -> Windows Logs -> Application -> source `ContosoPrismAgent`

## 6. Secure upload - device cert via SCEP + the gateway contract

The agent forwards spooled batches to your gateway over **mutual TLS**, using a
per-device certificate. Read-only posture is preserved: it only ever POSTs its
own device's usage.

**Provision the device cert (Intune):**
- Create a **SCEP** (or **PKCS**) certificate profile, deployed to the device,
  EKU = Client Authentication, key in the **machine** store (`LocalMachine\My`).
- Server-cert trust: if the gateway is on the **Azure Container Apps managed
  domain** (`*.azurecontainerapps.io`), its TLS cert is publicly trusted (DigiCert)
  and already chains on Windows — **no Trusted Certificate profile is needed**, and
  do **not** pin it (Azure rotates it). Deploy a Trusted Certificate profile only if
  the gateway uses a private/custom server cert. (Your SCEP **CA root** is consumed
  by the *gateway* to validate the device's client cert — not by the device.)

**Configure the uploader** by passing args to `install.ps1` (it writes
`C:\ProgramData\Prism\Agent\config.json`):
```
-GatewayUrl "https://gateway.contoso.com/api/v1/usage"
-CertIssuer "CN=Contoso Device CA"        # or -CertThumbprint <hex>
[-ServerCertThumbprint <hex>]             # optional: pin the gateway server cert
[-UploadIntervalSeconds 60]
```
`config.json` keys: `GatewayUrl`, `CertThumbprint`, `CertIssuer`,
`ServerCertThumbprint`, `UploadIntervalSeconds`, `MaxBatchesPerCycle`. You can
also deploy this file via a separate Intune config profile instead of install
args. No gateway configured => batches just spool locally.

**Gateway request contract (build the server side to match):**
- `POST {GatewayUrl}`
- TLS: client presents the device cert; the gateway authenticates the device by
  that cert (its subject/SAN identifies the machine).
- Header: `X-Prism-Device: <machineName>`, `User-Agent: ContosoPrismAgent/<version>`
- Body: `application/json`, camelCase (gzip-compressed when `CompressUploads` is
  on — the gateway decompresses on `Content-Encoding: gzip`), a `ReceivedBatch`:
  `{ receivedUtc, machineName, userSid, agentVersion, utcOffsetMinutes, rollups:[ UsageRollup... ] }`
  where `utcOffsetMinutes` is the device's UTC offset so the server can interpret
  each rollup's device-local `date` (`yyyy-MM-dd`). A `UsageRollup` carries only:
  `date, exePath, productName, company, fileVersion, launches, firstSeenUtc,
  lastSeenUtc, foregroundActiveSeconds, foregroundIdleSeconds,
  visibleBackgroundSeconds, minimizedSeconds, traySeconds` - no titles, URLs,
  keystrokes, or screen content.
- Response: **2xx** = accepted (agent deletes the spool file). `400/413/422` =
  permanent reject (agent quarantines it). `401/403` = auth/cert problem (agent
  keeps + logs). `5xx`/`429`/network = transient (agent retries next cycle).

**Where to watch it:**
- Pending uploads: `C:\ProgramData\Prism\Agent\spool\`
- Permanently rejected: `C:\ProgramData\Prism\Agent\quarantine\`
- Events: Event Viewer source `ContosoPrismAgent` (uploader event IDs 300-309; spool
  cap warning 110; ACL-hardening warning 111).

## Notes / caveats

- This is review-ready and tested as console instances; the **service-hosted**
  path (SCM control + `CreateProcessAsUser` into sessions) should get a first
  run on a pilot device before fleet rollout.
- `.intunewin` wrapping must be done with Microsoft's tool on a Windows machine;
  it can't be produced here.
- Antivirus/EDR: sign `prism-agent.exe` with your code-signing cert before
  packaging; an unsigned SYSTEM service that spawns processes into sessions will
  draw attention.

## 7. Contoso production wiring (Azure Container Apps gateway)

The gateway runs as a Container App behind Envoy ingress, which terminates TLS,
does the mTLS handshake, and forwards the device's leaf cert to the gateway in
the `X-Forwarded-Client-Cert` (XFCC) header. The agent needs no change for this —
it just presents its SCEP device cert; the ingress + gateway do the rest.

**One-time gateway side (deploy/runbook):**
```
# Require a client cert at the ingress so Envoy captures + forwards it as XFCC
az containerapp ingress update -n prism-gateway -g zai-rg-im-prd-001 \
  --client-certificate-mode require
# Gateway app config (already set when the gateway was deployed):
#   Gateway__BehindIngress=true            -> read client cert from XFCC, not the TLS socket
#   Gateway__ExpectedIssuerThumbprint=<SCEP CA thumbprint>   -> pin the issuing CA
```
The gateway FQDN is `https://prism-gateway.<env-suffix>.westeurope.azurecontainerapps.io`
(read it from `az containerapp show -n prism-gateway -g zai-rg-im-prd-001 --query properties.configuration.ingress.fqdn -o tsv`).

**Agent install command (pilot one device first):**
```
powershell.exe -ExecutionPolicy Bypass -WindowStyle Hidden -File install.ps1 ^
  -GatewayUrl "https://prism-gateway.<env-suffix>.westeurope.azurecontainerapps.io/api/v1/usage" ^
  -CertIssuer "CN=<your SCEP issuing CA common name>"
```
- Do **not** pass `-ServerCertThumbprint` for the managed domain (the install
  script will warn if you do) — the `*.azurecontainerapps.io` cert is publicly
  trusted and rotates.
- `-CertIssuer` selects the SCEP **device** cert from `LocalMachine\My`; the
  service runs as LocalSystem, so the machine store is correct. (Use
  `-CertThumbprint` instead if you want to lock onto one specific cert.)

**Pilot order (important):** push the **SCEP device-cert profile first** and
confirm the cert is present (`Get-ChildItem Cert:\LocalMachine\My | ? Issuer -match '<CA CN>'`)
**before** the agent app, so the very first upload cycle already has a cert. If
the agent installs first it simply spools locally (event ID 300) and starts
delivering once the cert lands — no data lost.

**End-to-end verification (pilot device -> dashboard):**
1. Device: `Get-ChildItem C:\ProgramData\Prism\Agent\spool\` should trend to
   near-empty; Event Viewer source `ContosoPrismAgent` shows event 301 (uploader
   started) and no 300/305/307. A 305 = cert/authorization reject (check the
   gateway's `ExpectedIssuerThumbprint` and that the leaf chains to that CA).
2. Gateway: `az containerapp logs show -n prism-gateway -g zai-rg-im-prd-001 --tail 50`
   shows accepted POSTs to `/api/v1/usage`.
3. Warehouse: rows appear in `fact.AppUsage` for the pilot machine.
4. Dashboard: once usage is flowing, the **Applications** tab can show real
   installed-but-idle apps (not just install footprint). Re-run scoring so idle
   days are computed against the new usage signal.

**Rollout:** after the pilot device round-trips cleanly, widen the Intune device
assignment. The SCEP profile and the agent app should target the **same** device
group so every device gets a cert and the agent together.
