// ============================================================
//  PriceConnector.cs
//  Source: pricing.skucost. Read-only against Azure.
//  Replaces hardcoded prices: loads your *negotiated* per-seat unit
//  prices from the Cost Management Price Sheet API (current month,
//  in your billing currency) and upserts them into ref.SkuCost.
//
//  Why this API: there is no public "M365 list price by region" API
//  for a customer. The Azure Retail Prices API is Azure-services-only;
//  Partner Center price lists are CSP-partner-only. For an EA/MCA
//  *direct* customer the Price Sheet (over ARM, the token we already
//  use) is the authoritative source — and it's your real price, not
//  list. See PRICING.md for the full landscape and caveats.
//
//  IMPORTANT (honest status): whether M365 *seats* appear in the price
//  sheet depends on the agreement (MCA billing profiles can include
//  them; EA Azure price sheets often don't), and the price-sheet CSV
//  column names / units vary. The column resolution and the
//  product->skuPartNumber map are deliberately isolated and
//  config-driven; validate them against YOUR price sheet. Rows that
//  can't be mapped are logged, never guessed. Manual entries
//  (Origin='manual'/'org') are never overwritten.
// ============================================================
using System.IO.Compression;
using System.Text;
using Microsoft.Data.SqlClient;
using Prism.Connectors.Graph;

namespace Prism.Connectors;

public sealed class PriceConnector : IConnector, IDisposable
{
    public string Name => "pricing.skucost";

    private static readonly string[] PriceCols    = ["unitPrice", "effectivePrice", "price", "basePrice"];
    private static readonly string[] CurrencyCols = ["pricingCurrency", "billingCurrency", "currency", "currencyCode"];
    private static readonly string[] ProductCols  = ["productName", "product", "productOrderName", "meterName", "skuDescription"];
    private static readonly string[] UomCols      = ["unitOfMeasure", "unit", "uom"];

    private readonly AzureTokenProvider _tokens;
    private readonly ConnectorOptions _opts;
    private readonly string _runId;
    private readonly ILogger<PriceConnector> _log;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };

    public PriceConnector(AzureTokenProvider tokens, ConnectorOptions opts, string runId, ILogger<PriceConnector> log)
    {
        _tokens = tokens; _opts = opts; _runId = runId; _log = log;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_opts.ConnectionString))
        {
            _log.LogInformation("pricing.skucost: no warehouse connection string; skipping (prices live in ref.SkuCost).");
            return;
        }
        if (string.IsNullOrWhiteSpace(_opts.BillingAccountName))
        {
            _log.LogInformation("pricing.skucost: no BillingAccountName configured; skipping. " +
                "Maintain ref.SkuCost manually, or see PRICING.md to wire the Price Sheet API.");
            return;
        }

        string token = await _tokens.GetTokenAsync(AzureTokenProvider.ArmScope, ct);
        string downloadUrl = await RequestPriceSheetAsync(token, ct);
        byte[] payload = await DownloadAsync(downloadUrl, ct);

        int priced = 0, unmapped = 0;
        var unmappedSamples = new List<string>();
        string asOf = DateTime.UtcNow.ToString("yyyy-MM-dd");

        await using var conn = new SqlConnection(_opts.ConnectionString);
        await conn.OpenAsync(ct);

        foreach (TextReader csv in EnumerateCsvs(payload))
        {
            IEnumerator<string[]> rows = Csv.ReadRows(csv).GetEnumerator();
            if (!rows.MoveNext()) continue;
            string[] header = rows.Current;
            int iPrice = IndexOf(header, PriceCols);
            int iCur   = IndexOf(header, CurrencyCols);
            int iProd  = IndexOf(header, ProductCols);
            int iUom   = IndexOf(header, UomCols);
            if (iPrice < 0 || iProd < 0)
            {
                _log.LogWarning("pricing.skucost: could not locate price/product columns in header [{Header}].", string.Join(",", header));
                continue;
            }

            while (rows.MoveNext())
            {
                ct.ThrowIfCancellationRequested();
                string[] r = rows.Current;
                string product = Get(r, iProd);
                if (string.IsNullOrWhiteSpace(product)) continue;

                string? part = MapToSkuPartNumber(product);
                if (part is null) continue;   // not a SKU we track; ignore (most rows are Azure meters)

                if (!decimal.TryParse(Get(r, iPrice), System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out decimal price) || price <= 0)
                {
                    unmapped++; if (unmappedSamples.Count < 8) unmappedSamples.Add($"{product} (unparseable price)");
                    continue;
                }
                string currency = iCur >= 0 ? Get(r, iCur) : "USD";
                string? uom = iUom >= 0 ? Get(r, iUom) : null;

                await UpsertAsync(conn, part, product, price, currency, asOf, ct);
                priced++;
                if (uom is not null && !uom.Contains("month", StringComparison.OrdinalIgnoreCase))
                    _log.LogWarning("pricing.skucost: {Sku} priced from unit-of-measure '{Uom}' — verify it is per-seat-per-month.", part, uom);
            }
        }

        _log.LogInformation("pricing.skucost: upserted {Priced} SKU price(s) (as of {AsOf}); {Unmapped} row(s) skipped.{Note}",
            priced, asOf, unmapped, priced == 0
                ? " No tracked SKUs were found in the price sheet — your M365 seats may be billed outside this billing scope; maintain ref.SkuCost manually (see PRICING.md)."
                : "");
        if (unmappedSamples.Count > 0)
            _log.LogInformation("pricing.skucost: sample skipped rows: {Samples}", string.Join(" | ", unmappedSamples));
    }

    // --- Price Sheet API (async: POST -> poll Location -> downloadUrl) -----
    private async Task<string> RequestPriceSheetAsync(string token, CancellationToken ct)
    {
        string url = BuildDownloadUrl();
        using var post = new HttpRequestMessage(HttpMethod.Post, url);
        post.Headers.Authorization = new("Bearer", token);
        HttpResponseMessage resp = await _http.SendAsync(post, ct).ConfigureAwait(false);

        // Synchronous completion (rare) returns the body directly.
        if (resp.StatusCode == System.Net.HttpStatusCode.OK)
            return ExtractDownloadUrl(await resp.Content.ReadAsStringAsync(ct));

        if (resp.StatusCode != System.Net.HttpStatusCode.Accepted)
            throw new InvalidOperationException($"Price sheet request failed: {(int)resp.StatusCode} {resp.ReasonPhrase}");

        Uri poll = resp.Headers.Location ?? throw new InvalidOperationException("Price sheet 202 had no Location header.");
        int waitS = (int?)resp.Headers.RetryAfter?.Delta?.TotalSeconds ?? 15;

        for (int attempt = 0; attempt < 20; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(waitS, 5, 60)), ct);
            using var get = new HttpRequestMessage(HttpMethod.Get, poll);
            get.Headers.Authorization = new("Bearer", token);
            using HttpResponseMessage pr = await _http.SendAsync(get, ct).ConfigureAwait(false);
            if (pr.StatusCode == System.Net.HttpStatusCode.Accepted) continue;
            if (pr.StatusCode == System.Net.HttpStatusCode.OK)
                return ExtractDownloadUrl(await pr.Content.ReadAsStringAsync(ct));
            throw new InvalidOperationException($"Price sheet poll failed: {(int)pr.StatusCode} {pr.ReasonPhrase}");
        }
        throw new TimeoutException("Price sheet generation did not complete in time.");
    }

    private string BuildDownloadUrl()
    {
        const string arm = "https://management.azure.com";
        string ba = _opts.BillingAccountName!;
        string v = _opts.PriceSheetApiVersion;
        if (_opts.PricingAgreementType.Equals("EA", StringComparison.OrdinalIgnoreCase))
        {
            // EA: by billing period (empty => current month).
            string period = string.IsNullOrWhiteSpace(_opts.BillingPeriodName)
                ? DateTime.UtcNow.ToString("yyyyMM") : _opts.BillingPeriodName!;
            return $"{arm}/providers/Microsoft.Billing/billingAccounts/{ba}/billingPeriods/{period}" +
                   $"/providers/Microsoft.CostManagement/pricesheets/default/download?api-version={v}";
        }
        // MCA / MPA: by billing profile (current month's price sheet).
        string bp = _opts.BillingProfileName ?? throw new InvalidOperationException(
            "BillingProfileName is required for MCA/MPA pricing (set Prism:BillingProfileName).");
        return $"{arm}/providers/Microsoft.Billing/billingAccounts/{ba}/billingProfiles/{bp}" +
               $"/providers/Microsoft.CostManagement/pricesheets/default/download?api-version={v}";
    }

    private static string ExtractDownloadUrl(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        if (TryFind(doc.RootElement, "downloadUrl", out string? u) && u is not null) return u;
        throw new InvalidOperationException("Price sheet response contained no downloadUrl.");
    }

    private static bool TryFind(System.Text.Json.JsonElement el, string name, out string? value)
    {
        value = null;
        if (el.ValueKind == System.Text.Json.JsonValueKind.Object)
            foreach (var p in el.EnumerateObject())
            {
                if (p.NameEquals(name) && p.Value.ValueKind == System.Text.Json.JsonValueKind.String) { value = p.Value.GetString(); return true; }
                if (TryFind(p.Value, name, out value)) return true;
            }
        return false;
    }

    private async Task<byte[]> DownloadAsync(string url, CancellationToken ct)
    {
        using HttpResponseMessage r = await _http.GetAsync(url, ct).ConfigureAwait(false);   // SAS URL: no auth header
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    // CSV directly, or a zip of CSVs (large EA/MCA sheets).
    private static IEnumerable<TextReader> EnumerateCsvs(byte[] payload)
    {
        bool isZip = payload.Length > 4 && payload[0] == 0x50 && payload[1] == 0x4B && payload[2] == 0x03 && payload[3] == 0x04;
        if (!isZip)
        {
            yield return new StreamReader(new MemoryStream(payload), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            yield break;
        }
        using var zip = new ZipArchive(new MemoryStream(payload), ZipArchiveMode.Read);
        foreach (ZipArchiveEntry e in zip.Entries)
        {
            if (!e.FullName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) continue;
            using var sr = new StreamReader(e.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            // ZipArchiveEntry streams aren't seekable/long-lived: materialize this entry now.
            yield return new StringReader(sr.ReadToEnd());
        }
    }

    private string? MapToSkuPartNumber(string product)
    {
        foreach (KeyValuePair<string, string> kv in _opts.PriceSheetProductMap)
            if (product.Contains(kv.Key, StringComparison.OrdinalIgnoreCase)) return kv.Value;
        return null;
    }

    private static async Task UpsertAsync(SqlConnection c, string part, string? name, decimal cost, string currency, string asOf, CancellationToken ct)
    {
        const string sql = @"
MERGE ref.SkuCost AS t
USING (SELECT @part AS SkuPartNumber) AS s ON t.SkuPartNumber = s.SkuPartNumber
WHEN MATCHED AND t.Origin NOT IN ('manual','org') THEN
    UPDATE SET MonthlyUnitCost=@cost, Currency=@cur, DisplayName=COALESCE(@name,t.DisplayName),
               Origin='price-sheet', AsOfDate=@asof, UpdatedUtc=sysutcdatetime()
WHEN NOT MATCHED THEN
    INSERT (SkuPartNumber, DisplayName, MonthlyUnitCost, Currency, Origin, AsOfDate, UpdatedUtc)
    VALUES (@part, @name, @cost, @cur, 'price-sheet', @asof, sysutcdatetime());";
        await using var cmd = new SqlCommand(sql, c);
        cmd.Parameters.AddWithValue("@part", part);
        cmd.Parameters.AddWithValue("@name", (object?)name ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@cost", cost);
        cmd.Parameters.AddWithValue("@cur", string.IsNullOrWhiteSpace(currency) ? "USD" : currency);
        cmd.Parameters.AddWithValue("@asof", asOf);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static int IndexOf(string[] header, string[] candidates)
    {
        for (int i = 0; i < header.Length; i++)
            foreach (string cand in candidates)
                if (string.Equals(header[i].Trim(), cand, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private static string Get(string[] row, int i) => i >= 0 && i < row.Length ? row[i].Trim() : "";

    public void Dispose() => _http.Dispose();
}
