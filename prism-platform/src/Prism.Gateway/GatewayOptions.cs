// ============================================================
//  GatewayOptions.cs
//  Strongly-typed configuration, bound from the "Gateway" section
//  of appsettings.json / environment variables.
// ============================================================
namespace Prism.Gateway;

public sealed class GatewayOptions
{
    public const string SectionName = "Gateway";

    // ---- Listener ------------------------------------------------------
    public int Port { get; set; } = 8443;

    // When true, a reverse proxy (Azure Container Apps ingress / Envoy) terminates
    // TLS and mutual-TLS: the gateway listens plain HTTP on Port and reads the
    // client certificate from the X-Forwarded-Client-Cert (XFCC) header instead of
    // the TLS connection. Set Gateway__BehindIngress=true in Container Apps.
    // When false (local/direct), Kestrel terminates HTTPS + mTLS itself.
    public bool BehindIngress { get; set; } = false;

    // ---- Server TLS certificate (what the gateway presents) ------------
    // PFX/PKCS#12 with private key. If unset, a self-signed DEV cert is
    // generated at startup (localhost only) with a loud warning.
    public string? ServerCertificatePath { get; set; }
    public string? ServerCertificatePassword { get; set; }

    // ---- Client (device) certificate trust -----------------------------
    // Public cert of the SCEP issuing/root CA. Device certs are validated
    // against THIS CA only (custom root trust), not the OS trust store.
    //
    // Resolution order (the FIRST source that is configured wins):
    //   1. Azure Key Vault SECRET  (CaCertificateKeyVaultUri + CaCertificateName)
    //      -- PRODUCTION. The container's managed identity reads the secret at startup.
    //      The CA is a PUBLIC certificate, so it is stored as a Key Vault SECRET whose
    //      value is the certificate PEM (a public-only cert cannot be a Key Vault
    //      *certificate* object -- those require a private key). The PEM may hold more
    //      than one cert (root + issuing CA); all are loaded as trust anchors.
    //   2. Inline PEM  (CaCertificatePem) -- convenient for local/dev.
    //   3. File path   (CaCertificatePath) -- PEM or DER on disk.

    // --- Key Vault source (production) ---
    // Vault base URI, e.g. "https://contoso-prism-dev.vault.azure.net/".
    public string? CaCertificateKeyVaultUri { get; set; }
    // Name of the Key Vault SECRET holding the CA public cert PEM. (A certificate
    // object's name also works -- it is read via the secret endpoint.)
    public string? CaCertificateName { get; set; }
    // Client id of the user-assigned managed identity used to read the vault. When
    // unset, DefaultAzureCredential falls back to AZURE_CLIENT_ID / az login.
    public string? ManagedIdentityClientId { get; set; }

    // --- Inline / file sources (local-dev fallbacks) ---
    public string? CaCertificatePem { get; set; }
    public string? CaCertificatePath { get; set; }
    // Optional extra pin: the chain must include a cert with this thumbprint.
    public string? ExpectedIssuerThumbprint { get; set; }
    // NoCheck | Online | Offline
    public string RevocationMode { get; set; } = "NoCheck";
    // Tolerate "revocation server offline / unknown" when RevocationMode != NoCheck.
    public bool AllowOfflineRevocation { get; set; } = true;
    // Reject if the body's machineName doesn't match the cert CN/SAN.
    public bool RequireDeviceNameMatch { get; set; } = false;
    // DEV ONLY: accept any client cert without chain validation. Never in prod.
    public bool AllowAnyClientCertificate { get; set; } = false;

    // ---- Landing (where batches are durably written) -------------------
    // "file": NDJSON under LandingDirectory (local/dev).
    // "warehouse": flatten each batch to FactAppUsage and UPSERT into Azure SQL (prod).
    public string Sink { get; set; } = "file";
    public string LandingDirectory { get; set; } = "./data/usage";
    // Required when Sink = "warehouse". Secret-free Entra auth.
    public string? ConnectionString { get; set; }

    // ---- Request limits / abuse protection -----------------------------
    public long MaxRequestBodyBytes { get; set; } = 12L * 1024 * 1024;   // > agent's 8 MB frame cap
    public int MaxRollupsPerBatch { get; set; } = 10_000;
    // Fixed-window rate limit on the INGEST endpoint only (health probes are
    // exempt), partitioned per device (cert thumbprint, else remote IP). A
    // healthy agent posts ~1 request / 5 min, so 30/min is already ~150x
    // headroom; anything hotter is a runaway or hostile client. Over-limit
    // requests are rejected with 429 + Retry-After.
    public int RateLimitPerMinute { get; set; } = 30;
    // Queue depth for over-limit requests before rejection. 0 (default) =
    // reject immediately: a backlogged device should back off and retry, not
    // hold a request slot open on the gateway.
    public int RateLimitBurst { get; set; } = 0;
}
