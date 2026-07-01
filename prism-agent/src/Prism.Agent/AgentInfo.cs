// ============================================================
//  AgentInfo.cs  (Prism.Agent)
//  Single source of truth for the agent's identity strings.
//  Reads the assembly's informational version (set in the csproj)
//  so the reported version, the HTTP User-Agent, and the service
//  description never drift apart. AOT-safe (no reflection beyond
//  the assembly's own attributes).
// ============================================================
using System.Reflection;

namespace Prism.Agent;

internal static class AgentInfo
{
    /// <summary>Semantic version, e.g. "1.0.0". Falls back if no attribute is present.</summary>
    public static string Version { get; } = ResolveVersion();

    /// <summary>Stable HTTP User-Agent for the uploader.</summary>
    public static string UserAgent { get; } = $"ContosoPrismAgent/{Version}";

    /// <summary>Windows service short name (SCM key). Single source of truth used by
    /// the service host, the installer, and the uninstaller so they can never drift.</summary>
    public const string ServiceName = "ContosoPrismAgent";

    /// <summary>Windows service display name.</summary>
    public const string ServiceDisplayName = "Contoso Prism Agent";

    /// <summary>Windows service description.</summary>
    public const string ServiceDescription =
        "Measures Win32 application usage for Contoso Prism license optimization (read-only).";

    private static string ResolveVersion()
    {
        try
        {
            Assembly asm = typeof(AgentInfo).Assembly;

            // Prefer the informational version (supports SemVer / build metadata).
            string? info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                // Strip any "+<commit>" build-metadata suffix for a clean public version.
                int plus = info.IndexOf('+');
                return (plus > 0 ? info[..plus] : info).Trim();
            }

            Version? v = asm.GetName().Version;
            if (v is not null) return $"{v.Major}.{v.Minor}.{v.Build}";
        }
        catch { /* fall through */ }

        return "1.0.0";
    }
}
