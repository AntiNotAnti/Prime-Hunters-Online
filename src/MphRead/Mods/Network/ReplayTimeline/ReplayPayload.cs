using System;
using System.Buffers;
using System.Threading;

namespace MphRead.Mods.Network;

/// <summary>Immutable after publication. Each retaining container owns one reference.
/// The finalizer is a safety net for abandoned diagnostic objects, never the live release path.</summary>
internal sealed class ReplayPayload
{
    private byte[]? _buffer;
    private readonly int _length;
    private int _references = 1;
    internal ReadOnlySpan<byte> Span => (_buffer ?? throw new ObjectDisposedException(nameof(ReplayPayload))).AsSpan(0, _length);
    internal int Capacity => _buffer?.Length ?? 0;
    private ReplayPayload(byte[] buffer, int length) { _buffer = buffer; _length = length; }
    internal static ReplayPayload Copy(ReadOnlySpan<byte> bytes)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(bytes.Length);
        bytes.CopyTo(buffer);
        return new(buffer, bytes.Length);
    }
    internal static ReplayPayload Take(byte[] buffer, int length) => new(buffer, length);
    internal void Retain()
    {
        int count;
        do
        {
            count = Volatile.Read(ref _references);
            if (count <= 0) throw new ObjectDisposedException(nameof(ReplayPayload));
        } while (Interlocked.CompareExchange(ref _references, count + 1, count) != count);
    }
    internal void Release()
    {
        if (Interlocked.Decrement(ref _references) != 0) return;
        Return(); GC.SuppressFinalize(this);
    }
    private void Return()
    {
        byte[]? buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer != null) ArrayPool<byte>.Shared.Return(buffer);
    }
    ~ReplayPayload() => Return();
}
