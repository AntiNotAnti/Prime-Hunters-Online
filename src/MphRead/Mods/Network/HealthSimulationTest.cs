using System;
using System.Linq;
using MphRead.Entities;
using MphRead.Mods.Input;
using MphRead.Mods.Multiplayer;
using OpenTK.Mathematics;

namespace MphRead.Mods.Network
{
    // Loads actual map entities on an authority and a fresh replica. This
    // exercises the pickup lifecycle in addition to the byte-level tests.
    internal static class HealthSimulationTest
    {
        public static int Run(string room)
        {
            var sim = new ServerSim();
            try
            {
                var session = new SessionStatePacket
                {
                    Phase = SessionPhase.InMatch, Policy = ServerSessionPolicy.Lobby,
                    Revision = 1, MatchId = 1, AuthorityEpoch = 1, MaxPlayers = 8, OwnerSlot = 0,
                    Match = new MatchDefinition { RoomKey = room, Mode = GameMode.Battle,
                        Format = MatchFormat.FreeForAll, PointGoal = 999, TimeLimitSeconds = 600 },
                    WorldProfile = MatchWorldProfile.Resolve(8)
                };
                RosterPacket roster = RosterPacket.Create();
                roster.MatchId = 1; roster.AuthorityEpoch = 1; roster.Revision = 1; roster.SessionRevision = 1;
                for (byte i = 0; i < 8; i++)
                { roster.Slots[i] = i; roster.Generations[i] = 1; roster.Teams[i] = -1; roster.Names[i] = $"Health{i}"; roster.Count++; }
                byte[] latest = [];
                if (!sim.Start(room, GameMode.Battle, 8, payload => latest = payload.ToArray(), () => { }, roster, session))
                    throw new ProgramException("Authority could not load the room.");
                for (int i = 0; i < 180; i++) sim.Step();
                ItemSpawnEntity spawn = NetHealthSync.RegisteredSpawns.First(s => s.Item != null && s.Active);
                int count = NetHealthSync.RegisteredSpawns.Count;
                short id = (short)spawn.Id;
                spawn.Item!.OnPickedUp();
                for (int i = 0; i < 3; i++) sim.Step();
                if (spawn.Item != null) throw new ProgramException("Picked health did not despawn.");
                byte[] unavailable = Tail(latest);
                for (int i = 0; i < 1200 && spawn.Item == null; i++) sim.Step();
                if (spawn.Item == null) throw new ProgramException("Health did not respawn.");
                byte[] available = Tail(latest);
                if (sim.StepFailures != 0) throw new ProgramException("Authority simulation had failed steps.");
                sim.Stop();

                NetSession.StartPlayback();
                NetSession.ApplySessionState(session);
                NetSession.ApplyRoster(roster);
                PlayerEntity.MaxPlayers = 8;
                var scene = new Scene(new Vector2i(256, 192), SyntheticInput.CreateKeyboard(),
                    SyntheticInput.CreateMouse(), _ => { }, () => { });
                NetLaunch.BuildPlayers(scene, Hunter.Samus, 0, localSlot: -1);
                scene.AddRoom(room, GameMode.Battle, playerCount: NetLaunch.RoomPlayerCount);
                scene.OnLoad();
                if (NetHealthSync.RegisteredSpawns.Count != count)
                    throw new ProgramException("Replica constructed different health entities.");
                var replica = NetHealthSync.RegisteredSpawns.Single(s => s.Id == id);
                NetHealthSync.Receive(unavailable);
                for (int i = 0; i < 3; i++) scene.OnSimulationFrame();
                if (replica.Item != null) throw new ProgramException("Replica spawned unavailable health.");
                NetHealthSync.Receive(available);
                for (int i = 0; i < 3; i++) scene.OnSimulationFrame();
                if (replica.Item == null || NetHealthSync.OwnsPickup(replica.Item))
                    throw new ProgramException("Replica did not restore authoritative availability.");
                NetHealthSync.Receive(unavailable);
                for (int i = 0; i < 3; i++) scene.OnSimulationFrame();
                if (replica.Item != null) throw new ProgramException("Replica did not remove consumed health.");
                Console.WriteLine($"[healthsimtest] PASS {room}: {count} health spawners; authority pickup/respawn and replica convergence.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[healthsimtest] FAIL {room}: {ex}");
                return 1;
            }
            finally { sim.Stop(); NetSession.Stop(); Read.ClearCache(); }
        }

        private static byte[] Tail(byte[] snapshot)
        {
            if (snapshot.Length < SnapshotHeader.Size) throw new ProgramException("No authoritative snapshot.");
            int start = SnapshotHeader.Size + SnapshotHeader.Read(snapshot).PlayerCount * PlayerState.Size + NetMatchTimeSync.Size;
            if (snapshot.Length < start || !NetHealthSync.Validate(snapshot.AsSpan(start)))
                throw new ProgramException("Invalid health snapshot tail.");
            return snapshot.AsSpan(start).ToArray();
        }
    }
}
