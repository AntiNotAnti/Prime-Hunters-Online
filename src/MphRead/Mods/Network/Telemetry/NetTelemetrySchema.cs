using System.Text.Json.Serialization;
namespace MphRead.Mods.Network.Telemetry;

[JsonSourceGenerationOptions(UseStringEnumConverter = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(NetTelemetryConfig))]
[JsonSerializable(typeof(TelemetryHeader))]
[JsonSerializable(typeof(NetTelemetryEvent))]
[JsonSerializable(typeof(TelemetrySummary))]
internal partial class TelemetryJsonContext : JsonSerializerContext { }
