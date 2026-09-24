using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MphRead.Mods.MapGen;

/// <summary>Bounded single-flight work. A worker stops when its last waiter leaves.</summary>
internal sealed class MapWorkQueue
{
    private sealed class Job
    {
        internal required Task Completion;
        internal readonly CancellationTokenSource Cancellation = new();
        internal int Waiters;
    }
    private readonly object _sync = new();
    private readonly Dictionary<string, Job> _jobs = new(StringComparer.Ordinal);
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
    internal Task<T> Schedule<T>(string key, Func<T> operation, CancellationToken cancellation)
        => Schedule(key, _ => operation(), cancellation);

    internal async Task<T> Schedule<T>(string key, Func<CancellationToken, T> operation, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        Job job;
        lock (_sync)
        {
            if (_jobs.TryGetValue(key, out var pending) && !pending.Cancellation.IsCancellationRequested)
            {
                job = pending;
                job.Waiters++;
                Interlocked.Increment(ref _shared);
            }
            else
            {
                if (pending == null && _jobs.Count >= _capacity) throw new InvalidOperationException("Map build queue is full.");
                var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
                job = new() { Completion = completion.Task, Waiters = 1 };
                _jobs[key] = job;
                _ = Run(key, job, operation, completion);
            }
        }
        try { return await ((Task<T>)job.Completion).WaitAsync(cancellation).ConfigureAwait(false); }
        finally
        {
            lock (_sync)
            {
                if (--job.Waiters == 0)
                {
                    if (!job.Completion.IsCompleted) job.Cancellation.Cancel();
                    else job.Cancellation.Dispose();
                }
            }
        }
    }

    private async Task Run<T>(string key, Job job, Func<CancellationToken, T> operation, TaskCompletionSource<T> completion)
    {
        bool entered = false;
        T value = default!;
        Exception? error = null;
        try
        {
            await _workers.WaitAsync(job.Cancellation.Token).ConfigureAwait(false);
            entered = true;
            value = await Task.Run(() => operation(job.Cancellation.Token), job.Cancellation.Token).ConfigureAwait(false);
            job.Cancellation.Token.ThrowIfCancellationRequested();
        }
        catch (Exception ex) { error = ex; }
        finally
        {
            if (entered) _workers.Release();
            lock (_sync)
            {
                if (_jobs.TryGetValue(key, out var current) && ReferenceEquals(current, job)) _jobs.Remove(key);
                if (error is OperationCanceledException) completion.TrySetCanceled();
                else if (error != null) completion.TrySetException(error);
                else completion.TrySetResult(value);
                if (job.Waiters == 0) job.Cancellation.Dispose();
            }
        }
    }
}
