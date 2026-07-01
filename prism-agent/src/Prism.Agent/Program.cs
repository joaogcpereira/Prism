// ============================================================
//  Program.cs  (Prism.Agent)  - single merged executable
//
//  One binary, multiple modes selected by a command-line switch:
//
//    prism-agent.exe                 -> service mode (default)
//    prism-agent.exe --service       -> service mode (explicit)
//    prism-agent.exe --session       -> per-session usage tracker
//    prism-agent.exe --install       -> register the Windows service (elevated)
//    prism-agent.exe --uninstall     -> remove the Windows service    (elevated)
//
//  The service launches THIS SAME exe with --session into each
//  interactive user session (CreateProcessAsUser), so there is
//  only one file to deploy.
// ============================================================
using Prism.Agent;

string mode = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : "--service";

return mode switch
{
    "--session"               => await SessionMode.RunAsync(),
    "--service" or ""         => await ServiceMode.RunAsync(args),
    "--install"               => InstallMode.Install(),
    "--uninstall"             => InstallMode.Uninstall(),
    "-h" or "--help" or "/?"  => Help(),
    _                         => Unknown(mode)
};

static int Help()
{
    Console.WriteLine("""
        Contoso Prism Agent (usage metering)
          (no args) | --service   Run the service host (pipe server; launches session trackers).
          --session               Run the per-session usage tracker (launched by the service).
          --install               Register the Windows service (run elevated).
          --uninstall             Stop and remove the Windows service (run elevated).
          --help                  Show this help.
        """);
    return 0;
}

static int Unknown(string mode)
{
    Console.Error.WriteLine($"Unknown argument '{mode}'. Try --help.");
    return 2;
}
