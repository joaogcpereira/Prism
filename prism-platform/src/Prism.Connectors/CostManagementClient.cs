// ============================================================
//  CostManagementClient.cs
//  Read-only client for the Azure Cost Management Query API:
//    POST {arm}/{scope}/providers/Microsoft.CostManagement/query?api-version=...
//  Uses an ARM-scoped token (not Graph). The Query API is heavily
//  throttled, so 429 Retry-After is honored; results paginate via
//  properties.nextLink (re-POST the same body to that URL).
//
//  Response is columns + positional rows; the caller maps by column
//  name (Microsoft orders/adds columns per query, so never by index).
// ============================================================
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Prism.Connectors.Cost;

public sealed class CostManagementClient : IDisposable
{
    private const string ArmBase = "https://management.azure.com";

    private readonly HttpClient _http;
    private readonly AzureTokenProvider _tokens;
    private readonly string _apiVersion;
    private readonly int _maxRetries;
    private readonly ILogger<CostManagementClient> _log;

    public CostManagementClient(AzureTokenProvider tokens, string apiVersion, int maxRetries, ILogger<CostManagementClient> log)
    {
        _tokens = tokens;
        _apiVersion = apiVersion;
        _maxRetries = Math.Max(0, maxRetries);
        _log = log;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>
    /// POST a cost query for a scope, following properties.nextLink across pages.
    /// Returns each page's deserialized result; the caller flattens rows by column.
    /// </summary>
    public async IAsyncEnumerable<TResult> QueryAsync<TBody, TResult>(
        string scope,
        TBody body,
        JsonTypeInfo<TBody> bodyType,
        JsonTypeInfo<TResult> resultType,
        Func<TResult, string?> nextLink,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(body, bodyType);
        string? url = $"{ArmBase}/{scope.Trim('/')}/providers/Microsoft.CostManagement/query?api-version={_apiVersion}";

        while (!string.IsNullOrEmpty(url))
        {
            using HttpResponseMessage resp = await PostWithRetryAsync(url, payload, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            await using Stream s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            TResult? page = await JsonSerializer.DeserializeAsync(s, resultType, ct).ConfigureAwait(false);
            if (page is null) yield break;

            yield return page;
            url = nextLink(page);   // re-POST the same body to the next page, if any
        }
    }

    /// <summary>
    /// Lists the subscription ids the managed identity can see (any RBAC role),
    /// filtered to enabled subscriptions. Used for the "subscriptions:*" scope.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListEnabledSubscriptionIdsAsync(
        JsonTypeInfo<Prism.Connectors.Cost.SubscriptionList> listType, CancellationToken ct = default)
    {
        var ids = new List<string>();
        string? url = $"{ArmBase}/subscriptions?api-version=2022-12-01";

        while (!string.IsNullOrEmpty(url))
        {
            using HttpResponseMessage resp = await SendWithRetryAsync(HttpMethod.Get, url, payload: null, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            await using Stream s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var page = await JsonSerializer.DeserializeAsync(s, listType, ct).ConfigureAwait(false);
            if (page is null) break;

            foreach (var sub in page.Value)
                if (!string.IsNullOrEmpty(sub.SubscriptionId) &&
                    string.Equals(sub.State, "Enabled", StringComparison.OrdinalIgnoreCase))
                    ids.Add(sub.SubscriptionId);

            url = page.NextLink;
        }
        return ids;
    }

    private Task<HttpResponseMessage> PostWithRetryAsync(string url, byte[] payload, CancellationToken ct) =>
        SendWithRetryAsync(HttpMethod.Post, url, payload, ct);

    // Single send path with auth, 401-reauth (once), and 429/5xx backoff for both GET and POST.
    private async Task<HttpResponseMessage> SendWithRetryAsync(HttpMethod method, string url, byte[]? payload, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(method, url);
            if (payload is not null)
            {
                req.Content = new ByteArrayContent(payload);
                req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            }
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
                await _tokens.GetTokenAsync(AzureTokenProvider.ArmScope, ct).ConfigureAwait(false));

            HttpResponseMessage resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if (resp.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                resp.Dispose();
                _tokens.Invalidate(AzureTokenProvider.ArmScope);
                continue;
            }

            bool transient = resp.StatusCode == HttpStatusCode.TooManyRequests || (int)resp.StatusCode >= 500;
            if (!transient || attempt >= _maxRetries) return resp;

            // Cost Management commonly returns Retry-After as an HTTP-date (not delta-seconds);
            // honor both forms, then fall back to exponential backoff. Clamp to 120s so a
            // pathological header can't park the run.
            TimeSpan delay;
            if (resp.Headers.RetryAfter?.Delta is { } rd)      delay = rd;
            else if (resp.Headers.RetryAfter?.Date is { } rdate) delay = rdate - DateTimeOffset.UtcNow;
            else delay = TimeSpan.FromSeconds(Math.Pow(2, attempt) * 2);                 // cost API throttles hard
            if (delay < TimeSpan.Zero) delay = TimeSpan.FromSeconds(5);
            if (delay > TimeSpan.FromSeconds(120)) delay = TimeSpan.FromSeconds(120);
            _log.LogWarning("Cost Management {Status}; retrying in {Delay}s (attempt {Attempt}/{Max}).",
                (int)resp.StatusCode, (int)delay.TotalSeconds, attempt + 1, _maxRetries);
            resp.Dispose();
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
    }

    public void Dispose() => _http.Dispose();
}
