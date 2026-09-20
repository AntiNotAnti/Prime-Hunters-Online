using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using MphRead.Entities;
using OpenTK.Mathematics;

namespace MphRead.Mods.Network
{
    // Versioned, explicit gameplay projection. Camera, draw timing, audio, object
    // identities and reflection-discovered fields are intentionally not part of it.
    internal static class ReplayStateHash
    {
        internal const ushort Schema = 2;
        internal static readonly string BuildId = typeof(ReplayStateHash).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

        internal static string Compute(Scene scene)
            => Compute(scene, DemoPlayback.CurrentFrame);

        // Clip-fidelity comparisons normalize a source frame range to clip frame 0.
        // Expected-hash playback continues to call the overload above, so its schema
        // and on-disk meaning are unchanged.
        internal static string Compute(Scene scene, uint normalizedFrame)
        {
            using var stream = new MemoryStream(4096);
            using var writer = new BinaryWriter(stream);
            writer.Write(Schema);
            writer.Write(normalizedFrame);
            writer.Write((int)GameState.Mode);
            writer.Write((int)GameState.MatchState);
            writer.Write(GameState.MatchTime);
            writer.Write(GameState.PrimeHunter);
            for (int slot = 0; slot < PlayerEntity.SlotCapacity; slot++)
            {
                writer.Write(slot);
                writer.Write(GameState.Points[slot]); writer.Write(GameState.Kills[slot]); writer.Write(GameState.Deaths[slot]);
                writer.Write(GameState.TeamPoints[slot]); writer.Write(GameState.TeamKills[slot]); writer.Write(GameState.TeamDeaths[slot]);
                writer.Write(GameState.Time[slot]); writer.Write(GameState.TeamTime[slot]);
                var player = PlayerEntity.Players[slot];
                writer.Write(player != null);
                if (player == null) continue;
                writer.Write((int)player.LoadFlags);
                writer.Write((int)player.Hunter); writer.Write(player.TeamIndex);
                Write(writer, player.Position); Write(writer, player.FacingVector); Write(writer, player.Speed);
                writer.Write(player.Health); writer.Write((int)player.CurrentWeapon);
                writer.Write(player.IsAltForm); writer.Write(player.IsMorphing); writer.Write(player.IsUnmorphing);
                writer.Write(player.RespawnTimer); writer.Write(player.DeathCountdown); writer.Write(player.TimeSinceShot);
                writer.Write(player.OctolithFlag?.Id ?? -1);
            }
            foreach (EntityBase entity in scene.Entities)
            {
                if (entity is OctolithFlagEntity flag)
                {
                    writer.Write((int)flag.Type); writer.Write(flag.Id); Write(writer, flag.Position);
                    writer.Write(flag.AtBase); writer.Write(flag.Carrier?.SlotIndex ?? -1);
                }
                else if (entity is NodeDefenseEntity node)
                {
                    writer.Write((int)node.Type); writer.Write(node.Id);
                    writer.Write(node.CurrentTeam); writer.Write(node.OccupyingTeam); writer.Write(node.Progress);
                    writer.Write(node.Contested); writer.Write(node.InProgress);
                    writer.Write(node.CapturedPlayer?.SlotIndex ?? -1);
                    foreach (bool occupied in node.OccupiedBy) writer.Write(occupied);
                }
            }
            writer.Flush();
            return Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, (int)stream.Length)));
        }

        private static void Write(BinaryWriter writer, Vector3 vector)
        {
            writer.Write(vector.X); writer.Write(vector.Y); writer.Write(vector.Z);
        }
    }

    internal static class ReplayVerification
    {
        // Used by the headless verifier after every complete engine step, including
        // frames processed inside a seek batch or a 4x presentation interval.
        internal static Action<Scene>? ObserveFrame;
        private static int _nextHash;
        internal static void Reset() => _nextHash = 0;
        internal static void SeekTo(uint frame)
        {
            _nextHash = 0;
            ReplayMetadata? metadata = DemoPlayback.Metadata;
            if (metadata == null) return;
            while (_nextHash < metadata.ExpectedHashes.Count
                && metadata.ExpectedHashes[_nextHash].Frame <= frame)
            {
                _nextHash++;
            }
        }

        internal static void AfterFrame(Scene scene)
        {
            ObserveFrame?.Invoke(scene);
            Replay.ReplayCheckpointManager.AfterFrame(scene);
            ReplayMetadata? metadata = DemoPlayback.Metadata;
            if (metadata == null || metadata.HashSchema != ReplayStateHash.Schema
                || metadata.HashBuildId != ReplayStateHash.BuildId || _nextHash >= metadata.ExpectedHashes.Count) return;
            ReplayExpectedHash expected = metadata.ExpectedHashes[_nextHash];
            if (expected.Frame != DemoPlayback.CurrentFrame) return;
            _nextHash++;
            string actual = ReplayStateHash.Compute(scene);
            if (actual != expected.Value)
                DemoPlayback.FailVerification($"Replay state differs at frame {expected.Frame}: expected {expected.Value}, got {actual}.");
        }
    }
}
