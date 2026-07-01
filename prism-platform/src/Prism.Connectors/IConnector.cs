// ============================================================
//  IConnector.cs
//  Common shape for every Prism ingestion connector. The job host
//  runs each in sequence; one failing doesn't stop the others.
// ============================================================
namespace Prism.Connectors;

public interface IConnector
{
    /// <summary>Stable source name, e.g. "m365.service-usage" (also the run filter key).</summary>
    string Name { get; }

    Task RunAsync(CancellationToken ct);
}
