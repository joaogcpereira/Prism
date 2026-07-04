// ============================================================
//  Ingestion.cs
//  - ClientCertEndpointFilter: enforces and validates the device
//    certificate at the application layer (so we can return precise
//    401/403 the agent understands, instead of an opaque TLS abort).
//  - IngestHandler: validates the body and durably lands the batch,
//    returning status codes that drive the agent's retry/quarantine
//    logic (200 ok, 400/413/422 permanent, 429/503 transient).
// ============================================================
using System.IO.Compression;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Prism.Agent.Contracts;

namespace Prism.Gateway;

public sealed class ClientCertEndpointFilter : IEndpointFilter
{
    private const string ItemKey = "prism.device";
    private readonly ClientCertificateValidator _validator;
    private readonly ILogger<ClientCertEndpointFilter> _log;

    public ClientCertEndpointFilter(ClientCertificateValidator validator, ILogger<ClientCertEndpointFilter> log)
    {
        _validator = validator;
        _log = log;
    }

    public static ClientIdentity Device(HttpContext ctx) => (ClientIdentity)ctx.Items[ItemKey]!;

    // Behind a reverse proxy that terminates mTLS (Azure Container Apps ingress / Envoy),
    // the leaf client certificate arrives in the X-Forwarded-Client-Cert (XFCC) header,
    // not on the TLS connection. XFCC is a comma-separated list of proxy hops; each hop is
    // a ';'-separated set of key=value pairs. The client cert is Cert="<url-encoded PEM>".
    internal static X509Certificate2? TryReadForwardedClientCert(HttpContext ctx, ILogger log)
    {
        if (!ctx.Request.Headers.TryGetValue("X-Forwarded-Client-Cert", out var values)) return null;
        string header = values.ToString();
        if (string.IsNullOrWhiteSpace(header)) return null;

        foreach (string hop in header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (string part in hop.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                if (!part.AsSpan(0, eq).Trim().Equals("Cert", StringComparison.OrdinalIgnoreCase)) continue;

                string val = part[(eq + 1)..].Trim().Trim('"');
                if (val.Length == 0) return null;
                try
                {
                    string decoded = Uri.UnescapeDataString(val);
                    if (decoded.Contains("BEGIN CERTIFICATE", StringComparison.Ordinal))
                        return X509Certificate2.CreateFromPem(decoded);
                    return X509CertificateLoader.LoadCertificate(Convert.FromBase64String(decoded)); // DER fallback
                }
                catch (Exception ex)
                {
                    log.LogWarning("XFCC present but its Cert element could not be parsed: {Msg}", ex.Message);
                    return null;
                }
            }
        }
        return null;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext efic, EndpointFilterDelegate next)
    {
        HttpContext ctx = efic.HttpContext;

        X509Certificate2? cert = ctx.Connection.ClientCertificate
            ?? await ctx.Connection.GetClientCertificateAsync(ctx.RequestAborted)
            ?? TryReadForwardedClientCert(ctx, _log);   // behind Container Apps ingress (XFCC)

        if (cert is null)
        {
            _log.LogWarning("Rejected {Path}: no client certificate.", ctx.Request.Path);
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "client certificate required");
        }

        CertValidationResult r = _validator.Validate(cert);
        if (!r.IsValid)
        {
            _log.LogWarning("Rejected {Path}: client cert {Thumb} invalid - {Reason}.",
                ctx.Request.Path, cert.Thumbprint, r.Reason);
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "client certificate not accepted");
        }

        ctx.Items[ItemKey] = r.Identity;
        return await next(efic);
    }
}

public static class IngestHandler
{
    public static async Task<IResult> HandleAsync(
        HttpContext ctx,
        GatewayOptions opts,
        IUsageSink sink,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        ILogger log = loggerFactory.CreateLogger("Ingest");
        ClientIdentity device = ClientCertEndpointFilter.Device(ctx);

        if (!IsJson(ctx.Request.ContentType))
            return Results.Problem(statusCode: StatusCodes.Status415UnsupportedMediaType, title: "expected application/json");

        ReceivedBatch? batch;
        try
        {
            // Parse with the SAME source-gen context the agent serialises with.
            // The agent gzips batches (Content-Encoding: gzip); decompress with a hard
            // cap - MaxRequestBodySize bounds only the COMPRESSED size, so even an
            // authenticated device must not be able to send a decompression bomb.
            if (ctx.Request.Headers.ContentEncoding.ToString().Contains("gzip", StringComparison.OrdinalIgnoreCase))
            {
                const long maxDecompressed = 32L * 1024 * 1024;
                await using var gz = new GZipStream(ctx.Request.Body, CompressionMode.Decompress);
                using var buf = new MemoryStream();
                await CopyBoundedAsync(gz, buf, maxDecompressed, ct);
                buf.Position = 0;
                batch = await JsonSerializer.DeserializeAsync(buf, AgentJsonContext.Default.ReceivedBatch, ct);
            }
            else
            {
                batch = await JsonSerializer.DeserializeAsync(
                    ctx.Request.Body, AgentJsonContext.Default.ReceivedBatch, ct);
            }
        }
        catch (JsonException ex)
        {
            log.LogWarning("400 from {Thumb}: malformed JSON - {Msg}", device.Thumbprint, ex.Message);
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "malformed JSON body");
        }
        catch (InvalidDataException ex) // corrupt gzip, or decompressed size exceeded the cap
        {
            log.LogWarning("400 from {Thumb}: bad gzip body - {Msg}", device.Thumbprint, ex.Message);
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "invalid compressed body");
        }
        catch (BadHttpRequestException) // body exceeded MaxRequestBodySize
        {
            return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "request body too large");
        }

        if (batch is null)
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "empty body");

        if (string.IsNullOrWhiteSpace(batch.MachineName))
            return Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "machineName is required");

        int count = batch.Rollups?.Count ?? 0;
        if (count > opts.MaxRollupsPerBatch)
            return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "too many rollups");

        // Optional: bind device identity in the body to the certificate identity.
        if (opts.RequireDeviceNameMatch && !DeviceNameMatches(batch.MachineName, device))
        {
            log.LogWarning("403 from {Thumb}: body machineName '{Machine}' does not match cert ({Subject}/{Dns}).",
                device.Thumbprint, batch.MachineName, device.Subject, device.Dns);
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "device name does not match certificate");
        }
        else if (!DeviceNameMatches(batch.MachineName, device))
        {
            log.LogInformation("machineName '{Machine}' differs from cert identity ({Subject}/{Dns}); recording cert as authoritative.",
                batch.MachineName, device.Subject, device.Dns);
        }

        // Empty batch: accept as an idempotent no-op (agent never sends these, but
        // don't make it quarantine a harmless one).
        if (count == 0)
            return Results.Json(new IngestResponse("noop", 0), GatewayJsonContext.Default.IngestResponse, statusCode: StatusCodes.Status200OK);

        string receiveId = Guid.NewGuid().ToString("N");
        var landed = new LandedBatch(
            receiveId,
            DateTime.UtcNow.ToString("o"),
            device.Thumbprint,
            device.Subject,
            ctx.Connection.RemoteIpAddress?.ToString(),
            batch);

        try
        {
            await sink.WriteAsync(landed, ct);   // durable (fsync) before we return 200
        }
        catch (OperationCanceledException)
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "request aborted");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "503: failed to persist batch {ReceiveId} from {Thumb}.", receiveId, device.Thumbprint);
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "storage unavailable");
        }

        ctx.Response.Headers["X-Prism-Receive-Id"] = receiveId;
        log.LogInformation("Stored batch {ReceiveId}: {Count} rollup(s) from {Machine} (cert {Thumb}).",
            receiveId, count, batch.MachineName, device.Thumbprint);

        return Results.Json(new IngestResponse(receiveId, count), GatewayJsonContext.Default.IngestResponse, statusCode: StatusCodes.Status200OK);
    }

    private static bool IsJson(string? contentType) =>
        !string.IsNullOrEmpty(contentType) &&
        contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase);

    private static async Task CopyBoundedAsync(Stream src, Stream dst, long maxBytes, CancellationToken ct)
    {
        byte[] buffer = new byte[81920];
        long total = 0;
        int n;
        while ((n = await src.ReadAsync(buffer, ct)) > 0)
        {
            total += n;
            if (total > maxBytes) throw new InvalidDataException($"decompressed body exceeds {maxBytes} bytes");
            await dst.WriteAsync(buffer.AsMemory(0, n), ct);
        }
    }

    private static bool DeviceNameMatches(string machineName, ClientIdentity id)
    {
        bool Match(string? candidate) =>
            !string.IsNullOrEmpty(candidate) &&
            (string.Equals(candidate, machineName, StringComparison.OrdinalIgnoreCase) ||
             candidate.StartsWith(machineName + ".", StringComparison.OrdinalIgnoreCase));
        return Match(id.Subject) || Match(id.Dns);
    }
}
