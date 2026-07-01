// ============================================================
//  VerdictWriter.cs  (Prism.Scoring)
//  Persists results: score.AssignmentVerdict + score.SkuVerdict are
//  full recomputes (REPLACE via the shared WarehouseWriter); a row
//  is appended to score.RunSummary for the dashboard KPIs.
// ============================================================
using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Prism.Warehouse;

namespace Prism.Scoring;

public sealed class VerdictWriter(string connectionString, ILogger log)
{
    private const string Source = "scoring";
    private readonly string _cs = connectionString;
    private readonly WarehouseWriter _wh = new(connectionString, log);

    public sealed record AssignmentRow(
        string UserId, string SkuId, string? Upn, string? DisplayName, string? SkuPart, string? SkuName,
        string Verdict, int Score, string Confidence, string Reasons, int? InactiveDays, decimal? Savings, string? Currency,
        string? Department, string? Country);

    public sealed record SkuRowOut(
        string SkuId, string? SkuPart, string? SkuName, string Verdict, int Owned, int Assigned, int Idle,
        string Reasons, decimal? Savings, string? Currency);

    private static readonly string[] AssignCols =
    [
        "UserId","SkuId","UserPrincipalName","DisplayName","SkuPartNumber","SkuName","Verdict","WasteScore",
        "Confidence","ReasonCodes","EffectiveInactiveDays","EstMonthlySavings","Currency","Department","Country","Source","RunId","ScoredUtc"
    ];
    private static readonly string[] SkuCols =
    [
        "SkuId","SkuPartNumber","SkuName","Verdict","SeatsOwned","SeatsAssigned","SeatsIdle",
        "ReasonCodes","EstMonthlySavings","Currency","Source","RunId","ScoredUtc"
    ];

    public async Task WriteAssignmentsAsync(IReadOnlyList<AssignmentRow> rows, string runId, DateTime scoredUtc, CancellationToken ct)
    {
        var dt = NewTable(AssignCols,
            [typeof(string),typeof(string),typeof(string),typeof(string),typeof(string),typeof(string),typeof(string),typeof(int),
             typeof(string),typeof(string),typeof(int),typeof(decimal),typeof(string),typeof(string),typeof(string),typeof(string),typeof(string),typeof(DateTime)]);
        foreach (AssignmentRow r in rows)
            dt.Rows.Add(O(r.UserId), O(r.SkuId), O(r.Upn), O(r.DisplayName), O(r.SkuPart), O(r.SkuName), O(r.Verdict), r.Score,
                O(r.Confidence), O(r.Reasons), O(r.InactiveDays), O(r.Savings), O(r.Currency), O(r.Department), O(r.Country), Source, runId, scoredUtc);
        await _wh.ReplaceAsync("score.AssignmentVerdict", AssignCols, dt, Source, runId, "assignment-verdict", ct);
    }

    public async Task WriteSkusAsync(IReadOnlyList<SkuRowOut> rows, string runId, DateTime scoredUtc, CancellationToken ct)
    {
        var dt = NewTable(SkuCols,
            [typeof(string),typeof(string),typeof(string),typeof(string),typeof(int),typeof(int),typeof(int),
             typeof(string),typeof(decimal),typeof(string),typeof(string),typeof(string),typeof(DateTime)]);
        foreach (SkuRowOut r in rows)
            dt.Rows.Add(O(r.SkuId), O(r.SkuPart), O(r.SkuName), O(r.Verdict), r.Owned, r.Assigned, r.Idle,
                O(r.Reasons), O(r.Savings), O(r.Currency), Source, runId, scoredUtc);
        await _wh.ReplaceAsync("score.SkuVerdict", SkuCols, dt, Source, runId, "sku-verdict", ct);
    }

    public async Task WriteRunSummaryAsync(string runId, DateTime scoredUtc, int assignments, int keep, int review,
        int reclaim, decimal reclaimSavings, decimal reviewSavings, decimal idleSavings, string currency, CancellationToken ct)
    {
        await using var c = new SqlConnection(_cs);
        await c.OpenAsync(ct);
        await using var cmd = new SqlCommand(
            "INSERT INTO score.RunSummary (RunId, ScoredUtc, Assignments, KeepCount, ReviewCount, ReclaimCount, " +
            "ReclaimMonthlySavings, ReviewMonthlySavings, IdleSeatMonthlySavings, Currency) " +
            "VALUES (@r,@s,@a,@k,@v,@c,@rs,@vs,@is,@cur)", c);
        cmd.Parameters.AddWithValue("@r", runId);
        cmd.Parameters.AddWithValue("@s", scoredUtc);
        cmd.Parameters.AddWithValue("@a", assignments);
        cmd.Parameters.AddWithValue("@k", keep);
        cmd.Parameters.AddWithValue("@v", review);
        cmd.Parameters.AddWithValue("@c", reclaim);
        cmd.Parameters.AddWithValue("@rs", reclaimSavings);
        cmd.Parameters.AddWithValue("@vs", reviewSavings);
        cmd.Parameters.AddWithValue("@is", idleSavings);
        cmd.Parameters.AddWithValue("@cur", currency);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static DataTable NewTable(string[] cols, Type[] types)
    {
        var dt = new DataTable();
        for (int i = 0; i < cols.Length; i++) dt.Columns.Add(cols[i], types[i]);
        return dt;
    }

    private static object O(object? v) => v ?? DBNull.Value;
}
