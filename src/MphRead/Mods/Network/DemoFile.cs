using System;
using System.IO;
using System.IO.Compression;

namespace MphRead.Mods.Network
{
    /// <summary>
    /// A recorded match: every packet a client received, verbatim, each
    /// tagged with the simulation frame it was acted on. Played back by
    /// handing them to <see cref="NetSession"/> on the matching frame of the
    /// replay, through the exact code path that applied them live -- see
    /// <see cref="DemoPlayback"/>.
    ///
    /// One file, sequential, no index: this is the format a "press record,
    /// press stop" button needs. Seeking would need one; nothing here reads
    /// or writes one yet.
    ///
    /// **Frames, not milliseconds.** Version 1 stamped each record with
    /// <c>Environment.TickCount64</c> and the player released them against a
    /// stopwatch. Three things were wrong with that and all three were
    /// visible:
    ///
    /// - The engine's clock is not the wall clock. <c>Renderer</c> advances
    ///   the simulation by a fixed 1/60 s per frame however long the frame
    ///   actually took, so a replay running at 58 fps consumed 60 frames of
    ///   recording every 60 frames and fell behind real time -- and then
    ///   caught up in bursts, several packets landing on one frame. Only the
    ///   newest survives that: <c>RemoteStates</c> and <c>RemoteIntents</c>
    ///   are one slot each, so every position, aim and button level in
    ///   between was dropped on the floor.
    /// - <c>TickCount64</c> ticks every 15.6 ms on Windows. A 60 Hz stream
    ///   stamped on a 64 Hz clock quantises into clumps that drift against
    ///   the frame boundaries, which produced exactly the same bursts on a
    ///   machine holding a perfect 60 fps.
    /// - The stopwatch starts in <see cref="DemoPlayback.Join"/> and the room
    ///   loads after it. Loading takes seconds, nothing is pumped while it
    ///   runs, and the first frame afterwards therefore released every packet
    ///   recorded during it at once -- so a replay opened several seconds in,
    ///   having discarded all but the last of them.
    ///
    /// A frame number has none of those failure modes. The recorder counts
    /// the same frames the simulation does, the player releases one frame's
    /// worth per simulated frame, and the replay reproduces the packet
    /// distribution of the recording exactly, on any machine, at any frame
    /// rate, with any load time in the middle.
    ///
    /// **Deflate.** The stream is dominated by 60 snapshots a second whose
    /// neighbours differ in a few floats, so it compresses better than two to
    /// one -- which is what pays for the authority recording its own outgoing
    /// snapshots (see <see cref="DemoRecorder.RecordOwnSnapshot"/>) without
    /// the file growing. Flushed a few times a second rather than per record:
    /// a sync flush costs a fraction of a percent at that rate and 14% at one
    /// per record, and a quarter second is what a demo that dies with the
    /// game loses.
    /// </summary>
    internal static class DemoFile
    {
        // "PPDM" -- Project Prime DeMo.
        public static readonly byte[] Magic = { (byte)'P', (byte)'P', (byte)'D', (byte)'M' };
        /// <summary>
        /// 2: frame-stamped records over a deflate stream. Version 1 files
        /// are refused rather than read -- their timestamps mean something
        /// else and their body is not compressed, so there is nothing here
        /// that could read one by accident.
        /// </summary>
        public const byte FormatVersion = 2;
        public const string Extension = ".ppdemo";

        /// <summary>Magic, format version, protocol version. Never compressed: it says how to read the rest.</summary>
        public const int HeaderSize = 4 + 1 + 1;

        /// <summary>
        /// A frame delta of 0xFF means "not a delta": a 32-bit one follows.
        /// One byte covers 254 frames, which is every record in a running
        /// match; the escape is for the gaps, where the recorder spent four
        /// seconds loading a room.
        /// </summary>
        public const byte LongGap = 0xFF;
    }

    /// <summary>Appends recorded packets to a file as they arrive. Not thread-safe -- called from the net-update thread only.</summary>
    internal sealed class DemoWriter : IDisposable
    {
        /// <summary>
        /// Frames between flushes. A demo that dies with the game stays
        /// watchable to within this much of the crash.
        /// </summary>
        private const uint FlushIntervalFrames = 15;

        private readonly FileStream _stream;
        private readonly DeflateStream _deflate;
        private uint _lastFrame;
        private uint _lastFlushFrame;
        private readonly byte[] _header = new byte[7];

        public DemoWriter(string path)
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            _stream.Write(DemoFile.Magic);
            _stream.WriteByte(DemoFile.FormatVersion);
            // NetConfig.ProtocolVersion is a const int, not a byte -- writing
            // it as one would put four bytes here while the reader takes one
            // back, desyncing every record after it. Narrow it explicitly so
            // there is exactly one byte to disagree about.
            _stream.WriteByte((byte)NetConfig.ProtocolVersion);
            _stream.Flush();
            _deflate = new DeflateStream(_stream, CompressionLevel.Fastest, leaveOpen: true);
        }

        /// <param name="frame">Simulation frames since this writer was created.</param>
        public void WriteRecord(uint frame, ReadOnlySpan<byte> data)
        {
            if (data.Length is < 1 or > ushort.MaxValue)
                throw new InvalidDataException("Invalid v2 packet size.");
            if (frame < _lastFrame)
            {
                // Only reachable if the frame counter were ever wound back.
                // Cheaper to pin than to encode a negative delta nothing
                // would know what to do with.
                frame = _lastFrame;
            }
            uint delta = frame - _lastFrame;
            _lastFrame = frame;
            int at = 0;
            if (delta < DemoFile.LongGap)
            {
                _header[at++] = (byte)delta;
            }
            else
            {
                _header[at++] = DemoFile.LongGap;
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                    _header.AsSpan(at), delta);
                at += 4;
            }
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(
                _header.AsSpan(at), (ushort)data.Length);
            at += 2;
            _deflate.Write(_header.AsSpan(0, at));
            _deflate.Write(data);
            if (frame - _lastFlushFrame >= FlushIntervalFrames)
            {
                _lastFlushFrame = frame;
                // The deflate stream first, which turns its pending symbols
                // into bytes the file can hold, then the file, which puts
                // them where a reader could find them after a crash.
                _deflate.Flush();
                _stream.Flush();
            }
        }

        public void Dispose()
        {
            _deflate.Dispose();
            _stream.Dispose();
        }
    }

    /// <summary>One recorded packet, read back from a demo file.</summary>
    internal readonly struct DemoRecord
    {
        /// <summary>Simulation frames after the recording started.</summary>
        public readonly uint Frame;
        public readonly byte[] Data;

        public DemoRecord(uint frame, byte[] data)
        {
            Frame = frame;
            Data = data;
        }
    }

    /// <summary>Reads a demo file's header, then its records in order.</summary>
    internal sealed class DemoReader : IDisposable
    {
        private readonly FileStream _stream;
        private readonly DeflateStream? _deflate;
        private readonly ReplayReaderV3? _v3;
        private readonly byte[] _header = new byte[7];
        private uint _frame;
        private bool _ended;
        private ReplayOpenResult _result;

        public byte ProtocolVersion { get; }
        public byte FormatVersion => _v3?.Metadata.FormatVersion ?? (byte)2;
        public ReplayMetadata? Metadata => _v3?.Metadata;
        public uint DurationFrames => _v3?.DurationFrames ?? _frame;
        public ReplayOpenResult LastResult => _v3?.LastResult ?? _result;

        /// <summary>Null if the file doesn't look like a demo at all (bad magic, wrong version, truncated header).</summary>
        public static DemoReader? Open(string path) => Open(path, out _);

        public static DemoReader? Open(string path, out ReplayOpenResult result, bool metadataOnly = false)
        {
            result = ReplayOpenResult.Success;
            FileStream? stream = null;
            try
            {
                stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                Span<byte> header = stackalloc byte[DemoFile.HeaderSize];
                if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false)
                    < header.Length)
                {
                    stream.Dispose();
                    result = ReplayOpenResult.Truncated;
                    return null;
                }
                if (!header[..DemoFile.Magic.Length].SequenceEqual(DemoFile.Magic))
                {
                    stream.Dispose();
                    result = ReplayOpenResult.InvalidMagic;
                    return null;
                }
                if (header[4] is not (2 or 3 or 4))
                {
                    stream.Dispose();
                    result = ReplayOpenResult.UnsupportedFormat;
                    return null;
                }
                return new DemoReader(stream, header[5], header[4], metadataOnly);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException
                || ex is ArgumentException || ex is InvalidDataException || ex is OverflowException)
            {
                stream?.Dispose();
                result = ReplayFormatV3.Failure(ex);
                return null;
            }
        }

        private DemoReader(FileStream stream, byte protocolVersion, byte version, bool metadataOnly)
        {
            _stream = stream;
            ProtocolVersion = protocolVersion;
            if (version >= 3) _v3 = new ReplayReaderV3(stream, protocolVersion, metadataOnly, version);
            else _deflate = new DeflateStream(stream, CompressionMode.Decompress, leaveOpen: true);
        }

        /// <summary>
        /// Position a freshly opened reader at the first packet after <paramref name="frame"/>.
        /// V3 uses its footer chunk index; legacy v2 remains sequential.
        /// </summary>
        public DemoRecord? SeekAfter(uint frame)
        {
            if (_v3 != null) return _v3.SeekAfter(frame);
            DemoRecord? record;
            do
            {
                record = ReadNext();
            }
            while (record is DemoRecord value && value.Frame <= frame);
            return record;
        }

        /// <summary>The next record, or null at end of file.</summary>
        public DemoRecord? ReadNext()
        {
            if (_v3 != null) return _v3.ReadNext();
            if (_ended) return null;
            try
            {
                int first = _deflate!.ReadByte();
                if (first < 0)
                {
                    _ended = true;
                    return null;
                }
                uint delta = (byte)first;
                if (delta == DemoFile.LongGap)
                {
                    if (!Fill(_header.AsSpan(0, 4)))
                    {
                        return null;
                    }
                    delta = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(_header);
                }
                if (!Fill(_header.AsSpan(0, 2)))
                {
                    return null;
                }
                int length = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(_header);
                if (length == 0 || delta > ReplayFormatV3.MaxFrame - _frame)
                    throw new InvalidDataException("Invalid v2 packet/frame.");
                byte[] data = new byte[length];
                if (!Fill(data))
                {
                    return null;
                }
                _frame += delta;
                return new DemoRecord(_frame, data);
            }
            catch (InvalidDataException)
            {
                // The deflate stream stops mid-block: the process that wrote
                // it did not get to close it. Everything up to the last flush
                // has already been handed over; treat the rest as the end.
                _result = ReplayOpenResult.Corrupt;
                _ended = true;
                return null;
            }
            catch (IOException ex)
            {
                _result = ex is EndOfStreamException ? ReplayOpenResult.Truncated : ReplayOpenResult.IoError;
                _ended = true;
                return null;
            }
        }

        /// <summary>True when the whole span was read; false at a clean or ragged end of file.</summary>
        private bool Fill(Span<byte> destination)
        {
            _deflate!.ReadExactly(destination);
            return true;
        }

        public void Dispose()
        {
            _v3?.Dispose();
            _deflate?.Dispose();
            _stream.Dispose();
        }
    }
}
