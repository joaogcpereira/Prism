// ============================================================
//  UsageTracker.cs
//  The heart of accurate usage measurement.
//
//  Single-threaded by design: a WinEvent foreground hook and a
//  WM_TIMER snapshot both run on one message-pump thread, so all
//  state is touched from one thread with no locks.
//
//  Accuracy model:
//   - Focus transitions  -> event-precise (the hook).
//   - Idle               -> GetLastInputInfo at each accrual.
//   - Other apps' presence (visible/minimized/tray)
//                        -> sampled every SampleInterval.
//
//  At every "accrue" point we attribute the wall-clock elapsed
//  since the last accrual to EACH running app, each in its single
//  current state (foreground app from the hook+idle, everyone
//  else from the last snapshot). States are mutually exclusive,
//  so totals never double-count. The slice is split at local
//  midnight so per-day numbers are exact, and a slice longer than
//  GapSeconds (sleep/resume, a stalled pump) is treated as a gap
//  and not attributed - we never invent usage the agent did not
//  actually observe.
//
//  Live re-tuning: the service can push a new sample interval /
//  idle threshold via the pipe ACK. ApplyConfig() updates the idle
//  threshold immediately and re-arms the snapshot timer on the
//  pump thread (via a posted WM_APP message) when the interval
//  changes.
// ============================================================
using System.Diagnostics;
using System.Runtime.InteropServices;
using Prism.Agent.Contracts;
using static Prism.Agent.WindowNative;

namespace Prism.Agent;

internal sealed class UsageTracker : IDisposable
{
    // ---- Tuning (live-overridable via ApplyConfig) ----------
    private volatile int  _sampleIntervalMs = 30_000;   // presence snapshot cadence
    private volatile uint _idleThresholdMs  = 60_000;   // no input for >60s => idle

    // A single accrual slice longer than this means the agent wasn't really
    // observing continuously (sleep/hibernate/resume, or a stalled pump). We
    // drop it rather than attribute phantom foreground time.
    private const double GapSeconds = 300.0;

    // ---- This process's own exe (never meter ourselves) -----
    private static readonly string s_ownExe = (Environment.ProcessPath ?? string.Empty).ToLowerInvariant();

    // ---- One-instance routing for AOT static callbacks ------
    private static UsageTracker? s_instance;

    // ---- Hook + loop state ---------------------------------
    private nint  _hForeground;
    private nint  _hMinimize;
    private uint  _threadId;
    private nuint _timerId;                              // the OS timer id (for KillTimer/re-arm)

    // ---- Accrual state -------------------------------------
    private long     _lastAccrualTicks;                 // Stopwatch ticks
    private DateTime _lastAccrualWall;                  // wall clock at last accrual (for midnight split)
    private string?  _focusedKey;                       // appKey currently focused, or null

    // ---- Current presence snapshot (appKey -> presence) -----
    private Dictionary<string, Presence> _snapshot = new(StringComparer.OrdinalIgnoreCase);

    // ---- Daily accumulators ((date|key) -> usage) -----------
    private readonly Dictionary<string, AppDailyUsage> _daily = new();

    // ---- Caches --------------------------------------------
    private readonly Dictionary<uint, AppIdentity?> _pidIdentity = new();   // pid -> identity (per process lifetime)
    private readonly Dictionary<string, HashSet<uint>> _seenPids = new(StringComparer.OrdinalIgnoreCase); // appKey -> pids seen (launch counting)

    // ---- Scratch buffers reused by the EnumWindows callbacks
    private static readonly List<nint> s_topWindows = new(256);
    private static uint  s_childHostPid;
    private static uint  s_childFoundPid;
    private static nint  s_childFoundHwnd;

    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private readonly struct Presence
    {
        public readonly AppState State;
        public readonly uint     RepPid;
        public readonly AppIdentity App;
        public Presence(AppState s, uint pid, AppIdentity app) { State = s; RepPid = pid; App = app; }
    }

    // ========================================================
    //  Lifecycle
    // ========================================================
    public unsafe void Start()
    {
        s_instance        = this;
        _threadId         = GetCurrentThreadId();
        _lastAccrualTicks = _clock.ElapsedTicks;
        _lastAccrualWall  = DateTime.Now;

        // Seed the focused app immediately.
        OnForegroundChanged(GetForegroundWindow());

        // Foreground focus changes (event-precise).
        _hForeground = SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, nint.Zero,
            (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, int, int, uint, uint, void>)&WinEventThunk,
            0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

        // Minimize start/end of the focused window (refines the focused app's state promptly).
        _hMinimize = SetWinEventHook(
            EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND, nint.Zero,
            (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, int, int, uint, uint, void>)&WinEventThunk,
            0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

        // Periodic presence snapshot via WM_TIMER on this same thread (no extra threads/locks).
        _timerId = SetTimer(nint.Zero, 0, (uint)_sampleIntervalMs, nint.Zero);

        // First snapshot now so non-focused apps are tracked from the start.
        OnSampleTick();

        RunMessageLoop();
    }

    private void RunMessageLoop()
    {
        // WinEvent (OUTOFCONTEXT) callbacks, WM_TIMER and our WM_APP re-tune are
        // delivered to this thread's queue and require an active message pump.
        while (GetMessageW(out MSG msg, nint.Zero, 0, 0) > 0)
        {
            if (msg.message == WM_TIMER)
                OnSampleTick();
            else if (msg.message == WM_APP)
                ReArmTimer((int)msg.wParam);     // wParam carries the new interval (ms)
            else
                DispatchMessageW(in msg);
        }
    }

    public void Stop() => PostThreadMessageW(_threadId, WM_QUIT, 0, nint.Zero);

    public void Dispose()
    {
        if (_timerId != nuint.Zero) KillTimer(nint.Zero, _timerId);
        if (_hForeground != nint.Zero) UnhookWinEvent(_hForeground);
        if (_hMinimize   != nint.Zero) UnhookWinEvent(_hMinimize);
        s_instance = null;
    }

    // ========================================================
    //  Live re-tune (called from any thread)
    // ========================================================
    public void ApplyConfig(int sampleIntervalSeconds, int idleThresholdSeconds)
    {
        // Clamp to sane bounds so a bad push can't wedge the tracker.
        int sampleMs = Math.Clamp(sampleIntervalSeconds, 5, 3600) * 1000;
        uint idleMs  = (uint)(Math.Clamp(idleThresholdSeconds, 5, 86_400) * 1000);

        _idleThresholdMs = idleMs;                       // read on the pump thread at each accrual

        if (sampleMs != _sampleIntervalMs)
        {
            _sampleIntervalMs = sampleMs;
            // Re-arm on the pump thread (SetTimer/KillTimer must run there).
            PostThreadMessageW(_threadId, WM_APP, (nuint)sampleMs, nint.Zero);
        }
    }

    private void ReArmTimer(int intervalMs)
    {
        if (_timerId != nuint.Zero) KillTimer(nint.Zero, _timerId);
        _timerId = SetTimer(nint.Zero, 0, (uint)intervalMs, nint.Zero);
    }

    // ========================================================
    //  AOT static thunks -> instance
    // ========================================================
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static void WinEventThunk(nint hHook, uint ev, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        // We only care about top-level window events (idObject == OBJID_WINDOW == 0).
        if (idObject != 0 || hwnd == nint.Zero) return;
        s_instance?.OnForegroundChanged(GetForegroundWindow());
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int EnumTopThunk(nint hwnd, nint lParam)
    {
        s_topWindows.Add(hwnd);
        return 1; // continue
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static int EnumChildThunk(nint hwnd, nint lParam)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid != 0 && pid != s_childHostPid)
        {
            s_childFoundPid  = pid;
            s_childFoundHwnd = hwnd;
            return 0; // stop: found the real hosted process
        }
        return 1;
    }

    // ========================================================
    //  Event: foreground changed (event-precise)
    // ========================================================
    private void OnForegroundChanged(nint hwnd)
    {
        Accrue();

        if (hwnd == nint.Zero) { _focusedKey = null; return; }

        AppIdentity? id = ResolveWindowApp(hwnd);
        if (id is null) { _focusedKey = null; return; }

        _focusedKey = id.Key;

        // Ensure the focused app is represented in the snapshot (it may have
        // launched between samples). Foreground state is decided in Accrue().
        if (!_snapshot.ContainsKey(id.Key))
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            _snapshot[id.Key] = new Presence(AppState.VisibleBackground, pid, id);
            CountLaunch(id.Key, pid);
        }
    }

    // ========================================================
    //  Timer: presence snapshot (sampled)
    // ========================================================
    private void OnSampleTick()
    {
        Accrue();

        var next = new Dictionary<string, Presence>(StringComparer.OrdinalIgnoreCase);

        s_topWindows.Clear();
        unsafe
        {
            EnumWindows((nint)(delegate* unmanaged[Stdcall]<nint, nint, int>)&EnumTopThunk, nint.Zero);
        }

        foreach (nint hwnd in s_topWindows)
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) continue;

            AppIdentity? id = ResolveWindowApp(hwnd, pid);
            if (id is null) continue;                       // unresolved / system / our own

            AppState windowState = ClassifyWindow(hwnd);

            // Keep the most "foreground-ish" state across an app's several windows:
            // Visible > Minimized > Tray.
            if (next.TryGetValue(id.Key, out Presence existing))
            {
                if (Rank(windowState) > Rank(existing.State))
                    next[id.Key] = new Presence(windowState, pid, id);
            }
            else
            {
                next[id.Key] = new Presence(windowState, pid, id);
                CountLaunch(id.Key, pid);
            }
        }

        _snapshot = next;

        // Drop identity cache entries for pids that are gone (cheap hygiene).
        if (_pidIdentity.Count > 4096) _pidIdentity.Clear();
    }

    private static int Rank(AppState s) => s switch
    {
        AppState.VisibleBackground => 3,
        AppState.Minimized         => 2,
        AppState.Tray              => 1,
        _                          => 0
    };

    // ========================================================
    //  Accrual: attribute elapsed wall-clock to each running app
    // ========================================================
    private void Accrue()
    {
        long now = _clock.ElapsedTicks;
        double seconds = (now - _lastAccrualTicks) / (double)Stopwatch.Frequency;
        DateTime wallNow = DateTime.Now;

        _lastAccrualTicks = now;
        DateTime wallPrev = _lastAccrualWall;
        _lastAccrualWall  = wallNow;

        if (seconds <= 0) return;

        // Gap (sleep/resume or stalled pump): advance the markers but invent nothing.
        if (seconds > GapSeconds) return;

        bool idle = IsUserIdle();

        // Split the slice at local midnight so each calendar day is exact.
        if (wallPrev != default && wallPrev.Date != wallNow.Date)
        {
            DateTime midnight = wallNow.Date;                       // 00:00 of the new day
            double after  = Math.Clamp((wallNow - midnight).TotalSeconds, 0, seconds);
            double before = seconds - after;
            if (before > 0) AccrueSlice(wallPrev.ToString("yyyy-MM-dd"), wallPrev, idle, before);
            if (after  > 0) AccrueSlice(wallNow.ToString("yyyy-MM-dd"),  wallNow,  idle, after);
        }
        else
        {
            AccrueSlice(wallNow.ToString("yyyy-MM-dd"), wallNow, idle, seconds);
        }
    }

    private void AccrueSlice(string date, DateTime wall, bool idle, double seconds)
    {
        foreach (var kv in _snapshot)
        {
            string key = kv.Key;
            Presence p = kv.Value;

            AppState state = key.Equals(_focusedKey, StringComparison.OrdinalIgnoreCase)
                ? (idle ? AppState.ForegroundIdle : AppState.ForegroundActive)
                : p.State;

            AppDailyUsage u = GetDaily(date, p.App);
            if (u.FirstSeen == DateTime.MinValue) u.FirstSeen = wall;
            u.LastSeen = wall;
            u.Add(state, seconds);
        }
    }

    private bool IsUserIdle()
    {
        LASTINPUTINFO lii = default;
        lii.cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>();
        if (!GetLastInputInfo(ref lii)) return false;
        uint idleMs = unchecked((uint)Environment.TickCount - lii.dwTime);
        return idleMs > _idleThresholdMs;
    }

    // ========================================================
    //  Window classification: visible / minimized / tray
    // ========================================================
    private static AppState ClassifyWindow(nint hwnd)
    {
        if (IsIconic(hwnd))
            return AppState.Minimized;                      // "opened but minimized"

        bool visible   = IsWindowVisible(hwnd);
        long exStyle   = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        bool toolWindow = (exStyle & WS_EX_TOOLWINDOW) != 0;

        // Hidden, or a tool window kept off the taskbar => "system tray" resident.
        if (!visible || toolWindow)
            return AppState.Tray;

        return AppState.VisibleBackground;                  // open, on-screen, not focused
    }

    // ========================================================
    //  App identity resolution (with UWP host handling)
    // ========================================================
    private AppIdentity? ResolveWindowApp(nint hwnd, uint pidHint = 0)
    {
        uint pid = pidHint;
        if (pid == 0) GetWindowThreadProcessId(hwnd, out pid);
        if (pid == 0) return null;

        // Packaged/UWP apps: the window belongs to ApplicationFrameHost.exe, not
        // the real app. Walk child windows to find the hosted process.
        AppIdentity? hostId = GetIdentity(pid);
        if (hostId is not null &&
            hostId.ExePath.EndsWith("applicationframehost.exe", StringComparison.OrdinalIgnoreCase))
        {
            uint realPid = FindHostedChildPid(hwnd, pid);
            if (realPid != 0 && realPid != pid)
                return GetIdentity(realPid) ?? hostId;
            // (Fully robust path: read the window's AppUserModelID via the property store.)
        }
        return hostId;
    }

    private static uint FindHostedChildPid(nint hwnd, uint hostPid)
    {
        s_childHostPid   = hostPid;
        s_childFoundPid  = 0;
        s_childFoundHwnd = nint.Zero;
        unsafe
        {
            EnumChildWindows(hwnd, (nint)(delegate* unmanaged[Stdcall]<nint, nint, int>)&EnumChildThunk, nint.Zero);
        }
        return s_childFoundPid;
    }

    private AppIdentity? GetIdentity(uint pid)
    {
        if (_pidIdentity.TryGetValue(pid, out AppIdentity? cached)) return cached;

        AppIdentity? id = null;
        nint h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h != nint.Zero)
        {
            try
            {
                var buf = new char[1024];
                uint size = (uint)buf.Length;
                bool ok;
                unsafe { fixed (char* p = buf) { ok = QueryFullProcessImageName(h, 0, p, ref size); } }
                if (ok && size > 0)
                {
                    string path = new string(buf, 0, (int)size);
                    id = new AppIdentity { ExePath = path.ToLowerInvariant() };
                    EnrichVersion(id, path);
                }
            }
            finally { CloseHandle(h); }
        }

        // Never meter ourselves (the session tracker is this same exe).
        if (id is not null && s_ownExe.Length > 0 &&
            string.Equals(id.ExePath, s_ownExe, StringComparison.Ordinal))
            id = null;

        _pidIdentity[pid] = id;     // cache even null (access-denied/system pids) for this snapshot window
        return id;
    }

    private static void EnrichVersion(AppIdentity id, string path)
    {
        try
        {
            // Managed + AOT-safe. Gives the catalog "Microsoft Word" instead of "winword.exe".
            FileVersionInfo fvi = FileVersionInfo.GetVersionInfo(path);
            id.ProductName = string.IsNullOrWhiteSpace(fvi.ProductName) ? null : fvi.ProductName.Trim();
            id.Description = string.IsNullOrWhiteSpace(fvi.FileDescription) ? null : fvi.FileDescription.Trim();
            id.Company     = string.IsNullOrWhiteSpace(fvi.CompanyName) ? null : fvi.CompanyName.Trim();
            id.FileVersion = string.IsNullOrWhiteSpace(fvi.FileVersion) ? null : fvi.FileVersion.Trim();
        }
        catch { /* metadata unavailable: exe path still identifies the app */ }
    }

    // ========================================================
    //  Bookkeeping
    // ========================================================
    private AppDailyUsage GetDaily(string date, AppIdentity app)
    {
        string k = date + "|" + app.Key;
        if (!_daily.TryGetValue(k, out AppDailyUsage? u))
        {
            u = new AppDailyUsage { Date = date, App = app };
            _daily[k] = u;
        }
        else
        {
            // Backfill any metadata a later resolution learned (some fields can resolve late).
            u.App.ProductName ??= app.ProductName;
            u.App.Description ??= app.Description;
            u.App.Company     ??= app.Company;
            u.App.FileVersion ??= app.FileVersion;
        }
        return u;
    }

    private void CountLaunch(string appKey, uint pid)
    {
        if (!_seenPids.TryGetValue(appKey, out HashSet<uint>? pids))
        {
            pids = new HashSet<uint>();
            _seenPids[appKey] = pids;
        }
        if (pids.Add(pid))
        {
            // A new pid for this app counts as a launch. Bump today's record if present.
            string date = DateTime.Now.ToString("yyyy-MM-dd");
            if (_daily.TryGetValue(date + "|" + appKey, out AppDailyUsage? u)) u.Launches++;
        }
    }

    // ========================================================
    //  Drain rollups for shipping (called by the host on a cadence)
    // ========================================================
    public IReadOnlyList<UsageRollup> SnapshotRollups()
    {
        // Final accrual so the returned numbers are current.
        Accrue();

        var list = new List<UsageRollup>(_daily.Count);
        foreach (AppDailyUsage u in _daily.Values)
        {
            list.Add(new UsageRollup(
                u.Date, u.App.ExePath, u.App.ProductName, u.App.Description, u.App.Company, u.App.FileVersion,
                u.Launches,
                u.FirstSeen == DateTime.MinValue ? null : u.FirstSeen.ToUniversalTime().ToString("o"),
                u.LastSeen  == DateTime.MinValue ? null : u.LastSeen.ToUniversalTime().ToString("o"),
                (long)u.ForegroundActiveSeconds,
                (long)u.ForegroundIdleSeconds,
                (long)u.VisibleBackgroundSeconds,
                (long)u.MinimizedSeconds,
                (long)u.TraySeconds));
        }
        return list;
    }

    /// <summary>Remove fully-elapsed previous days once the host has shipped them.</summary>
    public void PurgeShippedBefore(string todayDate)
    {
        List<string>? stale = null;
        foreach (string k in _daily.Keys)
        {
            // Key is "yyyy-MM-dd|exepath"; compare just the date segment without allocating.
            int bar = k.IndexOf('|');
            ReadOnlySpan<char> day = bar < 0 ? k.AsSpan() : k.AsSpan(0, bar);
            if (day.CompareTo(todayDate.AsSpan(), StringComparison.Ordinal) < 0)
                (stale ??= new List<string>()).Add(k);
        }
        if (stale is null) return;
        foreach (string k in stale) _daily.Remove(k);
    }
}
