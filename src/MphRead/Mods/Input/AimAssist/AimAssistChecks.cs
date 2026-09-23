using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace MphRead.Mods.Input.AimAssist
{
    internal static class AimAssistChecks
    {
        public static void Run()
        {
            void Check(bool ok, string name) => GamepadChecks.Check(ok, "aim assist: " + name);
            var state = new AimAssistState();
            var profile = AimAssistWeaponProfile.For(AimAssistWeaponClass.Standard, false);
            var targets = new[] { new AimAssistTarget(1, 1, new(1, .2f), new(.3f, .4f), 15, true, true) };
            AimAssistResult Apply(float stick = .5f, float move = 0, bool eligible = true, Vector2? raw = null,
                AimAssistWeaponProfile? overrideProfile = null)
                => AimAssist.Apply(state, targets, raw ?? new(.1f, .01f), stick, move, 1f / 60,
                    eligible, overrideProfile ?? profile);

            Check(Apply(0, 0, raw: Vector2.Zero) == new AimAssistResult(0, 0), "untouched pad never moves camera");
            Check(Apply(eligible: false).TargetSlot == -1, "mouse/menu/death eligibility bypasses assist");

            state.Reset();
            var weakProfile = profile with { Rotation = profile.Rotation / 4 };
            var weak = Apply(overrideProfile: weakProfile);
            state.Reset();
            var result = Apply();
            Check(Math.Abs(result.RotationStrength - weak.RotationStrength * 4) < .0001f,
                "requested four-times rotational assistance is explicit and testable");
            Check(result.TargetSlot == 1 && result.Friction >= AimAssistTuning.MinimumFriction && result.Friction < 1,
                "visible body gets bounded friction");

            Check(result.HeadBlend == 0, "head cannot acquire on the first target frame");
            for (int i = 0; i < 60; i++) result = Apply();
            Check(result.HeadBlend > 0 && result.HeadBlend <= AimAssistTuning.IntentionalMaxHeadBlend,
                "head refinement ramps after retained torso acquisition");
            targets[0] = targets[0] with { HeadError = new(.15f, .15f) };
            result = Apply();
            targets[0] = targets[0] with { HeadError = new(.05f, .08f) };
            result = Apply();
            Check(result.HeadPrediction >= 0 && result.HeadPrediction <= AimAssistTuning.MaxHeadPrediction + .0001f,
                "head prediction is bounded");

            targets[0] = targets[0] with { HeadVisible = false };
            Check(Apply().HeadBlend == 0, "head LOS loss drops refinement immediately");

            targets[0] = targets[0] with { BodyVisible = false };
            var hidden = Apply();
            Check(hidden.TargetSlot == 1 && hidden.Occluded && hidden.Friction == 1 && hidden.RotationStrength == 0,
                "brief occlusion retains identity without tracking through walls");
            for (int i = 0; i < 4; i++) hidden = Apply();
            Check(hidden.TargetSlot == -1 && state.TargetSlot == -1, "occlusion grace expires and clears the target");

            targets[0] = targets[0] with { BodyVisible = true, HeadVisible = true, Eligible = false };
            Check(Apply().TargetSlot == -1, "team/dead/spectator filtering");
            targets[0] = targets[0] with { Eligible = true, BodyError = new(float.NaN, 0) };
            Check(Apply().TargetSlot == -1, "nonfinite target rejected");

            targets[0] = targets[0] with { BodyError = new(1, .2f), HeadError = new(.3f, .4f), HeadVisible = true };
            Apply();
            targets[0] = targets[0] with { Life = 2 };
            Check(Apply().HeadBlend == 0 && state.TargetLife == 2, "respawn cannot inherit target history");

            var opposed = Apply(raw: new(-2, -2));
            Check(Math.Abs(opposed.X + 2) < .00001f && Math.Abs(opposed.Y + 2) < .00001f,
                "strong opposing input overrides both axes");
            Check(Apply(0, .5f, raw: Vector2.Zero).RotationStrength > 0,
                "movement intent permits reduced tracking");
            Check(Apply(0, 0, raw: Vector2.Zero).RotationStrength == 0,
                "no intent clears rotation");

            targets = new[] {
                new AimAssistTarget(1, 1, new(1, 0), new(1, 1), 15, true, false),
                new AimAssistTarget(2, 1, new(1.1f, 0), new(1, 1), 15, true, false)
            };
            state.Reset();
            Check(Apply().TargetSlot == 1, "best angular score wins");
            targets[1] = targets[1] with { BodyError = new(.9f, 0) };
            Check(Apply().TargetSlot == 1, "small challenger improvement does not oscillate");
            targets[0] = targets[0] with { BodyError = new(8, 0) };
            targets[1] = targets[1] with { BodyError = new(.1f, 0) };
            Check(Apply().TargetSlot == 2, "decisive challenger releases old target");
            Check(Apply(raw: new(.2f, 0)).InputAlignment > 0, "target score records player input alignment");

            float Simulate(int hz)
            {
                var memory = new AimAssistState();
                float angle = 2;
                var input = new AimAssistTarget[1];
                for (int i = 0; i < hz; i++)
                {
                    input[0] = new(1, 1, new(angle, 0), new(angle, 3), 15, true, false);
                    angle -= AimAssist.Apply(memory, input, Vector2.Zero, .5f, 0, 1f / hz, true, profile).X;
                }
                return angle;
            }
            Check(Math.Abs(Simulate(30) - Simulate(120)) < .08f,
                "rotation is stable across 30/120 Hz integration");

            AimInputSourceTracker.Reset();
            AimInputSourceTracker.Stick(.5f, 0, 1000);
            Check(AimInputSourceTracker.Current == AimInputSource.Gamepad, "initial stick owns aim");
            AimInputSourceTracker.Pointer(1, 0, false, 1001);
            Check(AimInputSourceTracker.Current == AimInputSource.Mouse, "mouse revokes immediately");
            AimInputSourceTracker.Stick(.5f, 0, 1002);
            AimInputSourceTracker.Stick(.5f, 0, 1050);
            Check(AimInputSourceTracker.Current == AimInputSource.Mouse, "medium stick still confirms takeover");
            AimInputSourceTracker.Stick(.5f, 0, 1062);
            Check(AimInputSourceTracker.Current == AimInputSource.Gamepad, "medium stick uses faster confirmation");
            AimInputSourceTracker.Pointer(1, 0, false, 1063);
            AimInputSourceTracker.Stick(.7f, 0, 1064);
            Check(AimInputSourceTracker.Current == AimInputSource.Gamepad, "strong intentional stick reclaims aim immediately");
            AimInputSourceTracker.Pointer(0, 1, true, 1065);
            Check(AimInputSourceTracker.Current == AimInputSource.Touch, "touch revokes immediately");

            AimInputSourceTracker.Reset();
            state.Reset();
            MeasureCoreAllocations(state, targets, profile);
            long allocated = MeasureCoreAllocations(state, targets, profile);
            Check(allocated == 0, $"steady-state assist core allocates no managed memory ({allocated} bytes)");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static long MeasureCoreAllocations(AimAssistState state, AimAssistTarget[] targets,
            AimAssistWeaponProfile profile)
        {
            long bytes = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10000; i++)
                AimAssist.Apply(state, targets, new(.1f, .01f), .5f, 0, 1f / 60, true, profile);
            return GC.GetAllocatedBytesForCurrentThread() - bytes;
        }
    }
}
