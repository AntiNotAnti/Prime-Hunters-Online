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
            var foregroundAudio = Sound.Sfx.Instance;
            string before = Sentinel(live);
            try
            {
                Vector2i size = screenshots != null ? new(640, 480) : new(256, 192);
                using var first = new PassiveReplayScene(path, size);
                using var second = new PassiveReplayScene(path, size);
                var hashes = new Queue<string>();
                int steps = 0, projectileFrames = 0;
                while (first.Step())
                {
                    hashes.Enqueue(ReplayStateHash.Compute(first.Scene, first.Session.CurrentFrame));
                    foreach (EntityBase entity in first.Scene.Entities)
                        if (entity is BeamProjectileEntity or BombEntity) { projectileFrames++; break; }
                    if (++steps % 17 == 0 || first.Session.AtEnd)
                    {
                        while (hashes.TryDequeue(out string? expected))
                        {
                            if (!second.Step() || ReplayStateHash.Compute(second.Scene, second.Session.CurrentFrame) != expected)
                                throw new InvalidDataException($"Interleaved replica worlds differ at frame {second.Session.CurrentFrame}.");
                        }
                        if (screenshots != null && steps % 170 == 0)
                        {
                            Draw(first, Path.Combine(screenshots, "first.png"));
                            Draw(second, Path.Combine(screenshots, "second.png"));
                        }
                    }
                    if (Sentinel(live) != before || !ReferenceEquals(GameState.Current, live.GameState)
                        || !ReferenceEquals(PlayerEntity.Players[0], foregroundPlayer)
                        || !ReferenceEquals(Sound.Sfx.Instance, foregroundAudio))
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
                if (Sentinel(live) != before || !ReferenceEquals(Sound.Sfx.Instance, foregroundAudio))
                    throw new InvalidDataException("Replica teardown changed foreground state.");
                if (steps == 0) throw new InvalidDataException("No replica frames were simulated.");
                Console.WriteLine($"[replayreplica] PASS: {steps} frames, {projectileFrames} frames with projectiles; two interleaved scenes and teardown preserve foreground state.");
                return 0;
            }
            catch (Exception ex) { Console.WriteLine($"[replayreplica] FAIL: {ex}"); return 1; }
            finally
            {
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
