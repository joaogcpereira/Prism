// ============================================================
//  WindowNative.cs  (Prism.Agent)  - window/hook/idle interop (session side)
//  AOT-friendly Win32 interop for the Prism usage tracker.
//  Uses [LibraryImport] (source-generated, no reflection) and
//  unmanaged function pointers for callbacks so it works under
//  .NET 10 Native AOT.
//
//  Every API here is documented and benign (no cross-process
//  memory reads). EDR-safe.
// ============================================================
using System.Runtime.InteropServices;

namespace Prism.Agent;

internal static partial class WindowNative
{
    // ---- Constants -----------------------------------------
    internal const uint EVENT_SYSTEM_FOREGROUND     = 0x0003;
    internal const uint EVENT_SYSTEM_MINIMIZESTART  = 0x0016;
    internal const uint EVENT_SYSTEM_MINIMIZEEND    = 0x0017;
    internal const uint WINEVENT_OUTOFCONTEXT       = 0x0000;
    internal const uint WINEVENT_SKIPOWNPROCESS     = 0x0002;

    internal const uint WM_QUIT  = 0x0012;
    internal const uint WM_TIMER = 0x0113;
    internal const uint WM_APP   = 0x8000;   // app-defined: carries a live re-tune to the pump thread

    internal const int  GWL_EXSTYLE        = -20;
    internal const long WS_EX_TOOLWINDOW   = 0x00000080;

    internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    // ---- Structs -------------------------------------------
    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSG
    {
        public nint  hwnd;
        public uint  message;
        public nuint wParam;
        public nint  lParam;
        public uint  time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    // ---- user32: window events / message loop --------------
    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial nint SetWinEventHook(
        uint eventMin, uint eventMax, nint hmodWinEventProc,
        nint pfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnhookWinEvent(nint hWinEventHook);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial int GetMessageW(out MSG lpMsg, nint hWnd, uint min, uint max);

    [LibraryImport("user32.dll")]
    internal static partial nint DispatchMessageW(in MSG lpMsg);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostThreadMessageW(uint idThread, uint msg, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    internal static partial nuint SetTimer(nint hWnd, nuint nIDEvent, uint uElapse, nint lpTimerFunc);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool KillTimer(nint hWnd, nuint uIDEvent);

    // ---- user32: window state / enumeration ----------------
    [LibraryImport("user32.dll")]
    internal static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    internal static partial uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsIconic(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindowVisible(nint hWnd);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    internal static partial nint GetWindowLongPtr(nint hWnd, int nIndex);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumWindows(nint lpEnumFunc, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumChildWindows(nint hWndParent, nint lpEnumFunc, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetLastInputInfo(ref LASTINPUTINFO plii);

    // ---- kernel32: process identity ------------------------
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint OpenProcess(uint dwDesiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint hObject);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool QueryFullProcessImageName(
        nint hProcess, uint dwFlags, char* lpExeName, ref uint lpdwSize);

    [LibraryImport("kernel32.dll")]
    internal static partial uint GetCurrentThreadId();
}
