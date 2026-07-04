// ============================================================
//  LocalSink.cs  (Prism.Agent)
//  Local working state for the service, under %ProgramData%\Prism\Agent:
//    spool\<ts>-<id>.json   - upload queue (one file per batch)
//    quarantine\            - batches the gateway permanently rejected
//  Plus the Windows Event Log (Application source "ContosoPrismAgent") for
//  service LIFECYCLE and WARNINGS/ERRORS only.
//
//  Design notes
//  ------------
//  * No per-batch file log. Earlier builds appended every received
//    batch to a permanent received\usage-*.jsonl audit; that grew
//    without bound and could fill the disk over time, so it has been
//    removed. The authoritative record lives server-side in the
//    warehouse once the uploader delivers each batch.
//  * No per-batch Event Log entry either - routine receipts are silent.
//    The Event Log carries start/stop, config, and warnings/errors,
//    which is what a SIEM / monitoring agent actually wants.
//  * Self-bounding queues. The uploader drains spool\ over mTLS and
//    deletes files on success. If the gateway is unreachable for a
//    long time the spool is capped (oldest dropped) so it can never
//    fill the disk; quarantine is likewise bounded by count and age.
// ============================================================
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Prism.Agent.Contracts;

namespace Prism.Agent;

internal static class LocalSink
{
    private static readonly object s_gate = new();

    public static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Prism", "Agent");
    public static readonly string SpoolDir      = Path.Combine(Dir, "spool");
    public static readonly string QuarantineDir = Path.Combine(Dir, "quarantine");

    // ---- Disk-safety caps (failure-mode only; steady state stays near-empty) ----
    // A permanently unreachable gateway must never be able to fill the disk.
    private const int  MaxSpoolFiles      = 20_000;            // hard ceiling on queued batches
    private const long MaxSpoolBytes      = 256L * 1024 * 1024; // ...or 256 MB, whichever hits first
    private const int  SpoolLowWaterFiles = 18_000;            // prune down to this when over the ceiling
    private const int  SpoolScanThreshold = 500;               // below this, skip the size scan (steady state)
    private const int  MaxQuarantineFiles = 1_000;
    private static readonly TimeSpan MaxQuarantineAge = TimeSpan.FromDays(30);

    private const string EventSource  = "ContosoPrismAgent";
    private const string EventLogName = "Application";
    private static bool s_eventReady;

    // Rate-limit the "spool full" warning so a long outage doesn't itself spam the log.
    private static DateTime s_lastSpoolWarnUtc = DateTime.MinValue;
    private static readonly TimeSpan SpoolWarnInterval = TimeSpan.FromMinutes(10);

    public static void Init()
    {
        foreach (var d in new[] { SpoolDir, QuarantineDir })
            try { Directory.CreateDirectory(d); } catch { }

        HardenDataDirAcl();

        try
        {
            if (OperatingSystem.IsWindows() && !EventLog.SourceExists(EventSource))
                EventLog.CreateEventSource(new EventSourceCreationData(EventSource, EventLogName));
            s_eventReady = OperatingSystem.IsWindows();
        }
        catch { s_eventReady = false; }

        // Clean up anything left over from previous runs.
        EnforceQuarantineCap();
        EnforceSpoolCaps();
    }

    /// <summary>
    /// Spool the batch for upload. Writes a single JSON file atomically
    /// (temp + rename) so the uploader never observes a half-written file,
    /// then enforces the spool disk-safety caps. No permanent audit log is
    /// kept; routine receipts are silent.
    /// </summary>
    public static void WriteBatch(UsageContext ctx)
    {
        var record = new ReceivedBatch(
            DateTimeOffset.UtcNow.ToString("o"),
            // Identity is stamped by the SERVICE, not taken from the helper's payload:
            // the helper runs in the user's session, so a malicious user could put any
            // machine name in the batch. The SID is likewise OS-derived (UsageContext).
            Environment.MachineName,
            ctx.ClientUserSid,
            ctx.Batch.AgentVersion,
            ctx.Batch.UtcOffsetMinutes,
            ctx.Batch.Rollups);

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(record, AgentJsonContext.Default.ReceivedBatch);

        // Chronological, collision-proof filename => FIFO ordering by name.
        string name  = $"{DateTime.UtcNow:yyyyMMddTHHmmssfffffff}-{Guid.NewGuid():N}.json";
        string final = Path.Combine(SpoolDir, name);
        string tmp   = final + ".tmp";                 // uploader globs *.json, so .tmp is ignored

        lock (s_gate)
        {
            try
            {
                File.WriteAllBytes(tmp, payload);
                File.Move(tmp, final, overwrite: true); // atomic publish on the same volume
            }
            catch (Exception ex)
            {
                try { File.Delete(tmp); } catch { }
                // v2: a failed spool write means REAL data loss (the tracker already got its
                // ACK) - that must be visible, not silent. Rate-limited via the spool-warn
                // window so a full disk can't itself flood the event log.
                DateTime nowUtc = DateTime.UtcNow;
                if (nowUtc - s_lastSpoolWarnUtc >= SpoolWarnInterval)
                {
                    s_lastSpoolWarnUtc = nowUtc;
                    Log($"FAILED to spool a usage batch ({ex.GetType().Name}) - that batch is lost. " +
                        "Check disk space / ACLs on " + SpoolDir, EventLogEntryType.Error, eventId: 112);
                }
            }

            EnforceSpoolCaps();
        }
    }

    /// <summary>
    /// Lock %ProgramData%\Prism\Agent down to SYSTEM + Administrators (inheritance
    /// disabled). ProgramData's default ACL would let ANY local user read other
    /// users' spooled usage batches AND drop forged *.json files into the spool -
    /// which the uploader would then deliver to the gateway as if the agent
    /// produced them. The service runs as LocalSystem, so it can always apply this;
    /// in console/dev mode (non-admin) the attempt just logs a warning.
    /// </summary>
    private static void HardenDataDirAcl()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var di = new DirectoryInfo(Dir);
            var ds = new DirectorySecurity();
            ds.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

            ds.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            ds.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            ds.SetOwner(admins);
            di.SetAccessControl(ds);
        }
        catch (Exception ex)
        {
            Log($"could not harden the data-dir ACL (running unprivileged?): {ex.Message}",
                EventLogEntryType.Warning, eventId: 111);
        }
    }

    public static void Log(string message, EventLogEntryType type = EventLogEntryType.Information, int eventId = 1)
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
        if (!s_eventReady) return;
        try { EventLog.WriteEntry(EventSource, message, type, eventId); } catch { }
    }

    // ========================================================
    //  Quarantine: bound by count and age (called by the uploader)
    // ========================================================
    public static void EnforceQuarantineCap()
    {
        try
        {
            var files = new DirectoryInfo(QuarantineDir).GetFiles("*.json");
            if (files.Length == 0) return;

            DateTime cutoff = DateTime.UtcNow - MaxQuarantineAge;

            // Drop anything older than the age limit.
            foreach (FileInfo f in files)
                if (f.LastWriteTimeUtc < cutoff)
                    try { f.Delete(); } catch { }

            // Then, if still over the count cap, drop the oldest by name (FIFO).
            string[] remaining = Directory.GetFiles(QuarantineDir, "*.json");
            if (remaining.Length <= MaxQuarantineFiles) return;

            Array.Sort(remaining, StringComparer.Ordinal);
            int drop = remaining.Length - MaxQuarantineFiles;
            for (int i = 0; i < drop; i++)
                try { File.Delete(remaining[i]); } catch { }
        }
        catch { /* best-effort hygiene */ }
    }

    // ========================================================
    //  Spool cap: never let a stuck queue fill the disk
    // ========================================================
    private static void EnforceSpoolCaps()
    {
        try
        {
            string[] files = Directory.GetFiles(SpoolDir, "*.json");

            // Cheap steady-state exit: the uploader keeps the spool near-empty, so
            // a small queue needs no size scan. Only a real backlog gets weighed.
            if (files.Length < SpoolScanThreshold) return;

            Array.Sort(files, StringComparer.Ordinal);     // oldest first

            var sizes = new long[files.Length];
            long total = 0;
            for (int i = 0; i < files.Length; i++)
            {
                try { sizes[i] = new FileInfo(files[i]).Length; } catch { sizes[i] = 0; }
                total += sizes[i];
            }

            bool overFiles = files.Length > MaxSpoolFiles;
            bool overBytes = total > MaxSpoolBytes;
            if (!overFiles && !overBytes) return;

            // If over the file ceiling, prune down to the low-water mark (hysteresis);
            // otherwise prune only enough to get back under the byte cap.
            int fileTarget = overFiles ? SpoolLowWaterFiles : int.MaxValue;

            int dropped = 0;
            for (int i = 0; i < files.Length; i++)
            {
                bool needFiles = (files.Length - dropped) > fileTarget;
                bool needBytes = total > MaxSpoolBytes;
                if (!needFiles && !needBytes) break;

                try { File.Delete(files[i]); dropped++; total -= sizes[i]; }
                catch { /* maybe the uploader just delivered it */ }
            }

            if (dropped > 0)
            {
                DateTime nowUtc = DateTime.UtcNow;
                if (nowUtc - s_lastSpoolWarnUtc >= SpoolWarnInterval)
                {
                    s_lastSpoolWarnUtc = nowUtc;
                    Log($"Upload spool exceeded its limit ({MaxSpoolFiles} files / " +
                        $"{MaxSpoolBytes / (1024 * 1024)} MB); dropped {dropped} oldest queued batch(es) " +
                        "to protect disk space. Check gateway connectivity / device certificate.",
                        EventLogEntryType.Warning, eventId: 110);
                }
            }
        }
        catch { /* best-effort */ }
    }
}

