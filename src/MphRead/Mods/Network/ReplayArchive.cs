using System;
using System.Collections.Generic;
using System.IO;

namespace MphRead.Mods.Network
{
    internal static class ReplayArchive
    {
        public static bool Recover(string path, out string? output, out ReplayOpenResult result)
        {
            output = null;
            using DemoReader? reader = DemoReader.Open(path, out result);
            if (reader?.Metadata is not { } metadata) return false;
            ReplayWriterV3? writer = null;
            try
            {
                string destination = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!,
                    Path.GetFileNameWithoutExtension(path) + $"_recovered_{Guid.NewGuid():N}" + DemoFile.Extension);
                DemoRecord? first = reader.ReadNext();
                if (first == null) { result = reader.LastResult == ReplayOpenResult.Success ? ReplayOpenResult.Empty : reader.LastResult; return false; }
                writer = new ReplayWriterV3(destination, Copy(metadata, metadata.Bootstrap, metadata.RoomKey,
                    metadata.Mode, metadata.Players, metadata.MapHash, metadata.Type, recovered: true));
                writer.WriteRecord(first.Value.Frame, first.Value.Data);
                uint last = first.Value.Frame;
                while (reader.ReadNext() is { } record) { writer.WriteRecord(record.Frame, record.Data); last = record.Frame; }
                if (last < metadata.LeadInFrames) { writer.Abort(); result = ReplayOpenResult.Empty; return false; }
                // Each chunk is CRC/record validated before exposing its first packet. The
                // source remains untouched, and only complete valid chunks reach this file.
                result = reader.LastResult;
                foreach (ReplayEvent e in metadata.Events) writer.WriteEvent(e with { Frame = e.Frame + metadata.LeadInFrames });
                writer.Dispose();
                output = destination;
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException)
            {
                writer?.Abort(); result = ReplayFormatV3.Failure(ex); return false;
            }
        }

        public static ReplayOpenResult Validate(string path)
        {
            using DemoReader? reader = DemoReader.Open(path, out var result);
            if (reader == null) return result;
            bool any = false;
            while (reader.ReadNext() != null) any = true;
            return reader.LastResult != ReplayOpenResult.Success ? reader.LastResult
                : any ? ReplayOpenResult.Success : ReplayOpenResult.Empty;
        }

        // Reference hashes come from a verified replay of the normal engine, not
        // from a live local player whose prediction differs from a replay puppet.
        public static ReplayOpenResult WithExpectedHashes(string source, string output, IReadOnlyList<ReplayExpectedHash> hashes)
        {
            using DemoReader? reader = DemoReader.Open(source, out var result);
            if (reader?.Metadata == null) return reader == null ? result : ReplayOpenResult.UnsupportedFormat;
            ReplayWriterV3? writer = null;
            try
            {
                writer = new ReplayWriterV3(output, reader.Metadata);
                while (reader.ReadNext() is { } record) writer.WriteRecord(record.Frame, record.Data);
                if (reader.LastResult != ReplayOpenResult.Success) { writer.Abort(); return reader.LastResult; }
                foreach (ReplayEvent value in reader.Metadata.Events) writer.WriteEvent(value with { Frame = value.Frame + reader.Metadata.LeadInFrames });
                foreach (ReplayExpectedHash value in hashes) writer.WriteExpectedHash(value with { Frame = value.Frame + reader.Metadata.LeadInFrames }, ReplayStateHash.Schema, ReplayStateHash.BuildId);
                writer.Dispose();
                return ReplayOpenResult.Success;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException || ex is ArgumentException)
            {
                writer?.Abort(); return ReplayFormatV3.Failure(ex);
            }
        }

        public static ReplayOpenResult Extract(string source, uint start, uint end, string output)
        {
            if (start > end) return ReplayOpenResult.Empty;
            using DemoReader? reader = DemoReader.Open(source, out var result);
            if (reader == null) return result;
            if (reader.ProtocolVersion != NetConfig.ProtocolVersion) return ReplayOpenResult.ProtocolMismatch;
            ReplayMetadata metadata = reader.Metadata ?? new ReplayMetadata();
            if (reader.FormatVersion == 2)
            {
                using var probe = new ReplayPlaybackSession(new PassiveReplaySessionHost());
                if (!probe.Join(source)) return probe.LastResult;
                var state = ((PassiveReplaySessionHost)probe.Host).State;
                var match = state.Match!.Value;
                var players = new List<ReplayPlayerInfo>();
                for (int slot = 0; slot < RosterPacket.MaxSlots; slot++)
                {
                    var occupant = state.Occupant(slot);
                    if (occupant.Generation != 0) players.Add(new((byte)slot, (byte)occupant.Hunter, occupant.Team, occupant.Name));
                }
                metadata = new ReplayMetadata { FormatVersion = 4, RoomKey = match.RoomKey,
                    Mode = (GameMode)match.Mode, MapHash = ReplayMapIdentity.Compute(match.RoomKey), Players = players,
                    Bootstrap = new ReplayBootstrap { Packets = new[] { ReplayTimelineArchive.Construction(state) } } };
                return ExtractRange(reader, metadata, start, end, output, probe.LastFrame);
            }
            // Preserve the initial world and every required warmup fact. Lead-in
            // is hidden by the session and advanced in bounded owner updates.
            // This is also how a legacy packet recording gets a faithful range:
            // reconstruct from its original bootstrap, not a mid-flight snapshot.
            if (reader.Metadata != null && (metadata.FormatVersion == 4 || metadata.MapHash != 0))
                return ExtractRange(reader, metadata, start, end, output);
            var bootstrap = new Dictionary<PacketType, byte[]>();
            foreach (byte[] packet in metadata.Bootstrap.Packets) Remember(bootstrap, packet);
            ReplayWriterV3? writer = null;
            try
            {
                DemoRecord? record;
                while ((record = reader.ReadNext()) is { } next && next.Frame < start) Remember(bootstrap, next.Data);
                if (record == null) return reader.LastResult == ReplayOpenResult.Success ? ReplayOpenResult.Empty : reader.LastResult;
                if (record.Value.Frame > end) return ReplayOpenResult.Empty;
                if (!bootstrap.TryGetValue(PacketType.MatchState, out byte[]? matchBytes)) return ReplayOpenResult.MissingMatchState;
                var match = MatchStatePacket.Read(matchBytes.AsSpan(1));
                var players = new List<ReplayPlayerInfo>();
                if (bootstrap.TryGetValue(PacketType.Roster, out byte[]? rosterBytes)
                    && rosterBytes.Length == 1 + RosterPacket.Size)
                {
                    RosterPacket roster = RosterPacket.Read(rosterBytes.AsSpan(1));
                    for (int i = 0; i < roster.Count; i++)
                    {
                        players.Add(new(roster.Slots[i], roster.Hunters[i], -1, roster.Names[i]));
                    }
                }
                var packets = new List<byte[]>();
                foreach (PacketType type in new[] { PacketType.SessionState, PacketType.MatchState,
                    PacketType.Roster, PacketType.Snapshot })
                    if (bootstrap.TryGetValue(type, out byte[]? packet)) packets.Add(packet);
                ulong hash = match.RoomKey == metadata.RoomKey ? metadata.MapHash : ReplayMapIdentity.Compute(match.RoomKey);
                var clip = Copy(metadata, new ReplayBootstrap { Packets = packets }, match.RoomKey,
                    (GameMode)match.Mode, players, hash, ReplayType.Clip, metadata.Recovered);
                writer = new ReplayWriterV3(output, clip);
                while (record is { } item && item.Frame <= end)
                {
                    writer.WriteRecord(item.Frame - start, item.Data);
                    record = reader.ReadNext();
                }
                if (reader.LastResult != ReplayOpenResult.Success) { writer.Abort(); return reader.LastResult; }
                foreach (ReplayEvent e in metadata.Events)
                    if (e.Frame >= start && e.Frame <= end) writer.WriteEvent(e with { Frame = e.Frame - start });
                writer.Dispose();
                return ReplayOpenResult.Success;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException || ex is ArgumentException)
            {
                writer?.Abort(); return ReplayFormatV3.Failure(ex);
            }
        }

        private static void Remember(Dictionary<PacketType, byte[]> packets, byte[] packet)
        {
            if (packet.Length == 0) return;
            PacketType type = (PacketType)packet[0];
            if (type == PacketType.MapChange) type = PacketType.MatchState;
            if (type == PacketType.MatchState && packet.Length == 1 + MatchStatePacket.Size)
            {
                if (packets.TryGetValue(type, out byte[]? previous)
                    && MatchStatePacket.Read(previous.AsSpan(1)).MatchId != MatchStatePacket.Read(packet.AsSpan(1)).MatchId)
                    packets.Remove(PacketType.Snapshot);
                if ((PacketType)packet[0] == PacketType.MapChange)
                {
                    packet = (byte[])packet.Clone(); packet[0] = (byte)PacketType.MatchState;
                }
                packets[type] = packet;
            }
            else if (type == PacketType.SessionState && packet.Length == 1 + SessionStatePacket.Size)
                packets[type] = packet;
            else if (type is PacketType.Roster or PacketType.Snapshot) packets[type] = packet;
        }

        private static ReplayMetadata Copy(ReplayMetadata source, ReplayBootstrap bootstrap, string room,
            GameMode mode, IReadOnlyList<ReplayPlayerInfo> players, ulong hash, ReplayType type, bool recovered) => new()
        {
            FormatVersion = source.FormatVersion, WorldCheckpoint = source.WorldCheckpoint,
            OriginRecordingFrame = source.OriginRecordingFrame, LeadInFrames = source.LeadInFrames,
            ProtocolVersion = source.ProtocolVersion, BuildVersion = source.BuildVersion, BuildId = source.BuildId,
            RecordedAtUtc = source.RecordedAtUtc, Type = type, RoomKey = room, Mode = mode, Players = players,
            MapHash = hash, Bootstrap = bootstrap, Recovered = recovered
        };

        private static ReplayOpenResult ExtractRange(DemoReader reader, ReplayMetadata source, uint start, uint end, string output, uint? legacyDuration = null)
        {
            ReplayWriterV3? writer = null;
            try
            {
                uint duration = legacyDuration ?? reader.DurationFrames;
                if (start > duration || start > end) return ReplayOpenResult.Empty;
                end = Math.Min(end, duration);
                uint lead = checked(source.LeadInFrames + start), last = checked(source.LeadInFrames + end);
                var metadata = new ReplayMetadata
                {
                    FormatVersion = 4, ProtocolVersion = source.ProtocolVersion,
                    BuildVersion = source.BuildVersion, BuildId = source.BuildId, RecordedAtUtc = source.RecordedAtUtc,
                    Type = ReplayType.Clip, RoomKey = source.RoomKey, Mode = source.Mode, Players = source.Players,
                    MapHash = source.MapHash, Bootstrap = source.Bootstrap, Recovered = source.Recovered,
                    OriginRecordingFrame = source.OriginRecordingFrame, WorldCheckpoint = source.WorldCheckpoint,
                    LeadInFrames = lead
                };
                writer = new ReplayWriterV3(output, metadata);
                while (reader.ReadNext() is { } record)
                {
                    if (record.Frame > last) break;
                    writer.WriteRecord(record.Frame, record.Data);
                }
                if (reader.LastResult != ReplayOpenResult.Success) { writer.Abort(); return reader.LastResult; }
                ReplayTimelineArchive.EndFrame(writer, last);
                foreach (ReplayEvent value in source.Events)
                    if (value.Frame >= start && value.Frame <= end)
                        writer.WriteEvent(value with { Frame = value.Frame + source.LeadInFrames });
                writer.Dispose(); return ReplayOpenResult.Success;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or OverflowException)
            { writer?.Abort(); return ReplayFormatV3.Failure(ex); }
        }
    }
}
