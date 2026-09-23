using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace MphRead.Mods.MapGen;

/// <summary>Private compiler graphs, never handed to editor code. Call inside a bounded worker.</summary>
internal sealed class MapCompilationCache
{
    private const int MaximumEntries = 8;
    private const long MaximumBytes = 128L * 1024 * 1024;
    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private long _clock, _bytes;
    private long _compilations;
    internal long Compilations => Interlocked.Read(ref _compilations);
    private sealed class Entry
    {
        internal required Lazy<MapCompilation> Value;
        internal long Used, Bytes;
        internal bool Counted;
    }
    internal int Count { get { lock (_sync) return _entries.Count; } }
    internal long Bytes { get { lock (_sync) return _bytes; } }
    internal MapCompilation Get(string key, MapDefinition definition)
    {
        key = ContextKey(key, definition);
        Entry entry;
        lock (_sync)
        {
            if (!_entries.TryGetValue(key, out entry!))
                _entries.Add(key, entry = new() { Value = new(() =>
                {
                    Interlocked.Increment(ref _compilations);
                    return MapCompiler.Compile(definition);
                }) });
            entry.Used = ++_clock;
        }
        MapCompilation result;
        try { result = entry.Value.Value; }
        catch
        {
            lock (_sync)
                if (_entries.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
                    _entries.Remove(key);
            throw;
        }
        lock (_sync)
        {
            // An input change can invalidate this entry while another worker is
            // finishing the same Lazy. Do not account for an orphaned result.
            if (!_entries.TryGetValue(key, out var current) || !ReferenceEquals(current, entry))
                return result;
            if (!entry.Counted)
            {
                entry.Counted = true;
                entry.Bytes = 4096 + definition.Serialize().Length * 4L
                    + (result.Map?.Entities.Count ?? 0) * 4096L
                    + (result.Map?.Faces.Concat(result.Map.Solid).Sum(f => 256L + f.Points.Length * 64L) ?? 0);
                _bytes += entry.Bytes;
            }
            while (_entries.Count > MaximumEntries || _bytes > MaximumBytes)
            {
                var oldest = _entries.Where(e => e.Value.Counted).OrderBy(e => e.Value.Used).FirstOrDefault();
                if (oldest.Value == null) break;
                _entries.Remove(oldest.Key); _bytes -= oldest.Value.Bytes;
            }
        }
        return result;
    }
    internal void Remove(string key, MapDefinition definition)
    {
        lock (_sync)
            if (_entries.Remove(ContextKey(key, definition), out Entry? entry) && entry.Counted)
                _bytes -= entry.Bytes;
    }
    // Geometry can retain a texture/import resolver. Do not reuse that resolver
    // from a different source directory, even when its content hash is identical.
    private static string ContextKey(string key, MapDefinition d) => string.Join('\0', key,
        d.BaseDirectory, d.BundlePath, d.SourcePath, d.Import?.BaseDirectory, d.Import?.BundlePath,
        d.Collision?.BaseDirectory, d.Collision?.BundlePath, CustomRooms.MapDirectory);
}
