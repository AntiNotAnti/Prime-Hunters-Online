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
        ReplayPerfTelemetry.Enabled = true;
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
            using (var bound = Replay.ReplayWorldCheckpoint.Capture(capture.World!))
            using (var fallback = Replay.ReplayWorldCheckpoint.Capture(capture.World!, boundAccessors: false))
                if (!bound.Bytes.SequenceEqual(fallback.Bytes)) throw new InvalidDataException("Bound capture changed checkpoint bytes.");
            ReplayPerformanceChecks.CheckScene(capture.World!.Scene);
            for (int warm = 0; warm < 5; warm++) { using var checkpoint = Replay.ReplayWorldCheckpoint.Capture(capture.World!); }
            long captureStart = System.Diagnostics.Stopwatch.GetTimestamp();
            long captureAllocations = GC.GetAllocatedBytesForCurrentThread();
            for (int sample = 0; sample < 100; sample++) { using var checkpoint = Replay.ReplayWorldCheckpoint.Capture(capture.World!); }
            Console.WriteLine($"[replayperf-warm] checkpoint={System.Diagnostics.Stopwatch.GetElapsedTime(captureStart).TotalMilliseconds / 100:F3}ms allocation={(GC.GetAllocatedBytesForCurrentThread() - captureAllocations) / 100}B");
            uint end = frame - 1;
            uint start = end - 250;
            if (!recorder.Timeline.TryFreeze(start, end, out var clip) || clip == null
                || clip.RestorePoint.Kind != ReplayRestoreKind.ReplicaCheckpoint)
                throw new InvalidDataException("Live world did not produce a restorable clip.");
            long bytes = recorder.Timeline.PayloadBytes;
            Console.WriteLine(ReplayPerfTelemetry.Summary(recorder.Timeline));
            capture.SetEnabled(false); capture.Advance(frame, live.Size);
            if (capture.World != null || recorder.Timeline.RecordCount != 0)
                throw new InvalidDataException("Disabled replay reconstruction retained a world/history.");
            capture.SetEnabled(true); capture.Advance(frame + 1, live.Size);
            if (capture.World == null || capture.LastError != null || recorder.Timeline.FirstRecordingFrame != frame + 1)
                throw new InvalidDataException("Re-enabled replay reconstruction did not seed a fresh boundary.");
            recorder.Reset(); // the playing clip must outlive a live match transition
            using var frozenLease = clip;
            using var player = new PassiveReplayPlayer(clip, new Vector2i(256, 192));
            if (player.Update(maximumSteps: 24, maximumMilliseconds: 0) > 1)
                throw new InvalidDataException("Clip warmup ignored its elapsed-time budget.");
            int comparisons = 0;
            while (!player.Current.Session.AtEnd || !player.Ready)
            {
                if (player.Update(maximumSteps: 24, maximumMilliseconds: 1) > 24) throw new InvalidDataException("Unbounded clip warmup.");
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
            string directory = Path.Combine(Path.GetTempPath(), "prime-world-clips-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string saved = Path.Combine(directory, "clip.ppdemo");
                ReplayTimelineArchive.Save(clip, player.Current, saved);
                CompareFile(saved, start, 250);
                string subrange = Path.Combine(directory, "subrange.ppdemo");
                if (ReplayArchive.Extract(saved, 40, 120, subrange) != ReplayOpenResult.Success)
                    throw new InvalidDataException("Could not extract a durable world subrange.");
                CompareFile(subrange, start + 40, 80);
                string nested = Path.Combine(directory, "nested.ppdemo");
                if (ReplayArchive.Extract(subrange, 10, 30, nested) != ReplayOpenResult.Success)
                    throw new InvalidDataException("Could not extract a nested world range.");
                CompareFile(nested, start + 50, 20);
                Console.WriteLine("[replaylive] Durable v4 clip and two nested ranges preserve every gameplay/presentation frame, exact frame zero, EOF and backward seek.");
                string legacyRange = Path.Combine(directory, "v3-range.ppdemo");
                if (ReplayArchive.Extract(path, 400, 500, legacyRange) != ReplayOpenResult.Success
                    || ReplayClipFidelity.Run(path, legacyRange, 400) != 0)
                    throw new InvalidDataException("V3 source extraction changed its world.");
                string v2 = Path.Combine(directory, "legacy-v2.ppdemo");
                using (var original = DemoReader.Open(path)!)
                using (var writer = new DemoWriter(v2))
                {
                    foreach (var packet in original.Metadata!.Bootstrap.Packets) writer.WriteRecord(0, packet);
                    while (original.ReadNext() is { } record) writer.WriteRecord(record.Frame, record.Data);
                }
                foreach (uint begin in new uint[] { 0, 400 })
                {
                    string range = Path.Combine(directory, $"v2-range-{begin}.ppdemo");
                    if (ReplayArchive.Extract(v2, begin, begin + 50, range) != ReplayOpenResult.Success
                        || ReplayClipFidelity.Run(v2, range, begin) != 0)
                        throw new InvalidDataException("V2 compatibility extraction changed its world.");
                }
            }
            finally { Directory.Delete(directory, recursive: true); }

            void CompareFile(string file, uint origin, uint duration)
            {
                using var disk = new PassiveReplayPlayer(file, new Vector2i(256, 192));
                while (!disk.Ready) disk.Update();
                if (disk.Current.Session.CurrentFrame != 0 || disk.Current.Session.LastFrame != duration)
                    throw new InvalidDataException("Durable clip range is not normalized.");
                do
                {
                    uint recorded = disk.Current.Session.CurrentFrame + origin;
                    if (ReplayStateHash.Compute(disk.Current.Scene, recorded) != reference[recorded].Gameplay
                        || disk.Current.Scene.ReplayPresentationHash(recorded) != reference[recorded].Presentation)
                        throw new InvalidDataException($"Durable clip differs at {recorded}.");
                    if (disk.Current.Session.AtEnd) break;
                    disk.Update();
                } while (true);
                disk.Seek(0);
                while (!disk.Ready) disk.Update();
                if (ReplayStateHash.Compute(disk.Current.Scene, origin) != reference[origin].Gameplay)
                    throw new InvalidDataException("Durable clip backward seek differs.");
            }
            if (ReplayStateHash.Compute(live, 0) != sentinel || !ReferenceEquals(GameState.Current, live.GameState))
                throw new InvalidDataException("Live clip playback changed its foreground owner.");
            Console.WriteLine($"[replaylive] PASS: {reference.Count} accepted-fact frames, {capture.CaptureCount} world checkpoints, {bytes} timeline bytes, {comparisons} frozen frame comparisons, backward seek and match-reset isolation. Last capture {capture.LastCaptureMilliseconds:F2} ms; replica tick {capture.LastStepMilliseconds:F3} ms.");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("[replaylive] FAIL: " + ex); return 1; }
        finally { live.DoCleanup(); }
    }

    internal static void Accept(ReplayRecorder recorder, ReadOnlySpan<byte> packet, uint frame)
    {
        switch ((PacketType)packet[0])
        {
            case PacketType.ReplayWorld: recorder.AcceptWorldPacket(packet[1..], frame); break;
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
