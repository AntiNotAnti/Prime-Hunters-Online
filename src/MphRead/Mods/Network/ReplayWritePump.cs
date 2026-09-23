using System;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace MphRead.Mods.Network;

/// <summary>Nonblocking producer, exclusive storage owner. Queue overflow aborts
/// this recording; completed chunks remain recoverable. No Scene crosses threads.</summary>
internal sealed class ReplayWritePump
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<ReplayWritePump, byte> Active = new();
    private static int _activeCount;
    static ReplayWritePump()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            // Only process shutdown may wait for storage. Match transitions never do.
            var tasks = new System.Collections.Generic.List<Task>();
            foreach (var pump in Active.Keys) { pump.Complete(); tasks.Add(pump.Completion); }
            try { Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(10)); } catch (AggregateException) { }
        };
    }
    internal const int MaximumCommands = 4096;
    internal const long MaximumBytes = 32L * 1024 * 1024;
    private enum Kind { Record, Checkpoint, Frame, Event }
    private readonly record struct Command(Kind Kind, ReplayTimelineRecord Record, uint Frame, ReplayEvent Event)
    {
        internal long Bytes => Kind is Kind.Record or Kind.Checkpoint ? Record.PayloadBytes : 128;
        internal void Retain() { if (Kind is Kind.Record or Kind.Checkpoint) Record.Retain(); }
        internal void Release() { if (Kind is Kind.Record or Kind.Checkpoint) Record.Release(); }
    }
    private readonly Channel<Command> _queue;
    private readonly uint _origin;
    private readonly long _maximumBytes;
    private long _bytes;
    private Exception? _error;
    internal Exception? Error => Volatile.Read(ref _error);
    internal long QueuedBytes => Interlocked.Read(ref _bytes);
    internal Task Completion { get; } = Task.CompletedTask;
    // The delay hook is used only by deterministic storage/backpressure checks.
    internal ReplayWritePump(string path, ReplayMetadata metadata, uint origin,
        int capacity = MaximumCommands, long maximumBytes = MaximumBytes,
        Action? beforeWrite = null, Action? finalized = null)
    {
        _origin = origin; _maximumBytes = maximumBytes;
        _queue = Channel.CreateBounded<Command>(new BoundedChannelOptions(capacity)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
        if (Interlocked.Increment(ref _activeCount) > 4)
        {
            Interlocked.Decrement(ref _activeCount);
            throw new IOException("Previous replay writers are still finishing.");
        }
        Active.TryAdd(this, 0);
        Completion = Task.Run(async () =>
        {
            ReplayWriterV3? writer = null;
            try
            {
                writer = new ReplayWriterV3(path, metadata);
                await foreach (var command in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
                {
                    try
                    {
                        if (Error != null) continue;
                        beforeWrite?.Invoke();
                        if (Error != null) continue;
                        switch (command.Kind)
                        {
                            case Kind.Record: ReplayTimelineArchive.Write(writer, command.Record, _origin); break;
                            case Kind.Checkpoint: writer.WriteCheckpoint(command.Record.RecordingFrame - _origin, command.Record.Payload); break;
                            case Kind.Frame: ReplayTimelineArchive.EndFrame(writer, command.Frame); break;
                            case Kind.Event: writer.WriteEvent(command.Event); break;
                        }
                    }
                    finally { long cost = command.Bytes; command.Release(); Interlocked.Add(ref _bytes, -cost); }
                }
                if (Error == null) { writer.Dispose(); writer = null; finalized?.Invoke(); }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { Abort(ex); }
            finally
            {
                try { writer?.Abort(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Abort(ex); }
                while (_queue.Reader.TryRead(out var remaining))
                { long cost = remaining.Bytes; remaining.Release(); Interlocked.Add(ref _bytes, -cost); }
                Active.TryRemove(this, out _); Interlocked.Decrement(ref _activeCount);
                if (Error != null) Console.WriteLine("[replay] Recording interrupted; recover completed chunks from its .part file. " + Error.Message);
            }
        });
    }
    private bool Enqueue(Command command)
    {
        using var perf = ReplayPerfTelemetry.Measure(ReplayPerfOperation.Enqueue);
        if (Error != null) return false;
        long cost = command.Bytes;
        if (Interlocked.Add(ref _bytes, cost) > _maximumBytes)
        {
            Interlocked.Add(ref _bytes, -cost);
            Abort(new IOException("Replay storage queue exceeded its byte budget.")); return false;
        }
        command.Retain();
        if (_queue.Writer.TryWrite(command)) return true;
        command.Release(); Interlocked.Add(ref _bytes, -cost);
        Abort(new IOException("Replay storage queue is full or closed.")); return false;
    }
    internal bool Record(ReplayTimelineRecord record) => Enqueue(new(Kind.Record, record, 0, default));
    internal bool Checkpoint(ReplayTimelineRecord record) => record.RecordingFrame <= _origin || Enqueue(new(Kind.Checkpoint, record, 0, default));
    internal bool EndFrame(uint frame) => Enqueue(new(Kind.Frame, default, frame, default));
    internal bool Event(ReplayEvent value) => Enqueue(new(Kind.Event, default, 0, value));
    internal void Complete() => _queue.Writer.TryComplete();
    internal void Abort(Exception? error = null)
    {
        Interlocked.CompareExchange(ref _error, error ?? new IOException("Recording aborted."), null);
        _queue.Writer.TryComplete();
    }
}
