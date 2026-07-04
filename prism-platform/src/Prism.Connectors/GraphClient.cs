// ============================================================
//  GraphClient.cs
//  Thin read-only Microsoft Graph client:
//   * Authenticates via the shared AzureTokenProvider (managed
//     identity in Azure, `az login` locally) - no secret here.
//   * Pages through list endpoints following @odata.nextLink.
//   * Honors 429 Retry-After and backs off on transient 5xx.
//   * Downloads the usage-report 302 -> CSV (auth-free) to a temp file.
// ============================================================
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Prism.Connectors.Graph;

public sealed class GraphClient : IDisposable
{
    // Shared, auth-free client for following the usage-report 302 -> download URL (the URL
    // is self-authorizing). Static so repeated report downloads reuse one pooled handler
    // instead of constructing/leaking a new HttpClient per download.
    private static readonly HttpClient s_downloadClient =
        new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) })
        { Timeout = TimeSpan.FromMinutes(5) };

    private readonly HttpClient _http;
    private readonly AzureTokenProvider _tokens;
    private readonly int _maxRetries;
    private readonly int _throttleMaxRetries;
    private readonly int _maxRetryAfterSeconds;
    private readonly ILogger<GraphClient> _log;

    public GraphClient(string baseUrl, AzureTokenProvider tokens, int maxRetries,
        int throttleMaxRetries, int maxRetryAfterSeconds, ILogger<GraphClient> log)
    {
        _maxRetries = Math.Max(0, maxRetries);
        _throttleMaxRetries = Math.Max(_maxRetries, throttleMaxRetries);
        _maxRetryAfterSeconds = Math.Max(1, maxRetryAfterSeconds);
        _log = log;
        _tokens = tokens;
        // No auto-redirect: the usage-report endpoints answer 302 with a preauthenticated,
        // auth-FREE download URL; we must follow it WITHOUT the bearer token (handled in
        // DownloadReportToFileAsync). The JSON endpoints return 200 directly, so this is safe.
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
        _http = new HttpClient(handler) { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(2) };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>Stream every item across all pages of a Graph list endpoint.
    /// <paramref name="headers"/> lets callers add request headers (e.g.
    /// "ConsistencyLevel: eventual" for $count/$filter advanced queries).</summary>
    public async IAsyncEnumerable<TItem> GetPagedAsync<TPage, TItem>(
        string relativeUrl,
        JsonTypeInfo<TPage> typeInfo,
        Func<TPage, (IEnumerable<TItem>? Items, string? NextLink)> select,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        string? url = relativeUrl;
        while (!string.IsNullOrEmpty(url))
        {
            using HttpResponseMessage resp = await SendWithRetryAsync(url, ct, headers).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            await using Stream s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            TPage? page = await JsonSerializer.DeserializeAsync(s, typeInfo, ct).ConfigureAwait(false);
            if (page is null) yield break;

            (IEnumerable<TItem>? items, string? next) = select(page);
            if (items is not null)
                foreach (TItem item in items) yield return item;

            // nextLink is an absolute URL; switch to absolute addressing for subsequent calls.
            url = next;
        }
    }

    /// <summary>GET a single JSON object (e.g. /admin/reportSettings).</summary>
    public async Task<T?> GetJsonAsync<T>(string relativeUrl, JsonTypeInfo<T> typeInfo, CancellationToken ct)
    {
        using HttpResponseMessage resp = await SendWithRetryAsync(relativeUrl, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using Stream s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync(s, typeInfo, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Invoke a usage-report endpoint and stream its CSV to a temp file. The endpoint
    /// answers 302 with a preauthenticated, short-lived download URL; we download that
    /// WITHOUT the Authorization header (a fresh client), since the URL is self-authorizing
    /// and Graph rejects an unexpected bearer token on the storage host. Returns the temp
    /// path; the caller deletes it.
    /// </summary>
    public async Task<string> DownloadReportToFileAsync(string relativeUrl, CancellationToken ct)
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"prism-report-{Guid.NewGuid():N}.csv");
        using HttpResponseMessage resp = await SendWithRetryAsync(relativeUrl, ct).ConfigureAwait(false);

        if ((int)resp.StatusCode is >= 300 and < 400)
        {
            Uri loc = resp.Headers.Location
                      ?? throw new InvalidOperationException("usage-report redirect had no Location header");
            // Reuse the shared, header-free client (no Authorization on the storage host).
            using HttpResponseMessage dl = await s_downloadClient.GetAsync(loc, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            dl.EnsureSuccessStatusCode();
            await CopyToFileAsync(dl, tmp, ct).ConfigureAwait(false);
        }
        else
        {
            resp.EnsureSuccessStatusCode();   // some configs may return 200 with the CSV inline
            await CopyToFileAsync(resp, tmp, ct).ConfigureAwait(false);
        }
        return tmp;

        static async Task CopyToFileAsync(HttpResponseMessage r, string path, CancellationToken c)
        {
            await using Stream src = await r.Content.ReadAsStreamAsync(c).ConfigureAwait(false);
            await using FileStream fs = File.Create(path);
            await src.CopyToAsync(fs, c).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// POST a Graph JSON $batch (1-20 GET sub-requests) and return the parsed response
    /// document ({"responses":[{id,status,headers,body},...]}). The OUTER call retries
    /// on 429/5xx honoring Retry-After; INNER per-request 429s are the caller's to
    /// handle (each inner response carries its own status / Retry-After header).
    /// Hand-built JSON + JsonDocument parsing - no new serializer types needed.
    /// </summary>
    public async Task<JsonDocument> PostBatchAsync(IReadOnlyList<(string Id, string Url)> requests, CancellationToken ct)
    {
        if (requests.Count is 0 or > 20)
            throw new ArgumentOutOfRangeException(nameof(requests), "Graph $batch accepts 1-20 sub-requests.");

        var sb = new StringBuilder(64 + requests.Count * 192);
        sb.Append("{\"requests\":[");
        for (int i = 0; i < requests.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"id\":\"").Append(requests[i].Id.Replace("\"", ""))
              .Append("\",\"method\":\"GET\",\"url\":\"").Append(requests[i].Url.Replace("\"", "").Replace("\\", ""))
              .Append("\"}");
        }
        sb.Append("]}");
        byte[] payload = Encoding.UTF8.GetBytes(sb.ToString());

        for (int attempt = 0; ; attempt++)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, new Uri(_http.BaseAddress!, "$batch"))
            {
                Content = new ByteArrayContent(payload)
            };
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
                await _tokens.GetTokenAsync(AzureTokenProvider.GraphScope, ct).ConfigureAwait(false));

            HttpResponseMessage resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if (resp.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                resp.Dispose();
                _tokens.Invalidate(AzureTokenProvider.GraphScope);
                continue;
            }

            bool throttled = resp.StatusCode == HttpStatusCode.TooManyRequests;
            bool server = (int)resp.StatusCode >= 500;
            int ceiling = throttled ? _throttleMaxRetries : _maxRetries;
            if ((!throttled && !server) || attempt >= ceiling)
            {
                using (resp)
                {
                    resp.EnsureSuccessStatusCode();
                    string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    return JsonDocument.Parse(json);
                }
            }

            TimeSpan delay = throttled ? ThrottleDelay(resp) : BackoffDelay(attempt);
            _log.LogWarning("Graph {Status} on $batch; waiting {Delay}s then retry (attempt {Attempt}/{Max}).",
                (int)resp.StatusCode, (int)delay.TotalSeconds, attempt + 1, ceiling);
            resp.Dispose();
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(string url, CancellationToken ct,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        for (int attempt = 0; ; attempt++)
        {
            var req = new HttpRequestMessage(HttpMethod.Get,
                url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? new Uri(url) : new Uri(_http.BaseAddress!, url));
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
                await _tokens.GetTokenAsync(AzureTokenProvider.GraphScope, ct).ConfigureAwait(false));
            if (headers is not null)
                foreach ((string k, string v) in headers) req.Headers.TryAddWithoutValidation(k, v);

            HttpResponseMessage resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if (resp.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                resp.Dispose();
                _tokens.Invalidate(AzureTokenProvider.GraphScope);   // token may have just expired
                continue;
            }

            bool throttled = resp.StatusCode == HttpStatusCode.TooManyRequests;
            bool server = (int)resp.StatusCode >= 500;
            int ceiling = throttled ? _throttleMaxRetries : _maxRetries;
            if ((!throttled && !server) || attempt >= ceiling) return resp;

            // 503/504 frequently carry Retry-After too - honoring it beats blind backoff.
            TimeSpan delay = throttled || resp.Headers.RetryAfter is not null
                ? ThrottleDelay(resp) : BackoffDelay(attempt);
            _log.LogWarning("Graph {Status} on {Url}; waiting {Delay}s then retry (attempt {Attempt}/{Max}).",
                (int)resp.StatusCode, url, (int)delay.TotalSeconds, attempt + 1, ceiling);
            resp.Dispose();
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
    }

    // Honor Retry-After exactly (429, and 5xx when the service sends one): delta-seconds
    // or an HTTP-date, clamped to the configured ceiling so a pathological header can't
    // park the run for hours. Jittered so a fleet of parallel loops doesn't re-arrive in
    // lockstep the moment the window opens (thundering herd).
    private TimeSpan ThrottleDelay(HttpResponseMessage resp)
    {
        TimeSpan d;
        if (resp.Headers.RetryAfter?.Delta is { } delta) d = delta;
        else if (resp.Headers.RetryAfter?.Date is { } at) d = at - DateTimeOffset.UtcNow;
        else d = TimeSpan.FromSeconds(5);
        if (d < TimeSpan.Zero) d = TimeSpan.FromSeconds(5);
        TimeSpan cap = TimeSpan.FromSeconds(_maxRetryAfterSeconds);
        if (d > cap) d = cap;
        return d + TimeSpan.FromMilliseconds(Random.Shared.Next(250, 1500));
    }

    // Exponential backoff for 5xx (capped at 60s) with full jitter on the step - parallel
    // connectors must decorrelate, not retry in synchronized waves.
    private static TimeSpan BackoffDelay(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, attempt)))
        + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1000));

    public void Dispose() => _http.Dispose();
}
