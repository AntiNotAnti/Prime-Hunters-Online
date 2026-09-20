using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Buffers.Binary;

namespace MphRead.Mods.Network
{
    public enum ReplayOpenResult
    {
        Success, FileMissing, InvalidMagic, UnsupportedFormat, ProtocolMismatch,
        Empty, Truncated, Corrupt, MissingMatchState, MapMissing, MapHashMismatch, IoError, StateMismatch
    }

    public enum ReplayIntegrity { Unknown, Healthy, Recovered, Truncated, Corrupt }
    public enum ReplayType : byte { FullMatch, Clip }
    public enum ReplayEventType : byte
    {
        PlayerSpawn, PlayerDeath, Kill, Damage, ScoreChanged, PlayerJoined,
        PlayerLeft, Objective, MatchStarted, MatchEnded
    }

    public readonly record struct ReplayEvent(uint Frame, ReplayEventType Type,
        byte ActorSlot = byte.MaxValue, byte TargetSlot = byte.MaxValue, int Value = 0);
    internal readonly record struct ReplayPlayerInfo(byte Slot, byte Hunter, sbyte Team, string Name);
    internal readonly record struct ReplayExpectedHash(uint Frame, string Value);

    // Bootstrap is composed of existing wire packets, applied by the usual session handlers.
    // It is not a serializer of engine objects or an alternative multiplayer model.
    internal sealed class ReplayBootstrap
    {
        public IReadOnlyList<byte[]> Packets { get; init; } = Array.Empty<byte[]>();
    }

    internal sealed class ReplayMetadata
    {
        public byte FormatVersion { get; init; } = 3;
        public byte ProtocolVersion { get; init; } = (byte)NetConfig.ProtocolVersion;
        public ushort TickRate { get; init; } = 60;
        public string BuildVersion { get; init; } = Update.BuildVersion.Display;
        public string BuildId { get; init; } = typeof(ReplayMetadata).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        public DateTime RecordedAtUtc { get; init; } = DateTime.UtcNow;
        public ReplayType Type { get; init; }
        public string RoomKey { get; init; } = "";
        public ulong MapHash { get; init; }
        public GameMode Mode { get; init; }
        public uint DurationFrames { get; set; }
        public ReplayIntegrity Integrity { get; set; }
        public bool Recovered { get; init; }
        public IReadOnlyList<ReplayPlayerInfo> Players { get; init; } = Array.Empty<ReplayPlayerInfo>();
        public ReplayBootstrap Bootstrap { get; init; } = new();
        public IReadOnlyList<ReplayEvent> Events { get; set; } = Array.Empty<ReplayEvent>();
        public ushort HashSchema { get; set; }
        public string HashBuildId { get; set; } = "";
        public IReadOnlyList<ReplayExpectedHash> ExpectedHashes { get; set; } = Array.Empty<ReplayExpectedHash>();
        public bool BuildMatches => BuildId == (typeof(ReplayMetadata).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown");
    }

    internal static class ReplayMapIdentity
    {
        // Hash the actual room binaries, not its filename or local absolute paths. Streaming
        // bounds memory and covers custom-map geometry, collision, entities and node layout.
        public static ulong Compute(string roomKey)
        {
            var (room, _) = Metadata.GetRoomByName(roomKey);
            if (room == null) return 0;
            string root;
            try { root = room.FirstHunt ? Paths.FhFileSystem : Paths.FileSystem; }
            catch (KeyNotFoundException) { return 0; } // Asset-free lobby/tools have no extraction configured.
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[64 * 1024];
            Span<byte> size = stackalloc byte[8];
            foreach (string? relative in new[] { room.ModelPath, room.CollisionPath,
                room.EntityPath, room.NodePath, room.AnimationPath, room.TexturePath })
            {
                if (string.IsNullOrEmpty(relative)) continue;
                string path = Paths.Combine(root, relative);
                using var stream = File.OpenRead(path);
                BinaryPrimitives.WriteInt64LittleEndian(size, stream.Length);
                hash.AppendData(size);
                int read;
                while ((read = stream.Read(buffer)) > 0) hash.AppendData(buffer.AsSpan(0, read));
            }
            return BinaryPrimitives.ReadUInt64LittleEndian(hash.GetHashAndReset());
        }

        public static ReplayOpenResult Validate(ReplayMetadata metadata)
        {
            // Zero identifies an unavailable hash (e.g. an asset-free format fixture).
            // Real recordings require a hash at Start, so never silently waive it there.
            if (metadata.MapHash == 0) return ReplayOpenResult.Success;
            try
            {
                ulong current = Compute(metadata.RoomKey);
                return current == 0 ? ReplayOpenResult.MapMissing : current == metadata.MapHash
                    ? ReplayOpenResult.Success : ReplayOpenResult.MapHashMismatch;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return ReplayOpenResult.MapMissing;
            }
        }
    }
}
