using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Threading;


namespace MphRead.Mods.Network.Telemetry;

/// <summary>TryWrite is the only hot-path operation. The consumer never owns a
/// gameplay lock, and all serialization, storage, cleanup and HTTP stay here.</summary>
public sealed class NetTelemetryWriter
{
    private readonly NetTelemetryQueue _queue;
    private readonly Thread _thread;
    private readonly NetTelemetryConfig _config;
    private readonly TelemetryHeader _header;
    private readonly Func<string, Stream> _open;
    private long _queued, _written, _dropped, _high, _failures, _uploadFailures;
    private int _stopping;
    public bool Finished => !_thread.IsAlive;
    public TelemetryCounters Counters => new(Interlocked.Read(ref _queued), Interlocked.Read(ref _written),
        Interlocked.Read(ref _dropped), Interlocked.Read(ref _high), Interlocked.Read(ref _failures), Interlocked.Read(ref _uploadFailures));
    public NetTelemetryWriter(NetTelemetryConfig config, TelemetryHeader header, Func<string, Stream>? open = null)
    {
        _config = config; _header = header;
        _open = open ?? (path => new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read));
        _queue = new NetTelemetryQueue(Math.Clamp(config.QueueCapacity, 8192, 32768));
        _thread = new Thread(Run) { IsBackground = true, Name = "network-telemetry" };
        _thread.Start();
    }
    public bool Emit(in NetTelemetryEvent e)
    {
        if (Volatile.Read(ref _stopping) != 0 || !_queue.TryWrite(e)) { Interlocked.Increment(ref _dropped); return false; }
        Interlocked.Increment(ref _queued);
        int count = _queue.Count;
        long previous = Volatile.Read(ref _high);
        if (count > previous) Interlocked.CompareExchange(ref _high, count, previous);
        return true;
    }
    // Nonblocking even when a filesystem or HTTP endpoint is stalled.
    public void Stop() { Interlocked.Exchange(ref _stopping, 1); }
    public bool WaitForExit(int milliseconds) => _thread.Join(milliseconds);
    private void Run()
    {
        StreamWriter? raw = null;
        string? summaryPath = null;
        var clock = Stopwatch.StartNew();
        var aggregate = new NetTelemetryAggregator();
        try
        {
            try
            {
                string root = Path.GetFullPath(_config.Directory);
                Directory.CreateDirectory(root);
                Cleanup(root);
                string directory = Path.Combine(root, DateTime.UtcNow.ToString("yyyy-MM-dd"));
                Directory.CreateDirectory(directory);
                string prefix = Path.Combine(directory, "match-" + _header.MatchSessionId);
                summaryPath = prefix + ".summary.json";
                if (_config.LocalRaw)
                {
                    raw = new StreamWriter(new GZipStream(new CappedTelemetryStream(_open(prefix + ".jsonl.gz"), Math.Clamp(_config.MaximumRawBytes, 1024 * 1024, 1024L * 1024 * 1024)), CompressionLevel.Fastest), new UTF8Encoding(false));
                    raw.WriteLine(JsonSerializer.Serialize(_header, TelemetryJsonContext.Default.TelemetryHeader));
                }
            }
            catch (Exception) { Interlocked.Increment(ref _failures); try { raw?.Dispose(); } catch { } raw = null; }
            while (Volatile.Read(ref _stopping) == 0 || _queue.Count > 0)
            {
                if (!_queue.TryRead(out var e)) { Thread.Sleep(10); continue; }
                aggregate.Add(e);
                if (raw != null)
                {
                    try
                    {
                        // Aggregate stores only periodic/session records in raw;
                        // combat samples still contribute to the bounded summary.
                        if (_config.Detail >= TelemetryDetail.Study || e.Type is TelemetryEventType.Connection or TelemetryEventType.Lifecycle)
                            raw.WriteLine(JsonSerializer.Serialize(e, TelemetryJsonContext.Default.NetTelemetryEvent));
                    }
                    catch (Exception) { Interlocked.Increment(ref _failures); try { raw.Dispose(); } catch { } raw = null; }
                }
                Interlocked.Increment(ref _written);
            }
            try { raw?.Dispose(); } catch (Exception) { Interlocked.Increment(ref _failures); } raw = null;
            if (summaryPath != null)
            {
                try
                {
                    var summary = aggregate.Capture(_header, clock.Elapsed.TotalSeconds, Counters);
                    File.WriteAllText(summaryPath, JsonSerializer.Serialize(summary, TelemetryJsonContext.Default.TelemetrySummary));
                    if (_config.Upload) NetTelemetryUpload.Run(_config, summaryPath, () => Interlocked.Increment(ref _uploadFailures));
                }
                catch (Exception) { Interlocked.Increment(ref _failures); }
            }
        }
        catch (Exception) { Interlocked.Increment(ref _failures); }
        finally { try { raw?.Dispose(); } catch { } Interlocked.Exchange(ref _stopping, 1); }
    }
    private void Cleanup(string root)
    {
        // Only our own raw files, never arbitrary contents of the configured directory.
        var files = new DirectoryInfo(root).GetFiles("match-*.jsonl.gz", SearchOption.AllDirectories);
        Array.Sort(files, (a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
        long bytes = 0; foreach (var file in files) bytes += file.Length;
        var cutoff = DateTime.UtcNow.AddDays(-Math.Clamp(_config.RetentionDays, 7, 30));
        foreach (var file in files)
            if (file.LastWriteTimeUtc < cutoff || bytes > Math.Max(1024 * 1024, _config.MaximumRawBytes))
            { long length = file.Length; file.Delete(); bytes -= length; }
    }
}
