// ============================================================
//  MdeClient.cs
//  Client for the Microsoft Defender for Endpoint / Defender
//  Vulnerability Management API (api.security.microsoft.com).
//
//  Auth (FIC -> managed identity, identical pattern to DefenderClient):
//   the managed identity gets a token for api://AzureADTokenExchange and
//   uses it as a client assertion to obtain a token for the federated app
//   registration (Prism-MdeConnector), scoped to the Defender for Endpoint
//   API. No secret is stored. Bearer scheme (NOT the MDCA "Token" scheme).
//
//  Transport:
//   * OData V4 list endpoints page via @odata.nextLink (absolute URLs).
//   * Throttle-hardened: 429 honors the exact Retry-After (delta-seconds or
//     HTTP-date, clamped) and retries up to a high ceiling; 5xx uses bounded
//     exponential backoff. The MDE API is rate-limited (≈100 calls/min,
//     1500/hour per tenant), so honoring Retry-After is essential.
// ============================================================
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;

namespace Prism.Connectors.Defender;

public sealed class MdeClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly ClientAssertionCredential _appCred;
    private readonly string[] _apiScope;
    private readonly int _maxRetries;
    private readonly int _throttleMaxRetries;
    private readonly int _maxRetryAfterSeconds;
    private readonly ILogger<MdeClient> _log;

    /// <summary>Count of 429 waits honored - lets a caller pace adaptively.</summary>
    public long ThrottleEvents { get; private set; }

    public MdeClient(string baseUrl, string tenantId, string appId, string apiScope,
                     AzureTokenProvider tokens, int maxRetries, int throttleMaxRetries,
                     int maxRetryAfterSeconds, ILogger<MdeClient> log)
    {
        _maxRetries = Math.Max(0, maxRetries);
        _throttleMaxRetries = Math.Max(_maxRetries, throttleMaxRetries);
        _maxRetryAfterSeconds = Math.Max(1, maxRetryAfterSeconds);
        _log = log;
        _apiScope = [apiScope];
        // Advanced-hunting queries may legitimately run up to the API's 200s ceiling.
        _http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(4) };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // App-registration token, assertion = the MI's token-exchange token.
        _appCred = new ClientAssertionCredential(tenantId, appId,
            async ct => await tokens.GetTokenAsync(AzureTokenProvider.TokenExchangeScope, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// GET an OData endpoint and stream every item across all @odata.nextLink pages.
    /// Each page is parsed, its value[] elements handed to <paramref name="onItem"/>,
    /// then disposed - so JsonElements never outlive their document.
    /// </summary>
    public async Task GetPagedAsync(string relativeUrl, Action<JsonElement> onItem, CancellationToken ct)
    {
        string? url = relativeUrl;
        while (!string.IsNullOrEmpty(url))
        {
            using JsonDocument doc = await GetAsync(url, ct).ConfigureAwait(false);
            JsonElement root = doc.RootElement;
            if (root.TryGetProperty("value", out JsonElement val) && val.ValueKind == JsonValueKind.Array)
                foreach (JsonElement item in val.EnumerateArray()) onItem(item);

            url = root.TryGetProperty("@odata.nextLink", out JsonElement nl) && nl.ValueKind == JsonValueKind.String
                ? nl.GetString() : null;     // nextLink is absolute; SendAsync handles absolute URLs
        }
    }

    /// <summary>GET a relative or absolute URL, returning the parsed JSON document (caller disposes).</summary>
    public Task<JsonDocument> GetAsync(string url, CancellationToken ct) => SendAsync(HttpMethod.Get, url, null, ct);

    /// <summary>POST a JSON body (e.g. an advanced-hunting query), returning the parsed JSON document (caller disposes).</summary>
    public Task<JsonDocument> PostAsync(string url, string jsonBody, CancellationToken ct) => SendAsync(HttpMethod.Post, url, jsonBody, ct);

    private async Task<JsonDocument> SendAsync(HttpMethod method, string url, string? body, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(method,
                url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? new Uri(url) : new Uri(_http.BaseAddress!, url));
            if (body is not null)
                req.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            string token = (await _appCred.GetTokenAsync(new TokenRequestContext(_apiScope), ct).ConfigureAwait(false)).Token;
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            HttpResponseMessage resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            // One-shot 401 retry: a token minted at the edge of its window can expire in
            // flight; the credential cache refreshes on the re-fetch above. A SECOND 401
            // is a real authorization problem and surfaces normally.
            if (resp.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                _log.LogWarning("Defender for Endpoint 401; refreshing the app token and retrying once.");
                resp.Dispose();
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
                    byte[] bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                    return JsonDocument.Parse(bytes);
                }
            }

            TimeSpan delay = throttled ? ThrottleDelay(resp) : BackoffDelay(attempt);
            if (throttled) ThrottleEvents++;
            // Advanced hunting explains WHICH quota was hit (calls vs CPU) in the 429 body -
            // surface it so the operator can tell pacing problems from CPU-heavy queries.
            string detail = "";
            if (throttled)
            {
                try { detail = (await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim(); }
                catch { /* body unavailable - header-driven wait still applies */ }
                if (detail.Length > 200) detail = detail[..200];
            }
            _log.LogWarning("Defender for Endpoint {Status}; waiting {Delay}s then retry (attempt {Attempt}/{Max}). {Detail}",
                (int)resp.StatusCode, (int)delay.TotalSeconds, attempt + 1, ceiling, detail);
            resp.Dispose();
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
    }

    // Honor 429 Retry-After exactly: delta-seconds or HTTP-date, clamped to the ceiling.
    private TimeSpan ThrottleDelay(HttpResponseMessage resp)
    {
        TimeSpan d;
        if (resp.Headers.RetryAfter?.Delta is { } delta) d = delta;
        else if (resp.Headers.RetryAfter?.Date is { } at) d = at - DateTimeOffset.UtcNow;
        else d = TimeSpan.FromSeconds(5);
        if (d < TimeSpan.Zero) d = TimeSpan.FromSeconds(5);
        TimeSpan cap = TimeSpan.FromSeconds(_maxRetryAfterSeconds);
        if (d > cap) d = cap;
        return d + TimeSpan.FromMilliseconds(Random.Shared.Next(250, 1500));   // jitter: decorrelate parallel loops
    }

    private static TimeSpan BackoffDelay(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, attempt)))
        + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1000));              // full jitter on the step

    public void Dispose() => _http.Dispose();
}
