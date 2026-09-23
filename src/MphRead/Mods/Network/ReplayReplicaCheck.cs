using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MphRead.Entities;
using MphRead.Mods.Input;
using OpenTK.Mathematics;
#if !ANDROID
using OpenTK.Graphics.OpenGL;
using OpenTK.Windowing.Desktop;
using GLFWBindingsContext = OpenTK.Windowing.GraphicsLibraryFramework.GLFWBindingsContext;
#endif

namespace MphRead.Mods.Network
{
    internal static class ReplayReplicaCheck
    {
        internal static int Run(string path, string? screenshots = null)
        {
#if !ANDROID
            NativeWindow? window = null;
            if (screenshots != null)
            {
                var settings = Render.DesktopGlContext.Settings(background: true);
                settings.ClientSize = new Vector2i(640, 480);
                window = new NativeWindow(settings);
                window.Context.MakeCurrent();
                GL.LoadBindings(new GLFWBindingsContext());
            }
            else
#endif
                Headless.Enter();
            NetSession.StartPlayback();
            var live = new Scene(new Vector2i(256, 192), SyntheticInput.CreateKeyboard(), SyntheticInput.CreateMouse(),
                _ => { }, () => { }, initializeRuntime: false);
            live.GameState.Points[2] = 731;
            live.Random.SetRng1(54321); live.Random.SetRng2(98765);
            NetPlayerBridge.ShootPressAge[2] = 23;
            var foregroundPlayer = PlayerEntity.Players[0];
            var foregroundAudio = global::MphRead.Sound.Sfx.Instance;
            string before = Sentinel(live);
            PassiveReplayScene? restored = null;
            try
            {
                Vector2i size = screenshots != null ? new(640, 480) : new(256, 192);
                using var first = new PassiveReplayScene(path, size);
                using var second = new PassiveReplayScene(path, size);
                ReplayAssetChecks.Run(first, second, path, size);
                var hashes = new Queue<(string Gameplay, string Presentation, ReplayReplicaCheckpoint Decoder)>();
                var drawnCheckpointFrames = new HashSet<uint>();
                var referenceFrames = new Dictionary<uint, (string Gameplay, string Presentation)>();
                Replay.ReplayWorldCheckpoint? clipCheckpoint = null;
                int steps = 0, projectileFrames = 0, modelRestores = 0, worldRestores = 0, continuedFrames = 0;
                bool projectileCheckpoint = false;
                while (first.Step())
                {
                    if (restored != null)
                    {
                        if (!restored.Step() || ReplayStateHash.Compute(restored.Scene, restored.Session.CurrentFrame)
                            != ReplayStateHash.Compute(first.Scene, first.Session.CurrentFrame)
                            || restored.Scene.ReplayPresentationHash(restored.Session.CurrentFrame)
                            != first.Scene.ReplayPresentationHash(first.Session.CurrentFrame))
                            throw new InvalidDataException($"Detached world continuation differs at frame {first.Session.CurrentFrame}.");
                        continuedFrames++;
                    }
                    bool captureProjectile = false;
                    if (!projectileCheckpoint)
                        foreach (EntityBase entity in first.Scene.Entities)
                            if (entity is BeamProjectileEntity or BombEntity) { captureProjectile = true; break; }
                    if (captureProjectile || first.Session.CurrentFrame > 0 && first.Session.CurrentFrame % 300 == 0)
                    {
                        projectileCheckpoint |= captureProjectile;
                        var timer = System.Diagnostics.Stopwatch.StartNew();
                        var checkpoint = Replay.ReplayWorldCheckpoint.Capture(first);
                        using (var reference = Replay.ReplayWorldCheckpoint.Capture(first, boundAccessors: false))
                            if (!checkpoint.Bytes.SequenceEqual(reference.Bytes))
                                throw new InvalidDataException("Bound checkpoint capture changed serialized bytes.");
                        if (checkpoint.Frame == 900) clipCheckpoint = checkpoint;
                        double captureMs = timer.Elapsed.TotalMilliseconds; timer.Restart();
                        restored?.Dispose(); restored = new PassiveReplayScene(path, size);
                        checkpoint.Restore(restored);
                        double restoreMs = timer.Elapsed.TotalMilliseconds;
                        if (ReplayStateHash.Compute(restored.Scene, restored.Session.CurrentFrame)
                            != ReplayStateHash.Compute(first.Scene, first.Session.CurrentFrame)
                            || restored.Scene.ReplayPresentationHash(restored.Session.CurrentFrame)
                            != first.Scene.ReplayPresentationHash(first.Session.CurrentFrame))
                            throw new InvalidDataException($"Detached world restore differs at frame {first.Session.CurrentFrame}.");
                        worldRestores++;
                        if (screenshots != null)
                        {
                            drawnCheckpointFrames.Add(checkpoint.Frame);
                            string linearImage = Path.Combine(screenshots, $"linear-{checkpoint.Frame}.png");
                            string restoredImage = Path.Combine(screenshots, $"restored-{checkpoint.Frame}.png");
                            Draw(first, linearImage); Draw(restored, restoredImage);
                            if (!File.ReadAllBytes(linearImage).SequenceEqual(File.ReadAllBytes(restoredImage)))
                                throw new InvalidDataException($"Detached world picture differs at frame {checkpoint.Frame}.");
                        }
                        Console.WriteLine($"[replayreplica] restored frame {checkpoint.Frame}: {checkpoint.Bytes.Length} bytes; capture {captureMs:F2} ms, create/restore {restoreMs:F2} ms");
                        if (checkpoint != clipCheckpoint) checkpoint.Dispose();
                    }
                    hashes.Enqueue((ReplayStateHash.Compute(first.Scene, first.Session.CurrentFrame),
                        first.Scene.ReplayPresentationHash(first.Session.CurrentFrame), first.State.CaptureCheckpoint()));
                    referenceFrames[first.Session.CurrentFrame] = (ReplayStateHash.Compute(first.Scene, first.Session.CurrentFrame),
                        first.Scene.ReplayPresentationHash(first.Session.CurrentFrame));
                    foreach (EntityBase entity in first.Scene.Entities)
                        if (entity is BeamProjectileEntity or BombEntity) { projectileFrames++; break; }
                    if (++steps % 17 == 0 || first.Session.AtEnd)
                    {
                        while (hashes.TryDequeue(out var expected))
                        {
                            if (!second.Step()) throw new InvalidDataException("Interleaved replay ended early.");
                            if (screenshots != null && drawnCheckpointFrames.Contains(second.Session.CurrentFrame))
                                Draw(second, Path.Combine(screenshots, "interleaved-checkpoint.png"));
                            if (ReplayStateHash.Compute(second.Scene, second.Session.CurrentFrame) != expected.Gameplay)
                                throw new InvalidDataException($"Interleaved replica worlds differ at frame {second.Session.CurrentFrame}.");
                            if (second.Scene.ReplayPresentationHash(second.Session.CurrentFrame) != expected.Presentation)
                                throw new InvalidDataException($"Interleaved replica animation/effects differ at frame {second.Session.CurrentFrame}.");
                            var decoded = new ReplayReplicaState(); decoded.RestoreCheckpoint(expected.Decoder);
                            if (!decoded.CaptureCheckpoint().Bytes.SequenceEqual(second.State.CaptureCheckpoint().Bytes))
                                throw new InvalidDataException($"Detached decoder differs at frame {second.Session.CurrentFrame}.");
                        }
                        foreach (EntityBase entity in second.Scene.Entities)
                            foreach (var model in entity.ReplayModels)
                            {
                                var captured = Replay.ReplayModelCheckpoint.Capture(model);
                                model.AnimInfo.Frame[0] += 7; model.Active = !model.Active;
                                captured.Restore(model);
                                if (!Replay.ReplayModelCheckpoint.Capture(model).Bytes.SequenceEqual(captured.Bytes))
                                    throw new InvalidDataException("Animation checkpoint did not restore its exact values.");
                                modelRestores++;
                            }
                        if (screenshots != null && steps % 170 == 0)
                        {
                            Draw(first, Path.Combine(screenshots, "first.png"));
                            Draw(second, Path.Combine(screenshots, "second.png"));
                            if (restored != null) Draw(restored, Path.Combine(screenshots, "restored.png"));
                        }
                    }
                    if (Sentinel(live) != before || !ReferenceEquals(GameState.Current, live.GameState)
                        || !ReferenceEquals(PlayerEntity.Players[0], foregroundPlayer)
                        || !ReferenceEquals(global::MphRead.Sound.Sfx.Instance, foregroundAudio))
                        throw new InvalidDataException($"Replica changed foreground state at frame {first.Session.CurrentFrame}.");
                }
                if (screenshots != null) Draw(second, Path.Combine(screenshots, "before-dispose.png"));
                first.Dispose();
                if (screenshots != null)
                {
                    Draw(second, Path.Combine(screenshots, "after-dispose.png"));
                    if (!File.ReadAllBytes(Path.Combine(screenshots, "before-dispose.png"))
                        .SequenceEqual(File.ReadAllBytes(Path.Combine(screenshots, "after-dispose.png"))))
                        throw new InvalidOperationException("Disposing a replica changed another scene's picture.");
                }
                second.Dispose();
                using (clipCheckpoint)
                    ReplayReplicaSeekChecks.Run(path, size, referenceFrames, clipCheckpoint, comparePresentation: screenshots == null);
                if (Sentinel(live) != before || !ReferenceEquals(global::MphRead.Sound.Sfx.Instance, foregroundAudio))
                    throw new InvalidDataException("Replica teardown changed foreground state.");
                if (steps == 0) throw new InvalidDataException("No replica frames were simulated.");
                Console.WriteLine($"[replayreplica] PASS: {steps} gameplay/presentation hashes and decoder restores, {modelRestores} animation restores, {worldRestores} detached worlds and {continuedFrames} continuation frames, {projectileFrames} frames with projectiles; interleaved scenes and teardown preserve foreground state.");
                return 0;
            }
            catch (Exception ex) { Console.WriteLine($"[replayreplica] FAIL: {ex}"); return 1; }
            finally
            {
                restored?.Dispose();
                live.DoCleanup(); NetSession.Stop();
#if !ANDROID
                window?.Dispose();
#endif
            }
        }

        private static void Draw(PassiveReplayScene replay, string path)
        {
#if !ANDROID
            var scene = replay.Scene;
            PlayerEntity player = scene.Players.Items[0];
            scene.SetReplicaCamera(player.Position + new Vector3(0, 1.6f, 0),
                player.Position + new Vector3(0, 1.6f, 0) + player.FacingVector, 78);
            string before = ReplayStateHash.Compute(scene, replay.Session.CurrentFrame);
            scene.OnDrawFrame();
            if (!scene.OnRenderFrame()) throw new InvalidOperationException("Replica renderer stopped.");
            if (GL.GetError() != ErrorCode.NoError) throw new InvalidOperationException("Replica draw produced a GL error.");
            if (ReplayStateHash.Compute(scene, replay.Session.CurrentFrame) != before)
                throw new InvalidOperationException("Replica drawing advanced gameplay state.");
            if (!ScreenCapture.Save(scene, path)) throw new IOException("Replica picture was empty.");
#endif
        }

        private static string Sentinel(Scene live) => string.Join('|', ReplayStateHash.Compute(live, 0),
            live.Random.Rng1, live.Random.Rng2, NetSession.Active, NetSession.Role, NetSession.LocalSlot,
            NetSession.CurrentMatchId, NetSession.AuthorityEpoch, NetSession.NetFrame,
            NetSession.SnapshotsReceived, NetSession.SnapshotsSent, NetSession.IntentsReceived,
            NetPlayerBridge.ShootPressAge[2], string.Join(',', NetDamage.PlayerChecks),
            string.Join(',', NetDamage.PlayerOverlaps), NetDamage.BombSpawnCalls,
            NetDamage.ShockCoilSpawned, NetHealthSync.RegisteredSpawns.Count);
    }
}
