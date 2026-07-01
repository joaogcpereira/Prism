// ============================================================
//  AgentJsonContext.cs  (Prism.Agent.Contracts)
//  Source-generated JSON for all wire types. Native AOT does not
//  support the reflection serializer, so everything that crosses
//  the pipe is registered here for compile-time serialization.
// ============================================================
using System.Text.Json.Serialization;

namespace Prism.Agent.Contracts;

// camelCase on the wire matches the documented gateway contract (receivedUtc,
// machineName, ...). Both pipe ends use this same context, so the helper<->service
// channel stays self-consistent. Case-insensitive on input so the PascalCase
// config.json written by install.ps1 still binds (output stays camelCase).
[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(UsageBatch))]
[JsonSerializable(typeof(UsageAck))]
[JsonSerializable(typeof(AgentConfig))]
[JsonSerializable(typeof(UsageRollup))]
[JsonSerializable(typeof(IReadOnlyList<UsageRollup>))]
[JsonSerializable(typeof(ReceivedBatch))]
[JsonSerializable(typeof(UploaderConfig))]
public sealed partial class AgentJsonContext : JsonSerializerContext
{
}
