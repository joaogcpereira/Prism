// ============================================================
//  ServiceHost.cs  (Prism.Agent)
//  Hand-rolled Windows Service control, via P/Invoke, so it is
//  fully Native-AOT compatible and needs no hosting NuGet
//  packages. Also gives us SERVICE_CONTROL_SESSIONCHANGE, which
//  the per-session launcher relies on.
//
//  Single service instance per process => static bridge state is
//  fine. The UnmanagedCallersOnly callbacks route into the live
//  AgentService instance.
// ============================================================
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Prism.Agent;

internal static partial class ServiceNative
{
    internal const uint SERVICE_WIN32_OWN_PROCESS = 0x10;
    internal const uint SERVICE_START_PENDING = 2, SERVICE_RUNNING = 4, SERVICE_STOP_PENDING = 3, SERVICE_STOPPED = 1;
    internal const uint SERVICE_ACCEPT_STOP = 0x1, SERVICE_ACCEPT_SHUTDOWN = 0x4, SERVICE_ACCEPT_SESSIONCHANGE = 0x80;
    internal const uint SERVICE_CONTROL_STOP = 0x1, SERVICE_CONTROL_INTERROGATE = 0x4, SERVICE_CONTROL_SHUTDOWN = 0x5, SERVICE_CONTROL_SESSIONCHANGE = 0xE;
    internal const uint WTS_SESSION_LOGON = 0x5, WTS_SESSION_LOGOFF = 0x6;
    internal const int  ERROR_FAILED_SERVICE_CONTROLLER_CONNECT = 1063;

    [StructLayout(LayoutKind.Sequential)]
    internal struct SERVICE_STATUS
    {
        public uint dwServiceType, dwCurrentState, dwControlsAccepted,
                    dwWin32ExitCode, dwServiceSpecificExitCode, dwCheckPoint, dwWaitHint;
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool StartServiceCtrlDispatcherW(nint lpServiceStartTable);

    [LibraryImport("advapi32.dll", EntryPoint = "RegisterServiceCtrlHandlerExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint RegisterServiceCtrlHandlerExW(string lpServiceName, nint lpHandlerProc, nint lpContext);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetServiceStatus(nint hServiceStatus, in SERVICE_STATUS lpServiceStatus);
}

internal static class ServiceHost
{
    private static nint s_statusHandle;
    private static ServiceNative.SERVICE_STATUS s_status;
    private static readonly ManualResetEventSlim s_stop = new(false);
    private static AgentService? s_service;
    private static string s_serviceName = AgentInfo.ServiceName;
    private static int s_checkPoint;

    /// <summary>
    /// Connects to the SCM. If launched by the SCM this BLOCKS until the
    /// service stops, then returns true. If launched from a console it returns
    /// false immediately (error 1063), so the caller can fall back to console.
    /// </summary>
    public static unsafe bool TryRunAsService(string serviceName, AgentService service)
    {
        s_serviceName = serviceName;
        s_service = service;

        nint namePtr = Marshal.StringToHGlobalUni(serviceName);
        int entry = 2 * nint.Size;                 // SERVICE_TABLE_ENTRYW = { name, proc }
        nint table = Marshal.AllocHGlobal(entry * 2);
        try
        {
            // entry[0] = { name, &ServiceMain }
            Marshal.WriteIntPtr(table, 0, namePtr);
            Marshal.WriteIntPtr(table, nint.Size,
                (nint)(delegate* unmanaged[Stdcall]<uint, nint, void>)&ServiceMain);
            // entry[1] = { null, null }  (terminator)
            Marshal.WriteIntPtr(table + entry, 0, nint.Zero);
            Marshal.WriteIntPtr(table + entry, nint.Size, nint.Zero);

            if (ServiceNative.StartServiceCtrlDispatcherW(table))
                return true;                       // ran as a service to completion

            int err = Marshal.GetLastPInvokeError();
            if (err == ServiceNative.ERROR_FAILED_SERVICE_CONTROLLER_CONNECT)
                return false;                      // not started by SCM => console mode
            throw new InvalidOperationException($"StartServiceCtrlDispatcher failed ({err}).");
        }
        finally
        {
            Marshal.FreeHGlobal(table);
            Marshal.FreeHGlobal(namePtr);
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static unsafe void ServiceMain(uint argc, nint argv)
    {
        s_statusHandle = ServiceNative.RegisterServiceCtrlHandlerExW(
            s_serviceName,
            (nint)(delegate* unmanaged[Stdcall]<uint, uint, nint, nint, uint>)&HandlerEx,
            nint.Zero);
        if (s_statusHandle == nint.Zero) return;

        s_status.dwServiceType = ServiceNative.SERVICE_WIN32_OWN_PROCESS;
        SetState(ServiceNative.SERVICE_START_PENDING, 0);

        try { s_service!.Start(enableSessionLauncher: true); }
        catch { SetState(ServiceNative.SERVICE_STOPPED, 0); return; }

        SetState(ServiceNative.SERVICE_RUNNING,
            ServiceNative.SERVICE_ACCEPT_STOP | ServiceNative.SERVICE_ACCEPT_SHUTDOWN | ServiceNative.SERVICE_ACCEPT_SESSIONCHANGE);

        s_stop.Wait();                              // block the dispatcher thread until stop

        SetState(ServiceNative.SERVICE_STOP_PENDING, 0);
        try { s_service!.Stop(); } catch { }
        SetState(ServiceNative.SERVICE_STOPPED, 0);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static uint HandlerEx(uint control, uint eventType, nint eventData, nint context)
    {
        switch (control)
        {
            case ServiceNative.SERVICE_CONTROL_STOP:
            case ServiceNative.SERVICE_CONTROL_SHUTDOWN:
                SetState(ServiceNative.SERVICE_STOP_PENDING, 0);
                s_stop.Set();
                break;

            case ServiceNative.SERVICE_CONTROL_SESSIONCHANGE:
                // eventData -> WTSSESSION_NOTIFICATION { DWORD cbSize; DWORD dwSessionId; }
                if (eventData != nint.Zero)
                {
                    uint sessionId = unchecked((uint)Marshal.ReadInt32(eventData, 4));
                    if (eventType == ServiceNative.WTS_SESSION_LOGON)  s_service?.OnSessionLogon(sessionId);
                    else if (eventType == ServiceNative.WTS_SESSION_LOGOFF) s_service?.OnSessionLogoff(sessionId);
                }
                break;

            case ServiceNative.SERVICE_CONTROL_INTERROGATE:
                Report();
                break;
        }
        return 0; // NO_ERROR
    }

    private static void SetState(uint state, uint accepts)
    {
        s_status.dwCurrentState = state;
        s_status.dwControlsAccepted = accepts;

        // For *_PENDING transitions the SCM needs a non-zero wait hint and an
        // advancing checkpoint, otherwise a slow Start()/Stop() (pipe ACL hardening,
        // cert-store enumeration, uploader drain) can be declared "hung" and killed.
        if (state == ServiceNative.SERVICE_START_PENDING || state == ServiceNative.SERVICE_STOP_PENDING)
        {
            s_status.dwCheckPoint = unchecked((uint)(++s_checkPoint));
            s_status.dwWaitHint   = 15000;   // ms
        }
        else
        {
            s_checkPoint = 0;
            s_status.dwCheckPoint = 0;
            s_status.dwWaitHint   = 0;
        }
        Report();
    }

    private static void Report()
    {
        if (s_statusHandle != nint.Zero)
            ServiceNative.SetServiceStatus(s_statusHandle, in s_status);
    }
}
