# Changelog - Prism Agent

## [1.3.0-rc.1] - 2026-07-04

Release candidate. Fleet-hardening release: the agent now defends its own data path
end-to-end - who may speak to it, whether its trackers are truly alive, and what happens
to every spooled batch under long outages.

### Security
- **Interactive-only pipe ACL**: the usage pipe now admits `INTERACTIVE` (S-1-5-4)
  sessions only. Domain service accounts can no longer submit usage under their own SID
  and skew licence metrics; scheduled-task clients, if ever needed, must be granted
  explicitly.
- **Pin + revocation TLS validation**: server-certificate pinning now *adds to* the
  tolerant chain check instead of replacing it - a pinned-but-revoked certificate (the
  stolen-key scenario pinning exists for) fails; only revocation-unreachable remains
  tolerated.
- **Installer ACL pre-hardening**: `install.ps1` locks `%ProgramData%\Prism\Agent` to
  SYSTEM + Administrators *before* writing config, closing the window before first
  service start. `uninstall.ps1` now also removes the Event Log source.

### Reliability
- **Silent-tracker watchdog**: the service tracks each session's last shipped batch via
  the pipe (OS-derived session id). A tracker that is alive as a process but silent past
  the threshold (20 min; 10 min first-contact grace) is killed and relaunched - hung
  trackers no longer measure nothing invisibly.
- **Upload backoff + poison protection**: per-file exponential backoff (60 s → 30 min,
  jittered), gateway `Retry-After` honoured per file, and a max-retry quarantine
  (`.maxretries`, loud event) so one undeliverable batch can never block the drain.
- **Private-key startup probe**: an inaccessible device-cert key (per-user container,
  TPM ACL) is reported as ONE clear actionable event at startup instead of endless
  cryptic TLS failures.
- **Spool-loss visibility**: a failed spool write (full disk, ACL) now logs an error -
  an acknowledged batch can no longer disappear silently.
- Documented wrap-safety of the idle-detection tick arithmetic (audited; modular
  subtraction is correct across the 49.7-day tick wrap).

### Known limitations
- UWP/Store apps hosted by ApplicationFrameHost still roll up under the host process
  when the hosted child cannot be resolved; AppUserModelID-based identity is planned.
- The SID↔user join remains Entra-joined-only (hybrid devices yield no agent
  corroboration - never a penalty).
