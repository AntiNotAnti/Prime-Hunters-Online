using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace MphRead.Mods.Network
{
    internal readonly record struct ReplayChunkIndex(uint FirstFrame, uint LastFrame,
        long Offset, int CompressedLength);

    internal static class ReplayFormatV3
    {
        internal const uint ChunkMagic = 0x334B4843; // CHK3
        internal const uint FooterMagic = 0x33584449; // IDX3
        internal const uint TrailerMagic = 0x33444E45; // END3
        internal const uint HashMagic = 0x33485348; // HSH3, optional footer extension
        internal const int MaxHeader = 128 * 1024;
        internal const int MaxChunk = 2 * 1024 * 1024;
        internal const int MaxFooter = 16 * 1024 * 1024;
        internal const int MaxChunks = 200000;
        internal const int MaxEvents = 200000;
        internal const int MaxHashes = 200000;
        internal const uint MaxFrame = 60 * 60 * 24 * 7;
        internal const int ChunkHeaderSize = 28;
        private static readonly UTF8Encoding Utf8 = new(false, true);
        private static readonly uint[] CrcTable = MakeCrcTable();

        private static uint[] MakeCrcTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < table.Length; i++)
            {
                uint value = i;
                for (int bit = 0; bit < 8; bit++) value = (value & 1) != 0
                    ? (value >> 1) ^ 0xEDB88320U : value >> 1;
                table[i] = value;
            }
            return table;
        }

        internal static uint Crc(ReadOnlySpan<byte> data)
        {
            uint value = uint.MaxValue;
            foreach (byte b in data) value = CrcTable[(value ^ b) & 255] ^ (value >> 8);
            return ~value;
        }

        internal static void WriteString(BinaryWriter writer, string value)
        {
            byte[] bytes = Utf8.GetBytes(value);
            if (bytes.Length > 1024) throw new InvalidDataException("Replay string exceeds 1024 bytes.");
            writer.Write((ushort)bytes.Length);
            writer.Write(bytes);
        }

        internal static string ReadString(BinaryReader reader)
        {
            int size = reader.ReadUInt16();
            if (size > 1024) throw new InvalidDataException("Invalid replay string size.");
            return Utf8.GetString(ReadBytes(reader, size));
        }

        internal static byte[] ReadBytes(BinaryReader reader, int size)
        {
            if (size < 0 || size > MaxFooter || size > reader.BaseStream.Length - reader.BaseStream.Position)
                throw new EndOfStreamException("Truncated replay section.");
            byte[] bytes = reader.ReadBytes(size);
            if (bytes.Length != size) throw new EndOfStreamException();
            return bytes;
        }

        internal static byte[] EncodeMetadata(ReplayMetadata metadata)
        {
            using var buffer = new MemoryStream();
            using var writer = new BinaryWriter(buffer, Encoding.UTF8, true);
            writer.Write(metadata.TickRate);
            writer.Write(metadata.RecordedAtUtc.ToUniversalTime().Ticks);
            writer.Write((byte)metadata.Type);
            writer.Write(metadata.Recovered);
            writer.Write(metadata.MapHash);
            writer.Write((byte)metadata.Mode);
            WriteString(writer, metadata.BuildVersion);
            WriteString(writer, metadata.BuildId);
            WriteString(writer, metadata.RoomKey);
            if (metadata.Players.Count > RosterPacket.MaxSlots) throw new InvalidDataException("Too many players.");
            writer.Write((byte)metadata.Players.Count);
            foreach (var player in metadata.Players)
            {
                writer.Write(player.Slot); writer.Write(player.Hunter); writer.Write(player.Team);
                WriteString(writer, player.Name);
            }
            if (metadata.Bootstrap.Packets.Count > 32) throw new InvalidDataException("Bootstrap too large.");
            writer.Write((byte)metadata.Bootstrap.Packets.Count);
            foreach (byte[] packet in metadata.Bootstrap.Packets)
            {
                if (packet.Length is < 1 or > NetConfig.MaxPacketSize) throw new InvalidDataException("Invalid bootstrap packet.");
                writer.Write((ushort)packet.Length); writer.Write(packet);
            }
            if (buffer.Length > MaxHeader) throw new InvalidDataException("Metadata too large.");
            return buffer.ToArray();
        }

        internal static ReplayMetadata DecodeMetadata(byte protocol, byte[] bytes)
        {
            using var reader = new BinaryReader(new MemoryStream(bytes), Utf8);
            ushort tickRate = reader.ReadUInt16();
            long ticks = reader.ReadInt64();
            byte type = reader.ReadByte();
            byte recovered = reader.ReadByte();
            if (tickRate != 60 || ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks
                || type > 1 || recovered > 1) throw new InvalidDataException("Invalid replay metadata.");
            ulong mapHash = reader.ReadUInt64();
            byte mode = reader.ReadByte();
            if (!Enum.IsDefined(typeof(GameMode), mode)) throw new InvalidDataException("Invalid game mode.");
            string build = ReadString(reader), buildId = ReadString(reader), room = ReadString(reader);
            int count = reader.ReadByte();
            if (count > RosterPacket.MaxSlots) throw new InvalidDataException("Invalid player count.");
            var players = new List<ReplayPlayerInfo>(count);
            int seen = 0;
            for (int i = 0; i < count; i++)
            {
                byte slot = reader.ReadByte(), hunter = reader.ReadByte();
                sbyte team = reader.ReadSByte();
                if (slot >= RosterPacket.MaxSlots || hunter >= 7 || team is < -1 or > 3 || (seen & (1 << slot)) != 0)
                    throw new InvalidDataException("Invalid replay roster.");
                seen |= 1 << slot;
                players.Add(new(slot, hunter, team, ReadString(reader)));
            }
            int packetCount = reader.ReadByte();
            if (packetCount > 32) throw new InvalidDataException("Invalid bootstrap count.");
            var packets = new List<byte[]>(packetCount);
            for (int i = 0; i < packetCount; i++)
            {
                int length = reader.ReadUInt16();
                if (length is < 1 or > NetConfig.MaxPacketSize) throw new InvalidDataException("Invalid bootstrap packet length.");
                byte[] packet = ReadBytes(reader, length);
                if (!ValidBootstrap(packet, protocol)) throw new InvalidDataException("Invalid bootstrap packet.");
                packets.Add(packet);
            }
            if (reader.BaseStream.Position != bytes.Length) throw new InvalidDataException("Unexpected metadata tail.");
            return new ReplayMetadata
            {
                ProtocolVersion = protocol, TickRate = tickRate, RecordedAtUtc = new DateTime(ticks, DateTimeKind.Utc),
                Type = (ReplayType)type, Recovered = recovered != 0, MapHash = mapHash, Mode = (GameMode)mode,
                BuildVersion = build, BuildId = buildId, RoomKey = room, Players = players,
                Bootstrap = new ReplayBootstrap { Packets = packets }
            };
        }

        private static bool ValidBootstrap(byte[] packet, byte protocol)
        {
            // Unknown protocols are inspectable as metadata, never handed to current wire parsers.
            if (protocol != NetConfig.ProtocolVersion) return true;
            ReadOnlySpan<byte> payload = packet.AsSpan(1);
            return (PacketType)packet[0] switch
            {
                PacketType.SessionState => payload.Length == SessionStatePacket.Size
                    && SessionStatePacket.TryRead(payload, out _),
                PacketType.MatchState => payload.Length == MatchStatePacket.Size,
                PacketType.Roster => payload.Length == RosterPacket.Size,
                PacketType.Snapshot => ValidSnapshotBootstrap(payload),
                PacketType.SlotIntent => payload.Length == 1 + IntentPacket.FullSize && payload[0] < RosterPacket.MaxSlots,
                _ => false
            };
        }

        private static bool ValidSnapshotBootstrap(ReadOnlySpan<byte> payload)
        {
            if (payload.Length < SnapshotHeader.Size) return false;
            SnapshotHeader header = SnapshotHeader.Read(payload);
            if (header.PlayerCount > RosterPacket.MaxSlots
                || !SnapshotWire.TryReadStateHeader(payload,
                    out bool keyframe, out byte activeMask, out uint baselineFrame)
                || !keyframe || baselineFrame != header.Frame)
            {
                return false;
            }
            int activeCount = 0;
            for (int slot = 0; slot < RosterPacket.MaxSlots; slot++)
                if ((activeMask & (1 << slot)) != 0) activeCount++;
            if (activeCount != header.PlayerCount) return false;
            if (!SnapshotWire.TryLocateTails(payload, header, out _, out int timeOffset)) return false;
            int healthOffset = timeOffset + NetMatchTimeSync.Size;
            if (healthOffset > payload.Length) return false;
            ReadOnlySpan<byte> health = payload[healthOffset..];
            return NetMatchTimeSync.Validate(payload.Slice(timeOffset, NetMatchTimeSync.Size))
                && NetHealthSync.Validate(health)
                && System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(health) == header.MatchId;
        }

        internal static ReplayOpenResult Failure(Exception ex) => ex switch
        {
            FileNotFoundException or DirectoryNotFoundException => ReplayOpenResult.FileMissing,
            EndOfStreamException => ReplayOpenResult.Truncated,
            InvalidDataException or DecoderFallbackException or OverflowException => ReplayOpenResult.Corrupt,
            _ => ReplayOpenResult.IoError
        };
    }

    /// <summary>Each completed chunk is durable; only a successfully closed file gets its final name.</summary>
    internal sealed class ReplayWriterV3 : IDisposable
    {
        private readonly string _path;
        private readonly FileStream _stream;
        private readonly BinaryWriter _writer;
        private readonly MemoryStream _chunk = new();
        private readonly BinaryWriter _records;
        private readonly List<ReplayChunkIndex> _index = new();
        private readonly List<ReplayEvent> _events = new();
        private readonly List<ReplayExpectedHash> _hashes = new();
        private ushort _hashSchema;
        private string _hashBuildId = "";
        private uint _first, _last, _count;
        private bool _disposed, _faulted;
        public string PartialPath => _path + ".part";

        public ReplayWriterV3(string path, ReplayMetadata metadata)
        {
            _path = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            // Never truncate an existing recording, including an orphan from a previous crash.
            if (File.Exists(_path)) throw new IOException("A replay already exists at that path.");
            byte[] header = ReplayFormatV3.EncodeMetadata(metadata);
            // The writer can also receive bootstrap packets extracted from an external v2
            // replay. Validate those before creating a file we would subsequently refuse.
            _ = ReplayFormatV3.DecodeMetadata(metadata.ProtocolVersion, header);
            _stream = new FileStream(PartialPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            _writer = new BinaryWriter(_stream, Encoding.UTF8, true);
            _records = new BinaryWriter(_chunk, Encoding.UTF8, true);
            try
            {
                _writer.Write(DemoFile.Magic); _writer.Write((byte)3); _writer.Write(metadata.ProtocolVersion);
                _writer.Write(header.Length); _writer.Write(ReplayFormatV3.Crc(header)); _writer.Write(header);
                _stream.Flush(true);
            }
            catch { Abort(); throw; }
        }

        public void WriteRecord(uint frame, ReadOnlySpan<byte> data)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (data.Length is < 1 or > NetConfig.MaxPacketSize || frame < _last || frame > ReplayFormatV3.MaxFrame)
                throw new InvalidDataException("Invalid replay packet/frame.");
            try
            {
                if (_count > 0 && (frame - _first >= 120 || _chunk.Length + data.Length + 6 > ReplayFormatV3.MaxChunk)) FlushChunk();
                if (_count == 0) _first = frame;
                _last = frame;
                _records.Write(frame); _records.Write((ushort)data.Length); _records.Write(data);
                _count++;
            }
            catch { _faulted = true; throw; }
        }

        public void WriteEvent(ReplayEvent value)
        {
            if (_events.Count < ReplayFormatV3.MaxEvents) _events.Add(value);
        }

        public void WriteExpectedHash(ReplayExpectedHash value, ushort schema, string buildId)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (schema == 0 || buildId.Length == 0 || Encoding.UTF8.GetByteCount(buildId) > 1024
                || value.Frame > ReplayFormatV3.MaxFrame || value.Value.Length != 64
                || _hashes.Count >= ReplayFormatV3.MaxHashes
                || (_hashes.Count > 0 && (value.Frame <= _hashes[^1].Frame || schema != _hashSchema || buildId != _hashBuildId)))
                throw new InvalidDataException("Invalid replay state hash.");
            try { _ = Convert.FromHexString(value.Value); }
            catch (FormatException ex) { throw new InvalidDataException("Invalid replay state hash.", ex); }
            _hashes.Add(value);
            _hashSchema = schema;
            _hashBuildId = buildId;
        }

        private void FlushChunk()
        {
            if (_count == 0) return;
            if (_index.Count >= ReplayFormatV3.MaxChunks) throw new IOException("Replay chunk limit reached.");
            using var compressed = new MemoryStream();
            using (var deflate = new DeflateStream(compressed, CompressionLevel.Fastest, true))
                deflate.Write(_chunk.GetBuffer().AsSpan(0, (int)_chunk.Length));
            if (compressed.Length > ReplayFormatV3.MaxChunk + 65536) throw new InvalidDataException("Compressed chunk too large.");
            _index.Add(new(_first, _last, _stream.Position, (int)compressed.Length));
            _writer.Write(ReplayFormatV3.ChunkMagic); _writer.Write(_first); _writer.Write(_last); _writer.Write(_count);
            _writer.Write((int)compressed.Length); _writer.Write((int)_chunk.Length);
            _writer.Write(ReplayFormatV3.Crc(_chunk.GetBuffer().AsSpan(0, (int)_chunk.Length)));
            _writer.Write(compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
            _stream.Flush(true);
            _count = 0; _chunk.SetLength(0);
        }

        public void Abort()
        {
            _faulted = true;
            if (_disposed) return;
            _disposed = true;
            _records.Dispose(); _chunk.Dispose(); _writer.Dispose(); _stream.Dispose();
        }

        public void Dispose()
        {
            if (_disposed) return;
            try
            {
                if (_faulted) return;
                FlushChunk();
                using var footer = new MemoryStream();
                using (var output = new BinaryWriter(footer, Encoding.UTF8, true))
                {
                    output.Write(_last); output.Write(_index.Count);
                    foreach (var entry in _index)
                    {
                        output.Write(entry.FirstFrame); output.Write(entry.LastFrame);
                        output.Write(entry.Offset); output.Write(entry.CompressedLength);
                    }
                    _events.Sort((a, b) => a.Frame.CompareTo(b.Frame));
                    _events.RemoveAll(e => e.Frame > _last);
                    output.Write(_events.Count);
                    foreach (var e in _events)
                    {
                        output.Write(e.Frame); output.Write((byte)e.Type); output.Write(e.ActorSlot);
                        output.Write(e.TargetSlot); output.Write(e.Value);
                    }
                    // No checkpoints are advertised until engine restore equivalence is proven.
                    output.Write(0);
                    if (_hashes.Count > 0)
                    {
                        if (_hashes[^1].Frame > _last) throw new InvalidDataException("Hash beyond replay duration.");
                        output.Write(ReplayFormatV3.HashMagic);
                        output.Write(_hashSchema);
                        ReplayFormatV3.WriteString(output, _hashBuildId);
                        output.Write(_hashes.Count);
                        foreach (ReplayExpectedHash hash in _hashes)
                        {
                            output.Write(hash.Frame);
                            output.Write(Convert.FromHexString(hash.Value));
                        }
                    }
                }
                if (footer.Length > ReplayFormatV3.MaxFooter) throw new IOException("Replay index too large.");
                long offset = _stream.Position;
                _writer.Write(ReplayFormatV3.FooterMagic); _writer.Write((int)footer.Length);
                _writer.Write(ReplayFormatV3.Crc(footer.GetBuffer().AsSpan(0, (int)footer.Length)));
                _writer.Write(footer.GetBuffer().AsSpan(0, (int)footer.Length));
                _writer.Write(offset); _writer.Write(ReplayFormatV3.TrailerMagic);
                _stream.Flush(true);
            }
            catch { _faulted = true; throw; }
            finally
            {
                _disposed = true;
                _records.Dispose(); _chunk.Dispose(); _writer.Dispose(); _stream.Dispose();
            }
            if (!_faulted) File.Move(PartialPath, _path);
        }
    }

    internal sealed class ReplayReaderV3 : IDisposable
    {
        private readonly FileStream _stream;
        private readonly BinaryReader _reader;
        private readonly List<ReplayChunkIndex> _index = new();
        private BinaryReader? _chunk;
        private uint _remaining, _first, _last, _previous;
        private int _chunkNumber;
        private readonly long _dataStart;
        private readonly bool _metadataOnly;
        private long _dataEnd;
        private bool _hasFooter, _ended;
        public ReplayMetadata Metadata { get; }
        public ReplayOpenResult LastResult { get; private set; } = ReplayOpenResult.Success;
        public uint DurationFrames => Metadata.DurationFrames;

        // stream is positioned immediately after the six-byte dispatch header.
        public ReplayReaderV3(FileStream stream, byte protocol, bool metadataOnly = false)
        {
            _stream = stream;
            _metadataOnly = metadataOnly;
            _reader = new BinaryReader(stream, Encoding.UTF8, true);
            int size = _reader.ReadInt32();
            uint crc = _reader.ReadUInt32();
            if (size is < 1 or > ReplayFormatV3.MaxHeader) throw new InvalidDataException("Invalid metadata length.");
            byte[] header = ReplayFormatV3.ReadBytes(_reader, size);
            if (ReplayFormatV3.Crc(header) != crc) throw new InvalidDataException("Metadata checksum failed.");
            Metadata = ReplayFormatV3.DecodeMetadata(protocol, header);
            _dataStart = stream.Position;
            _dataEnd = stream.Length;
            ReadFooter();
            if (!_hasFooter) Metadata.Integrity = ReplayIntegrity.Truncated;
            stream.Position = _dataStart;
        }

        private void ReadFooter()
        {
            if (_stream.Length - _dataStart < 12) return;
            _stream.Position = _stream.Length - 12;
            long offset = _reader.ReadInt64();
            if (_reader.ReadUInt32() != ReplayFormatV3.TrailerMagic) return;
            if (offset < _dataStart || offset > _stream.Length - 24) throw new InvalidDataException("Invalid footer offset.");
            _stream.Position = offset;
            if (_reader.ReadUInt32() != ReplayFormatV3.FooterMagic) throw new InvalidDataException("Invalid footer magic.");
            int length = _reader.ReadInt32();
            uint crc = _reader.ReadUInt32();
            if (length < 16 || length > ReplayFormatV3.MaxFooter || offset + 12 + length != _stream.Length - 12)
                throw new InvalidDataException("Invalid footer length.");
            byte[] bytes = ReplayFormatV3.ReadBytes(_reader, length);
            if (ReplayFormatV3.Crc(bytes) != crc) throw new InvalidDataException("Footer checksum failed.");
            using var footer = new BinaryReader(new MemoryStream(bytes));
            uint duration = footer.ReadUInt32();
            int count = footer.ReadInt32();
            if (duration > ReplayFormatV3.MaxFrame || count < 0 || count > ReplayFormatV3.MaxChunks
                || (long)count * 20 > length - 16) throw new InvalidDataException("Invalid chunk index count.");
            long nextOffset = _dataStart;
            uint previous = 0;
            for (int i = 0; i < count; i++)
            {
                var entry = new ReplayChunkIndex(footer.ReadUInt32(), footer.ReadUInt32(), footer.ReadInt64(), footer.ReadInt32());
                if (entry.FirstFrame < previous || entry.LastFrame < entry.FirstFrame || entry.LastFrame > duration
                    || entry.Offset != nextOffset || entry.CompressedLength is < 1 or > ReplayFormatV3.MaxChunk + 65536)
                    throw new InvalidDataException("Invalid chunk index entry.");
                nextOffset = entry.Offset + ReplayFormatV3.ChunkHeaderSize + entry.CompressedLength;
                if (nextOffset > offset) throw new InvalidDataException("Chunk overlaps footer.");
                previous = entry.LastFrame;
                if (!_metadataOnly) _index.Add(entry);
            }
            if (nextOffset != offset || (count != 0 && previous != duration) || (count == 0 && duration != 0))
                throw new InvalidDataException("Invalid replay duration or data extent.");
            int events = footer.ReadInt32();
            if (events < 0 || events > ReplayFormatV3.MaxEvents || (long)events * 11 > bytes.Length - footer.BaseStream.Position - 4)
                throw new InvalidDataException("Invalid event count.");
            var annotations = new List<ReplayEvent>(_metadataOnly ? 0 : events);
            uint lastEvent = 0;
            for (int i = 0; i < events; i++)
            {
                var e = new ReplayEvent(footer.ReadUInt32(), (ReplayEventType)footer.ReadByte(),
                    footer.ReadByte(), footer.ReadByte(), footer.ReadInt32());
                if (e.Frame < lastEvent || e.Frame > duration || e.Type > ReplayEventType.WeaponFired
                    || (e.ActorSlot != byte.MaxValue && e.ActorSlot >= RosterPacket.MaxSlots)
                    || (e.TargetSlot != byte.MaxValue && e.TargetSlot >= RosterPacket.MaxSlots))
                    throw new InvalidDataException("Invalid replay event.");
                if (!_metadataOnly) annotations.Add(e);
                lastEvent = e.Frame;
            }
            if (footer.ReadInt32() != 0)
                throw new InvalidDataException("Unsupported checkpoint index.");
            // Early v3 recordings ended after the checkpoint count. Later optional hash
            // sections are fully length/CRC protected by the same footer envelope.
            if (footer.BaseStream.Position < bytes.Length)
            {
                if (footer.ReadUInt32() != ReplayFormatV3.HashMagic) throw new InvalidDataException("Unknown replay footer extension.");
                Metadata.HashSchema = footer.ReadUInt16();
                Metadata.HashBuildId = ReplayFormatV3.ReadString(footer);
                int hashCount = footer.ReadInt32();
                if (Metadata.HashSchema == 0 || Metadata.HashBuildId.Length == 0
                    || hashCount < 1 || hashCount > ReplayFormatV3.MaxHashes
                    || (long)hashCount * 36 != bytes.Length - footer.BaseStream.Position)
                    throw new InvalidDataException("Invalid replay hash index.");
                var hashes = new List<ReplayExpectedHash>(_metadataOnly ? 0 : hashCount);
                uint priorHash = 0;
                for (int i = 0; i < hashCount; i++)
                {
                    uint frame = footer.ReadUInt32();
                    if (frame > duration || (i > 0 && frame <= priorHash)) throw new InvalidDataException("Invalid replay hash frame.");
                    if (_metadataOnly) footer.BaseStream.Position += 32;
                    else hashes.Add(new(frame, Convert.ToHexString(ReplayFormatV3.ReadBytes(footer, 32))));
                    priorHash = frame;
                }
                Metadata.ExpectedHashes = hashes;
            }
            if (footer.BaseStream.Position != bytes.Length) throw new InvalidDataException("Unexpected replay footer tail.");
            Metadata.Events = annotations;
            Metadata.DurationFrames = duration;
            // Header/footer validity does not prove chunk health. Only a full validation does.
            Metadata.Integrity = Metadata.Recovered ? ReplayIntegrity.Recovered : ReplayIntegrity.Unknown;
            _dataEnd = offset;
            _hasFooter = true;
        }

        public DemoRecord? SeekAfter(uint frame)
        {
            if (_metadataOnly) throw new InvalidOperationException("Metadata-only readers cannot seek packets.");

            _chunk?.Dispose();
            _chunk = null;
            _remaining = 0;
            _previous = 0;
            _ended = false;
            LastResult = ReplayOpenResult.Success;

            int index = _index.FindIndex(entry => entry.LastFrame > frame);
            if (index < 0)
            {
                _stream.Position = _dataEnd;
                _chunkNumber = _index.Count;
                _ended = true;
                return null;
            }

            _stream.Position = _index[index].Offset;
            _chunkNumber = index;
            DemoRecord? record;
            do
            {
                record = ReadNext();
            }
            while (record is DemoRecord value && value.Frame <= frame);
            return record;
        }

        public DemoRecord? ReadNext()
        {
            if (_metadataOnly) throw new InvalidOperationException("Metadata-only readers cannot read packets.");
            if (_ended) return null;
            try
            {
                if (_remaining == 0 && !ReadChunk()) return null;
                uint frame = _chunk!.ReadUInt32();
                int size = _chunk.ReadUInt16();
                if (frame < _first || frame > _last || frame < _previous || size is < 1 or > NetConfig.MaxPacketSize)
                    throw new InvalidDataException("Invalid packet record.");
                byte[] data = ReplayFormatV3.ReadBytes(_chunk, size);
                _remaining--; _previous = frame;
                if (_remaining == 0 && (frame != _last || _chunk.BaseStream.Position != _chunk.BaseStream.Length))
                    throw new InvalidDataException("Invalid chunk record count or frame range.");
                if (!_hasFooter) Metadata.DurationFrames = frame;
                return new DemoRecord(frame, data);
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is OverflowException)
            {
                LastResult = ReplayFormatV3.Failure(ex);
                Metadata.Integrity = LastResult == ReplayOpenResult.Truncated ? ReplayIntegrity.Truncated : ReplayIntegrity.Corrupt;
                _ended = true;
                return null;
            }
        }

        private bool ReadChunk()
        {
            _chunk?.Dispose(); _chunk = null;
            if (_stream.Position == _dataEnd)
            {
                _ended = true;
                LastResult = _hasFooter ? ReplayOpenResult.Success : ReplayOpenResult.Truncated;
                Metadata.Integrity = !_hasFooter ? ReplayIntegrity.Truncated : Metadata.Recovered
                    ? ReplayIntegrity.Recovered : ReplayIntegrity.Healthy;
                return false;
            }
            long offset = _stream.Position;
            if (_dataEnd - offset < ReplayFormatV3.ChunkHeaderSize) throw new EndOfStreamException();
            if (_reader.ReadUInt32() != ReplayFormatV3.ChunkMagic) throw new InvalidDataException("Invalid chunk magic.");
            _first = _reader.ReadUInt32(); _last = _reader.ReadUInt32(); _remaining = _reader.ReadUInt32();
            int compressedLength = _reader.ReadInt32(), rawLength = _reader.ReadInt32();
            uint crc = _reader.ReadUInt32();
            if (_first < _previous || _last < _first || _last > ReplayFormatV3.MaxFrame
                || rawLength is < 7 or > ReplayFormatV3.MaxChunk || _remaining == 0 || _remaining > rawLength / 7
                || compressedLength is < 1 or > ReplayFormatV3.MaxChunk + 65536)
                throw new InvalidDataException("Invalid chunk header.");
            if (_hasFooter && (_chunkNumber >= _index.Count
                || _index[_chunkNumber] != new ReplayChunkIndex(_first, _last, offset, compressedLength)))
                throw new InvalidDataException("Chunk differs from index.");
            if (compressedLength > _dataEnd - _stream.Position) throw new EndOfStreamException();
            byte[] compressed = ReplayFormatV3.ReadBytes(_reader, compressedLength);
            byte[] raw = new byte[rawLength];
            using (var deflate = new DeflateStream(new MemoryStream(compressed), CompressionMode.Decompress))
            {
                deflate.ReadExactly(raw);
                if (deflate.ReadByte() != -1) throw new InvalidDataException("Decompressed chunk exceeds declared size.");
            }
            if (ReplayFormatV3.Crc(raw) != crc) throw new InvalidDataException("Chunk checksum failed.");
            // Validate every record before returning any part of a chunk. Recovery must never
            // copy the valid prefix of a malformed chunk and label it recovered data.
            using (var check = new BinaryReader(new MemoryStream(raw)))
            {
                uint prior = _first;
                for (uint i = 0; i < _remaining; i++)
                {
                    uint frame = check.ReadUInt32(); int size = check.ReadUInt16();
                    if (frame < prior || frame > _last || (i == 0 && frame != _first)
                        || size is < 1 or > NetConfig.MaxPacketSize || size > raw.Length - check.BaseStream.Position)
                        throw new InvalidDataException("Invalid chunk packet bounds.");
                    check.BaseStream.Position += size; prior = frame;
                }
                if (prior != _last || check.BaseStream.Position != raw.Length) throw new InvalidDataException("Invalid chunk records.");
            }
            _chunk = new BinaryReader(new MemoryStream(raw));
            _chunkNumber++;
            return true;
        }

        public void Dispose() { _chunk?.Dispose(); _reader.Dispose(); _stream.Dispose(); }
    }
}
