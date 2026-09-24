using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Tasks;
using MphRead.Mods.MapGen;

namespace MphRead.Mods.MapEditor;

public sealed record MapAutosavePayload(MapBuildSnapshot Snapshot, string Path, string? FilePath,
    string? BaseDirectory, string? BundlePath, DocumentStateId State);
public sealed record MapAutosaveResult(DocumentStateId State, string Path, double Milliseconds, string? Error);

/// <summary>One writer and one replaceable pending snapshot; no live document or UI callbacks.</summary>
public sealed class MapAutosaveService : IDisposable
{
    private readonly object _gate = new();
    private MapAutosavePayload? _pending;
    private bool _disposed;
    private Task _worker = Task.CompletedTask;
    private MapAutosaveResult? _result;
    public Task Completion { get { lock (_gate) return _worker; } }
    public MapAutosaveResult? Result { get { lock (_gate) return _result; } }
    public bool Queue(MapAutosavePayload payload)
    {
        lock (_gate)
        {
            if (_disposed) return false;
            _pending = payload;
            if (_worker.IsCompleted) _worker = Task.Run(Drain);
            return true;
        }
    }
    private void Drain()
    {
        while (true)
        {
            MapAutosavePayload payload;
            lock (_gate)
            {
                if (_disposed || _pending == null) { _worker = Task.CompletedTask; return; }
                payload = _pending; _pending = null;
            }
            long start = Stopwatch.GetTimestamp(); string? error = null;
            try
            {
                var definition = payload.Snapshot.CreateDefinition();
                AtomicFile.Write(payload.Path + ".context.json", JsonSerializer.SerializeToUtf8Bytes(new
                { payload.FilePath, payload.BaseDirectory, payload.BundlePath }));
                definition.Save(payload.Path);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { error = ex.Message; }
            lock (_gate) if (!_disposed)
                _result = new(payload.State, payload.Path, Stopwatch.GetElapsedTime(start).TotalMilliseconds, error);
        }
    }
    public void Dispose() { lock (_gate) { _disposed = true; _pending = null; } }
}
