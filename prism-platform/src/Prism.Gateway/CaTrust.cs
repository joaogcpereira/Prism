// ============================================================
//  CaTrust.cs  (Prism.Gateway)
//  Resolves the client-cert TRUST ANCHOR(S) - the SCEP issuing/root CA public
//  certificate(s) that every device certificate must chain to.
//
//  Production reads the CA from AZURE KEY VAULT using the container's user-assigned
//  managed identity. The CA is a PUBLIC certificate (no private key), so it is
//  stored as a Key Vault SECRET whose value is the certificate PEM - a public-only
//  certificate cannot be a Key Vault *certificate* object (those require a private
//  key). The PEM may contain MORE THAN ONE certificate (e.g. the root plus the
//  issuing/intermediate CA); all of them are loaded as trust anchors, so a device
//  leaf validates whether it was issued directly by the root or by an intermediate.
//
//  The reader is tolerant of how the value was stored: a PEM block (normal), a
//  base64 DER certificate, or a base64 PKCS#12 (a Key Vault certificate object's
//  exported secret) - in every case only the PUBLIC certificate(s) are kept.
//
//  Local/dev can instead supply an inline PEM or a file path. The fetch is async,
//  so it runs once at startup (Program.cs) and the resolved anchor is registered as
//  a singleton consumed by ClientCertificateValidator.
// ============================================================
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;

namespace Prism.Gateway;

/// <summary>The resolved client-cert trust anchors (CA public certs) and where they came from.</summary>
public sealed class CaTrustAnchor
{
    public X509Certificate2Collection Certificates { get; init; } = new();
    public string Source { get; init; } = "none";   // keyvault | pem | file | none
}

public static class CaTrustResolver
{
    /// <summary>
    /// Resolve the CA trust anchor(s) once at startup. Key Vault first, then inline
    /// PEM, then file path. Returns an empty collection when none is configured (the
    /// validator then fails closed unless AllowAnyClientCertificate).
    /// </summary>
    public static async Task<CaTrustAnchor> ResolveAsync(
        GatewayOptions opts, ILogger log, CancellationToken ct = default)
    {
        X509Certificate2Collection certs;
        string source;

        // 1) Azure Key Vault (production): the CA public cert(s) stored as a SECRET.
        if (!string.IsNullOrWhiteSpace(opts.CaCertificateKeyVaultUri) &&
            !string.IsNullOrWhiteSpace(opts.CaCertificateName))
        {
            var cred = new DefaultAzureCredential(new DefaultAzureCredentialOptions
            {
                // Pin the user-assigned identity when supplied (a container can carry
                // several); otherwise DefaultAzureCredential honors AZURE_CLIENT_ID.
                ManagedIdentityClientId = string.IsNullOrWhiteSpace(opts.ManagedIdentityClientId)
                    ? null
                    : opts.ManagedIdentityClientId
            });

            var client = new SecretClient(new Uri(opts.CaCertificateKeyVaultUri!), cred);
            KeyVaultSecret secret = (await client.GetSecretAsync(opts.CaCertificateName, cancellationToken: ct)).Value;
            certs = ParseCaCertificates(secret.Value);
            source = $"keyvault secret '{opts.CaCertificateName}'";
        }
        // 2) Inline PEM (local/dev).
        else if (!string.IsNullOrWhiteSpace(opts.CaCertificatePem))
        {
            certs = new X509Certificate2Collection();
            certs.ImportFromPem(opts.CaCertificatePem);
            source = "inline PEM";
        }
        // 3) File path (PEM or DER).
        else if (!string.IsNullOrWhiteSpace(opts.CaCertificatePath))
        {
            certs = LoadFromFile(opts.CaCertificatePath!);
            source = $"file '{opts.CaCertificatePath}'";
        }
        else
        {
            return new CaTrustAnchor { Source = "none" };
        }

        if (certs.Count == 0)
            return new CaTrustAnchor { Source = "none" };

        log.LogInformation("Client-cert trust anchored to {Count} CA certificate(s) [{Source}]: {Subjects}",
            certs.Count, source, Describe(certs));

        string kind = source.StartsWith("keyvault") ? "keyvault" : source.StartsWith("inline") ? "pem" : "file";
        return new CaTrustAnchor { Certificates = certs, Source = kind };
    }

    /// <summary>
    /// Parse CA certificate(s) from a Key Vault secret value: a PEM block (one or
    /// more certs - the normal case), a base64 DER certificate, or a base64 PKCS#12
    /// (a KV certificate object's exported secret). Only PUBLIC certs are kept.
    /// </summary>
    private static X509Certificate2Collection ParseCaCertificates(string value)
    {
        var col = new X509Certificate2Collection();
        string v = value.Trim();

        if (v.Contains("BEGIN CERTIFICATE", StringComparison.Ordinal))
        {
            col.ImportFromPem(v);   // loads ALL certificate blocks (root + intermediates)
            return col;
        }

        byte[] raw = Convert.FromBase64String(v);
        try
        {
            col.Add(X509CertificateLoader.LoadCertificate(raw));    // DER certificate
        }
        catch (CryptographicException)
        {
            // Not a bare cert - treat as PKCS#12 (a KV certificate export). Keep only
            // the public cert; any private key is irrelevant to chain validation.
            using X509Certificate2 bundle = X509CertificateLoader.LoadPkcs12(raw, null);
            col.Add(X509CertificateLoader.LoadCertificate(bundle.Export(X509ContentType.Cert)));
        }
        return col;
    }

    private static X509Certificate2Collection LoadFromFile(string path)
    {
        var col = new X509Certificate2Collection();
        string text;
        try { text = File.ReadAllText(path); } catch { text = string.Empty; }
        if (text.Contains("BEGIN CERTIFICATE", StringComparison.Ordinal))
            col.ImportFromPem(text);                                          // PEM (one or more)
        else
            col.Add(X509CertificateLoader.LoadCertificateFromFile(path));     // DER/CER
        return col;
    }

    private static string Describe(X509Certificate2Collection col)
    {
        var parts = new List<string>(col.Count);
        for (int i = 0; i < col.Count; i++)
            parts.Add($"'{col[i].Subject}' ({col[i].Thumbprint})");
        return string.Join("; ", parts);
    }
}
