using System;
using System.Buffers.Binary;
using System.Linq;
using MphRead.Entities;
using MphRead.Mods.Input;
using MphRead.Mods.Multiplayer;
using OpenTK.Mathematics;

namespace MphRead.Mods.Network;

internal static class ReplayReplicaProjectionChecks
{
    internal static void Run(Action<bool, string> require)
    {
        var recorder = new ReplayRecorder();
        var match = new MatchStatePacket { RoomKey = "MP1 SANCTORUS", NextRoomKey = "",
            MatchId = 7, AuthorityEpoch = 9, Mode = (byte)GameMode.Battle };
        recorder.AcceptMatch(match, 0);
        var roster = RosterPacket.Create();
        roster.MatchId = 7; roster.AuthorityEpoch = 9; roster.Count = 1;
        roster.Slots[0] = 0; roster.Generations[0] = 2; roster.Names[0] = "actor";
        recorder.AcceptRoster(roster, 0);
        var configuration = new SessionStatePacket { MatchId = 7, AuthorityEpoch = 9,
            MaxPlayers = 8, WorldProfile = MatchWorldProfile.Resolve(8),
            Match = new MatchDefinition { RoomKey = match.RoomKey, Mode = GameMode.Battle,
                Format = MatchFormat.Auto, DisablePowerups = true } };
        recorder.AcceptConfiguration(configuration, 0);
        byte[] Snapshot(uint frame, ushort life)
        {
            int timeOffset = 1 + SnapshotHeader.Size + PlayerState.Size;
            int healthOffset = timeOffset + NetMatchTimeSync.Size;
            var packet = new byte[healthOffset + NetHealthSync.HeaderSize];
            packet[0] = (byte)PacketType.Snapshot;
            new SnapshotHeader { MatchId = 7, AuthorityEpoch = 9, Frame = frame, PlayerCount = 1 }.Write(packet.AsSpan(1));
            new PlayerState { SlotIndex = 0, SlotGeneration = 2, LifeId = life, Health = 99,
                Flags = PlayerState.FlagSpawned }.Write(packet.AsSpan(1 + SnapshotHeader.Size));
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(healthOffset), 7);
            return packet;
        }
        recorder.AcceptSnapshot(Snapshot(1, 1), 1, 1);
        var intent = new IntentPacket { MatchId = 7, AuthorityEpoch = 9, SlotGeneration = 2,
            LifeId = 1, Frame = 2, Buttons = IntentButtons.Shoot };
        recorder.AcceptIntent(0, intent, 2);
        int records = recorder.Timeline.RecordCount;
        recorder.AcceptIntent(0, intent, 3);
        intent.Frame = 1; recorder.AcceptIntent(0, intent, 3);
        intent.MatchId = 8; recorder.AcceptIntent(0, intent, 3);
        require(recorder.Timeline.RecordCount == records, "timeline rejects duplicate, old and foreign-match intents");
        require(recorder.Timeline.TryFreeze(1, 2, out var clip)
            && clip!.Records.Any(r => r.Kind == ReplayFactKind.Intent
                && IntentPacket.Read(r.Payload[2..]).Buttons == IntentButtons.Shoot),
            "accepted firing intent reaches frozen timeline");
        recorder.AcceptSnapshot(Snapshot(301, 1), 301, 301);
        require(recorder.Timeline.TryGetRestorePoint(301, out var baseline)
            && baseline!.Records.Count(r => r.Kind == ReplayFactKind.Intent) == 1,
            "network baseline retains current-life held input");
        require(baseline!.Records.Any(r => r.Payload[0] == (byte)PacketType.SessionState),
            "network baseline retains recorded match rules");
        recorder.AcceptSnapshot(Snapshot(601, 2), 601, 601);
        require(recorder.Timeline.TryGetRestorePoint(601, out baseline)
            && !baseline!.Records.Any(r => r.Kind == ReplayFactKind.Intent),
            "old-life firing input does not enter new-life baseline");
        require(clip!.Records.Any(r => r.Kind == ReplayFactKind.Intent), "later baselines preserve frozen facts");
        bool networkWorldRejected = false;
        try { PassiveReplayScene.Checkpoint(clip); } catch (System.IO.InvalidDataException) { networkWorldRejected = true; }
        require(networkWorldRejected, "network baseline cannot masquerade as a full replica world");
        foreach (byte[] invalidWorld in new[] { Array.Empty<byte>(), new byte[] { 0x50, 0x50, 0x57, 0x43, 255, 0 },
            new byte[Replay.ReplayWorldCheckpoint.MaximumBytes + 1] })
        {
            bool rejected = false;
            try { Replay.ReplayWorldCheckpoint.FromBytes(invalidWorld); }
            catch (Exception ex) when (ex is System.IO.InvalidDataException or System.IO.IOException) { rejected = true; }
            require(rejected, "world checkpoint rejects empty, incompatible or oversized payload before construction");
        }

        var decoder = new ReplayReplicaState();
        foreach (var fact in clip.RestorePoint.Records.Concat(clip.Records)) decoder.Accept(fact.Payload, fact.RecordingFrame);
        decoder.Advance(20);
        var checkpoint = decoder.CaptureCheckpoint();
        var restored = new ReplayReplicaState();
        restored.RestoreCheckpoint(new ReplayReplicaState().CaptureCheckpoint());
        require(restored.Match == null && restored.AcceptedPackets == 0, "empty decoder checkpoint restores");
        restored.RestoreCheckpoint(checkpoint);
        require(restored.CaptureCheckpoint().Bytes.SequenceEqual(checkpoint.Bytes), "decoder checkpoint roundtrip is exact");
        require(restored.IntentAge(0) == 18 && restored.Occupant(0).Generation == 2
            && restored.Configuration?.Match.DisablePowerups == true, "decoder restore retains input age, rules and occupant");
        decoder.Reset();
        require(restored.TryGetPlayer(0, out var restoredPlayer) && restoredPlayer.Health == 99,
            "decoder checkpoint is detached from its source");
        var invalid = checkpoint.Bytes.ToArray(); invalid[4]++;
        bool incompatible = false;
        try { restored.RestoreCheckpoint(new(invalid)); } catch (System.IO.InvalidDataException) { incompatible = true; }
        require(incompatible && restored.CaptureCheckpoint().Bytes.SequenceEqual(checkpoint.Bytes),
            "incompatible decoder checkpoint fails atomically");
        foreach (int length in new[] { 0, 4, 7, checkpoint.Bytes.Length / 2, checkpoint.Bytes.Length - 1 })
        {
            bool rejected = false;
            try { restored.RestoreCheckpoint(new(checkpoint.Bytes[..length])); }
            catch (System.IO.InvalidDataException) { rejected = true; }
            require(rejected && restored.CaptureCheckpoint().Bytes.SequenceEqual(checkpoint.Bytes),
                "truncated decoder checkpoint fails atomically at " + length);
        }
        var tombstone = new NetLifecycleTracker(); tombstone.SetOccupant(2);
        tombstone.Accept(2, 1, NetworkPlayerState.Dead, out _);
        tombstone.Accept(2, 1, NetworkPlayerState.Spectating, out _);
        var restoredLife = new NetLifecycleTracker(); restoredLife.Restore(tombstone.Capture());
        require(restoredLife.Accept(2, 1, NetworkPlayerState.Alive, out _) == LifecycleRejection.InvalidResurrection,
            "checkpoint retains death tombstone through spectator state");

        using var session = new ReplayPlaybackSession(new PassiveReplaySessionHost());
        var scene = new Scene(new(256, 192), SyntheticInput.CreateKeyboard(), SyntheticInput.CreateMouse(),
            _ => { }, () => { }, new ReplaySceneServices(session, new ReplayReplicaState()), initializeRuntime: false);
        try
        {
            var clock = new MatchStatePacket { Mode = (byte)GameMode.Defender, PointGoal = 90, TimeRemaining = 120,
                RoomKey = match.RoomKey, MatchId = 7, AuthorityEpoch = 9 };
            require(ReplaySceneServices.HistoricalMatchTime(clock, null, 130, 100) == 119.5f,
                "replica match clock advances between accepted packets");
            require(ReplaySceneServices.HistoricalMatchTime(clock, configuration, 130, 100) == -1,
                "replica unlimited clock uses the engine sentinel");
            clock.Flags |= MatchStatePacket.FlagEnding;
            require(ReplaySceneServices.HistoricalMatchTime(clock, configuration, 130, 100) == 120,
                "recorded match ending retains its accepted sequence clock");
            var rulesState = new ReplayReplicaState();
            byte[] rules = new byte[1 + MatchStatePacket.Size]; rules[0] = (byte)PacketType.MatchState; clock.Write(rules.AsSpan(1));
            rulesState.Accept(rules, 0);
            new ReplaySceneServices(session, rulesState).ApplyRules(scene, 0);
            require(scene.GameState.TimeGoal == 90 && scene.GameState.Mode == GameMode.Defender,
                "time-scored replica mode adopts its time goal");
            string empty = ReplayStateHash.Compute(scene, 0);
            var beam = new BeamProjectileEntity(scene) { Position = new(1, 2, 3), Velocity = Vector3.UnitX,
                Owner = scene.Players.Items[0], Lifespan = 3, Damage = 12 };
            scene.InsertEntity(beam);
            string original = ReplayStateHash.Compute(scene, 0);
            require(original != empty, "gameplay hash covers projectile membership");
            beam.Position += Vector3.UnitX;
            require(ReplayStateHash.Compute(scene, 0) != original, "gameplay hash covers projectile position");
            beam.Position -= Vector3.UnitX; beam.Velocity *= 2;
            require(ReplayStateHash.Compute(scene, 0) != original, "gameplay hash covers projectile velocity");
            beam.Velocity /= 2;
            require(ReplayStateHash.Compute(scene, 0) == original, "stable projection has no object-identity component");
            string presentation = scene.ReplayPresentationHash(0);
            beam.Color = Vector3.One; beam.PastPositions[0] = new(4, 5, 6);
            require(scene.ReplayPresentationHash(0) != presentation && ReplayStateHash.Compute(scene, 0) == original,
                "trail and color divergence is checked separately from gameplay");
            scene.RemoveEntity(beam);
        }
        finally { scene.DoCleanup(); }
    }
}
