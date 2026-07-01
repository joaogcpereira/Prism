// ============================================================
//  SessionMode.cs  (Prism.Agent)
//  The per-session usage tracker (runs in the user's session).
//  Owns the tracker on a dedicated STA message-pump thread and
//  ships rollups up the pipe with ACK-gated purge.
//
//  Shutdown is handled without disposing the CancellationTokenSource
//  out from under the Ctrl+C / ProcessExit handlers (that race threw
//  ObjectDisposedException on exit). The CTS is intentionally NOT
//  wrapped in `using`; it is cancelled, never disposed, so late
//  callbacks are safe.
// ============================================================
using Prism.Agent.Contracts;

namespace Prism.Agent;

internal static class SessionMode
{
    public static async Task<int> RunAsync()
    {
        // --- start the tracker on its own STA pump thread ---------------
        UsageTracker? tracker = null;
        using (var ready = new ManualResetEventSlim(false))
        {
            var pump = new Thread(() =>
            {
                tracker = new UsageTracker();
                ready.Set();
                tracker.Start();          // blocks in the message loop until Stop()
            })
            {
                IsBackground = true,
                Name = "PrismUsagePump"
            };
            pump.SetApartmentState(ApartmentState.STA);
            pump.Start();
            ready.Wait();
        }

        // NOTE: not `using` — see file header. Cancelled on shutdown, never disposed.
        var shutdown = new CancellationTokenSource();

        // Cancel quietly; guard against any late call after a (hypothetical) dispose.
        void RequestStop()
        {
            try { shutdown.Cancel(); } catch (ObjectDisposedException) { }
        }
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; RequestStop(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => RequestStop();

        var client = new UsagePipeClient();
        var shipInterval = TimeSpan.FromMinutes(5);

        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] session tracker started (pid {Environment.ProcessId}, v{AgentInfo.Version}); " +
                          $"measuring, shipping every {shipInterval.TotalMinutes:0} min to \\\\.\\pipe\\{PipeProtocol.PipeName}. Ctrl+C to stop.");

        async Task ShipAsync(CancellationToken ct, bool isFinal)
        {
            IReadOnlyList<UsageRollup> rollups = tracker!.SnapshotRollups();
            if (rollups.Count == 0)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] nothing to ship yet.");
                return;
            }

            int offsetMin = (int)DateTimeOffset.Now.Offset.TotalMinutes;
            var batch = new UsageBatch(PipeProtocol.SchemaVersion, Environment.MachineName,
                                       AgentInfo.Version, offsetMin, rollups);
            long active = 0; foreach (var r in rollups) active += r.ForegroundActiveSeconds;

            UsageAck? ack = await client.SendAsync(batch, ct);
            string tag = isFinal ? "final ship" : "ship";

            if (ack is { Accepted: true })
            {
                tracker.PurgeShippedBefore(DateTime.Now.ToString("yyyy-MM-dd"));
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {tag} OK: {rollups.Count} app-day record(s), " +
                                  $"{active}s fg-active, service accepted {ack.Received}.");
                if (ack.Config is { } cfg)
                {
                    // Apply the pushed tuning live (idle threshold immediately;
                    // a changed sample interval re-arms the snapshot timer).
                    tracker.ApplyConfig(cfg.SampleIntervalSeconds, cfg.IdleThresholdSeconds);
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] config applied: sample={cfg.SampleIntervalSeconds}s idle={cfg.IdleThresholdSeconds}s");
                }
            }
            else
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {tag} DEFERRED: service unreachable; " +
                                  $"{rollups.Count} record(s) retained, will retry.");
            }
        }

        try
        {
            while (!shutdown.IsCancellationRequested)
            {
                try { await Task.Delay(shipInterval, shutdown.Token); }
                catch (TaskCanceledException) { break; }   // Ctrl+C during the wait

                if (shutdown.IsCancellationRequested) break;
                try { await ShipAsync(shutdown.Token, isFinal: false); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ship error: {ex.Message}"); }
            }
        }
        finally
        {
            // Best-effort final flush (uncancelled token so it isn't aborted by the stop).
            try { await ShipAsync(CancellationToken.None, isFinal: true); }
            catch (Exception ex) { Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] final ship error: {ex.Message}"); }

            tracker!.Stop();
            tracker.Dispose();
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] session tracker stopped.");
            // shutdown is deliberately not disposed.
        }

        return 0;
    }
}
