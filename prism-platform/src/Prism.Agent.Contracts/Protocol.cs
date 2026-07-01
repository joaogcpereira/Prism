// ============================================================
//  Protocol.cs  (Prism.Agent.Contracts)
//  The helper -> service wire protocol over the named pipe.
//
//  Note: the batch does NOT carry the user's SID. The service
//  derives the connected client's identity from the OS (the pipe
//  client's process token), so identity cannot be spoofed by the
//  payload.
// ============================================================
namespace Prism.Agent.Contracts;

/// <summary>Helper -> service: a set of daily usage rollups.</summary>
public sealed record UsageBatch(
    int                        SchemaVersion,
    string                     MachineName,
    string                     AgentVersion,
    // Device's current UTC offset in minutes (e.g. +120 for CEST). Lets the
    // server interpret each rollup's device-local Date without guessing.
    int                        UtcOffsetMinutes,
    IReadOnlyList<UsageRollup> Rollups);

/// <summary>Service -> helper: delivery acknowledgement (+ optional config push).</summary>
public sealed record UsageAck(
    bool         Accepted,
    int          Received,
    string?      Message,
    AgentConfig? Config);

/// <summary>
/// Optional tuning the service can push back to the helper. The helper applies
/// it on the next cycle (idle threshold takes effect immediately; a changed
/// sample interval re-arms the snapshot timer).
/// </summary>
public sealed record AgentConfig(
    int SampleIntervalSeconds,
    int IdleThresholdSeconds);

/// <summary>The receipt shape the service spools for upload to the gateway.</summary>
public sealed record ReceivedBatch(
    string                     ReceivedUtc,
    string                     MachineName,
    string?                    UserSid,
    string                     AgentVersion,
    int                        UtcOffsetMinutes,
    IReadOnlyList<UsageRollup> Rollups);

public static class PipeProtocol
{
    /// <summary>Local pipe name (resolves to \\.\pipe\prism-agent-usage).</summary>
    public const string PipeName = "prism-agent-usage";

    public const int SchemaVersion = 1;

    /// <summary>Hard cap on a single framed message (anti-DoS).</summary>
    public const int MaxFrameBytes = 8 * 1024 * 1024;
}
