using System;
using System.IO;
using System.Linq;
using MphRead.Entities;
using OpenTK.Mathematics;

namespace MphRead.Mods.Network;

/// <summary>Authority facts deliberately contradict contact inference. Full world,
/// file and frozen-clip restore must retain the accepted result in every mode.</summary>
internal static class ReplayAuthoritySceneCheck
{
    internal static int Run(string directory, string output)
    {
        Headless.Enter(); Directory.CreateDirectory(output);
        try
        {
            int flags = 0, nodes = 0;
            foreach (string mode in new[] { "Capture", "Nodes", "Defender", "PrimeHunter" })
            {
                string source = Path.Combine(directory, mode + ".ppdemo"), target = Path.Combine(output, mode + ".ppdemo");
                using (var reader = DemoReader.Open(source)!)
                using (var replay = new PassiveReplayScene(source, new(256, 192)))
                using (var writer = new ReplayWriterV3(target, reader.Metadata!))
                {
                    DemoRecord? pending = reader.ReadNext();
                    while (replay.Step())
                    {
                        uint frame = replay.Session.CurrentFrame;
                        while (pending is { } packet && packet.Frame <= frame)
                        { writer.WriteRecord(packet.Frame, packet.Data); pending = reader.ReadNext(); }
                        if (frame % 6 != 0) continue;
                        var state = replay.State; var match = state.Match!.Value;
                        var captured = ReplayAuthorityWorld.Capture(replay.Scene, match.MatchId, match.AuthorityEpoch, state.ServerTick);
                        state.TryGetPlayer(0, out var player);
                        var actor = new ReplayActorRef(0, player.SlotGeneration, player.LifeId);
                        bool occupied = frame / 60 % 2 == 0;
                        var world = new ReplayAuthorityWorld
                        {
                            MatchId = match.MatchId, Epoch = match.AuthorityEpoch, Tick = state.ServerTick,
                            Prime = occupied ? actor : new(255, 0, 0), Phase = MatchState.InProgress, MatchTime = captured.MatchTime,
                            TeamPoints = captured.TeamPoints, FlagScores = Enumerable.Repeat((int)(frame / 120), 8).ToArray(),
                            NodesCaptured = Enumerable.Repeat((int)(frame / 180), 8).ToArray(),
                            Flags = captured.Flags.Select(f => f with { Carrier = occupied ? actor : new(255, 0, 0),
                                LastCarrier = actor, Position = player.Position + new Vector3(0, 1.05f, 0), AtBase = false }).ToArray(),
                            Nodes = captured.Nodes.Select(n => n with { Team = (sbyte)(frame / 60 % 4), Capturer = actor,
                                Occupying = 1, Occupants = 1, Progress = .5f, Contested = occupied, InProgress = !occupied }).ToArray(),
                            Pickups = captured.Pickups,
                            Drops = occupied ? [new(ItemType.MissileBig, player.Position + new Vector3(4, 1, 0), 800)] : [],
                            Doors = captured.Doors
                        };
                        flags += world.Flags.Length; nodes += world.Nodes.Length;
                        foreach (var packet in ReplayAuthorityWire.Packets(world)) writer.WriteRecord(frame, packet);
                    }
                }
                using (var replay = new PassiveReplayScene(target, new(256, 192)))
                    while (replay.Step())
                    {
                        if (replay.Session.CurrentFrame % 6 != 0 || replay.State.AuthorityWorld is not { } world) continue;
                        int expected = world.Prime.Resolve(replay.Scene, replay.State)?.SlotIndex ?? -1;
                        if (replay.Scene.GameState.PrimeHunter != expected) throw new InvalidDataException("Accepted Prime state was inferred away.");
                        foreach (var flag in world.Flags)
                            if (replay.Scene.TryGetEntity(flag.Id, out var entity) && entity is OctolithFlagEntity value
                                && (value.Carrier?.SlotIndex ?? -1) != (flag.Carrier.Resolve(replay.Scene, replay.State)?.SlotIndex ?? -1))
                                throw new InvalidDataException("Accepted carrier did not survive contact inference.");
                        foreach (var node in world.Nodes)
                            if (replay.Scene.TryGetEntity(node.Id, out var entity) && entity is NodeDefenseEntity value && value.CurrentTeam != node.Team)
                                throw new InvalidDataException("Accepted node ownership did not survive contact inference.");
                    }
                if (ReplayReplicaCheck.Run(target) != 0 || ReplayLiveCaptureCheck.Run(target) != 0) return 1;
            }
            if (flags == 0 || nodes == 0) throw new InvalidDataException($"Fixture rooms have no objective coverage (flags={flags}, nodes={nodes}).");
            Console.WriteLine($"[replayauthority] PASS: {flags} flag and {nodes} node states, Prime changes and dropped items; accepted facts, file/cache/clip restores and source isolation.");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("[replayauthority] FAIL: " + ex); return 1; }
    }
}
