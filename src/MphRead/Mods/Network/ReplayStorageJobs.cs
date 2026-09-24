using System;
using System.Threading;
using System.Threading.Tasks;

namespace MphRead.Mods.Network;

/// <summary>Bound disk-heavy replay operations independently from presentation.</summary>
internal static class ReplayStorageJobs
{
    private static readonly SemaphoreSlim Slots = new(2);
    internal static async Task<T> Run<T>(Func<T> operation, CancellationToken cancellation = default)
    {
        await Slots.WaitAsync(cancellation).ConfigureAwait(false);
        try { return await Task.Run(operation, cancellation).ConfigureAwait(false); }
        finally { Slots.Release(); }
    }
}
