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
            Match = new MatchDefinition { RoomKey = match.RoomKey, DisablePowerups = true } };
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

        using var session = new ReplayPlaybackSession(new PassiveReplaySessionHost());
        var scene = new Scene(new(256, 192), SyntheticInput.CreateKeyboard(), SyntheticInput.CreateMouse(),
            _ => { }, () => { }, new ReplaySceneServices(session, new ReplayReplicaState()), initializeRuntime: false);
        try
        {
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
