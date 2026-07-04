// ============================================================
//  AgentService.cs  (Prism.Agent)
//  The actual work the service does, independent of how it's
//  hosted (real Windows Service or console/dev). Owns the pipe
//  server and, in service mode, the per-session launcher.
//
//  Every received batch is spooled locally for the uploader to
//  deliver to the gateway over mTLS. No permanent local audit file
//  is kept (it would grow unbounded); lifecycle and warnings go to
//  the Windows Event Log.
// ============================================================
using Prism.Agent.Contracts;

namespace Prism.Agent;

internal sealed class AgentService
{
    private UsagePipeServer? _pipe;
    private SessionLauncher? _launcher;
    private Uploader? _uploader;
    private Timer? _watchdog;
    private bool _launch;

    public void Start(bool enableSessionLauncher)
    {
        _launch = enableSessionLauncher;

        LocalSink.Init();
        LocalSink.Log($"Contoso Prism Agent starting (v{AgentInfo.Version}, mode={(enableSessionLauncher ? "service" : "console")}); " +
                      $"pipe \\\\.\\pipe\\{PipeProtocol.PipeName}; spool in {LocalSink.SpoolDir}", eventId: 100);

        _pipe = new UsagePipeServer(HandleBatchAsync, concurrentInstances: 4);
        _pipe.Start();

        // mTLS uploader: forwards spooled batches to the gateway. Self-disables
        // if there's no config.json or no device certificate yet.
        _uploader = Uploader.TryCreate();
        _uploader?.Start();

        if (_launch)
        {
            _launcher = new SessionLauncher();
            _launcher.LaunchInAllActiveSessions();
            // Watchdog: every 60s (a) re-launch the tracker in any active session that
            // lacks a live one (idempotent; covers missed logons and helper crashes),
            // and (b) v2: restart trackers that are alive-but-silent on the pipe - a
            // hung tracker passes the process-exists check yet measures nothing. A
            // healthy tracker ships every ~5 min; 20 min of silence (or 10 min with
            // no first contact after launch) is decisively wedged.
            _watchdog = new Timer(_ =>
            {
                try
                {
                    _launcher?.LaunchInAllActiveSessions();
                    _launcher?.RestartSilentTrackers(UsagePipeServer.LastContactTicks,
                        silentAfterMs: 20 * 60_000, firstContactGraceMs: 10 * 60_000);
                }
                catch { /* keep service alive */ }
            }, null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
        }
    }

    public void OnSessionLogon(uint sessionId)
    {
        if (_launch) { try { _launcher?.LaunchInSession(sessionId); } catch { } }
    }

    // On logoff, terminate (not merely forget) the tracker we spawned into that
    // session; the user's token is gone and a stranded tracker would otherwise
    // linger until its next failed pipe send.
    public void OnSessionLogoff(uint sessionId) => _launcher?.StopSession(sessionId);

    public void Stop()
    {
        LocalSink.Log("Contoso Prism Agent stopping.", eventId: 101);
        _watchdog?.Dispose();          // stop the watchdog first so it cannot relaunch trackers
        _launcher?.StopAll();          // terminate every per-session tracker we spawned
        _uploader?.Dispose();
        if (_pipe is not null)
            _pipe.DisposeAsync().AsTask().GetAwaiter().GetResult();   // sync wait: caller is the service stop path
    }

    private static Task<UsageAck> HandleBatchAsync(UsageContext ctx, CancellationToken ct)
    {
        // Spool the batch for upload. The uploader drains the spool to the
        // gateway over mTLS on its own cadence. No permanent local audit is kept.
        LocalSink.WriteBatch(ctx);

        return Task.FromResult(new UsageAck(
            Accepted: true, Received: ctx.Batch.Rollups.Count, Message: null,
            Config: new AgentConfig(SampleIntervalSeconds: 30, IdleThresholdSeconds: 60)));
    }
}
