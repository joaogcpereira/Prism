// ============================================================
//  UsageSink.cs
//  The landing abstraction and its durable file implementation.
//
//  Durability contract: the agent deletes its spooled file only on
//  a 2xx, so WriteAsync MUST flush to disk before completing. The
//  file sink appends one NDJSON line per batch under a single-writer
//  lock and fsyncs (Flush(flushToDisk:true)) before returning.
//
//  Swap this for Blob / Event Hub / ADX by implementing IUsageSink;
//  the file sink is the runnable default and the documented seam.
// ============================================================
using System.Text;
using System.Text.Json;

namespace Prism.Gateway;

public interface IUsageSink
{
    /// <summary>Durably persist one landed batch. Throws on failure (=> caller returns 503).</summary>
    Task WriteAsync(LandedBatch batch, CancellationToken ct);
}

public sealed class FileUsageSink : IUsageSink, IAsyncDisposable
{
    private static readonly byte[] s_newline = "\n"u8.ToArray();

    private readonly string _dir;
    private readonly ILogger<FileUsageSink> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);   // single writer => no interleaving/corruption

    private FileStream? _stream;
    private string? _openDay;

    public FileUsageSink(GatewayOptions opts, ILogger<FileUsageSink> log)
    {
        _dir = Path.GetFullPath(opts.LandingDirectory);
        _log = log;
        Directory.CreateDirectory(_dir);
        _log.LogInformation("Usage landing directory: {Dir}", _dir);
    }

    public async Task WriteAsync(LandedBatch batch, CancellationToken ct)
    {
        byte[] line = JsonSerializer.SerializeToUtf8Bytes(batch, GatewayJsonContext.Default.LandedBatch);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            FileStream fs = EnsureStreamForToday();
            await fs.WriteAsync(line, ct).ConfigureAwait(false);
            await fs.WriteAsync(s_newline, ct).ConfigureAwait(false);
            fs.Flush(flushToDisk: true);                 // fsync: durable before we return 200
        }
        finally { _gate.Release(); }
    }

    private FileStream EnsureStreamForToday()
    {
        string day = DateTime.UtcNow.ToString("yyyy-MM-dd");
        if (_openDay == day && _stream is not null) return _stream;

        _stream?.Flush(true);
        _stream?.Dispose();

        string path = Path.Combine(_dir, $"usage-{day}.ndjson");
        _stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read,
                                 bufferSize: 4096, FileOptions.None);
        _openDay = day;
        _log.LogInformation("Appending to {Path}", path);
        return _stream;
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { _stream?.Flush(true); _stream?.Dispose(); _stream = null; }
        finally { _gate.Release(); _gate.Dispose(); }
    }
}
