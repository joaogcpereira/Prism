// ============================================================
//  LandedBatch.cs
//  What the gateway durably records for each accepted batch:
//  the device's payload plus server-stamped, trustworthy metadata
//  (receive id/time, the cert-derived identity, source IP).
// ============================================================
using System.Text.Json.Serialization;
using Prism.Agent.Contracts;

namespace Prism.Gateway;

public sealed record LandedBatch(
    string         ReceiveId,
    string         ReceivedAtUtc,
    string         DeviceThumbprint,   // authoritative identity (from the client cert)
    string?        DeviceSubject,
    string?        RemoteIp,
    ReceivedBatch  Batch);             // the device-reported payload

public sealed record IngestResponse(string ReceiveId, int Stored);

[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LandedBatch))]
[JsonSerializable(typeof(IngestResponse))]
public sealed partial class GatewayJsonContext : JsonSerializerContext
{
}
