using System;
using System.IO;
using MphRead.Mods.Render;
using OpenTK.Mathematics;
#if !ANDROID
using OpenTK.Graphics.OpenGL;
using OpenTK.Windowing.Desktop;
using GLFWBindingsContext = OpenTK.Windowing.GraphicsLibraryFramework.GLFWBindingsContext;
#endif

namespace MphRead.Mods.Network;

/// <summary>Real GL draws driven by virtual display cadences. Verifies state
/// isolation; this is not a measurement of a physical display's refresh rate.</summary>
internal static class ReplayCadenceCheck
{
    internal static int Run(string path)
    {
#if ANDROID
        return 1;
#else
        var settings = DesktopGlContext.Settings(background: true);
        settings.ClientSize = new Vector2i(400, 300);
        using var window = new NativeWindow(settings);
        window.Context.MakeCurrent(); GL.LoadBindings(new GLFWBindingsContext());
        int oldCap = FrameTiming.FrameRateCap;
        const int steps = 600;
        var reference = new string[steps + 1];
        try
        {
            foreach (int hz in new[] { 60, 120, 144, 240, 360, 540 })
            {
                using var world = new PassiveReplayScene(path, settings.ClientSize);
                FrameTiming.Reset(); FrameTiming.ResetDiagnostics();
                FrameTiming.FrameRateCap = hz; // also enables sub-frame presentation
                int simulated = 0, pictures = 0;
                while (simulated < steps)
                {
                    int due = FrameTiming.Advance(1.0 / hz);
                    for (int i = 0; i < due && simulated < steps; i++)
                    {
                        if (!world.Step()) throw new InvalidDataException("Cadence fixture needs 600 frames.");
                        string hash = ReplayStateHash.Compute(world.Scene, world.Session.CurrentFrame);
                        simulated++;
                        if (hz == 60) reference[simulated] = hash;
                        else if (hash != reference[simulated])
                            throw new InvalidDataException($"Gameplay differs at {hz} Hz, step {simulated}.");
                    }
                    // An unstepped world has no initialized player presentation yet.
                    if (simulated == 0) continue;
                    Scene scene = world.Scene;
                    string before = ReplayStateHash.Compute(scene, world.Session.CurrentFrame);
                    var player = scene.Players.Items[0];
                    scene.ReplayRenderAlpha = (float)FrameTiming.PresentationAlpha;
                    scene.SetReplicaCamera(player.Position + new Vector3(0, 2, -4),
                        player.Position + Vector3.UnitZ * 5, 78);
                    scene.OnDrawFrame();
                    if (!scene.OnRenderFrame()) throw new InvalidDataException("Cadence renderer stopped.");
                    scene.AfterRenderFrame();
                    if (ReplayStateHash.Compute(scene, world.Session.CurrentFrame) != before)
                        throw new InvalidDataException($"Drawing changed gameplay at {hz} Hz.");
                    pictures++;
                }
                if (FrameTiming.DroppedSteps != 0 || FrameTiming.Stalls != 0)
                    throw new InvalidDataException("Virtual cadence dropped simulation steps.");
                Console.WriteLine($"[replaycadence] PASS {hz} Hz: {simulated} identical gameplay frames, {pictures} GL pictures; {FrameTiming.Describe()}");
            }
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("[replaycadence] FAIL: " + ex); return 1; }
        finally { FrameTiming.Reset(); FrameTiming.FrameRateCap = oldCap; }
#endif
    }
}
