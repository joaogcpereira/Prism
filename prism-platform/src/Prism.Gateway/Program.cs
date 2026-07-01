// ============================================================
//  Program.cs  (Prism.Gateway)
//  Composition root. Kestrel terminates mutual TLS (requesting the
//  client cert but validating it in-app so we can return precise
//  status codes), applies per-device rate limiting, and exposes a
//  single cert-gated ingestion endpoint plus an open health probe.
// ============================================================
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Prism.Gateway;
using Prism.Warehouse;

var builder = WebApplication.CreateBuilder(args);

var opts = new GatewayOptions();
builder.Configuration.GetSection(GatewayOptions.SectionName).Bind(opts);
builder.Services.AddSingleton(opts);

// Resolve the client-cert trust anchor (Key Vault secret / inline PEM / file)
// ONCE at startup, before the validator is constructed. The Key Vault fetch is
// async and uses the container's managed identity; the resolved PUBLIC CA cert is
// then shared as a singleton consumed by ClientCertificateValidator.
using (var bootstrapLog = LoggerFactory.Create(b => b.AddConsole()))
{
    CaTrustAnchor caAnchor = await CaTrustResolver.ResolveAsync(
        opts, bootstrapLog.CreateLogger("Prism.Gateway.CaTrust"));
    builder.Services.AddSingleton(caAnchor);
}

builder.Services.AddSingleton<ClientCertificateValidator>();

// Landing sink: file (dev) or the Azure SQL warehouse (prod).
if (opts.Sink.Equals("warehouse", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IIngestionSink>(sp =>
        new SqlIngestionSink(
            new WarehouseOptions { ConnectionString = opts.ConnectionString ?? "" },
            sp.GetRequiredService<ILogger<SqlIngestionSink>>()));
    builder.Services.AddSingleton<IUsageSink, WarehouseUsageSink>();
}
else
{
    builder.Services.AddSingleton<FileUsageSink>();
    builder.Services.AddSingleton<IUsageSink>(sp => sp.GetRequiredService<FileUsageSink>());
}

// ---- Kestrel listener ----------------------------------------------------
bool isDevCert = false;
// Behind Container Apps ingress, TLS + client-cert are handled by the platform;
// the container serves plain HTTP and reads the cert from the XFCC header. In
// direct/local mode, Kestrel terminates HTTPS + mutual TLS itself.
if (opts.BehindIngress)
{
    builder.WebHost.ConfigureKestrel(k =>
    {
        k.AddServerHeader = false;
        k.Limits.MaxRequestBodySize = opts.MaxRequestBodyBytes;
        k.ListenAnyIP(opts.Port);   // plain HTTP; ingress already did TLS + mTLS
    });
}
else
{
    X509Certificate2 serverCert = LoadServerCertificate(opts, builder.Environment, out isDevCert);

    builder.WebHost.ConfigureKestrel(k =>
    {
        k.AddServerHeader = false;
        k.Limits.MaxRequestBodySize = opts.MaxRequestBodyBytes;

        k.ListenAnyIP(opts.Port, listen =>
        {
            listen.UseHttps(https =>
            {
                https.ServerCertificate = serverCert;
                https.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
                // Request the client cert but DON'T reject at the TLS layer: we validate
                // in-app (ClientCertEndpointFilter) so the agent gets a real 401/403.
                https.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
                https.CheckCertificateRevocation = false;            // revocation handled by our validator
                https.ClientCertificateValidation = (_, _, _) => true; // defer entirely to app logic
            });
        });
    });
}

// ---- Per-device rate limiting (partition by cert thumbprint, else IP) ----
builder.Services.AddRateLimiter(rl =>
{
    rl.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    rl.AddPolicy("per-device", ctx =>
    {
        string key = RateKey(ctx, opts);
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = opts.RateLimitPermitPerMinute,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        });
    });
});

var app = builder.Build();

if (isDevCert)
    app.Logger.LogWarning("Using a self-signed DEV server certificate (localhost). Configure Gateway:ServerCertificatePath for production.");

app.UseRateLimiter();

// Open liveness probe (no client cert) for load balancers / orchestrators.
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

// Cert-gated ingestion.
var api = app.MapGroup("/api/v1");
api.MapPost("/usage", IngestHandler.HandleAsync)
   .AddEndpointFilter<ClientCertEndpointFilter>()
   .RequireRateLimiting("per-device");

app.Logger.LogInformation("Prism gateway listening on {Scheme}://0.0.0.0:{Port}  (POST /api/v1/usage){Mode}",
    opts.BehindIngress ? "http" : "https", opts.Port,
    opts.BehindIngress ? "  [behind ingress: client cert via XFCC]" : "");
app.Run();


// --------------------------------------------------------------------------
// Rate-limit partition key. Behind Container Apps ingress the TCP peer is the
// proxy and the client cert lives in XFCC, so partitioning by the connection
// would lump the whole fleet into one bucket. Use the forwarded per-device
// identity (XFCC, which is unique+stable per device cert; else X-Forwarded-For)
// when behind ingress, and the real connection otherwise.
static string RateKey(HttpContext ctx, GatewayOptions o)
{
    if (o.BehindIngress)
    {
        string xfcc = ctx.Request.Headers["X-Forwarded-Client-Cert"].ToString();
        if (!string.IsNullOrEmpty(xfcc)) return "dev:" + StableHash(xfcc);
        string xff = ctx.Request.Headers["X-Forwarded-For"].ToString();
        if (!string.IsNullOrEmpty(xff)) return "ip:" + xff.Split(',')[0].Trim();
    }
    return ctx.Connection.ClientCertificate?.Thumbprint
        ?? ctx.Connection.RemoteIpAddress?.ToString()
        ?? "anonymous";
}

// FNV-1a 64-bit: a short, stable, allocation-light key from the (large) XFCC header.
static string StableHash(string s)
{
    ulong h = 1469598103934665603UL;
    foreach (char c in s) { h ^= c; h *= 1099511628211UL; }
    return h.ToString("x");
}

// --------------------------------------------------------------------------
static X509Certificate2 LoadServerCertificate(GatewayOptions opts, IWebHostEnvironment env, out bool isDev)
{
    isDev = false;
    if (!string.IsNullOrWhiteSpace(opts.ServerCertificatePath))
    {
        return X509CertificateLoader.LoadPkcs12FromFile(
            opts.ServerCertificatePath!, opts.ServerCertificatePassword);
    }

    // No server cert configured: generate an ephemeral self-signed cert for local
    // development so the gateway is runnable out of the box. NEVER for production.
    isDev = true;
    using var rsa = RSA.Create(2048);
    var req = new CertificateRequest("CN=prism-gateway-dev", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
    req.CertificateExtensions.Add(new X509KeyUsageExtension(
        X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
    var san = new SubjectAlternativeNameBuilder();
    san.AddDnsName("localhost");
    san.AddIpAddress(System.Net.IPAddress.Loopback);
    req.CertificateExtensions.Add(san.Build());

    using X509Certificate2 ephemeral = req.CreateSelfSigned(
        DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    // Re-import via PFX so the private key is usable by Kestrel across platforms.
    return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), null);
}
