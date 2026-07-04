// ============================================================
//  SessionLauncher.cs  (Prism.Agent)
//  Launches THIS exe with --session inside each interactive user
//  session, from the LocalSystem service (session 0 can't host the
//  UI hooks itself). Standard WTSQueryUserToken + CreateProcessAsUser
//  pattern. Tracks one tracker pid per session and (via the
//  AgentService watchdog) relaunches if it dies.
// ============================================================
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Prism.Agent;

internal static partial class SessionNative
{
    internal const int  WTSActive = 0;                       // WTS_CONNECTSTATE_CLASS.WTSActive
    internal const uint MAXIMUM_ALLOWED = 0x02000000;
    internal const int  SecurityImpersonation = 2;           // SECURITY_IMPERSONATION_LEVEL
    internal const int  TokenPrimary = 1;                    // TOKEN_TYPE
    internal const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    internal const uint CREATE_NO_WINDOW          = 0x08000000;

    [StructLayout(LayoutKind.Sequential)]
    internal struct STARTUPINFOW
    {
        public uint cb;
        public nint lpReserved, lpDesktop, lpTitle;
        public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public ushort wShowWindow, cbReserved2;
        public nint lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_INFORMATION
    {
        public nint hProcess, hThread;
        public uint dwProcessId, dwThreadId;
    }

    [LibraryImport("wtsapi32.dll", EntryPoint = "WTSEnumerateSessionsW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WTSEnumerateSessions(nint hServer, uint reserved, uint version, out nint ppSessionInfo, out uint pCount);

    [LibraryImport("wtsapi32.dll")]
    internal static partial void WTSFreeMemory(nint pMemory);

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WTSQueryUserToken(uint sessionId, out nint phToken);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DuplicateTokenEx(nint hExistingToken, uint dwDesiredAccess, nint lpTokenAttributes,
        int impersonationLevel, int tokenType, out nint phNewToken);

    [LibraryImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CreateEnvironmentBlock(out nint lpEnvironment, nint hToken, [MarshalAs(UnmanagedType.Bool)] bool bInherit);

    [LibraryImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyEnvironmentBlock(nint lpEnvironment);

    [LibraryImport("advapi32.dll", EntryPoint = "CreateProcessAsUserW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CreateProcessAsUser(nint hToken, nint lpApplicationName, nint lpCommandLine,
        nint lpProcessAttributes, nint lpThreadAttributes, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags, nint lpEnvironment, nint lpCurrentDirectory, in STARTUPINFOW si, out PROCESS_INFORMATION pi);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint h);
}

internal sealed class SessionLauncher
{
    private readonly object _gate = new();
    private readonly Dictionary<uint, int> _sessionPid = new();   // sessionId -> tracker pid

    public void LaunchInAllActiveSessions()
    {
        if (!SessionNative.WTSEnumerateSessions(nint.Zero, 0, 1, out nint info, out uint count)) return;
        try
        {
            // WTS_SESSION_INFOW { DWORD SessionId; LPWSTR pWinStationName; WTS_CONNECTSTATE_CLASS State; }
            int stride      = nint.Size == 8 ? 24 : 12;
            int stateOffset = nint.Size == 8 ? 16 : 8;
            for (uint i = 0; i < count; i++)
            {
                nint cur = info + (int)(i * stride);
                uint sessionId = unchecked((uint)Marshal.ReadInt32(cur, 0));
                int  state     = Marshal.ReadInt32(cur, stateOffset);
                if (state == SessionNative.WTSActive)
                    LaunchInSession(sessionId);
            }
        }
        finally { SessionNative.WTSFreeMemory(info); }
    }

    public void LaunchInSession(uint sessionId)
    {
        if (sessionId == 0) return;                              // session 0 is non-interactive

        lock (_gate)
        {
            if (_sessionPid.TryGetValue(sessionId, out int pid) && IsAlive(pid)) return;

            if (!SessionNative.WTSQueryUserToken(sessionId, out nint userToken)) return; // no logged-on user

            nint dup = nint.Zero, env = nint.Zero, cmd = nint.Zero, desktop = nint.Zero;
            try
            {
                if (!SessionNative.DuplicateTokenEx(userToken, SessionNative.MAXIMUM_ALLOWED, nint.Zero,
                        SessionNative.SecurityImpersonation, SessionNative.TokenPrimary, out dup))
                    return;

                SessionNative.CreateEnvironmentBlock(out env, dup, false);   // best-effort

                string exe = Environment.ProcessPath ?? "";
                if (exe.Length == 0) return;
                cmd     = Marshal.StringToHGlobalUni($"\"{exe}\" --session");
                desktop = Marshal.StringToHGlobalUni(@"winsta0\default");

                var si = new SessionNative.STARTUPINFOW
                {
                    cb = (uint)Marshal.SizeOf<SessionNative.STARTUPINFOW>(),
                    lpDesktop = desktop
                };
                uint flags = SessionNative.CREATE_UNICODE_ENVIRONMENT | SessionNative.CREATE_NO_WINDOW;

                if (SessionNative.CreateProcessAsUser(dup, nint.Zero, cmd, nint.Zero, nint.Zero, false,
                        flags, env, nint.Zero, in si, out SessionNative.PROCESS_INFORMATION pi))
                {
                    _sessionPid[sessionId] = (int)pi.dwProcessId;
                    _sessionLaunchTicks[sessionId] = Environment.TickCount64;   // v2: liveness baseline
                    LocalSink.Log($"Launched session tracker in session {sessionId} (pid {pi.dwProcessId}).", eventId: 200);
                    SessionNative.CloseHandle(pi.hProcess);
                    SessionNative.CloseHandle(pi.hThread);
                }
                else
                {
                    LocalSink.Log($"CreateProcessAsUser failed for session {sessionId} ({Marshal.GetLastPInvokeError()}).",
                                  System.Diagnostics.EventLogEntryType.Warning, eventId: 201);
                }
            }
            finally
            {
                if (env     != nint.Zero) SessionNative.DestroyEnvironmentBlock(env);
                if (cmd     != nint.Zero) Marshal.FreeHGlobal(cmd);
                if (desktop != nint.Zero) Marshal.FreeHGlobal(desktop);
                if (dup     != nint.Zero) SessionNative.CloseHandle(dup);
                if (userToken != nint.Zero) SessionNative.CloseHandle(userToken);
            }
        }
    }

    // v2: when each session's tracker last shipped a batch (baseline = launch time).
    private readonly Dictionary<uint, long> _sessionLaunchTicks = new();

    /// <summary>
    /// v2 liveness: restart trackers that are ALIVE as processes but SILENT on the pipe.
    /// IsAlive() only proves the process exists; a wedged tracker passes that check and
    /// silently stops measuring. A healthy tracker ships every ~5 minutes, so one that
    /// has said nothing for <paramref name="silentAfterMs"/> (well past several ship
    /// intervals) is presumed hung: kill + relaunch. Sessions that have not yet made
    /// FIRST contact get <paramref name="firstContactGraceMs"/> from launch (logon storms,
    /// profile load) before the same treatment.
    /// </summary>
    public void RestartSilentTrackers(Func<uint, long?> lastContact, long silentAfterMs, long firstContactGraceMs)
    {
        var restart = new List<uint>();
        long now = Environment.TickCount64;
        lock (_gate)
        {
            foreach ((uint sid, int pid) in _sessionPid)
            {
                if (!IsAlive(pid)) continue;                       // dead → normal relaunch path handles it
                long started = _sessionLaunchTicks.TryGetValue(sid, out long t) ? t : now;
                long? lc = lastContact(sid);
                bool silent = lc is { } c ? (now - Math.Max(c, started)) > silentAfterMs
                                          : (now - started) > firstContactGraceMs;
                if (silent) restart.Add(sid);
            }
        }
        foreach (uint sid in restart)
        {
            LocalSink.Log($"Session {sid} tracker is alive but has shipped nothing for too long - restarting it.",
                          System.Diagnostics.EventLogEntryType.Warning, eventId: 202);
            StopSession(sid);
            UsagePipeServer.ForgetSessionContact(sid);
            LaunchInSession(sid);
        }
    }

    public void ForgetSession(uint sessionId)
    {
        lock (_gate) { _sessionPid.Remove(sessionId); _sessionLaunchTicks.Remove(sessionId); }
    }

    /// <summary>Terminate the tracker spawned into a single session (e.g. on logoff).</summary>
    public void StopSession(uint sessionId)
    {
        lock (_gate)
        {
            if (_sessionPid.TryGetValue(sessionId, out int pid)) Kill(pid);
            _sessionPid.Remove(sessionId);
            _sessionLaunchTicks.Remove(sessionId);
        }
    }

    /// <summary>Terminate every tracker this launcher spawned (on service stop/uninstall).</summary>
    public void StopAll()
    {
        lock (_gate)
        {
            foreach (int pid in _sessionPid.Values) Kill(pid);
            _sessionPid.Clear();
        }
    }

    private static bool IsAlive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }

    private static readonly string AgentProcessName =
        Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "prism-agent");

    private static void Kill(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            // Guard against PID reuse: only terminate it if it is actually our agent exe.
            if (!p.HasExited && string.Equals(p.ProcessName, AgentProcessName, StringComparison.OrdinalIgnoreCase))
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit(2000);
            }
        }
        catch { /* already gone or no access: best-effort */ }
    }
}
