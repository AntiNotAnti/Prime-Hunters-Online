using System;
using System.Buffers.Binary;
using System.Net;
using System.Reflection;
using MphRead.Entities;
using OpenTK.Mathematics;
namespace MphRead.Mods.Network;

/// <summary>Asset-backed frozen bootstrap application through the live decoder.
/// UDP loss/retry is covered separately by the transport/lifecycle suites.</summary>
public static class NetBootstrapCheck
{
    public static int Run(string room)
    {
        var sim = new ServerSim(); byte[] canonical = new byte[NetConfig.MaxPacketSize]; int length = 0;
        if (!sim.Start(room, GameMode.Battle, 8, payload => { payload.CopyTo(canonical); length = payload.Length; }, () => { })) return 1;
        try
        {
            static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
            NetSession.ApplyMatchState(new MatchStatePacket { MatchId = 1, AuthorityEpoch = 1, RoomKey = room, Mode = (byte)GameMode.Battle }, false);
            var roster = RosterPacket.Create(); roster.MatchId = 1; roster.AuthorityEpoch = 1; roster.Revision = 1; roster.Count = 8;
            for (byte i = 0; i < 8; i++) { roster.Slots[i] = i; roster.Generations[i] = 1; roster.Hunters[i] = (byte)(i % 7); roster.Names[i] = $"BOOT{i}"; }
            var session = new SessionStatePacket { MatchId = 1, AuthorityEpoch = 1, StartGeneration = 1,
                Phase = SessionPhase.Starting, StartStage = StartStage.Synchronizing, ExpectedParticipants = 255,
                LoadedParticipants = 255, MaxPlayers = 8, Revision = 1,
                Match = new MatchDefinition { RoomKey = room, Mode = GameMode.Battle } };
            NetSession.ApplySessionState(session);
            NetSession.ApplyRoster(roster); NetSlotManager.Sync();
            foreach (var player in PlayerEntity.Players) player.ModBootstrapSpawn();
            PlayerEntity.Players[0].ModArmWeapon(BeamType.Missile);
            PlayerEntity.Players[0].Health = 77; GameState.Points[0] = 17;
            foreach (int slot in new[] { 1, 6 }) { PlayerEntity.Players[slot].ModStartFormSwitch(); PlayerEntity.Players[slot].ModForceForm(true); }
            NetSession.BroadcastSnapshot();
            Check(length > 0 && SnapshotHeader.Read(canonical).Frame != 0, "authority baseline exists while frozen");
            var lanes = new NetReplicationLanes(); lanes.Prepare(canonical.AsSpan(0, length));
            var identity = new WorldBootstrapIdentity(new(1, 1, 1), 1, 1, SnapshotHeader.Read(canonical).Frame, lanes.SlowRevision, lanes.WorldRevision);
            var flags = BindingFlags.NonPublic | BindingFlags.Static;
            typeof(NetSession).GetProperty(nameof(NetSession.Role))!.SetValue(null, NetRole.Client);
            typeof(NetSession).GetProperty(nameof(NetSession.IsAuthority))!.SetValue(null, false);
            typeof(NetSession).GetProperty(nameof(NetSession.LocalSlot))!.SetValue(null, 0);
            typeof(NetSession).GetField("_loadedStart", flags)!.SetValue(null, identity.Start);
            typeof(NetSession).GetField("_loadedSlot", flags)!.SetValue(null, 0);
            typeof(NetSession).GetField("_loadedSlotGeneration", flags)!.SetValue(null, (ushort)1);
            typeof(NetSession).GetField("_appliedBootstrap", flags)!.SetValue(null, null);
            uint frameBefore = NetSession.NetFrame;
            var position = PlayerState.Read(canonical.AsSpan(SnapshotHeader.Size)).Position;
            PlayerEntity.Players[0].Position += new Vector3(100, 100, 100);
            PlayerEntity.Players[0].ModArmWeapon(BeamType.PowerBeam); PlayerEntity.Players[0].Health = 60;
            GameState.Points[0] = 0;
            foreach (int slot in new[] { 1, 6 }) { PlayerEntity.Players[slot].ModStartFormSwitch(); PlayerEntity.Players[slot].ModForceForm(false); }
            void Deliver(int lane, WorldBootstrapIdentity stamp, bool malformed = false)
            {
                var data = lane == 0 ? lanes.Fast.AsSpan(0, lanes.FastLength) : lane == 1
                    ? lanes.Slow.AsSpan(0, lanes.SlowLength) : lanes.World.AsSpan(0, lanes.WorldLength);
                byte[] wire = new byte[2 + WorldBootstrapIdentity.Size + data.Length + (malformed ? 600 : 0)]; wire[0] = (byte)PacketType.WorldBootstrap;
                stamp.Write(wire.AsSpan(1)); wire[1 + WorldBootstrapIdentity.Size] = (byte)lane;
                data.CopyTo(wire.AsSpan(2 + WorldBootstrapIdentity.Size));
                typeof(NetSession).GetMethod("HandleWorldBootstrap", flags)!.Invoke(null,
                    new object[] { new ReceivedPacket(new IPEndPoint(IPAddress.Loopback, 1), wire, wire.Length) });
            }
            Deliver(0, identity with { Start = new(1, 1, 2) });
            Check(!NetSession.WorldIsReady && NetSession.FreezeGameplay, "wrong start cannot initialize/release client");
            typeof(NetSession).GetField("_hasRoster", flags)!.SetValue(null, false);
            Deliver(0, identity); Deliver(1, identity); Deliver(2, identity);
            Check(!NetSession.WorldIsReady, "roster is required before applying the world");
            typeof(NetSession).GetField("_hasRoster", flags)!.SetValue(null, true);
            Deliver(2, identity); Deliver(2, identity, malformed: true); Deliver(0, identity); Deliver(0, identity);
            Check(!NetSession.WorldIsReady, "missing slow baseline keeps client frozen despite duplicates/reordering");
            Deliver(1, identity);
            Check(NetSession.WorldIsReady && NetSession.FreezeGameplay, "complete baseline applied, countdown still required");
            Check(NetSession.AppliedSnapshotFrame == identity.AuthorityFrame && NetSession.NetFrame == frameBefore,
                "authoritative ACK initialized without advancing simulation");
            Check(PlayerEntity.Players[0].Position == position, "owner starts at authoritative spawn");
            Check(PlayerEntity.Players[0].Health == 77 && PlayerEntity.Players[0].CurrentWeapon == BeamType.Missile
                && GameState.Points[0] == 17 && PlayerEntity.Players[1].IsAltForm && !PlayerEntity.Players[1].IsMorphing && PlayerEntity.Players[6].IsAltForm
                && PlayerEntity.Players[6].Flags2.TestFlag(PlayerFlags2.Halfturret),
                "health, weapon, score and settled remote alt form applied before WorldReady");
            var appliedHeader = SnapshotHeader.Read(canonical);
            Check(Rng.Rng1 == appliedHeader.Rng1 && Rng.Rng2 == appliedHeader.Rng2, "bootstrap preserves authoritative random state");
            Deliver(2, identity);
            Check(PlayerEntity.Players[0].Position == position, "duplicate bootstrap idempotent");
            // Late-join release and a newer life can arrive in one loading
            // pump. There is no simulation step before its first rendered view.
            session.Phase = SessionPhase.InMatch; session.Revision++; NetSession.ApplySessionState(session);
            var nextHeader = SnapshotHeader.Read(canonical); nextHeader.Frame++;
            nextHeader.Write(canonical);
            var nextLife = PlayerState.Read(canonical.AsSpan(SnapshotHeader.Size + 2 * PlayerState.Size));
            nextLife.LifeId++; nextLife.Position += new Vector3(30, 0, 0);
            nextLife.Write(canonical.AsSpan(SnapshotHeader.Size + 2 * PlayerState.Size));
            lanes.Prepare(canonical.AsSpan(0, length));
            byte[] fast = new byte[1 + lanes.FastLength]; fast[0] = (byte)PacketType.SnapshotFast;
            lanes.Fast.AsSpan(0, lanes.FastLength).CopyTo(fast.AsSpan(1));
            typeof(NetSession).GetMethod("HandleLane", flags)!.Invoke(null,
                new object[] { new ReceivedPacket(new IPEndPoint(IPAddress.Loopback, 1), fast, fast.Length) });
            Check(PlayerEntity.Players[2].Position != nextLife.Position, "new life waits for scene application");
            NetSession.PumpLoading();
            Check(PlayerEntity.Players[2].Position == nextLife.Position && NetSession.NetFrame == frameBefore
                && NetSession.AppliedSnapshotFrame == nextHeader.Frame,
                "release pump applies newer spawn before drawing without simulating");
            var host = new IPEndPoint(IPAddress.Loopback, 1);
            typeof(NetSession).GetField("_hostEndPoint", flags)!.SetValue(null, host);
            byte[] welcome = new byte[18]; welcome[0] = (byte)PacketType.Welcome;
            BinaryPrimitives.WriteUInt32LittleEndian(welcome.AsSpan(2), NetSession.ClientId);
            BinaryPrimitives.WriteUInt16LittleEndian(welcome.AsSpan(6), 1);
            BinaryPrimitives.WriteUInt64LittleEndian(welcome.AsSpan(8), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(welcome.AsSpan(16), 2);
            void Admit() => typeof(NetSession).GetMethod("Handle", flags)!.Invoke(null,
                new object[] { new ReceivedPacket(host, welcome, welcome.Length), NetSession.Clock });
            Admit();
            Check(!NetSession.WorldIsReady && NetSession.FreezeGameplay
                && (ushort)typeof(NetSession).GetField("_loadedSlotGeneration", flags)!.GetValue(null)! == 2,
                "re-admission reports the loaded scene for the new occupant and requires a new bootstrap");
            var newOwner = PlayerState.Read(canonical.AsSpan(SnapshotHeader.Size)); newOwner.SlotGeneration = 2;
            newOwner.Write(canonical.AsSpan(SnapshotHeader.Size)); lanes.Prepare(canonical.AsSpan(0, length));
            var readmitted = identity with { Revision = 2, SlotGeneration = 2, AuthorityFrame = nextHeader.Frame,
                SlowRevision = lanes.SlowRevision, WorldRevision = lanes.WorldRevision };
            Deliver(0, readmitted); Deliver(1, readmitted); Deliver(2, readmitted);
            Check(NetSession.WorldIsReady && !NetSession.FreezeGameplay, "same-match re-admission resumes after its new bootstrap");
            Admit();
            Check(NetSession.WorldIsReady, "duplicate same-generation Welcome does not restart bootstrap");
            session.Phase = SessionPhase.Starting;
            session.StartGeneration++; session.Revision++; NetSession.ApplySessionState(session);
            Deliver(0, identity); Deliver(1, identity); Deliver(2, identity);
            Check(!NetSession.WorldIsReady && NetSession.FreezeGameplay, "old bootstrap cannot release rematch");
            Console.WriteLine("BOOTSTRAP PASS: eight real players, frozen spawn, lane loss/reorder/duplicate, valid first ACK and rematch fence"); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { sim.Stop(); }
    }
}
