// ============================================================
//  Json.cs
//  System.Text.Json source-gen contexts. Graph payloads are
//  camelCase; we read case-insensitively for resilience. Output
//  (the NDJSON we land) is camelCase to match the house style.
// ============================================================
using System.Text.Json.Serialization;
using Prism.Connectors.Cost;
using Prism.Connectors.Graph;

namespace Prism.Connectors.Json;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(GraphUsersResponse))]
[JsonSerializable(typeof(GraphSkusResponse))]
[JsonSerializable(typeof(ReportSettings))]
[JsonSerializable(typeof(GraphDevicesResponse))]
[JsonSerializable(typeof(GraphDetectedAppsResponse))]
[JsonSerializable(typeof(GraphManagedDevicesResponse))]
[JsonSerializable(typeof(GraphSignInsResponse))]
[JsonSerializable(typeof(GraphDeletedUsersResponse))]
[JsonSerializable(typeof(AppHealthResponse))]
[JsonSerializable(typeof(MobileAppsResponse))]
[JsonSerializable(typeof(SpSignInResponse))]
[JsonSerializable(typeof(CostQuery))]
[JsonSerializable(typeof(CostQueryResult))]
[JsonSerializable(typeof(SubscriptionList))]
// v2 signal connectors
[JsonSerializable(typeof(GraphUserIdsResponse))]
[JsonSerializable(typeof(GraphMailboxSettings))]
[JsonSerializable(typeof(PstnCallsResponse))]
[JsonSerializable(typeof(AuthRegistrationResponse))]
public sealed partial class GraphJsonContext : JsonSerializerContext { }

