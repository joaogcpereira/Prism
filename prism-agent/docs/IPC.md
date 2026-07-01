# Agent IPC — Named Pipe (helper → service)

The per-session **helper** measures usage as the logged-on user; the
**service** runs as LocalSystem. Measured rollups travel up this local pipe.

## Channel

- **Name:** `\\.\pipe\prism-agent-usage`
- **Owner:** the **service** (LocalSystem) creates the pipe; the helper connects
  as client. The service auto-starts at boot, so it owns the name before any
  user logs on — a hostile process can't squat it first.
- **Transport:** byte mode with **length-prefixed framing**
  (`[4-byte LE length][payload]`), 8 MB hard cap per frame.
- **Concurrency:** several server instances accept in parallel, so multiple
  interactive sessions (fast user switching / RDP) report simultaneously.

## Protocol

1. Helper connects, sends one `UsageBatch` frame (UTF-8 JSON):
   `{ schemaVersion, machineName, agentVersion, rollups[] }`.
2. Service reads it, derives the client identity (below), hands it to its
   handler, and replies with one `UsageAck` frame:
   `{ accepted, received, message?, config? }`.
3. On `accepted = true` the helper **purges prior days** it has shipped. Today's
   record keeps accumulating and is re-sent each cycle; the service upserts on
   `(device, user, date, app)`, so re-sends are idempotent.
4. `config` (optional) lets the service push tuning (sample interval, idle
   threshold) back down; the helper applies it next cycle.

All wire types live in `Prism.Agent.Contracts` and serialize via a
source-generated JSON context (AOT-safe).

## Security model

- **Identity is OS-proven, not self-reported.** The batch carries no user SID.
  The service derives it from the connected client's process token via
  `GetNamedPipeClientProcessId`, so a payload cannot spoof who it is. If the SID
  can't be read, attribution falls back to the device's primary user.
- **The client connects at `Identification` impersonation level**, so even a
  hypothetical rogue server can learn *who* the client is but cannot impersonate
  the user to reach the user's resources.
- **Explicit ACL on the pipe:** LocalSystem full control; Authenticated Users
  connect/read/write (so any logged-on user's helper can report); no one else.
  Tighten to `Interactive` (S-1-5-4) if you never expect service-account or
  scheduled-task clients.
- **Input is untrusted.** Payloads are size-bounded, schema-checked, only
  deserialized — never executed. This is a telemetry channel, never a command
  channel. Worst-case abuse is one device's own usage numbers being skewed.
- **Resilience:** one bad client never stops the accept loop; an unreachable
  service simply defers shipping (data is retained and retried).

## Try it end-to-end

```bash
# Terminal 1 - the service-side pipe endpoint
dotnet run --project src/Prism.Agent -- --service

# Terminal 2 - the session helper (measures, then ships every 5 min;
# lower the interval in Program.cs while testing)
dotnet run --project src/Prism.Agent -- --session
```

The service prints each received batch with the OS-derived user SID; the helper
logs ship/defer and any pushed config.

## Where this fits

`UsagePipeServer` is final and the full service host will reuse it unchanged.
Still to come: the **service host** (per-session helper launch via
`WTSQueryUserToken` + `CreateProcessAsUser`, local buffering/store, and **mTLS
upload** to the telemetry gateway using the Intune-provisioned device cert),
then **`.intunewin` packaging**.
