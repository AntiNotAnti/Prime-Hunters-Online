using System;
using System.IO;
using System.Text.Json;

namespace MphRead.Mods.Network.Telemetry;

public enum TelemetryDetail { Off, Aggregate, Study, Verbose }
public sealed record NetTelemetryConfig
{
    public bool Enabled { get; init; } = true;
    public TelemetryDetail Detail { get; init; } = TelemetryDetail.Aggregate;
    public bool LocalRaw { get; init; } = true;
    public bool Upload { get; init; }
    public string Endpoint { get; init; } = "";
    public string TokenEnvironmentVariable { get; init; } = "PRIME_TELEMETRY_TOKEN";
    public string Directory { get; init; } = "telemetry";
    public int RetentionDays { get; init; } = 14;
    public int QueueCapacity { get; init; } = 16384;
    public int MaximumPendingUploads { get; init; } = 128;
    public long MaximumUploadBytes { get; init; } = 16 * 1024 * 1024;
    public long MaximumRawBytes { get; init; } = 512 * 1024 * 1024;
    public int UploadTimeoutSeconds { get; init; } = 5;
    public static NetTelemetryConfig Load()
    {
        NetTelemetryConfig config = new();
        try
        {
            string? path = Environment.GetEnvironmentVariable("PRIME_TELEMETRY_CONFIG");
            if (!string.IsNullOrEmpty(path)) config = JsonSerializer.Deserialize(File.ReadAllText(path), TelemetryJsonContext.Default.NetTelemetryConfigOverrides)?.Apply(config) ?? config;
        }
        catch (Exception) { config = config with { Enabled = false }; }
        foreach (string arg in Environment.GetCommandLineArgs())
        {
            if (arg == "-netstudy") config = config with { Detail = TelemetryDetail.Study };
            if (arg == "-netstudyverbose") config = config with { Detail = TelemetryDetail.Verbose };
        }
        // Off wins irrespective of argument order.
        if (Array.IndexOf(Environment.GetCommandLineArgs(), "-notelemetry") >= 0) config = config with { Enabled = false, Detail = TelemetryDetail.Off };
        return config;
    }
}

// Source-generated construction of init-only records supplies omitted properties
// as default(T). Nullable overrides distinguish omission from an explicit value.
internal sealed record NetTelemetryConfigOverrides
{
    public bool? Enabled { get; init; }
    public TelemetryDetail? Detail { get; init; }
    public bool? LocalRaw { get; init; }
    public bool? Upload { get; init; }
    public string? Endpoint { get; init; }
    public string? TokenEnvironmentVariable { get; init; }
    public string? Directory { get; init; }
    public int? RetentionDays { get; init; }
    public int? QueueCapacity { get; init; }
    public int? MaximumPendingUploads { get; init; }
    public long? MaximumUploadBytes { get; init; }
    public long? MaximumRawBytes { get; init; }
    public int? UploadTimeoutSeconds { get; init; }
    public NetTelemetryConfig Apply(NetTelemetryConfig defaults) => defaults with
    {
        Enabled = Enabled ?? defaults.Enabled, Detail = Detail ?? defaults.Detail,
        LocalRaw = LocalRaw ?? defaults.LocalRaw, Upload = Upload ?? defaults.Upload,
        Endpoint = Endpoint ?? defaults.Endpoint, TokenEnvironmentVariable = TokenEnvironmentVariable ?? defaults.TokenEnvironmentVariable,
        Directory = Directory ?? defaults.Directory, RetentionDays = RetentionDays ?? defaults.RetentionDays,
        QueueCapacity = QueueCapacity ?? defaults.QueueCapacity, MaximumPendingUploads = MaximumPendingUploads ?? defaults.MaximumPendingUploads,
        MaximumUploadBytes = MaximumUploadBytes ?? defaults.MaximumUploadBytes, MaximumRawBytes = MaximumRawBytes ?? defaults.MaximumRawBytes,
        UploadTimeoutSeconds = UploadTimeoutSeconds ?? defaults.UploadTimeoutSeconds
    };
}
