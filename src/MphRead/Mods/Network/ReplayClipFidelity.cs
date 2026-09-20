using System;
using System.IO;
using MphRead.Entities;
using MphRead.Mods.Input;
using OpenTK.Mathematics;

namespace MphRead.Mods.Network
{
    /// <summary>
    /// Verifies that an extracted replay clip reproduces the same explicit gameplay
    /// projection as its source range. Source frame <paramref name="sourceStart"/>
    /// is compared to clip frame 0, so replay-local frame numbers are normalized
    /// before hashing.
    /// </summary>
    internal static class ReplayClipFidelity
    {
        public static int Run(string source, string clip, uint sourceStart)
        {
            Headless.Enter();
            string tracePath = Path.Combine(Path.GetTempPath(),
                "fruity-clip-fidelity-" + Guid.NewGuid().ToString("N"));
            try
            {
                using var clipReader = DemoReader.Open(clip, out ReplayOpenResult clipOpen,
                    metadataOnly: true);
                if (clipReader == null)
                    throw new InvalidDataException($"Clip could not be opened: {clipOpen}.");
                if (clipReader.Metadata?.Type != ReplayType.Clip)
                    throw new InvalidDataException("The comparison file is not marked as a replay clip.");
                uint duration = clipReader.DurationFrames;
                ulong end64 = (ulong)sourceStart + duration;
                if (end64 > UInt32.MaxValue)
                    throw new InvalidDataException("Source range overflows the replay frame counter.");
                uint sourceEnd = (uint)end64;

                using var sourceReader = DemoReader.Open(source, out ReplayOpenResult sourceOpen,
                    metadataOnly: true);
                if (sourceReader == null)
                    throw new InvalidDataException($"Source could not be opened: {sourceOpen}.");
                uint sourceDuration = sourceReader.FormatVersion == 3
                    ? sourceReader.DurationFrames : DemoLibrary.Duration(source);
                if (sourceEnd > sourceDuration)
                    throw new InvalidDataException(
                        $"Clip range ends at source frame {sourceEnd}, beyond source duration {sourceDuration}.");

                using var trace = new FileStream(tracePath, FileMode.CreateNew,
                    FileAccess.ReadWrite, FileShare.None, 64 * 1024,
                    FileOptions.DeleteOnClose);

                CaptureSource(source, sourceStart, sourceEnd, trace);
                CompareClip(clip, duration, sourceStart, trace);

                Console.WriteLine($"[replayclipcheck] PASS: clip frames 0-{duration} "
                    + $"match source frames {sourceStart}-{sourceEnd}.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[replayclipcheck] FAIL: {ex}");
                return 1;
            }
            finally
            {
                ReplayVerification.ObserveFrame = null;
                DemoPlayback.Stop();
                NetSession.Stop();
            }
        }

        private static void CaptureSource(string path, uint start, uint end, FileStream trace)
        {
            Scene scene = Build(path);
            try
            {
                ReplayVerification.ObserveFrame = current =>
                {
                    uint frame = DemoPlayback.CurrentFrame;
                    if (frame < start || frame > end) return;
                    uint normalized = frame - start;
                    long offset = normalized * 32L;
                    if (trace.Position != offset)
                        throw new InvalidOperationException(
                            $"Source trace skipped/duplicated frame {frame} (clip frame {normalized}).");
                    trace.Write(Convert.FromHexString(
                        ReplayStateHash.Compute(current, normalized)));
                };

                do
                {
                    scene.OnSimulationFrame();
                }
                while (!DemoPlayback.AtEnd && DemoPlayback.CurrentFrame < end);

                if (DemoPlayback.CurrentFrame < end)
                    throw new InvalidDataException(
                        $"Source ended at frame {DemoPlayback.CurrentFrame}, expected {end}.");
                if (DemoPlayback.LastResult != ReplayOpenResult.Success)
                    throw new InvalidDataException(DemoPlayback.LastError);
                trace.Flush();
                long expectedBytes = ((long)(end - start) + 1) * 32;
                if (trace.Length != expectedBytes)
                    throw new InvalidOperationException(
                        $"Source trace contains {trace.Length / 32} frames; expected {end - start + 1}.");
            }
            finally
            {
                Cleanup(scene);
            }
        }

        private static void CompareClip(string path, uint duration, uint sourceStart,
            FileStream trace)
        {
            Scene scene = Build(path);
            try
            {
                trace.Position = 0;
                ReplayVerification.ObserveFrame = current =>
                {
                    uint frame = DemoPlayback.CurrentFrame;
                    if (frame > duration) return;
                    long offset = frame * 32L;
                    if (trace.Position != offset)
                        throw new InvalidOperationException(
                            $"Clip trace skipped/duplicated frame {frame}.");
                    Span<byte> expected = stackalloc byte[32];
                    trace.ReadExactly(expected);
                    string actual = ReplayStateHash.Compute(current, frame);
                    byte[] actualBytes = Convert.FromHexString(actual);
                    if (!actualBytes.AsSpan().SequenceEqual(expected))
                    {
                        throw new InvalidOperationException(
                            $"First clip fidelity divergence: source frame "
                            + $"{sourceStart + frame}, clip frame {frame}; expected "
                            + $"{Convert.ToHexString(expected)}, got {actual}.");
                    }
                };

                do
                {
                    scene.OnSimulationFrame();
                }
                while (!DemoPlayback.AtEnd && DemoPlayback.CurrentFrame < duration);

                if (DemoPlayback.CurrentFrame < duration)
                    throw new InvalidDataException(
                        $"Clip ended at frame {DemoPlayback.CurrentFrame}, expected {duration}.");
                if (DemoPlayback.LastResult != ReplayOpenResult.Success)
                    throw new InvalidDataException(DemoPlayback.LastError);
            }
            finally
            {
                Cleanup(scene);
            }
        }

        private static Scene Build(string path)
        {
            if (!DemoPlayback.Join(path))
                throw new InvalidDataException(DemoPlayback.LastError);
            var room = NetLaunch.ServerRoom()
                ?? throw new InvalidDataException("Replay has no room.");
            PlayerEntity.MaxPlayers = PlayerEntity.SlotCapacity;
            var scene = new Scene(new Vector2i(256, 192),
                SyntheticInput.CreateKeyboard(), SyntheticInput.CreateMouse(),
                _ => { }, () => { });
            NetLaunch.BuildPlayers(scene, Hunter.Samus, 0,
                GameState.IsTeamMode(room.Mode), localSlot: -1);
            scene.AddRoom(room.RoomKey, room.Mode,
                playerCount: NetLaunch.RoomPlayerCount);
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
    }
}
