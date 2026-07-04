// ============================================================
//  WarehouseUsageSink.cs  (Prism.Gateway)
//  Production IUsageSink: flattens each accepted batch into
//  canonical FactAppUsage rows and writes them to the warehouse
//  via the shared IIngestionSink (UPSERT by date+device+sid+exe).
//
//  Durability: WriteAsync awaits the SQL MERGE commit, so by the
//  time it returns the rows are durable - same 200-after-persist
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

        // Input hardening at the trust boundary: the payload is authenticated (mTLS)
        // but still CLIENT-SUPPLIED. Clamp strings to the fact.AppUsage column widths
        // (one oversized path must not fail the whole batch's bulk copy), zero any
        // negative counters, and drop rollups whose date is not a plausible calendar
        // day (yyyy-MM-dd within today-370 .. today+2 - tolerating timezone skew).
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        int dropped = 0;
        var envelopes = new List<EntityEnvelope<FactAppUsage>>(b.Rollups.Count);
        foreach (UsageRollup r in b.Rollups)
        {
            if (!DateOnly.TryParseExact(r.Date, "yyyy-MM-dd", out DateOnly day)
                || day < today.AddDays(-370) || day > today.AddDays(2))
            { dropped++; continue; }

            envelopes.Add(new EntityEnvelope<FactAppUsage>(
                Source, landed.ReceiveId, landed.ReceivedAtUtc,
                new FactAppUsage(
                    Date: r.Date,
                    DeviceThumbprint: landed.DeviceThumbprint,   // authoritative (cert-derived)
                    MachineName: Trunc(b.MachineName, 128),
                    UserSid: Trunc(b.UserSid, 128),
                    ExePath: Trunc(r.ExePath, 500) ?? "",
                    DisplayName: Trunc(ResolveName(r), 256),     // Description -> ProductName -> filename
                    ProductName: Trunc(r.ProductName, 256),
                    Description: Trunc(r.Description, 256),
                    Company: Trunc(r.Company, 256),
                    FileVersion: Trunc(r.FileVersion, 64),
                    Launches: Math.Max(0, r.Launches),
                    FirstSeenUtc: r.FirstSeenUtc,
                    LastSeenUtc: r.LastSeenUtc,
                    ForegroundActiveSeconds: Math.Max(0, r.ForegroundActiveSeconds),
                    ForegroundIdleSeconds: Math.Max(0, r.ForegroundIdleSeconds),
                    VisibleBackgroundSeconds: Math.Max(0, r.VisibleBackgroundSeconds),
                    MinimizedSeconds: Math.Max(0, r.MinimizedSeconds),
                    TraySeconds: Math.Max(0, r.TraySeconds),
                    UtcOffsetMinutes: Math.Clamp(b.UtcOffsetMinutes, -14 * 60, 14 * 60),
                    AgentVersion: Trunc(b.AgentVersion, 32),
                    ReceiveId: landed.ReceiveId)));
        }
        if (dropped > 0)
            _log.LogWarning("Dropped {Dropped} rollup(s) with implausible dates from {Machine} (batch {Id}).",
                dropped, b.MachineName, landed.ReceiveId);

        await _ingestion.WriteAsync("app-usage", envelopes, ct).ConfigureAwait(false);
        _log.LogInformation("Warehoused {Count} app-usage row(s) from {Machine} (batch {Id}).",
            envelopes.Count, b.MachineName, landed.ReceiveId);
    }

    private static string? Trunc(string? s, int max)
        => s is null || s.Length <= max ? s : s[..max];

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
