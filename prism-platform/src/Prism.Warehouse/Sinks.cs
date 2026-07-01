// ============================================================
//  Sinks.cs  (Prism.Warehouse)
//  The IIngestionSink seam plus the dev/local FileIngestionSink.
//  The SQL implementation lives in SqlIngestionSink.cs.
//
//  The interface deliberately does NOT take a JsonTypeInfo: the
//  production (SQL) sink never serialises, and the dev file sink
//  uses reflection JSON (these hosts are not AOT/trimmed). One
//  interface, one call shape, no source-gen ceremony at call sites.
// ============================================================
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Prism.Warehouse.Model;

namespace Prism.Warehouse;

public sealed class WarehouseOptions
{
    // Azure SQL connection string, e.g.:
    //   Server=tcp:prism-sql.database.windows.net,1433;Database=prism;
    //   Authentication=Active Directory Default;Encrypt=True;TrustServerCertificate=False;
    // "Active Directory Default" => managed identity in Azure, az login locally. No secret.
    public string ConnectionString { get; set; } = "";
}

public interface IIngestionSink
{
    /// <summary>Persist a batch of envelopes of one entity type.</summary>
    Task WriteAsync<T>(string entityName, IEnumerable<EntityEnvelope<T>> items, CancellationToken ct);
}

/// <summary>Dev/local sink: one NDJSON file per entity under a per-run folder.</summary>
public sealed class FileIngestionSink : IIngestionSink
{
    private static readonly byte[] s_newline = "\n"u8.ToArray();
    private static readonly JsonSerializerOptions s_json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string _runDir;
    private readonly ILogger _log;

    public FileIngestionSink(string landingRoot, string runId, ILogger log)
    {
        _log = log;
        _runDir = Path.Combine(Path.GetFullPath(landingRoot), runId);
        Directory.CreateDirectory(_runDir);
    }

    public async Task WriteAsync<T>(string entityName, IEnumerable<EntityEnvelope<T>> items, CancellationToken ct)
    {
        string path = Path.Combine(_runDir, $"{entityName}.ndjson");
        await using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        int n = 0;
        foreach (EntityEnvelope<T> item in items)
        {
            ct.ThrowIfCancellationRequested();
            byte[] line = JsonSerializer.SerializeToUtf8Bytes(item, s_json);
            await fs.WriteAsync(line, ct).ConfigureAwait(false);
            await fs.WriteAsync(s_newline, ct).ConfigureAwait(false);
            n++;
        }
        fs.Flush(flushToDisk: true);
        _log.LogInformation("wrote {Count} {Entity} record(s) -> {Path}", n, entityName, Path.GetFileName(path));
    }
}
