// ============================================================
//  WarehouseUsageSink.cs  (Prism.Gateway)
//  Production IUsageSink: flattens each accepted batch into
//  canonical FactAppUsage rows and writes them to the warehouse
//  via the shared IIngestionSink (UPSERT by date+device+sid+exe).
//
//  Durability: WriteAsync awaits the SQL MERGE commit, so by the
//  time it returns the rows are durable — same 200-after-persist
//  contract the file sink honoured. A failure throws, and the
//  ingest handler turns that into a 503 so the agent retries; the
//  UPSERT key makes that retry idempotent (no double counting).
//
//  This replaces the old connector-side gateway-landing loader:
//  the agent's usage now lands in the warehouse in one hop.
// ============================================================
using Prism.Agent.Contracts;
using Prism.Warehouse;
using Prism.Warehouse.Model;

namespace Prism.Gateway;

public sealed class WarehouseUsageSink : IUsageSink
{
    private const string Source = "agent.app-usage";

    private readonly IIngestionSink _ingestion;
    private readonly ILogger<WarehouseUsageSink> _log;

    public WarehouseUsageSink(IIngestionSink ingestion, ILogger<WarehouseUsageSink> log)
    {
        _ingestion = ingestion;
        _log = log;
    }

    public async Task WriteAsync(LandedBatch landed, CancellationToken ct)
    {
        ReceivedBatch b = landed.Batch;

        var envelopes = b.Rollups.Select(r => new EntityEnvelope<FactAppUsage>(
            Source, landed.ReceiveId, landed.ReceivedAtUtc,
            new FactAppUsage(
                Date: r.Date,
                DeviceThumbprint: landed.DeviceThumbprint,   // authoritative (cert-derived)
                MachineName: b.MachineName,
                UserSid: b.UserSid,
                ExePath: r.ExePath,
                DisplayName: ResolveName(r),                 // Description -> ProductName -> filename
                ProductName: r.ProductName,
                Description: r.Description,
                Company: r.Company,
                FileVersion: r.FileVersion,
                Launches: r.Launches,
                FirstSeenUtc: r.FirstSeenUtc,
                LastSeenUtc: r.LastSeenUtc,
                ForegroundActiveSeconds: r.ForegroundActiveSeconds,
                ForegroundIdleSeconds: r.ForegroundIdleSeconds,
                VisibleBackgroundSeconds: r.VisibleBackgroundSeconds,
                MinimizedSeconds: r.MinimizedSeconds,
                TraySeconds: r.TraySeconds,
                UtcOffsetMinutes: b.UtcOffsetMinutes,
                AgentVersion: b.AgentVersion,
                ReceiveId: landed.ReceiveId)))
            .ToList();

        await _ingestion.WriteAsync("app-usage", envelopes, ct).ConfigureAwait(false);
        _log.LogInformation("Warehoused {Count} app-usage row(s) from {Machine} (batch {Id}).",
            envelopes.Count, b.MachineName, landed.ReceiveId);
    }

    private static string? ResolveName(UsageRollup r)
    {
        if (!string.IsNullOrWhiteSpace(r.Description)) return r.Description!.Trim();
        if (!string.IsNullOrWhiteSpace(r.ProductName)) return r.ProductName!.Trim();
        if (!string.IsNullOrWhiteSpace(r.ExePath))
        {
            try { return Path.GetFileName(r.ExePath); } catch { /* fall through */ }
        }
        return null;
    }
}
