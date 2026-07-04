// ============================================================
//  UsagePipeServer.cs  (Prism.Agent)
//  The LocalSystem-side named-pipe endpoint. Accepts connections
//  from per-session helpers, reads a usage batch, derives the
//  client's user SID from the OS, hands the batch to a handler,
//  and returns an ACK.
//
//  - ACLed: LocalSystem full control; Authenticated Users may
//    connect/read/write; nobody else.
//  - Multi-instance: several concurrent accept loops so multiple
//    interactive sessions (incl. RDP / fast user switching) can
//    report at once.
//  - Untrusted input: payloads are bounded and only deserialized,
//    never executed.
// ============================================================
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Prism.Agent.Contracts;

namespace Prism.Agent;

/// <summary>Context passed to the batch handler. ClientUserSid is OS-derived (not self-reported).</summary>
public sealed record UsageContext(string? ClientUserSid, UsageBatch Batch);

public sealed class UsagePipeServer : IAsyncDisposable
{
    private readonly Func<UsageContext, CancellationToken, Task<UsageAck>> _handler;
    private readonly int _concurrentInstances;
    private CancellationTokenSource? _cts;
    private Task[]? _loops;
    private long _lastErrLogTicks;   // throttle for client-error logging (shared across loops)

    // v2 liveness ledger: TickCount64 of the last batch each SESSION delivered. A tracker
    // that is alive-as-a-process but hung stops shipping; the watchdog reads this to tell
    // "healthy but quiet" from "wedged" and restarts the latter. Session id comes from the
    // OS (client PID -> session), never from the payload.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, long> s_lastContact = new();
    public static long? LastContactTicks(uint sessionId)
        => s_lastContact.TryGetValue(sessionId, out long t) ? t : null;
    public static void ForgetSessionContact(uint sessionId) => s_lastContact.TryRemove(sessionId, out _);

    public UsagePipeServer(Func<UsageContext, CancellationToken, Task<UsageAck>> handler, int concurrentInstances = 4)
    {
        _handler = handler;
        _concurrentInstances = Math.Max(1, concurrentInstances);
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _loops = Enumerable.Range(0, _concurrentInstances)
            .Select(_ => Task.Run(() => AcceptLoopAsync(_cts.Token)))
            .ToArray();
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = CreateInstance();
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                await HandleClientAsync(server, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* shutting down */ }
            catch (Exception ex)
            {
                // One bad client must never take down the loop. Log to the Event
                // Log (there's no console under the SCM), throttled so a misbehaving
                // local client can't flood it.
                LogClientErrorThrottled(ex.Message);
            }
            finally
            {
                try { if (server is { IsConnected: true }) server.Disconnect(); } catch { /* ignore */ }
                server?.Dispose();
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        byte[]? payload = await PipeFraming.ReadFrameAsync(server, PipeProtocol.MaxFrameBytes, ct).ConfigureAwait(false);
        if (payload is null || payload.Length == 0) return;

        UsageBatch? batch = JsonSerializer.Deserialize(payload, AgentJsonContext.Default.UsageBatch);
        UsageAck ack;
        if (batch is null || batch.SchemaVersion != PipeProtocol.SchemaVersion)
        {
            ack = new UsageAck(false, 0, "unsupported or malformed batch", null);
        }
        else
        {
            // Trust the OS, not the payload, for the user's identity.
            string? sid = ProcessNative.TryGetClientUserSid(server.SafePipeHandle);
            try
            {
                ack = await _handler(new UsageContext(sid, batch), ct).ConfigureAwait(false);
                // Liveness: a successfully-handled batch proves the session's tracker works.
                if (ProcessNative.TryGetClientSessionId(server.SafePipeHandle) is { } sess)
                    s_lastContact[sess] = Environment.TickCount64;
            }
            catch (Exception ex)
            {
                ack = new UsageAck(false, 0, $"handler error: {ex.Message}", null);
            }
        }

        byte[] ackBytes = JsonSerializer.SerializeToUtf8Bytes(ack, AgentJsonContext.Default.UsageAck);
        await PipeFraming.WriteFrameAsync(server, ackBytes, PipeProtocol.MaxFrameBytes, ct).ConfigureAwait(false);
    }

    private void LogClientErrorThrottled(string message)
    {
        long now  = Environment.TickCount64;
        long last = Interlocked.Read(ref _lastErrLogTicks);
        if (now - last < 5_000) return;                 // at most ~one every 5s across all loops
        Interlocked.Exchange(ref _lastErrLogTicks, now);
        LocalSink.Log($"pipe client error: {message}", System.Diagnostics.EventLogEntryType.Warning, eventId: 120);
    }

    private static NamedPipeServerStream CreateInstance()
    {
        var security = new PipeSecurity();

        // LocalSystem (the service): full control.
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));

        // INTERACTIVE (S-1-5-4) only: exactly the logged-on users whose sessions the
        // launcher spawns trackers into. The previous Authenticated Users grant also
        // admitted domain service accounts, which could submit fabricated usage under
        // their own (real) SID and skew licence metrics - tightened v2. If a scheduled-
        // task client ever becomes a requirement, add ITS specific account SID here
        // explicitly rather than re-widening to all authenticated principals.
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            PipeProtocol.PipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            pipeSecurity: security);
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is null) return;
        await _cts.CancelAsync().ConfigureAwait(false);
        try { if (_loops is not null) await Task.WhenAll(_loops).ConfigureAwait(false); }
        catch { /* loops cancel */ }
        _cts.Dispose();
    }
}
