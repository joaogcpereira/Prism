// ============================================================
//  AzureTokenProvider.cs
//  One DefaultAzureCredential, shared by every client (Graph, ARM).
//  Caches a token per scope and refreshes a few minutes before
//  expiry so long runs don't fail mid-stream. Thread-safe.
//
//  DefaultAzureCredential resolves the user-assigned managed identity
//  in Azure (when its client id is supplied) and `az login` / Visual
//  Studio locally - so the same build runs unattended and on a dev box.
// ============================================================
using System.Collections.Concurrent;
using Azure.Core;
using Azure.Identity;

namespace Prism.Connectors;

public sealed class AzureTokenProvider
{
    public const string GraphScope = "https://graph.microsoft.com/.default";
    public const string ArmScope = "https://management.azure.com/.default";
    // The managed identity requests this audience; the resulting token is used as a
    // client assertion to obtain a token for a federated app registration (FIC -> MI).
    public const string TokenExchangeScope = "api://AzureADTokenExchange/.default";

    private readonly TokenCredential _credential;
    private readonly ConcurrentDictionary<string, Cached> _cache = new(StringComparer.Ordinal);

    public AzureTokenProvider(string? managedIdentityClientId)
    {
        _credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            ManagedIdentityClientId = managedIdentityClientId
        });
    }

    public async Task<string> GetTokenAsync(string scope, CancellationToken ct)
    {
        Cached entry = _cache.GetOrAdd(scope, _ => new Cached());

        if (entry.Token.Token is { Length: > 0 } && entry.Token.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(5))
            return entry.Token.Token;

        await entry.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (entry.Token.Token is { Length: > 0 } && entry.Token.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(5))
                return entry.Token.Token;
            entry.Token = await _credential.GetTokenAsync(new TokenRequestContext([scope]), ct).ConfigureAwait(false);
            return entry.Token.Token;
        }
        finally { entry.Gate.Release(); }
    }

    /// <summary>Force the next call to re-acquire (used after a 401).</summary>
    public void Invalidate(string scope)
    {
        if (_cache.TryGetValue(scope, out Cached? entry)) entry.Token = default;
    }

    private sealed class Cached
    {
        public AccessToken Token;
        public readonly SemaphoreSlim Gate = new(1, 1);
    }
}
