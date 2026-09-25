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
    Task<MapAnalysisResult> AnalyzeAsync(MapBuildSnapshot snapshot, bool navigation = false, CancellationToken cancellation = default);
    Task<string> PackageAsync(MapBuildSnapshot snapshot, string destination, CancellationToken cancellation = default);
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

/// <summary>Bounded single-flight map work. Cancelling one waiter preserves work still needed by another.</summary>
public sealed class MapBuildScheduler : IMapBuildScheduler
{
    private readonly MapWorkQueue _queue;
    private readonly MapCompilationCache _compilations = new();
    private readonly string _cacheRoot;
    private readonly Func<MapDefinition, string, MapValidationResult>? _build;
    public static MapBuildScheduler Shared { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProjectPrime", "map-cache"));
    public long SharedRequests => _queue.Shared;
    public int PendingCount => _queue.Count;
    public int CompiledCacheCount => _compilations.Count;
    public long CompiledCacheBytes => _compilations.Bytes;
    public long CompilationCount => _compilations.Compilations;
    public MapBuildScheduler(string cacheRoot, int concurrency = 2, int maximumPending = 32,
        Func<MapDefinition, string, MapValidationResult>? build = null)
    {
        _queue = new(concurrency, maximumPending);
        _cacheRoot = Path.GetFullPath(cacheRoot);
        _build = build;
    }
    public async Task<MapBuildResult> BuildAsync(MapBuildSnapshot snapshot, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        // Snapshot cloning and dependency hashing run off the UI thread.
        (MapDefinition Definition, MapBuildFingerprint Fingerprint) input;
        try
        {
            input = await Prepare(snapshot, cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Failure("", ex.Message); }
        cancellation.ThrowIfCancellationRequested();
        string key = input.Fingerprint.ContentKey;
        try
        {
            return await _queue.Schedule("runtime:" + key,
                token => Execute(input.Definition, input.Fingerprint, token), cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Failure(key, ex.Message); }
    }

    public async Task<MapAnalysisResult> AnalyzeAsync(MapBuildSnapshot snapshot, bool navigation = false,
        CancellationToken cancellation = default)
    {
        string key = "";
        try
        {
            var input = await Prepare(snapshot, cancellation).ConfigureAwait(false);
            key = input.Fingerprint.ContentKey;
            return await _queue.Schedule((navigation ? "navigation:" : "analysis:") + key, token =>
            {
                MapCompilation compilation = _compilations.Get(key, input.Definition, token);
                var result = new MapAnalysisResult(key, compilation, navigation, token);
                RequireUnchanged(input.Definition, input.Fingerprint);
                return result;
            }, cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            var validation = new MapValidationResult();
            validation.Error("FP-MAP-BUILD", ex.Message);
            return new MapAnalysisResult(key, new(null, validation), navigation: false);
        }
    }

    public async Task<string> PackageAsync(MapBuildSnapshot snapshot, string destination,
        CancellationToken cancellation = default)
    {
        var input = await Prepare(snapshot, cancellation).ConfigureAwait(false);
        string path = Path.GetFullPath(destination), key = input.Fingerprint.ContentKey;
        return await _queue.Schedule("package:" + key + ":" + path, token =>
        {
            var compilation = _compilations.Get(key, input.Definition, token);
            MapCompiler.ThrowIfInvalid(compilation.Validation);
            string staging = path + "." + Guid.NewGuid().ToString("N") + ".staging";
            try
            {
                MapPackageBuilder.WriteValidated(input.Definition, staging, token);
                RequireUnchanged(input.Definition, input.Fingerprint);
                token.ThrowIfCancellationRequested();
                File.Move(staging, path, overwrite: true);
                return path;
            }
            finally { if (File.Exists(staging)) File.Delete(staging); }
        }, cancellation).ConfigureAwait(false);
    }

    private async Task<(MapDefinition Definition, MapBuildFingerprint Fingerprint)> Prepare(
        MapBuildSnapshot snapshot, CancellationToken cancellation)
    {
        var input = await Task.Run(() =>
        {
            var definition = snapshot.CreateDefinition();
            return (Definition: definition, Fingerprint: MapBuildFingerprint.Create(definition));
        }, cancellation).ConfigureAwait(false);
        if (input.Definition.BundlePath == null && input.Definition.Import is { Textures.Length: > 0 } import
            && import.ResolveTextures() == null && import.Resolve() != null)
        {
            // A clean checkout can contain a PK3 and a recipe naming a derived
            // texture pack. Materialize that compiler input before fixing the
            // job's content identity. Real source changes still reject the job.
            await _queue.Schedule("prepare:" + input.Fingerprint.ContentKey, token =>
            {
                lock (MapCompiler.ContentReadLock)
                {
                    var before = MapDependencyAnalyzer.Analyze(input.Definition)
                        .Where(d => d.Kind != "textures").ToArray();
                    if (import.ResolveTextures() == null)
                        Q3Import.BakeTextures(Q3Bsp.Load(import.Resolve()!, import.MapName, token), import, verbose: false, cancellation: token);
                    var after = MapDependencyAnalyzer.Analyze(input.Definition).Where(d => d.Kind != "textures");
                    if (!before.SequenceEqual(after))
                        throw new IOException("Map source dependencies changed while preparing textures. Build again.");
                }
                return true;
            }, cancellation).ConfigureAwait(false);
            input.Fingerprint = await Task.Run(() => MapBuildFingerprint.Create(input.Definition), cancellation)
                .ConfigureAwait(false);
        }
        return input;
    }

    private void RequireUnchanged(MapDefinition definition, MapBuildFingerprint fingerprint)
    {
        if (MapBuildFingerprint.Create(definition) != fingerprint)
        {
            _compilations.Remove(fingerprint.ContentKey, definition);
            throw new IOException("Map dependencies changed while building. Build again using the updated inputs.");
        }
    }

    private MapBuildResult Execute(MapDefinition definition, MapBuildFingerprint fingerprint, CancellationToken cancellation)
    {
        var watch = Stopwatch.StartNew();
        string key = fingerprint.ContentKey, directory = Path.Combine(_cacheRoot, key);
        Directory.CreateDirectory(_cacheRoot);
        // FileShare.None fences cache publication across independent application
        // processes as well as this scheduler's single-flight dictionary.
        using var lease = AcquireLease(Path.Combine(_cacheRoot, key + ".lock"), cancellation);
        var outputs = MapOutputSet.Create(definition, directory, directory, directory);
        string manifest = Path.Combine(directory, "cache.json");
        var cached = ReadCache(manifest, key, outputs);
        if (cached != null) return new(key, outputs, Array.AsReadOnly(cached.Diagnostics), Array.AsReadOnly(cached.Budgets), true, watch.Elapsed.TotalMilliseconds);
        Directory.CreateDirectory(_cacheRoot);
        string staging = Path.Combine(_cacheRoot, ".build-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var validation = _build?.Invoke(definition, staging) ?? BuildRuntime(definition, staging, key, cancellation);
            if (!validation.IsValid) return new(key, null, Array.AsReadOnly(validation.Diagnostics.ToArray()),
                Array.AsReadOnly(validation.Budgets.ToArray()), false, watch.Elapsed.TotalMilliseconds);
            // External files are not the editor graph. Detect changes during a build
            // rather than publishing output under a fingerprint of different bytes.
            cancellation.ThrowIfCancellationRequested();
            RequireUnchanged(definition, fingerprint);
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
    private MapValidationResult BuildRuntime(MapDefinition definition, string directory, string key, CancellationToken cancellation)
    {
        var compilation = _compilations.Get(key, definition, cancellation);
        if (compilation.Validation.IsValid && compilation.Map != null)
            MapPacker.Generate(compilation.Map, directory, directory, directory,
                verbose: false, cancellation: cancellation);
        return compilation.Validation;
    }
    private static FileStream AcquireLease(string path, CancellationToken cancellation = default)
    {
        var timeout = Stopwatch.StartNew();
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (timeout.Elapsed < TimeSpan.FromSeconds(30))
            {
                // Only bounded workers wait here; independent application processes
                // can finish publishing the same immutable cache entry.
                Thread.Sleep(25);
            }
        }
    }

    public static void Install(MapBuildResult result, MapDefinition definition, string archive, string entities, string nodes)
    {
        if (!result.Succeeded || result.Outputs == null) throw new InvalidOperationException("Cannot install a failed map build.");
        // A build requested before an external source edit must not install stale binaries.
        var fingerprint = MapBuildFingerprint.Create(definition);
        if (fingerprint.ContentKey != result.Fingerprint)
            throw new IOException("Map inputs changed after the build. Build again before installing.");
        if (ReadCache(Path.Combine(Path.GetDirectoryName(result.Outputs.Model)!, "cache.json"), result.Fingerprint, result.Outputs) == null)
            throw new IOException("Cached map outputs failed integrity validation. Build again.");
        var destination = MapOutputSet.Create(definition, archive, entities, nodes);
        Directory.CreateDirectory(Path.GetDirectoryName(destination.Manifest)!);
        using var lease = AcquireLease(destination.Manifest + ".lock");
        if (File.Exists(destination.Manifest)) File.Delete(destination.Manifest);
        foreach (var pair in result.Outputs.Files.Zip(destination.Files)) AtomicFile.Write(pair.Second, File.ReadAllBytes(pair.First));
        MapBuildManifest.Write(definition, destination, fingerprint);
    }
}
