using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using MphRead.Mods.Multiplayer;
using OpenTK.Mathematics;

namespace MphRead.Mods.Network;

/// <summary>Asset-backed synthetic coverage, not a substitute for live combat acceptance.
/// The source supplies a verified room and spawn; generated facts exercise every mode,
/// all seven hunters, eight occupants, weapons, alt forms, afflictions and a new life.</summary>
internal static class ReplayWorldCoverageCheck
{
    internal static int Run(string source, string? output)
    {
        string directory = output ?? Path.Combine(Path.GetTempPath(), "prime-world-coverage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var reader = DemoReader.Open(source, out var result) ?? throw new InvalidDataException(result.ToString());
            var decoder = new ReplayReplicaState();
            if (reader.Metadata is { } metadata)
                foreach (var packet in metadata.Bootstrap.Packets) decoder.Accept(packet, 0);
            Vector3? origin = null;
            while (reader.ReadNext() is DemoRecord record)
            {
                decoder.Accept(record.Data, record.Frame);
                for (int slot = 0; slot < 8; slot++)
                    if (decoder.TryGetPlayer(slot, out var player) && player.Health > 0)
                    { origin = player.Position; break; }
                if (origin.HasValue && decoder.Match.HasValue) break;
            }
            if (origin == null || decoder.Match == null) throw new InvalidDataException("Source needs a room and an alive player.");
            for (GameMode mode = GameMode.Battle; mode <= GameMode.PrimeHunter; mode++)
            {
                string path = Path.Combine(directory, mode + ".ppdemo");
                Write(path, decoder.Match.Value.RoomKey, mode, origin.Value);
                Console.WriteLine($"[replayworld] {mode}: eight actors, all hunters, weapon/alt/affliction/death/respawn transitions");
                if (ReplayReplicaCheck.Run(path) != 0) return 1;
            }
            Console.WriteLine("[replayworld] PASS: all 12 multiplayer modes with detached restore, continuation and file/frozen-clip seeks.");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine($"[replayworld] FAIL: {ex}"); return 1; }
        finally { if (output == null) Directory.Delete(directory, recursive: true); }
    }

    private static void Write(string path, string room, GameMode mode, Vector3 origin)
    {
        const ushort matchId = 17;
        const ulong epoch = 19;
        var match = new MatchStatePacket
        {
            MatchId = matchId, AuthorityEpoch = epoch, RoomKey = room, NextRoomKey = "", Mode = (byte)mode,
            PlayerCount = 8, Flags = MatchStatePacket.FlagInProgress, PointGoal = MatchGoalRules.DefaultValue(mode),
            TimeRemaining = 600
        };
        var config = new SessionStatePacket
        {
            MatchId = matchId, AuthorityEpoch = epoch, Phase = SessionPhase.InMatch, MaxPlayers = 8, Revision = 1,
            WorldProfile = MatchWorldProfile.Resolve(8), OwnerSlot = 0,
            Match = new MatchDefinition { RoomKey = room, Mode = mode, Format = MatchFormat.Auto,
                TimeLimitSeconds = 600, PointGoal = match.PointGoal, AffinityWeapons = true, ShadowFreeze = true }
        };
        var roster = Network.RosterPacket.Create();
        roster.MatchId = matchId; roster.AuthorityEpoch = epoch; roster.Revision = 1; roster.Count = 8;
        for (byte slot = 0; slot < 8; slot++)
        {
            roster.Slots[slot] = slot; roster.Hunters[slot] = (byte)(slot % 7); roster.Colors[slot] = (byte)(slot % 4);
            roster.Teams[slot] = (sbyte)(slot % 2); roster.Generations[slot] = 1; roster.Names[slot] = "Actor " + slot;
        }
        byte[] MatchPacket()
        { byte[] p = new byte[1 + MatchStatePacket.Size]; p[0] = (byte)PacketType.MatchState; match.Write(p.AsSpan(1)); return p; }
        byte[] RosterPacket()
        { byte[] p = new byte[1 + Network.RosterPacket.Size]; p[0] = (byte)PacketType.Roster; roster.Write(p.AsSpan(1)); return p; }
        byte[] configuration = new byte[1 + SessionStatePacket.Size]; configuration[0] = (byte)PacketType.SessionState;
        config.Write(configuration.AsSpan(1));
        using var writer = new ReplayWriterV3(path, new ReplayMetadata
        {
            RoomKey = room, Mode = mode, MapHash = ReplayMapIdentity.Compute(room),
            Bootstrap = new ReplayBootstrap { Packets = new List<byte[]> { MatchPacket(), configuration, RosterPacket() } }
        });
        for (uint frame = 0; frame <= 1800; frame++)
        {
            if (frame % 60 == 0)
            {
                match.TimeRemaining = 600 - frame / 60f; match.TimeElapsed = frame / 60f;
                writer.WriteRecord(frame, MatchPacket());
                roster.Revision++; writer.WriteRecord(frame, RosterPacket());
            }
            bool alt = frame is >= 750 and < 1050;
            bool dead = frame is >= 1602 and < 1662;
            ushort life = (ushort)(frame >= 1662 ? 2 : 1);
            Vector3 Position(int slot) => origin + new Vector3((slot % 4 - 1.5f) * 2, 0, (slot / 4) * 3);
            if (frame % 3 == 0)
            {
                byte[] snapshot = new byte[1 + SnapshotHeader.Size + 8 * PlayerState.Size + NetMatchTimeSync.Size + NetHealthSync.HeaderSize];
                snapshot[0] = (byte)PacketType.Snapshot;
                new SnapshotHeader { MatchId = matchId, AuthorityEpoch = epoch, Frame = frame, PlayerCount = 8,
                    Rng1 = 12345 + frame, Rng2 = 98765 + frame }.Write(snapshot.AsSpan(1));
                for (byte slot = 0; slot < 8; slot++)
                {
                    byte flags = PlayerState.FlagActive | PlayerState.FlagSpawned;
                    if (alt) flags |= PlayerState.FlagAltForm;
                    if (frame is >= 1200 and < 1260) flags |= PlayerState.FlagFrozen;
                    if (frame is >= 1300 and < 1360) flags |= PlayerState.FlagDisrupted;
                    if (frame is >= 1400 and < 1460) flags |= PlayerState.FlagBurning;
                    new PlayerState
                    {
                        SlotIndex = slot, SlotGeneration = 1, LifeId = life, Flags = flags,
                        Position = Position(slot), Facing = slot < 4 ? Vector3.UnitZ : -Vector3.UnitZ,
                        Health = (ushort)(dead ? 0 : frame >= 1500 && frame < 1602 ? 90 : 190),
                        CurrentWeapon = (byte)(slot % 8), Team = (byte)(slot % 2),
                        Deaths = (ushort)(frame >= 1602 ? 1 : 0), DamageEventId = (ushort)(frame >= 1602 ? 2 : frame >= 1500 ? 1 : 0),
                        AttackerSlot = (byte)((slot + 1) % 8), DamageBeam = (byte)(slot % 8),
                        Damage0 = frame < 1500 ? default : new DamageEvent { EventId = (ushort)(frame >= 1602 ? 2 : 1),
                            AttackerGeneration = 1, AttackerSlot = (byte)((slot + 1) % 8),
                            Beam = (byte)(slot % 8), Damage = 90, Direction = Vector3.UnitZ * .1f }
                    }.Write(snapshot.AsSpan(1 + SnapshotHeader.Size + slot * PlayerState.Size));
                }
                BinaryPrimitives.WriteUInt16LittleEndian(snapshot.AsSpan(snapshot.Length - NetHealthSync.HeaderSize), matchId);
                writer.WriteRecord(frame, snapshot);
            }
            for (byte slot = 0; slot < 8; slot++)
            {
                bool fire = !dead && frame > 120 && frame % 30 < 10;
                IntentButtons buttons = dead ? 0 : IntentButtons.InPlayState;
                if (alt) buttons |= IntentButtons.AltFormState;
                if (fire) buttons |= alt ? IntentButtons.AltAttack : IntentButtons.Shoot;
                var presses = new uint[IntentPacket.PressHistory];
                if (fire && frame % 30 == 0) presses[0] = (uint)(alt ? IntentButtons.AltAttack : IntentButtons.Shoot);
                var intent = new IntentPacket { MatchId = matchId, AuthorityEpoch = epoch, SlotGeneration = 1, LifeId = life,
                    Frame = frame, Buttons = buttons, Presses = presses, HasState = true, AmmoUa = 999, AmmoMissiles = 99,
                    WeaponSelect = slot, Aim = slot < 4 ? Vector3.UnitZ : -Vector3.UnitZ, Position = Position(slot),
                    ChargeLevel = (byte)(frame % 90 == 0 ? 60 : 0), HomingTarget = IntentPacket.HomingTargetValid };
                byte[] packet = new byte[2 + IntentPacket.FullSize]; packet[0] = (byte)PacketType.SlotIntent; packet[1] = slot;
                intent.Write(packet.AsSpan(2)); writer.WriteRecord(frame, packet);
            }
        }
    }
}
