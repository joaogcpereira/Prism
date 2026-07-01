// ============================================================
//  Uploader.cs  (Prism.Agent)
//  Drains the local spool to the telemetry gateway over mutual
//  TLS, authenticating with the Intune-provisioned device
//  certificate from LocalMachine\My. Read-only posture preserved:
//  the agent only ever POSTs (appends) its own device's usage.
//
//  Disposition per spool file:
//    2xx            -> delete (delivered)
//    400/413/422    -> quarantine (permanent reject; don't loop forever)
//    401/403        -> keep + log loudly (likely cert/authorization issue)
//    5xx/429/network-> leave for the next cycle (transient)
//
//  Hardening:
//    * TLS 1.2 / 1.3 only.
//    * Optional server-cert pinning that also checks validity dates.
//    * Bounded response buffer (a hostile/broken gateway can't balloon RAM).
//    * Single async drain loop (PeriodicTimer) - no sync-over-async, no
//      overlapping cycles, cancellable on stop.
// ============================================================
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Prism.Agent.Contracts;

namespace Prism.Agent;

internal sealed class Uploader : IDisposable
{
    private const long MaxResponseBytes = 1 * 1024 * 1024;   // gateway replies are tiny; cap defensively

    private readonly UploaderConfig _cfg;
    private HttpClient? _http;
    private X509Certificate2? _cert;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    private Uploader(UploaderConfig cfg) => _cfg = cfg;

    /// <summary>Returns a configured uploader, or null if there's no gateway configured.</summary>
    public static Uploader? TryCreate()
    {
        UploaderConfig? cfg = LoadConfig();
        if (cfg is null || string.IsNullOrWhiteSpace(cfg.GatewayUrl))
        {
            LocalSink.Log("Uploader disabled: no gateway configured (spooling locally only).");
            return null;
        }
        if (!Uri.TryCreate(cfg.GatewayUrl, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            LocalSink.Log($"Uploader disabled: GatewayUrl must be an absolute https URL (got '{cfg.GatewayUrl}').",
                          System.Diagnostics.EventLogEntryType.Warning, eventId: 309);
            return null;
        }
        return new Uploader(cfg);
    }

    public void Start()
    {
        _cert = SelectDeviceCertificate();
        if (_cert is null)
        {
            LocalSink.Log("Uploader disabled: no matching device certificate in LocalMachine\\My " +
                          "(batches will keep spooling until a cert is provisioned).",
                          System.Diagnostics.EventLogEntryType.Warning, eventId: 300);
            return;
        }

        _http = BuildClient(_cert);
        _cts  = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
        LocalSink.Log($"Uploader started -> {_cfg.GatewayUrl} (device cert {_cert.Thumbprint}).", eventId: 301);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        int periodSec = Math.Max(10, _cfg.UploadIntervalSeconds);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(periodSec));
        try
        {
            // Settle + jitter: a fleet booting together (patch night) must not hit
            // the gateway in lock-step. 5-30s randomised start staggers the herd.
            await Task.Delay(TimeSpan.FromSeconds(5 + Random.Shared.Next(0, 26)), ct).ConfigureAwait(false);
            do
            {
                try { await DrainAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    LocalSink.Log($"upload cycle error: {Describe(ex)}",
                                  System.Diagnostics.EventLogEntryType.Warning, 302);
                }
            }
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { /* stopping */ }
    }

    private async Task DrainAsync(CancellationToken ct)
    {
        string[] files;
        try { files = Directory.GetFiles(LocalSink.SpoolDir, "*.json"); }
        catch { return; }
        Array.Sort(files, StringComparer.Ordinal);          // FIFO by timestamped name

        int budget = Math.Max(1, _cfg.MaxBatchesPerCycle);
        foreach (string f in files)
        {
            if (ct.IsCancellationRequested) break;
            if (budget-- <= 0) break;
            if (!await UploadOneAsync(f, ct).ConfigureAwait(false)) break;  // stop on transient failure; retry next cycle
        }
    }

    /// <returns>true to keep draining, false to stop this cycle (transient failure).</returns>
    private async Task<bool> UploadOneAsync(string path, CancellationToken ct)
    {
        byte[] body;
        try { body = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return false; }
        catch { return true; } // file vanished/locked; skip, keep going

        // Usage JSON compresses ~10x; at fleet scale that is real egress. Gzip
        // anything non-trivial (the gateway decompresses on Content-Encoding).
        bool gzip = _cfg.CompressUploads && body.Length > 1024;
        if (gzip) body = Compress(body);

        using var content = new ByteArrayContent(body);
        content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
        if (gzip) content.Headers.TryAddWithoutValidation("Content-Encoding", "gzip");
        using var req = new HttpRequestMessage(HttpMethod.Post, _cfg.GatewayUrl) { Content = content };
        req.Headers.TryAddWithoutValidation("X-Prism-Device", Environment.MachineName);

        HttpResponseMessage resp;
        try { resp = await _http!.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            LocalSink.Log($"upload deferred ({Path.GetFileName(path)}): {Describe(ex)}",
                          System.Diagnostics.EventLogEntryType.Warning, 303);
            return false; // gateway unreachable: stop, retry whole cycle later
        }

        using (resp)
        {
            if (resp.IsSuccessStatusCode)
            {
                TryDelete(path);
                return true;
            }
            if (IsPermanentReject(resp.StatusCode))
            {
                Quarantine(path);
                LocalSink.Log($"quarantined {Path.GetFileName(path)} (HTTP {(int)resp.StatusCode}).",
                              System.Diagnostics.EventLogEntryType.Warning, 304);
                return true; // poison removed; continue with the rest
            }
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                LocalSink.Log($"upload rejected ({Path.GetFileName(path)}): HTTP {(int)resp.StatusCode} " +
                              "- check the device certificate / gateway authorization.",
                              System.Diagnostics.EventLogEntryType.Warning, 305);
                return false; // likely affects all; stop this cycle
            }
            LocalSink.Log($"upload deferred ({Path.GetFileName(path)}): HTTP {(int)resp.StatusCode}.",
                          System.Diagnostics.EventLogEntryType.Warning, 306);
            return false; // transient (5xx/429/etc.): retry later
        }
    }

    private static bool IsPermanentReject(HttpStatusCode s) =>
        s is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.UnprocessableEntity;

    private static byte[] Compress(byte[] data)
    {
        using var ms = new MemoryStream(data.Length / 4);
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true)) gz.Write(data);
        return ms.ToArray();
    }

    /// <summary>Exception TYPE chain for the event log. The size-trimmed AOT build replaces
    /// exception TEXT with resource keys ("net_http_client_execution_error"), but type names
    /// survive — AuthenticationException vs SocketException vs TaskCanceledException tells an
    /// operator immediately whether it's TLS, network, or a timeout.</summary>
    private static string Describe(Exception ex)
    {
        var sb = new System.Text.StringBuilder();
        for (Exception? e = ex; e is not null && sb.Length < 400; e = e.InnerException)
        {
            if (sb.Length > 0) sb.Append(" <- ");
            sb.Append(e.GetType().Name).Append(": ").Append(e.Message);
        }
        return sb.ToString();
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }

    private static void Quarantine(string path)
    {
        try { File.Move(path, Path.Combine(LocalSink.QuarantineDir, Path.GetFileName(path)), overwrite: true); }
        catch { TryDelete(path); }
        LocalSink.EnforceQuarantineCap();   // keep the quarantine bounded
    }

    private HttpClient BuildClient(X509Certificate2 cert)
    {
        var ssl = new SslClientAuthenticationOptions
        {
            // TLS 1.2 ONLY — deliberate, not an oversight. TLS 1.3 requires the client-cert
            // CertificateVerify signature to be RSA-PSS; device certs whose private key sits
            // in a legacy CSP (typical for AD CS SCEP templates) cannot produce PSS, and
            // SChannel then fails the handshake with "The message received was unexpected or
            // badly formatted" (observed live against Container Apps ingress in require mode).
            // TLS 1.2 performs the same mutual auth with PKCS#1 v1.5, which every provider
            // supports. Revisit when the PKI issues KSP/CNG keys fleet-wide.
            EnabledSslProtocols = SslProtocols.Tls12,
            ClientCertificates  = new X509CertificateCollection { cert },
            // Revocation: check online, but TOLERATE "could not check" — plant/office
            // networks frequently block the CA's CRL/OCSP endpoints, and a strict
            // check would then fail every upload. A certificate that is actually
            // REVOKED, untrusted, expired or name-mismatched still hard-fails.
            CertificateRevocationCheckMode = X509RevocationMode.Online,
            RemoteCertificateValidationCallback = static (_, _, chain, errors) =>
            {
                if (errors == SslPolicyErrors.None) return true;
                // Anything beyond chain errors (name mismatch, no cert) => fail.
                if ((errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != 0) return false;
                if (chain is null) return false;
                foreach (X509ChainStatus s in chain.ChainStatus)
                {
                    if (s.Status is X509ChainStatusFlags.NoError
                                 or X509ChainStatusFlags.RevocationStatusUnknown
                                 or X509ChainStatusFlags.OfflineRevocation) continue;
                    return false;   // real chain problem (incl. Revoked) => fail
                }
                return true;        // only revocation-unreachable => accept
            }
        };

        // Optional server-cert pinning. Default (unset) = standard chain + name validation.
        if (!string.IsNullOrWhiteSpace(_cfg.ServerCertThumbprint))
        {
            string pin = _cfg.ServerCertThumbprint!.Replace(" ", "").Trim();
            ssl.RemoteCertificateValidationCallback = (_, c, _, _) =>
            {
                if (c is not X509Certificate2 x) return false;
                DateTime now = DateTime.Now;
                bool dateOk = x.NotBefore <= now && now < x.NotAfter;
                return dateOk && string.Equals(x.Thumbprint, pin, StringComparison.OrdinalIgnoreCase);
            };
        }

        var handler = new SocketsHttpHandler
        {
            SslOptions               = ssl,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectTimeout           = TimeSpan.FromSeconds(15),
            AutomaticDecompression   = DecompressionMethods.All
        };

        var http = new HttpClient(handler)
        {
            Timeout                     = TimeSpan.FromSeconds(30),
            MaxResponseContentBufferSize = MaxResponseBytes
        };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", AgentInfo.UserAgent);
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
        return http;
    }

    private X509Certificate2? SelectDeviceCertificate()
    {
        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);

            string? wantThumb  = _cfg.CertThumbprint?.Replace(" ", "").Trim();
            string? wantIssuer = _cfg.CertIssuer?.Trim();
            DateTime now = DateTime.Now;

            X509Certificate2? chosen = null;          // exact thumbprint match
            X509Certificate2? bestIssuer = null;      // NEWEST (latest NotAfter) issuer match
            X509Certificate2? bestFallback = null;    // NEWEST ClientAuth-EKU cert

            foreach (X509Certificate2 c in store.Certificates)
            {
                bool keep = false;

                if (c.HasPrivateKey && c.NotBefore <= now && c.NotAfter > now)
                {
                    if (!string.IsNullOrEmpty(wantThumb))
                    {
                        if (chosen is null && string.Equals(c.Thumbprint, wantThumb, StringComparison.OrdinalIgnoreCase))
                        { chosen = c; keep = true; }
                    }
                    else if (!string.IsNullOrEmpty(wantIssuer))
                    {
                        // SCEP renewal leaves BOTH the old and the new cert in the store;
                        // store enumeration order is not deterministic. Always prefer the
                        // one that expires last so every restart picks the same (renewed) cert.
                        if (c.Issuer.Contains(wantIssuer, StringComparison.OrdinalIgnoreCase)
                            && (bestIssuer is null || c.NotAfter > bestIssuer.NotAfter))
                        { bestIssuer?.Dispose(); bestIssuer = c; keep = true; }
                    }
                    else if (HasClientAuthEku(c)
                             && (bestFallback is null || c.NotAfter > bestFallback.NotAfter))
                    {
                        bestFallback?.Dispose(); bestFallback = c; keep = true;
                    }
                }

                // Dispose every cert handle we're not keeping.
                if (!keep && !ReferenceEquals(c, chosen) && !ReferenceEquals(c, bestIssuer) && !ReferenceEquals(c, bestFallback))
                    c.Dispose();
            }

            if (chosen is not null)
            {
                bestIssuer?.Dispose();
                bestFallback?.Dispose();
                return chosen;          // remains valid after the store is closed (modern .NET)
            }
            if (bestIssuer is not null)
            {
                bestFallback?.Dispose();
                return bestIssuer;
            }
            return bestFallback;
        }
        catch (Exception ex)
        {
            LocalSink.Log($"certificate selection failed: {ex.Message}", System.Diagnostics.EventLogEntryType.Warning, 307);
            return null;
        }
    }

    private static bool HasClientAuthEku(X509Certificate2 cert)
    {
        const string ClientAuthOid = "1.3.6.1.5.5.7.3.2";
        foreach (X509Extension ext in cert.Extensions)
            if (ext is X509EnhancedKeyUsageExtension eku)
                foreach (var oid in eku.EnhancedKeyUsages)
                    if (oid.Value == ClientAuthOid) return true;
        return false;
    }

    private static UploaderConfig? LoadConfig()
    {
        try
        {
            string path = Path.Combine(LocalSink.Dir, "config.json");
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize(File.ReadAllText(path), AgentJsonContext.Default.UploaderConfig);
        }
        catch (Exception ex) { LocalSink.Log($"config.json unreadable: {ex.Message}", System.Diagnostics.EventLogEntryType.Warning, 308); return null; }
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _loop?.Wait(TimeSpan.FromSeconds(5)); } catch { /* best-effort drain stop */ }
        _cts?.Dispose();
        _http?.Dispose();
        _cert?.Dispose();
    }
}
