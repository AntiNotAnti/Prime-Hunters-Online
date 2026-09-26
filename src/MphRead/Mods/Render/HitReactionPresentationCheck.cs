using System;
using System.Reflection;
using MphRead.Entities;
using MphRead.Mods.Input;
using OpenTK.Mathematics;

namespace MphRead.Mods.Render;

/// <summary>Exercise the real first-person pose path without assets or a GL window.</summary>
internal static class HitReactionPresentationCheck
{
    internal static bool Run()
    {
        int priorCap = FrameTiming.FrameRateCap;
        bool priorPro = Features.ProHud, priorFixed = Features.FixedCrosshair;
        var priorGame = GameState.Current;
        var priorPlayers = PlayerEntity.LegacyRegistry;
        var priorRandom = Rng.Current;
        bool ok = true;
        void Check(bool value, string label)
        {
            ok &= value;
            Console.WriteLine($"FRAMETIMING {(value ? "ok  " : "FAIL")} hit presentation | {label}");
        }
        try
        {
            Features.ProHud = false;
            Features.FixedCrosshair = true;
            var scene = new Scene(new Vector2i(256, 192), SyntheticInput.CreateKeyboard(),
                SyntheticInput.CreateMouse(), _ => { }, () => { }, initializeRuntime: false);
            var player = scene.Players.Main;
            typeof(PlayerEntity).GetProperty(nameof(PlayerEntity.Values))!
                .SetValue(player, Metadata.PlayerValues[(int)Hunter.Samus]);
            void Set(string name, Vector3 value) => typeof(PlayerEntity)
                .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(player, value);
            void Pose(Vector3 aim, Vector3 cameraFacing)
            {
                Set("_gunVec1", aim);
                Set("_aimVec", aim);
                Set("_upVector", Vector3.UnitY);
                var basis = EntityBase.GetTransformMatrix(aim, Vector3.UnitY);
                Set("_gunDrawPos", Vector3.TransformVector(new Vector3(.2f, -.15f, .5f), basis));
                player.CameraInfo.Position = Vector3.Zero;
                player.CameraInfo.Target = cameraFacing;
                player.CameraInfo.UpVector = Vector3.UnitY;
                player.CameraInfo.Fov = 78;
                player.CameraInfo.Update();
                player.CameraInfo.ModCaptureDrawState();
                player.ModCaptureFirstPersonDrawState();
            }
            (Vector3 Cannon, Vector3 Camera) Render(double alpha, float pointer = 0)
            {
                if (!player.ModPrepareFirstPersonRenderPose(alpha, pointer, 0, 0, 0,
                        out var view, out _, out _)
                    || !player.ModGetFirstPersonGunTransform(out var gun))
                    throw new InvalidOperationException("First-person pose was unavailable.");
                return (Vector3.TransformVector(gun.Row2.Xyz, view).Normalized(),
                    -view.Inverted().Row2.Xyz.Normalized());
            }
            bool Near(Vector3 a, Vector3 b) => (a - b).Length < .0001f;
            Vector3 forward = -Vector3.UnitZ;
            Vector3 shaken = new Vector3(.3f, .1f, -1).Normalized();
            foreach (int rate in new[] { 120, 144, 240 })
            {
                FrameTiming.FrameRateCap = rate;
                FrameTiming.Reset(); FrameTiming.ResetDiagnostics();
                Pose(forward, forward);
                player.ModResetFirstPersonDrawState();
                Vector3 before = Render(1).Cannon;
                // A hit changes the view, not the firing ray. This is the same
                // relationship CameraInfo.Update produces when it applies shake.
                Pose(forward, shaken);
                var start = Render(0);
                var middle = Render(.5);
                var end = Render(1);
                Check(Near(start.Cannon, before), $"{rate} Hz hit starts at previous cannon pose");
                Check(!Near(middle.Cannon, start.Cannon) && !Near(middle.Cannon, end.Cannon)
                    && Vector3.Dot(middle.Cannon, (start.Cannon + end.Cannon).Normalized()) > .99999f,
                    $"{rate} Hz cannon rotates between hit samples");
                var tinyInput = Render(.5, .001f);
                Check((tinyInput.Camera - middle.Camera).Length < .0001f,
                    $"{rate} Hz tiny aim input preserves hit shake");
                Pose(forward, forward);
                Check(Near(Render(0).Cannon, end.Cannon), $"{rate} Hz hit recovery has no boundary snap");

                // Interpolating camera-local effects must not delay a real turn.
                player.ModResetFirstPersonDrawState();
                Pose(-Vector3.UnitX, -Vector3.UnitX);
                var turn = Render(0);
                Check(Near(turn.Camera, -Vector3.UnitX) && Near(turn.Cannon, before),
                    $"{rate} Hz camera and cannon turn immediately together");
            }
            // Spectator/replay POV does not late-latch local input, but its
            // arm cannon must still consume the same fractional camera pose.
            FrameTiming.FrameRateCap = 144;
            FrameTiming.Reset(); FrameTiming.ResetDiagnostics();
            Pose(forward, forward);
            player.ModResetFirstPersonDrawState();
            Pose(shaken, shaken);
            player.CameraInfo.ModGetFirstPersonDrawPose(.5,
                out Vector3 observedPosition, out Vector3 observedTarget,
                out Vector3 observedUp, out float observedFov);
            Matrix4 observedView = Matrix4.LookAt(
                observedPosition, observedTarget, observedUp);
            Check(player.ModPrepareObservedFirstPersonViewmodel(.5,
                    observedPosition, observedTarget, observedUp,
                    observedFov, observedView)
                && player.ModGetFirstPersonGunTransform(out Matrix4 observedGun),
                "144 Hz observed POV prepares a fractional arm-cannon pose");
            Vector3 observedCannon = Vector3.TransformVector(
                observedGun.Row2.Xyz, observedView).Normalized();
            Vector3 observedCamera = -observedView.Inverted().Row2.Xyz.Normalized();
            Check(Vector3.Dot(observedCannon, observedCamera) > .95f,
                "observed POV camera and arm cannon share one presentation basis");

            FrameTiming.FrameRateCap = 60;
            FrameTiming.Reset(); FrameTiming.ResetDiagnostics();
            Pose(forward, forward);
            player.ModResetFirstPersonDrawState();
            Pose(forward, shaken);
            Check(Near(Render(0).Cannon, Render(1).Cannon), "60 Hz keeps the current pose");
            Vector3 savedTarget = player.CameraInfo.Target;
            Vector3 savedAim = player.ModGunVector;
            uint savedRng = scene.Random.Rng2;
            Render(.25, 10); Render(.75, -10);
            Check(player.CameraInfo.Target == savedTarget && player.ModGunVector == savedAim
                && scene.Random.Rng2 == savedRng, "draws preserve simulation aim, hit shake and RNG");
            return ok;
        }
        finally
        {
            Features.ProHud = priorPro; Features.FixedCrosshair = priorFixed;
            FrameTiming.FrameRateCap = priorCap;
            FrameTiming.Reset(); FrameTiming.ResetDiagnostics();
            GameState.Current = priorGame; PlayerEntity.LegacyRegistry = priorPlayers; Rng.Current = priorRandom;
        }
    }
}
