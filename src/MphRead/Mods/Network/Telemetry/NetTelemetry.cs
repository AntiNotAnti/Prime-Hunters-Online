using System;
using System.Reflection;
using System.Threading;
namespace MphRead.Mods.Network.Telemetry;

public static class ProductionTelemetry
{
    private static NetTelemetryWriter? _writer;
    private static readonly NetTelemetryWriter?[] _retired = new NetTelemetryWriter?[2];
    private static NetTelemetryConfig _config = new() { Enabled = false };
    public static void Configure(NetTelemetryConfig config) => _config = config;
    public static bool Enabled => _writer != null;
    public static TelemetryCounters Counters => _writer?.Counters ?? default;
    public static void Begin(string map, string mode, int players)
    {
        End();
        if (!_config.Enabled || _config.Detail == TelemetryDetail.Off) return;
        int retiring = 0;
        foreach (var prior in _retired) if (prior is { Finished: false }) retiring++;
        // Permit one previous match to drain/upload while the new match records.
        // Two stalled writers are the hard cap; gameplay never waits for either.
        if (retiring >= 2) return;
        try
        {
            var assembly = typeof(ProductionTelemetry).Assembly;
            string version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
            int separator = version.IndexOf('+');
            var header = new TelemetryHeader(2, NetConfig.ProtocolVersion, Guid.NewGuid().ToString("N"),
                separator < 0 ? "unknown" : version[(separator + 1)..], version,
                System.Runtime.InteropServices.RuntimeInformation.OSDescription, mode, map, players);
            _writer = new NetTelemetryWriter(_config, header);
        }
        catch (Exception) { _writer = null; }
    }
    public static void Emit(in NetTelemetryEvent e)
    {
        var writer = Volatile.Read(ref _writer);
        if (writer != null) writer.Emit(e with { TimestampMilliseconds = System.Diagnostics.Stopwatch.GetTimestamp() * (1000.0 / System.Diagnostics.Stopwatch.Frequency) });
    }
    public static void End()
    {
        var writer = Interlocked.Exchange(ref _writer, null);
        if (writer != null)
        {
            writer.Stop();
            for (int i = 0; i < _retired.Length; i++)
                if (_retired[i] == null || _retired[i]!.Finished) { _retired[i] = writer; break; }
        }
    }
    public static void Shutdown()
    {
        End(); long started = System.Diagnostics.Stopwatch.GetTimestamp();
        foreach (var writer in _retired)
        {
            int remaining = Math.Max(0, 500 - (int)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            if (remaining > 0) writer?.WaitForExit(remaining);
        }
    }
}
