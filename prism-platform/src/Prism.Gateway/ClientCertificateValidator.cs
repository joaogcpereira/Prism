// ============================================================
//  ClientCertificateValidator.cs
//  The trust gate. A device certificate is accepted only if it
//  chains to the configured SCEP CA (custom root trust - NOT the
//  OS trust store), is within its validity window, and (optionally)
//  passes revocation and an issuer-thumbprint pin.
//
//  The authoritative device identity is taken from the certificate
//  (thumbprint + subject + SAN), never from the request body.
// ============================================================
using System.Security.Cryptography.X509Certificates;

namespace Prism.Gateway;

public readonly record struct ClientIdentity(string Thumbprint, string? Subject, string? Dns);

public readonly record struct CertValidationResult(bool IsValid, ClientIdentity Identity, string? Reason)
{
    public static CertValidationResult Ok(ClientIdentity id) => new(true, id, null);
    public static CertValidationResult Fail(string reason) => new(false, default, reason);
}

public sealed class ClientCertificateValidator
{
    private readonly GatewayOptions _opts;
    private readonly ILogger<ClientCertificateValidator> _log;
    private readonly X509Certificate2Collection _caCerts;
    private readonly X509RevocationMode _revocationMode;

    public ClientCertificateValidator(GatewayOptions opts, CaTrustAnchor anchor, ILogger<ClientCertificateValidator> log)
    {
        _opts = opts;
        _log = log;
        _revocationMode = Enum.TryParse<X509RevocationMode>(opts.RevocationMode, ignoreCase: true, out var m)
            ? m : X509RevocationMode.NoCheck;

        // The trust anchor(s) (Key Vault secret / inline PEM / file) are resolved
        // once at startup by CaTrustResolver; the success line is logged there.
        _caCerts = anchor.Certificates;

        if (_caCerts.Count == 0 && opts.AllowAnyClientCertificate)
        {
            _log.LogWarning("AllowAnyClientCertificate=true: client certificates are NOT chain-validated. DEV ONLY.");
        }
        else if (_caCerts.Count == 0)
        {
            _log.LogError("No CA trust anchor resolved (Key Vault / PEM / file) and AllowAnyClientCertificate=false: " +
                          "ALL client certificates will be rejected (fail-closed). " +
                          "Configure Gateway:CaCertificateKeyVaultUri + Gateway:CaCertificateName.");
        }
    }

    public CertValidationResult Validate(X509Certificate2 cert)
    {
        // X509Certificate2.NotBefore/NotAfter are surfaced in LOCAL time; normalize both
        // sides to UTC so the validity check is correct on a server in any timezone (and
        // not just one whose local zone happens to be UTC, like the container host).
        DateTime nowUtc = DateTime.UtcNow;
        if (cert.NotBefore.ToUniversalTime() > nowUtc)  return CertValidationResult.Fail("certificate not yet valid");
        if (cert.NotAfter.ToUniversalTime()  <= nowUtc) return CertValidationResult.Fail("certificate expired");

        ClientIdentity id = ExtractIdentity(cert);

        // Dev escape hatch: explicit opt-in, no CA configured.
        if (_caCerts.Count == 0)
        {
            if (_opts.AllowAnyClientCertificate)
                return CertValidationResult.Ok(id);
            return CertValidationResult.Fail("no trusted CA configured (fail-closed)");
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = _revocationMode;
        chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;   // trust ONLY our CA(s)
        chain.ChainPolicy.CustomTrustStore.AddRange(_caCerts);
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;

        bool built = chain.Build(cert);
        if (!built && !IsTolerable(chain))
            return CertValidationResult.Fail("chain validation failed: " + DescribeChain(chain));

        // Defense-in-depth: optional pin that some cert in the chain has the expected issuer thumbprint.
        if (!string.IsNullOrWhiteSpace(_opts.ExpectedIssuerThumbprint))
        {
            string pin = _opts.ExpectedIssuerThumbprint!.Replace(" ", "").Trim();
            bool pinned = chain.ChainElements.Any(e =>
                string.Equals(e.Certificate.Thumbprint, pin, StringComparison.OrdinalIgnoreCase));
            if (!pinned) return CertValidationResult.Fail("issuer thumbprint pin not satisfied");
        }

        return CertValidationResult.Ok(id);
    }

    /// <summary>True if the only chain problems are tolerable (offline/unknown revocation when allowed).</summary>
    private bool IsTolerable(X509Chain chain)
    {
        foreach (X509ChainStatus s in chain.ChainStatus)
        {
            if (s.Status == X509ChainStatusFlags.NoError) continue;
            bool revocationSoft = s.Status is X509ChainStatusFlags.RevocationStatusUnknown
                                            or X509ChainStatusFlags.OfflineRevocation;
            if (revocationSoft && _opts.AllowOfflineRevocation && _revocationMode != X509RevocationMode.NoCheck)
                continue;
            return false;
        }
        return true;
    }

    private static string DescribeChain(X509Chain chain) =>
        string.Join("; ", chain.ChainStatus.Select(s => $"{s.Status}:{s.StatusInformation?.Trim()}"));

    private static ClientIdentity ExtractIdentity(X509Certificate2 cert)
    {
        string? subject = SafeName(cert, X509NameType.SimpleName);
        string? dns = SafeName(cert, X509NameType.DnsName);
        return new ClientIdentity(cert.Thumbprint, subject, dns);
    }

    private static string? SafeName(X509Certificate2 cert, X509NameType type)
    {
        try { string v = cert.GetNameInfo(type, forIssuer: false); return string.IsNullOrWhiteSpace(v) ? null : v; }
        catch { return null; }
    }
}
