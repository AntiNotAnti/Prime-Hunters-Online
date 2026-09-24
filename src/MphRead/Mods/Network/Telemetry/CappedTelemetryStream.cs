using System;
using System.IO;
namespace MphRead.Mods.Network.Telemetry;

internal sealed class CappedTelemetryStream : Stream
{
    private readonly Stream _inner;
    private readonly long _limit;
    private long _written;
    public CappedTelemetryStream(Stream inner, long limit) { _inner = inner; _limit = limit; }
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (_written + buffer.Length > _limit) throw new IOException("Telemetry raw storage cap reached");
        _inner.Write(buffer); _written += buffer.Length;
    }
    public override void Flush() => _inner.Flush();
    protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _written;
    public override long Position { get => _written; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
