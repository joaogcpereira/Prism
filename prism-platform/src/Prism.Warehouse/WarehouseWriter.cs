// ============================================================
//  WarehouseWriter.cs  (Prism.Warehouse)
//  Loads a DataTable into a target table via:
//    1. SELECT <cols> INTO #stg FROM <target> WHERE 1=0   (typed staging)
//    2. SqlBulkCopy DataTable -> #stg                      (fast load)
//    3. REPLACE (delete-by-source + insert) or UPSERT (MERGE) in a transaction
//  #stg is session-scoped, so concurrent loads (gateway app-usage vs. the
//  connector job) never collide. Each load is logged to meta.LoadRun.
// ============================================================
using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Prism.Warehouse;

public enum LoadMode { Replace, Upsert }

public sealed class WarehouseWriter
{
    private readonly string _cs;
    private readonly ILogger _log;

    public WarehouseWriter(string connectionString, ILogger log)
    {
        _cs = connectionString;
        _log = log;
    }

    /// <summary>Full-snapshot load: replace all rows for this Source with the incoming set.</summary>
    public Task ReplaceAsync(string target, string[] cols, DataTable data, string source, string runId, string entity, CancellationToken ct)
        => LoadAsync(target, cols, data, LoadMode.Replace, keyCols: [], onPredicate: null, source, runId, entity, ct);

    /// <summary>Time-series load: MERGE by natural key (update in place, insert new).</summary>
    public Task UpsertAsync(string target, string[] cols, string[] keyCols, string onPredicate, DataTable data, string source, string runId, string entity, CancellationToken ct)
        => LoadAsync(target, cols, data, LoadMode.Upsert, keyCols, onPredicate, source, runId, entity, ct);

    private async Task LoadAsync(string target, string[] cols, DataTable data, LoadMode mode,
        string[] keyCols, string? onPredicate, string source, string runId, string entity, CancellationToken ct)
    {
        DateTime started = DateTime.UtcNow;
        string colList = string.Join(", ", cols.Select(Q));

        // Safety: never replace/merge a table with an empty snapshot. A transient empty
        // result (or a not-yet-populated warehouse) must not delete previously good rows
        // and blank the dashboard. Keep the prior load until a non-empty one arrives.
        if (data.Rows.Count == 0)
        {
            _log.LogWarning("Skipping {Mode} of {Target} for source '{Source}': incoming snapshot is empty; keeping existing rows.",
                mode, target, source);
            return;
        }

        await using var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        // 1. typed session-scoped staging table
        await Exec(conn, null, $"SELECT {colList} INTO #stg FROM {target} WHERE 1 = 0", ct).ConfigureAwait(false);

        // 2. bulk load into staging
        using (var bulk = new SqlBulkCopy(conn) { DestinationTableName = "#stg", BulkCopyTimeout = 600 })
        {
            foreach (string c in cols) bulk.ColumnMappings.Add(c, c);
            await bulk.WriteToServerAsync(data, ct).ConfigureAwait(false);
        }

        // 3. merge into target, transactionally
        await using (SqlTransaction tx = (SqlTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false))
        {
            try
            {
                string sql = mode == LoadMode.Replace
                    ? $"DELETE FROM {target} WHERE ISNULL(Source,'') = ISNULL(@src,''); " +
                      $"INSERT INTO {target} ({colList}) SELECT {colList} FROM #stg;"
                    : BuildUpsert(target, cols, keyCols, onPredicate!);

                await using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = sql;
                    cmd.CommandTimeout = 600;
                    if (mode == LoadMode.Replace)
                        cmd.Parameters.Add(new SqlParameter("@src", (object?)source ?? DBNull.Value));
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                await tx.CommitAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                await LogRun(conn, runId, source, entity, mode, data.Rows.Count, started, "ERROR", ct).ConfigureAwait(false);
                throw;
            }
        }

        await LogRun(conn, runId, source, entity, mode, data.Rows.Count, started, "OK", ct).ConfigureAwait(false);
        _log.LogInformation("warehouse {Entity}: {Mode} {Rows} row(s) -> {Target}", entity, mode, data.Rows.Count, target);
    }

    private static string BuildUpsert(string target, string[] cols, string[] keyCols, string onPredicate)
    {
        string colList = string.Join(", ", cols.Select(Q));
        string insVals = string.Join(", ", cols.Select(c => "S." + Q(c)));
        IEnumerable<string> nonKey = cols.Where(c => !keyCols.Contains(c, StringComparer.OrdinalIgnoreCase));
        string setList = string.Join(", ", nonKey.Select(c => $"T.{Q(c)} = S.{Q(c)}"));

        return $@"
MERGE {target} AS T
USING #stg AS S ON ({onPredicate})
WHEN MATCHED THEN UPDATE SET {setList}
WHEN NOT MATCHED BY TARGET THEN INSERT ({colList}) VALUES ({insVals});";
    }

    private static async Task LogRun(SqlConnection conn, string runId, string? source, string entity,
        LoadMode mode, int rows, DateTime started, string status, CancellationToken ct)
    {
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO meta.LoadRun (RunId, Source, Entity, Mode, [RowCount], StartedUtc, CompletedUtc, Status) " +
                "VALUES (@r, @s, @e, @m, @c, @st, sysutcdatetime(), @status)";
            cmd.Parameters.AddWithValue("@r", (object?)runId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@s", (object?)source ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@e", (object?)entity ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@m", mode.ToString());
            cmd.Parameters.AddWithValue("@c", rows);
            cmd.Parameters.AddWithValue("@st", started);
            cmd.Parameters.AddWithValue("@status", status);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch { /* logging is best-effort; never fail a load because the log insert failed */ }
    }

    private static async Task Exec(SqlConnection conn, SqlTransaction? tx, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        if (tx is not null) cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.CommandTimeout = 600;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static string Q(string ident) => "[" + ident.Replace("]", "]]") + "]";
}
