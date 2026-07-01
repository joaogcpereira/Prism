// ============================================================
//  InstallMode.cs  (Prism.Agent)
//  --install / --uninstall via the Windows service-control API
//  (advapi32). Using the API (not `sc.exe create`) sidesteps the
//  binPath= space-quoting trap for paths like C:\Program Files\...
//  Zero dependencies. Crash-recovery is configured separately by
//  the install script with `sc.exe failure` (no quoting risk there).
// ============================================================
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Prism.Agent;

internal static partial class ScmNative
{
    internal const uint SC_MANAGER_CONNECT = 0x0001, SC_MANAGER_CREATE_SERVICE = 0x0002;
    internal const uint SERVICE_ALL_ACCESS = 0xF01FF;
    internal const uint SERVICE_WIN32_OWN_PROCESS = 0x10;
    internal const uint SERVICE_AUTO_START = 0x2, SERVICE_DEMAND_START = 0x3, SERVICE_DISABLED = 0x4, SERVICE_ERROR_NORMAL = 0x1;
    internal const uint SERVICE_NO_CHANGE = 0xFFFFFFFF;          // leave a ChangeServiceConfig field untouched
    internal const uint SERVICE_CONTROL_STOP = 0x1;
    internal const uint SERVICE_CONFIG_DESCRIPTION = 1;
    internal const int  ERROR_SERVICE_EXISTS = 1073, ERROR_SERVICE_DOES_NOT_EXIST = 1060,
                        ERROR_SERVICE_ALREADY_RUNNING = 1056, ERROR_SERVICE_MARKED_FOR_DELETE = 1072;

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint OpenSCManager(string? machineName, string? databaseName, uint access);

    [LibraryImport("advapi32.dll", EntryPoint = "CreateServiceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint CreateService(nint hSCManager, string lpServiceName, string? lpDisplayName,
        uint dwDesiredAccess, uint dwServiceType, uint dwStartType, uint dwErrorControl,
        string lpBinaryPathName, string? lpLoadOrderGroup, nint lpdwTagId,
        string? lpDependencies, string? lpServiceStartName, string? lpPassword);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint OpenService(nint hSCManager, string lpServiceName, uint dwDesiredAccess);

    [LibraryImport("advapi32.dll", EntryPoint = "StartServiceW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool StartService(nint hService, uint dwNumServiceArgs, nint lpServiceArgVectors);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ControlService(nint hService, uint dwControl, out ServiceNative.SERVICE_STATUS lpServiceStatus);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteService(nint hService);

    [LibraryImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ChangeServiceConfig2(nint hService, uint dwInfoLevel, nint lpInfo);

    [LibraryImport("advapi32.dll", EntryPoint = "ChangeServiceConfigW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ChangeServiceConfig(nint hService, uint dwServiceType, uint dwStartType, uint dwErrorControl,
        string? lpBinaryPathName, string? lpLoadOrderGroup, nint lpdwTagId,
        string? lpDependencies, string? lpServiceStartName, string? lpPassword, string? lpDisplayName);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool QueryServiceStatus(nint hService, out ServiceNative.SERVICE_STATUS lpServiceStatus);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseServiceHandle(nint hSCObject);
}

internal static class InstallMode
{
    private const string ServiceName = AgentInfo.ServiceName;
    private const string DisplayName = AgentInfo.ServiceDisplayName;
    private const string Description = AgentInfo.ServiceDescription;

    public static int Install()
    {
        if (!IsAdmin()) return Fail("--install must be run from an elevated (Administrator) prompt.");
        string exe = Environment.ProcessPath ?? "";
        if (exe.Length == 0) return Fail("Could not determine the executable path.");
        string binPath = "\"" + exe + "\"";                 // quoted => SCM stores it correctly

        nint scm = ScmNative.OpenSCManager(null, null, ScmNative.SC_MANAGER_CONNECT | ScmNative.SC_MANAGER_CREATE_SERVICE);
        if (scm == nint.Zero) return Fail($"OpenSCManager failed ({Marshal.GetLastPInvokeError()}).");
        try
        {
            nint svc = ScmNative.CreateService(scm, ServiceName, DisplayName, ScmNative.SERVICE_ALL_ACCESS,
                ScmNative.SERVICE_WIN32_OWN_PROCESS, ScmNative.SERVICE_AUTO_START, ScmNative.SERVICE_ERROR_NORMAL,
                binPath, null, nint.Zero, null, null /* LocalSystem */, null);

            if (svc == nint.Zero)
            {
                int e = Marshal.GetLastPInvokeError();
                if (e == ScmNative.ERROR_SERVICE_EXISTS)
                {
                    Console.WriteLine("Service already exists; opening it to (re)configure and start.");
                    svc = ScmNative.OpenService(scm, ServiceName, ScmNative.SERVICE_ALL_ACCESS);
                }
                else return Fail($"CreateService failed ({e}).");
            }
            if (svc == nint.Zero) return Fail($"OpenService failed ({Marshal.GetLastPInvokeError()}).");

            try
            {
                // Force the start type back to automatic and re-assert the binary path.
                // CreateService's SERVICE_AUTO_START is a no-op on the re-install path
                // (service already existed), so without this an admin/GPO that previously
                // set the service to Disabled or Manual would survive a re-install and the
                // agent would silently never auto-start on the next boot. Also clears a
                // prior SERVICE_DISABLED so the StartService below can succeed.
                EnsureAutoStart(svc, binPath);
                SetDescription(svc);
                if (!ScmNative.StartService(svc, 0, nint.Zero))
                {
                    int e = Marshal.GetLastPInvokeError();
                    if (e != ScmNative.ERROR_SERVICE_ALREADY_RUNNING)
                        Console.WriteLine($"Service registered; start returned {e} (it will start on next boot).");
                }
                Console.WriteLine($"Service '{ServiceName}' installed (auto-start, LocalSystem) and start requested.");
                return 0;
            }
            finally { ScmNative.CloseServiceHandle(svc); }
        }
        finally { ScmNative.CloseServiceHandle(scm); }
    }

    public static int Uninstall()
    {
        if (!IsAdmin()) return Fail("--uninstall must be run from an elevated (Administrator) prompt.");

        nint scm = ScmNative.OpenSCManager(null, null, ScmNative.SC_MANAGER_CONNECT);
        if (scm == nint.Zero) return Fail($"OpenSCManager failed ({Marshal.GetLastPInvokeError()}).");
        try
        {
            nint svc = ScmNative.OpenService(scm, ServiceName, ScmNative.SERVICE_ALL_ACCESS);
            if (svc == nint.Zero)
            {
                int e = Marshal.GetLastPInvokeError();
                if (e == ScmNative.ERROR_SERVICE_DOES_NOT_EXIST) { Console.WriteLine("Service not installed; nothing to do."); return 0; }
                return Fail($"OpenService failed ({e}).");
            }
            try
            {
                ScmNative.ControlService(svc, ScmNative.SERVICE_CONTROL_STOP, out _);  // best-effort stop
                WaitForStopped(svc, timeoutMs: 10000);                                 // poll instead of a blind sleep

                // Disable BEFORE delete. If deletion is deferred by the SCM (e.g. another
                // open handle to the service), the service is "marked for delete" but
                // survives until reboot; setting it Disabled guarantees it cannot auto-start
                // in that window. Best-effort.
                if (!ScmNative.ChangeServiceConfig(svc, ScmNative.SERVICE_NO_CHANGE, ScmNative.SERVICE_DISABLED,
                        ScmNative.SERVICE_NO_CHANGE, null, null, nint.Zero, null, null, null, null))
                {
                    int e = Marshal.GetLastPInvokeError();
                    if (e != ScmNative.ERROR_SERVICE_MARKED_FOR_DELETE)
                        Console.WriteLine($"Note: could not disable service before delete ({e}).");
                }

                if (!ScmNative.DeleteService(svc))
                {
                    int e = Marshal.GetLastPInvokeError();
                    if (e != ScmNative.ERROR_SERVICE_DOES_NOT_EXIST && e != ScmNative.ERROR_SERVICE_MARKED_FOR_DELETE)
                        return Fail($"DeleteService failed ({e}).");
                }

                // Stopping the service does NOT terminate the per-session tracker processes
                // it spawned via CreateProcessAsUser (they are independent processes). Kill any
                // remaining "prism-agent" executables now; an orphaned tracker keeps the image
                // file open and would block removal of the install directory.
                KillAgentProcesses();

                Console.WriteLine($"Service '{ServiceName}' stopped, disabled, and removed; lingering agent processes terminated.");
                return 0;
            }
            finally { ScmNative.CloseServiceHandle(svc); }
        }
        finally { ScmNative.CloseServiceHandle(scm); }
    }

    private static void EnsureAutoStart(nint svc, string binPath)
    {
        // SERVICE_NO_CHANGE for the fields we don't want to touch; force start type to
        // automatic and re-assert the (quoted) binary path so a moved install dir is fixed.
        if (!ScmNative.ChangeServiceConfig(svc, ScmNative.SERVICE_NO_CHANGE, ScmNative.SERVICE_AUTO_START,
                ScmNative.SERVICE_NO_CHANGE, binPath, null, nint.Zero, null, null, null, DisplayName))
            Console.WriteLine($"Warning: could not set service start type to automatic ({Marshal.GetLastPInvokeError()}).");
    }

    private static void WaitForStopped(nint svc, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (!ScmNative.QueryServiceStatus(svc, out ServiceNative.SERVICE_STATUS st)) return;
            if (st.dwCurrentState == ServiceNative.SERVICE_STOPPED) return;
            Thread.Sleep(250);
        }
    }

    private static void KillAgentProcesses()
    {
        int self = Environment.ProcessId;
        string name = System.IO.Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "prism-agent");
        if (string.IsNullOrEmpty(name)) name = "prism-agent";

        foreach (Process p in Process.GetProcessesByName(name))
        {
            try
            {
                if (p.Id == self) continue;                  // never kill the running uninstaller
                p.Kill(entireProcessTree: true);
                p.WaitForExit(3000);
            }
            catch { /* already exited or access denied: best-effort */ }
            finally { p.Dispose(); }
        }
    }

    private static void SetDescription(nint svc)
    {
        // SERVICE_DESCRIPTION { LPWSTR lpDescription; }
        nint descStr = Marshal.StringToHGlobalUni(Description);
        nint blob = Marshal.AllocHGlobal(nint.Size);
        try
        {
            Marshal.WriteIntPtr(blob, descStr);
            ScmNative.ChangeServiceConfig2(svc, ScmNative.SERVICE_CONFIG_DESCRIPTION, blob);
        }
        finally { Marshal.FreeHGlobal(blob); Marshal.FreeHGlobal(descStr); }
    }

    private static bool IsAdmin()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }
}
