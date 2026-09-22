using System;
using System.IO;
using MphRead.Entities;
using MphRead.Mods.Input;
using MphRead.Mods.Replay;
using OpenTK.Mathematics;
#if !ANDROID
using OpenTK.Graphics.OpenGL;
using OpenTK.Windowing.Desktop;
using GLFWBindingsContext = OpenTK.Windowing.GraphicsLibraryFramework.GLFWBindingsContext;
#endif

namespace MphRead.Mods.Network;

internal static class ReplayTheatreCheck
{
    internal static int Run(string path, string? shots)
    {
#if !ANDROID
        NativeWindow? window = null;
        if (shots != null)
        {
            var settings = Render.DesktopGlContext.Settings(background: true);
            settings.ClientSize = new Vector2i(640, 480);
            window = new NativeWindow(settings); window.Context.MakeCurrent();
            GL.LoadBindings(new GLFWBindingsContext());
        }
        else
#endif
            Headless.Enter();
        var size = new Vector2i(640, 480);
        var shell = new Scene(size, SyntheticInput.CreateKeyboard(), SyntheticInput.CreateMouse(), _ => { }, () => { }, initializeRuntime: false);
        shell.GameState.Mode = GameMode.Battle; shell.GameState.Points[0] = 987;
        shell.Random.SetRng1(8643);
        NetSession.StartPlayback();
        string sentinel = ReplayStateHash.Compute(shell, 0);
        ushort match = NetSession.CurrentMatchId;
        try
        {
            if (!DemoPlayback.Join(path)) throw new InvalidDataException(DemoPlayback.LastError);
            using var reference = new PassiveReplayPlayer(path, size);
            int checkedFrames = 0;
            for (int i = 0; i < 620 && !DemoPlayback.AtEnd; i++)
            {
                shell.OnSimulationFrame();
                if (DemoPlayback.PresentationScene is not { } scene) continue;
                uint frame = DemoPlayback.CurrentFrame;
                reference.Seek(frame);
                while (!reference.Ready) reference.Update();
                if (ReplayStateHash.Compute(scene, frame) != ReplayStateHash.Compute(reference.Current.Scene, frame))
                {
                    Describe(scene, "theatre"); Describe(reference.Current.Scene, "reference");
                    throw new InvalidDataException($"Theatre simulation differs from passive playback at {frame}.");
                }
                checkedFrames++;
                if (shots != null && i % 100 == 0)
                {
                    shell.OnDrawFrame(); shell.OnRenderFrame();
                    ScreenCapture.SaveWindow(scene, Path.Combine(shots, $"theatre-{i}.png"));
                }
                if (i % 120 == 20) SpectatorMode.CycleNext();
                if (i % 120 == 50) ReplayCamera.SetMode(ReplayCameraMode.Chase);
                if (i % 120 == 70) ReplayCamera.SetMode(ReplayCameraMode.FirstPerson);
                if (NetSession.CurrentMatchId != match || ReplayStateHash.Compute(shell, 0) != sentinel
                    || !ReferenceEquals(GameState.Current, shell.GameState))
                    throw new InvalidDataException("Theatre changed live network/foreground state.");
            }
            foreach (uint target in new uint[] { 35, 600, 0, 450 })
            {
                ReplayController.Seek(Math.Min(target, DemoPlayback.LastFrame), resume: false);
                while (ReplayController.IsSeeking) shell.OnSimulationFrame();
                reference.Seek(DemoPlayback.CurrentFrame);
                while (!reference.Ready) reference.Update();
                if (ReplayStateHash.Compute(DemoPlayback.PresentationScene!, DemoPlayback.CurrentFrame)
                    != ReplayStateHash.Compute(reference.Current.Scene, DemoPlayback.CurrentFrame))
                    throw new InvalidDataException("Theatre checkpoint seek differs.");
            }
            ReplayController.Pause();
            uint paused = DemoPlayback.CurrentFrame;
            for (int i = 0; i < 5; i++) shell.OnSimulationFrame();
            if (DemoPlayback.CurrentFrame != paused) throw new InvalidDataException("Paused theatre advanced.");
            if (shots == null)
            {
                NetSession.Stop();
                if (!DemoPlayback.TakeControl(1, out string? branch) || branch == null
                    || DemoPlayback.IsActive || DemoPlayback.PresentationScene is not { IsReplayLab: true } practice
                    || practice.Services.IsReplica || NetHooks.LocalSlot != 1
                    || !ReplayLab.TryRead(branch, out var descriptor) || descriptor?.SourceFrame != paused)
                    throw new InvalidDataException("Replay Lab did not detach the selected private actor into practice.");
                File.Delete(branch);
                ulong before = practice.FrameCount;
                shell.OnSimulationFrame();
                if (practice.FrameCount != before + 1) throw new InvalidDataException("The practice fork did not simulate.");
            }
            Console.WriteLine($"[replaytheatre] PASS: {checkedFrames} isolated frames, camera/player changes, four seeks, pause and foreground/transport sentinels.");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("[replaytheatre] FAIL: " + ex); return 1; }
        finally
        {
            DemoPlayback.Stop(); shell.DoCleanup(); shell.UnloadGl(); NetSession.Stop();
#if !ANDROID
            window?.Dispose();
#endif
        }
    }
    private static void Describe(Scene scene, string label)
    {
        Console.WriteLine($"[{label}] rng {scene.Random.Rng1}/{scene.Random.Rng2}; clock {scene.GameState.MatchTime}; main {scene.Players.MainPlayerIndex}");
        foreach (var player in scene.Players.Items)
            Console.WriteLine($"[{label}] p{player.SlotIndex} {player.Position} face {player.FacingVector} speed {player.Speed} weapon {player.CurrentWeapon} shot {player.TimeSinceShot} flags {player.LoadFlags} respawn {player.RespawnTimer} death {player.DeathCountdown}");
        foreach (var entity in scene.Entities)
            if (entity is BeamProjectileEntity beam) Console.WriteLine($"[{label}] beam {beam.Beam} {beam.Position} age {beam.Age} velocity {beam.Velocity} owner {beam.Owner?.Id}");
            else if (entity is BombEntity bomb) Console.WriteLine($"[{label}] bomb {bomb.Position} {bomb.Countdown}");
    }
}
