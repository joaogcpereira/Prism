// ============================================================
//  ServiceMode.cs  (Prism.Agent)
//  Entry for service mode. If launched by the SCM, runs as a real
//  Windows Service (blocks until stopped). If launched from a
//  console, falls back to a console host (pipe server only, no
//  per-session launching) for local/dev testing.
// ============================================================
using Prism.Agent.Contracts;

namespace Prism.Agent;

internal static class ServiceMode
{
    private const string ServiceName = AgentInfo.ServiceName;

    public static async Task<int> RunAsync(string[] args)
    {
        var service = new AgentService();

        // Blocks until the service stops if launched by the SCM; returns false
        // immediately (error 1063) when run from a console.
        if (ServiceHost.TryRunAsService(ServiceName, service))
            return 0;

        return await RunConsoleAsync(service);
    }

    private static async Task<int> RunConsoleAsync(AgentService service)
    {
        var cts = new CancellationTokenSource();                 // not disposed: avoids Ctrl+C race
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        };

        // Console/dev mode: do NOT launch per-session trackers (that needs the
        // SYSTEM service context). Start them by hand with `prism-agent.exe --session`.
        service.Start(enableSessionLauncher: false);
        Console.WriteLine($"Contoso Prism Agent (console/dev): pipe server on \\\\.\\pipe\\{PipeProtocol.PipeName}. Ctrl+C to stop.");

        try { await Task.Delay(Timeout.Infinite, cts.Token); }
        catch (TaskCanceledException) { }

        service.Stop();
        return 0;
    }
}
