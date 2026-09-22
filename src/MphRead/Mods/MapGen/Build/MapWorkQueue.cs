using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MphRead.Mods.MapGen;

/// <summary>One bounded queue for compiler, navigation, packer and package work.</summary>
internal sealed class MapWorkQueue
{
    private readonly object _sync = new();
    private readonly Dictionary<string, Task> _jobs = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _workers;
    private readonly int _capacity;
    private long _shared;
    internal int Count { get { lock (_sync) return _jobs.Count; } }
    internal long Shared => Interlocked.Read(ref _shared);
    internal MapWorkQueue(int workers, int capacity)
    {
        if (workers < 1 || capacity < workers) throw new ArgumentOutOfRangeException(nameof(workers));
        _workers = new(workers, workers); _capacity = capacity;
    }

    internal async Task<T> Schedule<T>(string key, Func<T> operation, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        Task<T> job;
        lock (_sync)
        {
            if (_jobs.TryGetValue(key, out Task? pending))
            {
                job = (Task<T>)pending;
                Interlocked.Increment(ref _shared);
            }
            else
            {
                if (_jobs.Count >= _capacity) throw new InvalidOperationException("Map build queue is full.");
                var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
                job = completion.Task; _jobs.Add(key, job);
                _ = Run(key, operation, completion);
            }
        }
        return await job.WaitAsync(cancellation).ConfigureAwait(false);
    }

    private async Task Run<T>(string key, Func<T> operation, TaskCompletionSource<T> completion)
    {
        await _workers.WaitAsync().ConfigureAwait(false);
        T value = default!;
        Exception? error = null;
        try { value = await Task.Run(operation).ConfigureAwait(false); }
        catch (Exception ex) { error = ex; }
        finally
        {
            lock (_sync) _jobs.Remove(key);
            _workers.Release();
        }
        if (error != null) completion.TrySetException(error);
        else completion.TrySetResult(value);
    }
}
