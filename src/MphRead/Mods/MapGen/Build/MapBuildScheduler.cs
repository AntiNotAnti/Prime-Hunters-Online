using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MphRead.Mods.MapGen;

public interface IMapBuildScheduler
{
    Task<MapBuildResult> BuildAsync(MapBuildSnapshot snapshot, CancellationToken cancellation = default);
}
public sealed record MapBuildResult(string Fingerprint, MapOutputSet? Outputs,
    IReadOnlyList<MapDiagnostic> Diagnostics, IReadOnlyList<MapBudget> Budgets, bool CacheHit, double Milliseconds)
{
    public bool Succeeded => Outputs != null && Diagnostics.All(d => d.Severity != MapDiagnosticSeverity.Error);
    public MapValidationResult Validation()
    {
        var value = new MapValidationResult(); value.Diagnostics.AddRange(Diagnostics); value.Budgets.AddRange(Budgets); return value;
    }
}

/// <summary>Bounded single-flight runtime builds. Cancelling one waiter never cancels shared work.</summary>
public sealed class MapBuildScheduler : IMapBuildScheduler
{
    private readonly object _sync = new();
    private readonly Dictionary<string, Task<MapBuildResult>> _jobs = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _workers;
    private readonly string _cacheRoot;
    private readonly Func<MapDefinition, string, MapValidationResult> _build;
    private readonly int _maximumPending;
    public static MapBuildScheduler Shared { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProjectPrime", "map-cache"));
    private long _sharedRequests;
    public long SharedRequests => Interlocked.Read(ref _sharedRequests);
    public int PendingCount { get { lock (_sync) return _jobs.Count; } }
    public MapBuildScheduler(string cacheRoot, int concurrency = 2, int maximumPending = 32,
        Func<MapDefinition, string, MapValidationResult>? build = null)
    {
        if (concurrency < 1 || maximumPending < concurrency) throw new ArgumentOutOfRangeException(nameof(concurrency));
        _cacheRoot = Path.GetFullPath(cacheRoot); _workers = new(concurrency, concurrency);
        _maximumPending = maximumPending; _build = build ?? BuildRuntime;
    }
    public async Task<MapBuildResult> BuildAsync(MapBuildSnapshot snapshot, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        // Snapshot cloning and dependency hashing run off the UI thread.
        (MapDefinition Definition, MapBuildFingerprint Fingerprint) input;
        try
        {
            input = await Task.Run(() =>
            {
                var definition = snapshot.CreateDefinition();
                return (definition, MapBuildFingerprint.Create(definition));
            }, cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Failure("", ex.Message); }
        cancellation.ThrowIfCancellationRequested();
        string key = input.Fingerprint.ContentKey;
        Task<MapBuildResult> job;
        lock (_sync)
        {
            if (!_jobs.TryGetValue(key, out job!))
            {
                if (_jobs.Count >= _maximumPending) return Failure(key, "Map build queue is full.");
                var completion = new TaskCompletionSource<MapBuildResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                job = completion.Task; _jobs.Add(key, job);
                _ = Run(input.Definition, input.Fingerprint, completion);
            }
            else Interlocked.Increment(ref _sharedRequests);
        }
        return await job.WaitAsync(cancellation).ConfigureAwait(false);
    }
    private async Task Run(MapDefinition definition, MapBuildFingerprint fingerprint, TaskCompletionSource<MapBuildResult> completion)
    {
        string key = fingerprint.ContentKey;
        await _workers.WaitAsync().ConfigureAwait(false);
        MapBuildResult result;
        try { result = await Task.Run(() => Execute(definition, fingerprint)).ConfigureAwait(false); }
        catch (Exception ex) { result = Failure(key, ex.Message); }
        finally { _workers.Release(); }
        lock (_sync) _jobs.Remove(key);
        completion.TrySetResult(result);
    }
    private MapBuildResult Execute(MapDefinition definition, MapBuildFingerprint fingerprint)
    {
        var watch = Stopwatch.StartNew();
        string key = fingerprint.ContentKey, directory = Path.Combine(_cacheRoot, key);
        Directory.CreateDirectory(_cacheRoot);
        // FileShare.None fences cache publication across independent application
        // processes as well as this scheduler's single-flight dictionary.
        using var lease = new FileStream(Path.Combine(_cacheRoot, key + ".lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var outputs = MapOutputSet.Create(definition, directory, directory, directory);
        string manifest = Path.Combine(directory, "cache.json");
        var cached = ReadCache(manifest, key, outputs);
        if (cached != null) return new(key, outputs, Array.AsReadOnly(cached.Diagnostics), Array.AsReadOnly(cached.Budgets), true, watch.Elapsed.TotalMilliseconds);
        Directory.CreateDirectory(_cacheRoot);
        string staging = Path.Combine(_cacheRoot, ".build-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var validation = _build(definition, staging);
            if (!validation.IsValid) return new(key, null, Array.AsReadOnly(validation.Diagnostics.ToArray()),
                Array.AsReadOnly(validation.Budgets.ToArray()), false, watch.Elapsed.TotalMilliseconds);
            // External files are not the editor graph. Detect changes during a build
            // rather than publishing output under a fingerprint of different bytes.
            if (MapBuildFingerprint.Create(definition) != fingerprint)
                return Failure(key, "Map dependencies changed while building. Build again using the updated inputs.");
            var staged = MapOutputSet.Create(definition, staging, staging, staging);
            if (!staged.Complete) return Failure(key, "Compiler did not produce a complete map output set.");
            var cache = new CacheManifest(key, staged.Files.Select(MapBuildFingerprint.HashFile).ToArray(),
                validation.Diagnostics.ToArray(), validation.Budgets.ToArray());
            AtomicFile.Write(Path.Combine(staging, "cache.json"), JsonSerializer.SerializeToUtf8Bytes(cache));
            // A separate application process may have published the same content.
            // Never replace a valid immutable cache entry in that case.
            if (Directory.Exists(directory))
            {
                if (ReadCache(manifest, key, outputs) != null)
                    return new(key, outputs, Array.AsReadOnly(cache.Diagnostics), Array.AsReadOnly(cache.Budgets), true, watch.Elapsed.TotalMilliseconds);
                Directory.Delete(directory, true);
            }
            try { Directory.Move(staging, directory); }
            catch (IOException) when (ReadCache(manifest, key, outputs) != null) { }
            return new(key, outputs, Array.AsReadOnly(cache.Diagnostics), Array.AsReadOnly(cache.Budgets), false, watch.Elapsed.TotalMilliseconds);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }
    private sealed record CacheManifest(string Fingerprint, string[] Hashes, MapDiagnostic[] Diagnostics, MapBudget[] Budgets);
    private static CacheManifest? ReadCache(string path, string key, MapOutputSet outputs)
    {
        try
        {
            if (!outputs.Complete || !File.Exists(path)) return null;
            var manifest = JsonSerializer.Deserialize<CacheManifest>(File.ReadAllText(path));
            if (manifest?.Fingerprint != key || manifest.Hashes?.Length != 5 || manifest.Diagnostics == null || manifest.Budgets == null) return null;
            return manifest.Hashes.SequenceEqual(outputs.Files.Select(MapBuildFingerprint.HashFile)) ? manifest : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
    private static MapBuildResult Failure(string key, string message) => new(key, null,
        Array.AsReadOnly(new[] { new MapDiagnostic("FP-MAP-BUILD", MapDiagnosticSeverity.Error, message) }),
        Array.Empty<MapBudget>(), false, 0);
    private static MapValidationResult BuildRuntime(MapDefinition definition, string directory)
    {
        var compilation = MapCompiler.Compile(definition);
        if (compilation.Map != null) MapPacker.Generate(compilation.Map, directory, directory, directory, verbose: false);
        return compilation.Validation;
    }
    public static void Install(MapBuildResult result, MapDefinition definition, string archive, string entities, string nodes)
    {
        if (!result.Succeeded || result.Outputs == null) throw new InvalidOperationException("Cannot install a failed map build.");
        // A build requested before an external source edit must not install stale binaries.
        if (MapBuildFingerprint.Create(definition).ContentKey != result.Fingerprint)
            throw new IOException("Map inputs changed after the build. Build again before installing.");
        if (ReadCache(Path.Combine(Path.GetDirectoryName(result.Outputs.Model)!, "cache.json"), result.Fingerprint, result.Outputs) == null)
            throw new IOException("Cached map outputs failed integrity validation. Build again.");
        var destination = MapOutputSet.Create(definition, archive, entities, nodes);
        if (File.Exists(destination.Manifest)) File.Delete(destination.Manifest);
        foreach (var pair in result.Outputs.Files.Zip(destination.Files)) AtomicFile.Write(pair.Second, File.ReadAllBytes(pair.First));
        MapBuildManifest.Write(definition, destination);
    }
}
