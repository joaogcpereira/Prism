// ============================================================
//  UsageRollup.cs  (Prism.Agent.Contracts)
//  The compact, shippable record: one per app per day.
//
//  Privacy by design: this record deliberately contains NO window
//  titles, document names, URLs, command lines, or any input
//  content. It identifies *which* application ran and *how long*
//  it spent in each visibility state on a given day - nothing more.
//  That is exactly what license true-up / reclaim needs, and no
//  more (GDPR data-minimisation).
// ============================================================
namespace Prism.Agent.Contracts;

public sealed record UsageRollup(
    // Device-LOCAL calendar day (yyyy-MM-dd) the usage was accrued on.
    // The enclosing UsageBatch carries the device's UtcOffsetMinutes so the
    // server can normalise across time zones. Local day is intentional: it
    // matches the user's working day for licence-utilisation reporting.
    string  Date,
    string  ExePath,
    string? ProductName,
    string? Description,
    string? Company,
    string? FileVersion,
    int     Launches,
    string? FirstSeenUtc,
    string? LastSeenUtc,
    long    ForegroundActiveSeconds,
    long    ForegroundIdleSeconds,
    long    VisibleBackgroundSeconds,
    long    MinimizedSeconds,
    long    TraySeconds);
