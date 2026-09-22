using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MphRead.Mods.Input;
using OpenTK.Mathematics;

namespace MphRead.Mods.Network;

internal static class ReplayLiveCaptureCheck
{
    internal static int Run(string path)
    {
        Headless.Enter();
        var live = new Scene(new Vector2i(256, 192), SyntheticInput.CreateKeyboard(), SyntheticInput.CreateMouse(),
            _ => { }, () => { }, initializeRuntime: false);
        live.GameState.Points[0] = 892; live.Random.SetRng1(5343);
        string sentinel = ReplayStateHash.Compute(live, 0);
        var recorder = new ReplayRecorder();
        using var capture = new ReplayLiveWorld(recorder);
        try
        {
            using var reader = DemoReader.Open(path, out var result) ?? throw new InvalidDataException(result.ToString());
            if (reader.Metadata is { } metadata)
                foreach (var packet in metadata.Bootstrap.Packets.OrderBy(p => p[0] == (byte)PacketType.MatchState ? 0 : 1))
                    Accept(recorder, packet, 0);
            DemoRecord? pending = reader.ReadNext();
            var reference = new Dictionary<uint, (string Gameplay, string Presentation)>();
            uint frame = 0;
            while (pending != null)
            {
                while (pending is DemoRecord record && record.Frame <= frame)
                { Accept(recorder, record.Data, frame); pending = reader.ReadNext(); }
                capture.Advance(frame, new Vector2i(256, 192));
                if (capture.LastError != null) throw new InvalidDataException(capture.LastError);
                if (capture.World is { } world)
                    reference[frame] = (ReplayStateHash.Compute(world.Scene, frame), world.Scene.ReplayPresentationHash(frame));
                if (ReplayStateHash.Compute(live, 0) != sentinel || !ReferenceEquals(GameState.Current, live.GameState))
                    throw new InvalidDataException("Live capture changed its foreground owner.");
                frame++;
            }
            if (reference.Count < 600) throw new InvalidDataException("Coverage needs at least ten seconds of accepted facts.");
            uint end = frame - 1;
            uint start = end - 250;
            if (!recorder.Timeline.TryFreeze(start, end, out var clip) || clip == null
                || clip.RestorePoint.Kind != ReplayRestoreKind.ReplicaCheckpoint)
                throw new InvalidDataException("Live world did not produce a restorable clip.");
            long bytes = recorder.Timeline.PayloadBytes;
            recorder.Reset(); // the playing clip must outlive a live match transition
            using var player = new PassiveReplayPlayer(clip, new Vector2i(256, 192));
            int comparisons = 0;
            while (!player.Current.Session.AtEnd || !player.Ready)
            {
                if (player.Update() > PassiveReplayPlayer.MaximumStepsPerUpdate) throw new InvalidDataException("Unbounded clip warmup.");
                if (!player.Ready) continue;
                var world = player.Current;
                var expected = reference[world.Session.CurrentFrame];
                if (ReplayStateHash.Compute(world.Scene, world.Session.CurrentFrame) != expected.Gameplay
                    || world.Scene.ReplayPresentationHash(world.Session.CurrentFrame) != expected.Presentation)
                    throw new InvalidDataException($"Live frozen clip differs at {world.Session.CurrentFrame}.");
                comparisons++;
            }
            player.Seek(start);
            while (!player.Ready) player.Update();
            if (ReplayStateHash.Compute(player.Current.Scene, start) != reference[start].Gameplay)
                throw new InvalidDataException("Live frozen clip backward seek differs.");
            if (ReplayStateHash.Compute(live, 0) != sentinel || !ReferenceEquals(GameState.Current, live.GameState))
                throw new InvalidDataException("Live clip playback changed its foreground owner.");
            Console.WriteLine($"[replaylive] PASS: {reference.Count} accepted-fact frames, {capture.CaptureCount} world checkpoints, {bytes} timeline bytes, {comparisons} frozen frame comparisons, backward seek and match-reset isolation. Last capture {capture.LastCaptureMilliseconds:F2} ms; replica tick {capture.LastStepMilliseconds:F3} ms.");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("[replaylive] FAIL: " + ex); return 1; }
        finally { live.DoCleanup(); }
    }

    private static void Accept(ReplayRecorder recorder, ReadOnlySpan<byte> packet, uint frame)
    {
        switch ((PacketType)packet[0])
        {
            case PacketType.MatchState: recorder.AcceptMatch(MatchStatePacket.Read(packet[1..]), frame); break;
            case PacketType.SessionState:
                if (SessionStatePacket.TryRead(packet[1..], out var config)) recorder.AcceptConfiguration(config, frame);
                break;
            case PacketType.Roster:
                if (RosterPacket.TryRead(packet[1..], out var roster)) recorder.AcceptRoster(roster, frame);
                break;
            case PacketType.Snapshot: recorder.AcceptSnapshot(packet, frame, SnapshotHeader.Read(packet[1..]).Frame); break;
            case PacketType.SlotIntent: recorder.AcceptIntent(packet[1], IntentPacket.Read(packet[2..]), frame); break;
        }
    }
}
