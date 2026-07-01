// ============================================================
//  DefenderClient.cs
//  Client for the Defender for Cloud Apps (MDCA) cloud-discovery API.
//
//  Auth (FIC -> managed identity): the managed identity gets a token
//  for api://AzureADTokenExchange and uses it as a client assertion to
//  obtain a token for the federated app registration (Prism-DfCAConnector),
//  scoped to the Defender for Cloud Apps API. No secret is stored.
//
//  Transport quirks (verified against the current MDCA API):
//   * Tenant-specific base URL (https://<tenant>.<region>.portal.cloudappsecurity.com)
//     - NOT derivable; must be configured.
//   * Authorization header uses the "Token" scheme, NOT "Bearer".
//   * Discovery list endpoints page via skip/limit and return { total, hasNext, data }.
//
//  This API is partly legacy and tenant/region-specific; treat as best-effort
//  and validate against a real tenant.
// ============================================================
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;

namespace Prism.Connectors.Defender;

public sealed class DefenderClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly ClientAssertionCredential _appCred;
    private readonly string[] _apiScope;
    private readonly int _maxRetries;
    private readonly ILogger<DefenderClient> _log;

    public DefenderClient(string baseUrl, string tenantId, string appId, string apiScope,
                          AzureTokenProvider tokens, int maxRetries, ILogger<DefenderClient> log)
    {
        _maxRetries = Math.Max(0, maxRetries);
        _log = log;
        _apiScope = [apiScope];
        _http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(2) };

        // App-registration token, assertion = the MI's token-exchange token.
        _appCred = new ClientAssertionCredential(tenantId, appId,
            async ct => await tokens.GetTokenAsync(AzureTokenProvider.TokenExchangeScope, ct).ConfigureAwait(false));
    }

    /// <summary>GET a relative path, returning the parsed JSON document (caller disposes).</summary>
    public async Task<JsonDocument> GetAsync(string relativeUrl, CancellationToken ct)
    {
        using HttpResponseMessage resp = await SendAsync(HttpMethod.Get, relativeUrl, null, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        byte[] bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return JsonDocument.Parse(bytes);
    }

    /// <summary>POST a JSON body, returning the parsed JSON document (caller disposes).</summary>
    public async Task<JsonDocument> PostAsync(string relativeUrl, string jsonBody, CancellationToken ct)
    {
        using HttpResponseMessage resp = await SendAsync(HttpMethod.Post, relativeUrl, jsonBody, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        byte[] bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return JsonDocument.Parse(bytes);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string? body, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(method, url);
            if (body is not null)
                req.Content = new StringContent(body, Encoding.UTF8, "application/json");

            string token = (await _appCred.GetTokenAsync(new TokenRequestContext(_apiScope), ct).ConfigureAwait(false)).Token;
            // MDCA uses the "Token" scheme, not "Bearer".
            req.Headers.TryAddWithoutValidation("Authorization", "Token " + token);

            HttpResponseMessage resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            bool transient = resp.StatusCode == HttpStatusCode.TooManyRequests || (int)resp.StatusCode >= 500;
            if (!transient || attempt >= _maxRetries) return resp;

            TimeSpan delay = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, attempt)));
            _log.LogWarning("Defender API {Status}; retrying in {Delay}s (attempt {Attempt}/{Max}).",
                (int)resp.StatusCode, (int)delay.TotalSeconds, attempt + 1, _maxRetries);
            resp.Dispose();
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
    }

    public void Dispose() => _http.Dispose();
}
