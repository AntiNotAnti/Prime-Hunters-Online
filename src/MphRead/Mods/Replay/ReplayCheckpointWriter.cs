using System;
using System.Buffers;
using System.IO;
using MphRead.Mods.Network;

namespace MphRead.Mods.Replay;

/// <summary>One bounded pooled buffer, with seek/backpatch support for nested components.</summary>
internal sealed class ReplayCheckpointWriter : Stream
{
    private byte[]? _buffer;
    private int _position, _length;
    private readonly int _maximum;
    internal ReplayCheckpointWriter(int maximum = ReplayWorldCheckpoint.MaximumBytes, int capacity = 1024 * 1024)
    { _maximum = maximum; _buffer = ArrayPool<byte>.Shared.Rent(Math.Min(capacity, maximum)); }
    internal ReplayPayload Detach()
    {
        var payload = ReplayPayload.Take(_buffer!, _length);
        _buffer = null; _position = _length = 0;
        return payload;
    }
    internal ReadOnlySpan<byte> Written => _buffer.AsSpan(0, _length);
    internal void Reset() { _position = _length = 0; }
    public override void Write(ReadOnlySpan<byte> bytes)
    {
        int end = checked(_position + bytes.Length);
        if (end > _maximum) throw new InvalidDataException("Replay checkpoint exceeds its budget.");
        if (_buffer == null) throw new ObjectDisposedException(nameof(ReplayCheckpointWriter));
        if (end > _buffer.Length)
        {
            byte[] next = ArrayPool<byte>.Shared.Rent(Math.Min(_maximum, Math.Max(end, _buffer.Length * 2)));
            _buffer.AsSpan(0, _length).CopyTo(next); ArrayPool<byte>.Shared.Return(_buffer); _buffer = next;
        }
        bytes.CopyTo(_buffer.AsSpan(_position)); _position = end; _length = Math.Max(_length, end);
    }
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void WriteByte(byte value) { Span<byte> one = stackalloc byte[1]; one[0] = value; Write(one); }
    public override bool CanRead => false;
    public override bool CanSeek => true;
    public override bool CanWrite => _buffer != null;
    public override long Length => _length;
    public override long Position { get => _position; set => _position = value >= 0 && value <= _length ? (int)value : throw new ArgumentOutOfRangeException(nameof(value)); }
    public override long Seek(long offset, SeekOrigin origin) { Position = (origin == SeekOrigin.Begin ? 0 : origin == SeekOrigin.Current ? _position : _length) + offset; return Position; }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    { if (_buffer != null) { ArrayPool<byte>.Shared.Return(_buffer); _buffer = null; } base.Dispose(disposing); }
    internal static long BeginComponent(BinaryWriter writer)
    { long at = writer.BaseStream.Position; writer.Write(0); return at; }
    internal static void EndComponent(BinaryWriter writer, long at)
    {
        long end = writer.BaseStream.Position; writer.BaseStream.Position = at;
        writer.Write(checked((int)(end - at - 4))); writer.BaseStream.Position = end;
    }
}
