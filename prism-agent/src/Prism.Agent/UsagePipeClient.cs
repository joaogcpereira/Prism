// ============================================================
//  UsagePipeClient.cs  (Prism.Agent)
//  Connects to the Prism service's named pipe and ships a batch
//  of usage rollups, returning the service's acknowledgement.
//
//  Security: connects at TokenImpersonationLevel.Identification
//  so the server can learn who we are but can NOT impersonate us
//  to access resources as the logged-on user.
// ============================================================
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using Prism.Agent.Contracts;

namespace Prism.Agent;

internal sealed class UsagePipeClient
{
    private readonly int _connectTimeoutMs;

    public UsagePipeClient(int connectTimeoutMs = 5_000) => _connectTimeoutMs = connectTimeoutMs;

    /// <summary>
    /// Sends a batch. Returns the ACK on success, or null if the service
    /// could not be reached (the caller keeps the data and retries later).
    /// </summary>
    public async Task<UsageAck?> SendAsync(UsageBatch batch, CancellationToken ct)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(
                serverName: ".",
                pipeName:   PipeProtocol.PipeName,
                direction:  PipeDirection.InOut,
                options:    PipeOptions.Asynchronous,
                impersonationLevel: TokenImpersonationLevel.Identification);

            await pipe.ConnectAsync(_connectTimeoutMs, ct).ConfigureAwait(false);

            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(batch, AgentJsonContext.Default.UsageBatch);
            await PipeFraming.WriteFrameAsync(pipe, payload, PipeProtocol.MaxFrameBytes, ct).ConfigureAwait(false);

            byte[]? ackBytes = await PipeFraming.ReadFrameAsync(pipe, PipeProtocol.MaxFrameBytes, ct).ConfigureAwait(false);
            if (ackBytes is null || ackBytes.Length == 0) return null;

            return JsonSerializer.Deserialize(ackBytes, AgentJsonContext.Default.UsageAck);
        }
        catch (OperationCanceledException) { throw; }
        catch (TimeoutException)           { return null; }  // service not listening
        catch (IOException)                { return null; }  // pipe broke mid-transfer
        catch (UnauthorizedAccessException){ return null; }
    }
}
