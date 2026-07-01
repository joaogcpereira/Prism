// ============================================================
//  ProcessNative.cs  (Prism.Agent)  - process-token interop (service side)
//  Derives the connected pipe client's user SID directly from
//  its process token. This works at the client's Identification
//  impersonation level and never impersonates the client, so the
//  service learns *who* connected without being able to act as
//  them - trustworthy attribution, minimal privilege.
// ============================================================
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Prism.Agent;

internal static partial class ProcessNative
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TOKEN_QUERY = 0x0008;
    private const int  TokenUser   = 1;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(nint Pipe, out uint ClientProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint dwDesiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint ProcessHandle, uint DesiredAccess, out nint TokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(nint TokenHandle, int TokenInformationClass,
        nint TokenInformation, uint TokenInformationLength, out uint ReturnLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint hObject);

    /// <summary>Returns the client's user SID string, or null if it can't be determined.</summary>
    public static string? TryGetClientUserSid(SafePipeHandle pipeHandle)
    {
        if (!GetNamedPipeClientProcessId(pipeHandle.DangerousGetHandle(), out uint pid) || pid == 0)
            return null;

        nint hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProc == nint.Zero) return null;
        try
        {
            if (!OpenProcessToken(hProc, TOKEN_QUERY, out nint hTok)) return null;
            try
            {
                GetTokenInformation(hTok, TokenUser, nint.Zero, 0, out uint len);
                if (len == 0) return null;

                nint buf = Marshal.AllocHGlobal((int)len);
                try
                {
                    if (!GetTokenInformation(hTok, TokenUser, buf, len, out _)) return null;
                    // TOKEN_USER { SID_AND_ATTRIBUTES User { PSID Sid; DWORD Attributes; } }
                    nint pSid = Marshal.ReadIntPtr(buf);   // User.Sid
                    if (pSid == nint.Zero) return null;
                    return new SecurityIdentifier(pSid).Value;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            finally { CloseHandle(hTok); }
        }
        finally { CloseHandle(hProc); }
    }
}
