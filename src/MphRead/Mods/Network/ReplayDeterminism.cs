using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using MphRead.Entities;
using MphRead.Mods.Input;
using OpenTK.Mathematics;

namespace MphRead.Mods.Network
{
    internal static class ReplayDeterminism
    {
        private static readonly Dictionary<uint, string[]> Baselines = new();
        private static readonly List<string> Fields = new();
        public static int Run(string path, string? hashOutput = null)
        {
            Headless.Enter();
            Baselines.Clear();
            string tracePath = Path.Combine(Path.GetTempPath(), "fruity-replay-trace-" + Guid.NewGuid().ToString("N"));
            var expectedHashes = new List<ReplayExpectedHash>();
            try
            {
                using var trace = new FileStream(tracePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                    64 * 1024, FileOptions.DeleteOnClose);
                using var reader = DemoReader.Open(path);
                if (reader == null) throw new InvalidDataException("Replay could not be opened.");
                uint duration = reader.FormatVersion == 3 ? reader.DurationFrames : DemoLibrary.Duration(path);
                var random = new Random(2718);
                uint[] targets = new[] { 0u, Math.Min(60u, duration), Math.Min(300u, duration), Math.Min(900u, duration), duration / 3, duration * 2 / 3, duration }
                    .Concat(Enumerable.Range(0, 8).Select(_ => (uint)random.NextInt64(duration + 1L))).Distinct().OrderBy(f => f).ToArray();
                Dictionary<uint, string> baseline = Linear(path, targets, trace, expectedHashes);
                VerifyDivergenceProbe(path, trace, Math.Min(300u, duration));
                foreach (uint target in targets)
                {
                    Scene scene = Build(path);
                    try
                    {
                        CompareTrace(trace);
                        ReplayController.ContinueSeek(target, resume: false);
                        int intervals = 0;
                        while (ReplayController.IsSeeking && intervals++ <= target / 32 + 2) scene.OnSimulationFrame();
                        string hash = Hash(scene);
                        if (hash != baseline[target])
                        {
                            string[] before = Baselines[target];
                            for (int i = 0, shown = 0; i < Math.Min(before.Length, Fields.Count) && shown < 20; i++)
                                if (before[i] != Fields[i]) { Console.WriteLine($"[replaydeterminism] {before[i]} -> {Fields[i]}"); shown++; }
                            throw new InvalidOperationException($"Reset-forward differs at frame {target}: {hash} vs {baseline[target]}");
                        }
                        Console.WriteLine($"[replaydeterminism] frame {target}: linear/reset-forward match {hash}");
                    }
                    finally { Cleanup(scene); }
                }
                foreach (float rate in new[] { 0.25f, 0.5f, 2f, 4f })
                {
                    Scene scene = Build(path);
                    try
                    {
                        CompareTrace(trace);
                        ReplayController.SetPlaybackRate(rate);
                        while (!DemoPlayback.AtEnd) scene.OnSimulationFrame();
                        if (Hash(scene) != baseline[duration]) throw new InvalidOperationException($"{rate}x final state differs");
                        ReplayController.Pause();
                        string before = Hash(scene);
                        for (int i = 0; i < 20; i++) scene.OnSimulationFrame();
                        if (Hash(scene) != before) throw new InvalidOperationException("Stopped replay advanced");
                        Console.WriteLine($"[replaydeterminism] {rate}x final state and frozen end match");
                    }
                    finally { Cleanup(scene); }
                }
                if (hashOutput != null)
                {
                    ReplayOpenResult saved = ReplayArchive.WithExpectedHashes(path, hashOutput, expectedHashes);
                    if (saved != ReplayOpenResult.Success) throw new IOException($"Could not save reference hashes: {saved}");
                    Console.WriteLine($"[replaydeterminism] Wrote {expectedHashes.Count} expected gameplay hashes to {hashOutput}");
                }
                Console.WriteLine("[replaydeterminism] PASS: every gameplay frame matches; sampled engine scalar state, transforms, score, timers and packet counts match.");
                return 0;
            }
            catch (Exception ex) { Console.WriteLine($"[replaydeterminism] FAIL: {ex}"); return 1; }
            finally { ReplayVerification.ObserveFrame = null; Baselines.Clear(); Fields.Clear(); }
        }
        private static Dictionary<uint, string> Linear(string path, uint[] targets, FileStream trace, List<ReplayExpectedHash> expectedHashes)
        {
            Scene scene = Build(path);
            try
            {
                uint lastTarget = targets.Max();
                ReplayVerification.ObserveFrame = current =>
                {
                    uint frame = DemoPlayback.CurrentFrame;
                    string hash = ReplayStateHash.Compute(current);
                    // Disk-backed trace keeps verifier RAM independent of replay duration.
                    // It enables exact first-divergence reporting even inside a seek batch.
                    if (trace.Position != frame * 32L) throw new InvalidOperationException($"Missing/duplicate simulation frame {frame}");
                    trace.Write(Convert.FromHexString(hash));
                    if (frame % 300 == 0 || frame == lastTarget) expectedHashes.Add(new(frame, hash));
                };
                var hashes = new Dictionary<uint, string>();
                do
                {
                    scene.OnSimulationFrame();
                    if (targets.Contains(DemoPlayback.CurrentFrame))
                    {
                        hashes[DemoPlayback.CurrentFrame] = Hash(scene);
                        Baselines[DemoPlayback.CurrentFrame] = Fields.ToArray();
                    }
                }
                while (!DemoPlayback.AtEnd && DemoPlayback.CurrentFrame < targets.Max());
                if (DemoPlayback.LastResult != ReplayOpenResult.Success) throw new InvalidDataException(DemoPlayback.LastError);
                trace.Flush();
                return hashes;
            }
            finally { Cleanup(scene); }
        }
        private static void CompareTrace(FileStream trace)
        {
            trace.Position = 0;
            ReplayVerification.ObserveFrame = scene =>
            {
                uint frame = DemoPlayback.CurrentFrame;
                if (trace.Position != frame * 32L) throw new InvalidOperationException($"Missing/duplicate simulation frame {frame}");
                Span<byte> expected = stackalloc byte[32];
                trace.ReadExactly(expected);
                string actual = ReplayStateHash.Compute(scene);
                if (!Convert.FromHexString(actual).AsSpan().SequenceEqual(expected))
                    throw new InvalidOperationException($"First gameplay divergence at frame {frame}: expected {Convert.ToHexString(expected)}, got {actual}");
            };
        }
        private static void VerifyDivergenceProbe(string path, FileStream trace, uint frame)
        {
            long offset = frame * 32L;
            trace.Position = offset;
            int original = trace.ReadByte();
            if (original < 0) throw new InvalidDataException("Missing baseline frame.");
            Scene scene = Build(path);
            try
            {
                trace.Position = offset;
                trace.WriteByte((byte)(original ^ 1));
                CompareTrace(trace);
                ReplayController.ContinueSeek(frame, resume: false);
                try
                {
                    while (ReplayController.IsSeeking) scene.OnSimulationFrame();
                }
                catch (InvalidOperationException ex) when (ex.Message.StartsWith($"First gameplay divergence at frame {frame}: ", StringComparison.Ordinal))
                {
                    Console.WriteLine($"[replaydeterminism] injected reference mismatch correctly identified first frame {frame}");
                    return;
                }
                throw new InvalidOperationException("The verifier did not detect an injected reference mismatch.");
            }
            finally
            {
                trace.Position = offset;
                trace.WriteByte((byte)original);
                trace.Flush();
                Cleanup(scene);
            }
        }
        private static Scene Build(string path)
        {
            if (!DemoPlayback.Join(path)) throw new InvalidDataException(DemoPlayback.LastError);
            var room = NetLaunch.ServerRoom() ?? throw new InvalidDataException("No room.");
            PlayerEntity.MaxPlayers = PlayerEntity.SlotCapacity;
            var scene = new Scene(new Vector2i(256, 192), SyntheticInput.CreateKeyboard(), SyntheticInput.CreateMouse(), _ => { }, () => { });
            NetLaunch.BuildPlayers(scene, Hunter.Samus, 0, GameState.IsTeamMode(room.Mode), localSlot: -1);
            scene.AddRoom(room.RoomKey, room.Mode, playerCount: NetLaunch.RoomPlayerCount);
            scene.OnLoad();
            return scene;
        }
        private static void Cleanup(Scene scene)
        {
            ReplayVerification.ObserveFrame = null;
            scene.DoCleanup();
            DemoPlayback.Stop();
            NetSession.Stop();
        }
        private static string Hash(Scene scene)
        {
            Fields.Clear();
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write(scene.FrameCount);
            writer.Write(scene.GlobalElapsedTime);
            writer.Write(GameState.MatchTime);
            writer.Write((int)GameState.MatchState);
            foreach (int value in GameState.Points) writer.Write(value);
            foreach (int value in GameState.Kills) writer.Write(value);
            foreach (int value in GameState.Deaths) writer.Write(value);
            writer.Write(NetSession.SnapshotsReceived);
            writer.Write(NetSession.IntentsReceived);
            foreach (EntityBase entity in scene.Entities)
            {
                writer.Write(entity.GetType().FullName!);
                writer.Write(entity.Position.X); writer.Write(entity.Position.Y); writer.Write(entity.Position.Z);
                WriteFields(writer, entity);
            }
            foreach (PlayerEntity player in PlayerEntity.Players) WriteFields(writer, player);
            writer.Flush();
            return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
        }
        // Scalar simulation fields include private health/ammo/cooldown/movement state.
        // Object identities, render models and camera handles are deliberately excluded.
        private static void WriteFields(BinaryWriter writer, object value)
        {
            for (Type? type = value.GetType(); type != null; type = type.BaseType)
            foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).OrderBy(f => f.Name))
            {
                object? item = field.GetValue(value);
                if (item is float or double or bool or Vector3 or Enum or byte or sbyte or short or ushort or int or uint or long or ulong)
                    Fields.Add($"{value.GetType().Name}.{field.Name}={item}");
                if (item is float f) writer.Write(f);
                else if (item is double d) writer.Write(d);
                else if (item is bool b) writer.Write(b);
                else if (item is Vector3 v) { writer.Write(v.X); writer.Write(v.Y); writer.Write(v.Z); }
                else if (item is Enum e) writer.Write(Convert.ToInt64(e));
                else if (item is byte or sbyte or short or ushort or int or uint or long) writer.Write(Convert.ToInt64(item));
                else if (item is ulong ul) writer.Write(ul);
            }
        }
    }
}
