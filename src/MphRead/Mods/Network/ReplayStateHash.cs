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
        internal const ushort Schema = 3;
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
            writer.Write((int)scene.GameState.Mode);
            writer.Write((int)scene.GameState.MatchState);
            writer.Write(scene.GameState.MatchTime);
            writer.Write(scene.GameState.PrimeHunter);
            writer.Write(scene.Random.Rng1);
            for (int slot = 0; slot < PlayerEntity.SlotCapacity; slot++)
            {
                writer.Write(slot);
                writer.Write(scene.GameState.Points[slot]); writer.Write(scene.GameState.Kills[slot]); writer.Write(scene.GameState.Deaths[slot]);
                writer.Write(scene.GameState.TeamPoints[slot]); writer.Write(scene.GameState.TeamKills[slot]); writer.Write(scene.GameState.TeamDeaths[slot]);
                writer.Write(scene.GameState.Time[slot]); writer.Write(scene.GameState.TeamTime[slot]);
                var player = scene.Players.Items[slot];
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
                else if (entity is BeamProjectileEntity beam)
                {
                    writer.Write((int)beam.Type);
                    WriteIdentity(writer, beam.Owner); WriteIdentity(writer, beam.Target);
                    writer.Write(beam.ModLaunchFrame); writer.Write(beam.ModLaunchMatch);
                    writer.Write(beam.ModLaunchAuthority); writer.Write(beam.ModLaunchGeneration); writer.Write(beam.ModLaunchLife);
                    writer.Write((int)beam.Beam); writer.Write((int)beam.BeamKind); writer.Write((int)beam.Flags);
                    Write(writer, beam.Position); Write(writer, beam.Velocity); Write(writer, beam.Acceleration);
                    Write(writer, beam.SpawnPosition); Write(writer, beam.Direction);
                    writer.Write(beam.Age); writer.Write(beam.Lifespan); writer.Write(beam.Speed);
                    writer.Write(beam.Homing); writer.Write(beam.Damage); writer.Write(beam.HeadshotDamage);
                    writer.Write(beam.SplashDamage); writer.Write(beam.SplashRadius); writer.Write(beam.CylinderRadius);
                    writer.Write(beam.ModContinuousPhase); writer.Write(beam.ModHasSharedContinuousPhase);
                }
                else if (entity is BombEntity bomb)
                {
                    writer.Write((int)bomb.Type); WriteIdentity(writer, bomb.Owner);
                    writer.Write((int)bomb.BombType); writer.Write((int)bomb.Flags);
                    Write(writer, bomb.Position); writer.Write(bomb.Countdown); writer.Write(bomb.BombIndex);
                    writer.Write(bomb.Radius); writer.Write(bomb.SelfRadius);
                    writer.Write(bomb.Damage); writer.Write(bomb.EnemyDamage);
                }
                else if (entity is ItemSpawnEntity spawn)
                {
                    writer.Write((int)spawn.Type); writer.Write(spawn.Id); Write(writer, spawn.Position);
                    var state = spawn.ModHealthState;
                    writer.Write(state.Available); writer.Write(state.Active); writer.Write(state.Cooldown);
                    writer.Write(state.SpawnCount); writer.Write(state.PickerSlot);
                }
                else if (entity is ItemInstanceEntity item)
                {
                    writer.Write((int)item.Type); writer.Write((int)item.ItemType); Write(writer, item.Position);
                    writer.Write(item.ParentId); writer.Write(item.Owner?.Id ?? -1); writer.Write(item.DespawnTimer);
                }
            }
            writer.Write(-1); // terminates the ordered world entity projection
            writer.Flush();
            return Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, (int)stream.Length)));
        }

        private static void Write(BinaryWriter writer, Vector3 vector)
        {
            writer.Write(vector.X); writer.Write(vector.Y); writer.Write(vector.Z);
        }

        private static void WriteIdentity(BinaryWriter writer, EntityBase? entity)
        {
            writer.Write(entity == null ? -1 : (int)entity.Type);
            writer.Write(entity is PlayerEntity player ? player.SlotIndex : entity?.Id ?? -1);
        }
    }

    internal static class ReplayVerification
    {
        // Used by the headless verifier after every complete engine step, including
        // frames processed inside a seek batch or a 4x presentation interval.
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
            if (!scene.Services.IsReplica) Replay.ReplayCheckpointManager.AfterFrame(scene);
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
