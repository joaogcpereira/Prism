// ============================================================
//  UsageModels.cs
//  The data model for usage measurement.
//
//  The five states are mutually exclusive: at any instant a
//  running, user-facing process is in exactly one of them, so
//  per-state seconds never double-count and sum to total
//  running time.
// ============================================================
// The shippable UsageRollup record now lives in Prism.Agent.Contracts
// (shared by the helper and the service). See Contracts/UsageRollup.cs.
namespace Prism.Agent;

internal enum AppState
{
    /// <summary>Focused AND user input within the idle window. The strongest "actively using it" signal.</summary>
    ForegroundActive,
    /// <summary>Focused but the user is idle (walked away). Focus without engagement.</summary>
    ForegroundIdle,
    /// <summary>Has a visible, non-minimized window but is not focused. Passive/secondary use.</summary>
    VisibleBackground,
    /// <summary>All windows minimized (IsIconic). "Opened but minimized."</summary>
    Minimized,
    /// <summary>Running with only hidden / tool-window windows. "In the system tray."</summary>
    Tray
}

/// <summary>Identity of an application, resolved from its executable.</summary>
internal sealed class AppIdentity
{
    public required string ExePath     { get; init; }   // normalized, lower-cased full path
    public string?         ProductName { get; set; }    // version-resource ProductName (suite/product; good for licence grouping)
    public string?         Description { get; set; }    // version-resource FileDescription (per-exe friendly name; best for display)
    public string?         Company     { get; set; }
    public string?         FileVersion { get; set; }

    /// <summary>Stable key used for aggregation (the normalized exe path).</summary>
    public string Key => ExePath;
}

/// <summary>Per-app, per-day accumulator. Seconds are kept per state.</summary>
internal sealed class AppDailyUsage
{
    public required string   Date      { get; init; }   // device-local calendar day (yyyy-MM-dd)
    public required AppIdentity App     { get; init; }

    public double ForegroundActiveSeconds  { get; set; }
    public double ForegroundIdleSeconds    { get; set; }
    public double VisibleBackgroundSeconds { get; set; }
    public double MinimizedSeconds         { get; set; }
    public double TraySeconds              { get; set; }

    public int       Launches  { get; set; }
    public DateTime  FirstSeen { get; set; } = DateTime.MinValue;
    public DateTime  LastSeen  { get; set; } = DateTime.MinValue;

    public double RunningSeconds =>
        ForegroundActiveSeconds + ForegroundIdleSeconds +
        VisibleBackgroundSeconds + MinimizedSeconds + TraySeconds;

    public void Add(AppState state, double seconds)
    {
        switch (state)
        {
            case AppState.ForegroundActive:  ForegroundActiveSeconds  += seconds; break;
            case AppState.ForegroundIdle:    ForegroundIdleSeconds    += seconds; break;
            case AppState.VisibleBackground: VisibleBackgroundSeconds += seconds; break;
            case AppState.Minimized:         MinimizedSeconds         += seconds; break;
            case AppState.Tray:              TraySeconds              += seconds; break;
        }
    }
}
