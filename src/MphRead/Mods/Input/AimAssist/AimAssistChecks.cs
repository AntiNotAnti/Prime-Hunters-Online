using System;
using System.Numerics;

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
            AimAssistResult Apply(float stick = .5f, float move = 0, bool eligible = true, Vector2? raw = null)
                => AimAssist.Apply(state, targets, raw ?? new(.1f, .01f), stick, move, 1f / 60, eligible, profile);
            Check(Apply(0, 0, raw: Vector2.Zero) == new AimAssistResult(0, 0), "untouched pad never moves camera");
            Check(Apply(eligible: false).TargetSlot == -1, "mouse/menu/death eligibility bypasses assist");
            var result = Apply();
            Check(result.TargetSlot == 1 && result.Friction >= .62f && result.Friction < 1, "visible body gets bounded friction");
            Check(result.HeadBlend == 0, "head cannot acquire a target");
            for (int i = 0; i < 60; i++) result = Apply();
            Check(result.HeadBlend > 0 && result.HeadBlend <= .8f, "head refinement ramps after retained torso acquisition");
            targets[0] = targets[0] with { HeadVisible = false };
            Check(Apply().HeadBlend == 0, "head LOS loss drops refinement immediately");
            targets[0] = targets[0] with { BodyVisible = false };
            Check(Apply().TargetSlot == -1 && state.TargetSlot == -1, "wall clears retained target");
            targets[0] = targets[0] with { BodyVisible = true, Eligible = false };
            Check(Apply().TargetSlot == -1, "team/dead/spectator filtering");
            targets[0] = targets[0] with { Eligible = true, BodyError = new(float.NaN, 0) };
            Check(Apply().TargetSlot == -1, "nonfinite target rejected");
            targets[0] = targets[0] with { BodyError = new(1, .2f), HeadVisible = true };
            Apply(); targets[0] = targets[0] with { Life = 2 };
            Check(Apply().HeadBlend == 0 && state.TargetLife == 2, "respawn cannot inherit target history");
            var opposed = Apply(raw: new(-2, -2));
            Check(Math.Abs(opposed.X + 2) < .00001f && Math.Abs(opposed.Y + 2) < .00001f, "strong opposing input overrides both axes");
            Check(Apply(0, .5f, raw: Vector2.Zero).RotationStrength > 0, "movement intent permits reduced tracking");
            Check(Apply(0, 0, raw: Vector2.Zero).RotationStrength == 0, "no intent clears rotation");
            targets = new[] { new AimAssistTarget(1, 1, new(1, 0), new(1, 1), 15, true, false),
                new AimAssistTarget(2, 1, new(1.1f, 0), new(1, 1), 15, true, false) };
            state.Reset(); Check(Apply().TargetSlot == 1, "best angular score wins");
            targets[1] = targets[1] with { BodyError = new(.9f, 0) };
            Check(Apply().TargetSlot == 1, "small challenger improvement does not oscillate");
            targets[0] = targets[0] with { BodyError = new(8, 0) };
            targets[1] = targets[1] with { BodyError = new(.1f, 0) };
            Check(Apply().TargetSlot == 2, "decisive challenger releases old target");
            float Simulate(int hz)
            {
                var memory = new AimAssistState(); float angle = 2;
                var input = new AimAssistTarget[1];
                for (int i = 0; i < hz; i++)
                {
                    input[0] = new(1, 1, new(angle, 0), new(angle, 3), 15, true, false);
                    angle -= AimAssist.Apply(memory, input, Vector2.Zero, .5f, 0, 1f / hz, true, profile).X;
                }
                return angle;
            }
            Check(Math.Abs(Simulate(30) - Simulate(120)) < .06f, "rotation is stable across 30/120 Hz integration");
            AimInputSourceTracker.Reset(); AimInputSourceTracker.Stick(.5f, 0, 1000);
            Check(AimInputSourceTracker.Current == AimInputSource.Gamepad, "initial stick owns aim");
            AimInputSourceTracker.Pointer(1, 0, false, 1001);
            Check(AimInputSourceTracker.Current == AimInputSource.Mouse, "mouse revokes immediately");
            AimInputSourceTracker.Stick(.5f, 0, 1002); AimInputSourceTracker.Stick(.5f, 0, 1100);
            Check(AimInputSourceTracker.Current == AimInputSource.Mouse, "controller must confirm takeover");
            AimInputSourceTracker.Stick(.5f, 0, 1122);
            Check(AimInputSourceTracker.Current == AimInputSource.Gamepad, "confirmed aim stick reclaims aim");
            AimInputSourceTracker.Pointer(0, 1, true, 1123);
            Check(AimInputSourceTracker.Current == AimInputSource.Touch, "touch revokes immediately");
            AimInputSourceTracker.Reset();
            state.Reset();
            for (int i = 0; i < 1000; i++) Apply();
            long bytes = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10000; i++) Apply();
            Check(GC.GetAllocatedBytesForCurrentThread() == bytes, "steady-state assist core allocates no managed memory");
        }
    }
}
