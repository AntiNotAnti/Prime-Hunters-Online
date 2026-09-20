using System;
using System.Numerics;

namespace MphRead.Mods.Input.AimAssist
{
    // Pure, allocation-free camera intent processing. No access to networking or damage.
    public static class AimAssist
    {
        public static AimAssistResult Apply(AimAssistState state, ReadOnlySpan<AimAssistTarget> targets,
            Vector2 raw, float stickIntent, float moveIntent, float dt, bool eligible, AimAssistWeaponProfile profile)
        {
            if (!AimAssistMath.Finite(raw)) raw = Vector2.Zero;
            if (!eligible || !AimAssistMath.Finite(raw) || !float.IsFinite(dt) || dt <= 0 || dt > .1f)
            { state.Reset(); return new(raw.X, raw.Y); }
            float intent = stickIntent > .08f ? 1 : moveIntent > .20f ? .5f : 0;
            if (intent == 0) { state.Reset(); return new(raw.X, raw.Y); }
            int best = -1, retained = -1; float bestScore = -1, retainedScore = -1;
            for (int i = 0; i < targets.Length; i++)
            {
                ref readonly var t = ref targets[i];
                bool keep = t.Slot == state.TargetSlot && t.Life == state.TargetLife;
                float rangeScale = 1 - .4f * AimAssistMath.Smooth(25, 60, t.Distance);
                float cone = (keep ? profile.ReleaseCone : profile.Cone) * rangeScale;
                float angle = t.BodyError.Length();
                if (!t.Eligible || !t.BodyVisible || !AimAssistMath.Finite(t.BodyError)
                    || !float.IsFinite(t.Distance) || t.Distance < .2f || t.Distance > 60 || angle > cone) continue;
                float score = AimAssistMath.Score(angle, cone, t.Distance, keep, keep ? Math.Min(state.AngularVelocity.Length() / 45, 1) : 0);
                if (keep) { retained = i; retainedScore = score; }
                if (score > bestScore) { best = i; bestScore = score; }
            }
            if (best < 0) { state.Reset(); return new(raw.X, raw.Y); }
            bool deliberate = raw.Length() / dt > 90;
            if (retained >= 0 && best != retained && bestScore < retainedScore * (deliberate ? 1 : AimAssistTuning.ChallengerRatio)) best = retained;
            ref readonly var target = ref targets[best];
            bool same = state.TargetSlot == target.Slot && state.TargetLife == target.Life;
            if (!same) state.Reset();
            state.TargetSlot = target.Slot; state.TargetLife = target.Life;
            state.RetainedSeconds += dt;
            // Add back the prior camera turn so this measures target motion, not our own correction.
            Vector2 velocity = same ? (target.BodyError - state.PreviousError + state.PreviousOutput) / dt : default;
            velocity = Vector2.Clamp(velocity, new(-120), new(120));
            state.AngularVelocity = Vector2.Lerp(state.AngularVelocity, velocity, 1 - MathF.Exp(-12 * dt));
            float headAngle = target.HeadError.Length();
            bool head = profile.Head && same && state.RetainedSeconds >= AimAssistTuning.HeadDelay
                && target.HeadVisible && AimAssistMath.Finite(target.HeadError) && target.Distance > 5
                && (headAngle < target.BodyError.Length() * .8f || raw.Y > .02f)
                && headAngle < 1.5f && raw.Y >= -.02f && AimAssistMath.Opposition(raw.X, target.HeadError.X) > .5f;
            float desiredHead = head ? Math.Min(.8f, .1f + .7f * AimAssistMath.Smooth(1.5f, 0, headAngle)) : 0;
            state.HeadBlend = head ? state.HeadBlend + (desiredHead - state.HeadBlend) * (1 - MathF.Exp(-8 * dt)) : 0;
            Vector2 error = Vector2.Lerp(target.BodyError, target.HeadError, state.HeadBlend);
            if (!AimAssistMath.Finite(error)) error = target.BodyError;
            float distanceStrength = (.55f + .45f * AimAssistMath.Smooth(0, 5, target.Distance))
                * (1 - .5f * AimAssistMath.Smooth(25, 60, target.Distance));
            float bubble = 1 - AimAssistMath.Smooth(profile.Inner, profile.ReleaseCone, target.BodyError.Length());
            float opposeX = AimAssistMath.Opposition(raw.X / (dt * 60), error.X);
            float opposeY = AimAssistMath.Opposition(raw.Y / (dt * 60), error.Y);
            float friction = 1 - .38f * bubble * distanceStrength;
            Vector2 adjusted = new(raw.X * (1 - (1 - friction) * opposeX), raw.Y * (1 - (1 - friction) * opposeY));
            float strength = intent * distanceStrength * bubble * profile.Rotation;
            Vector2 rotation = new((error.X * 4 + state.AngularVelocity.X) * .24f * opposeX,
                (error.Y * 4 + state.AngularVelocity.Y) * .15f * opposeY);
            rotation = Vector2.Clamp(rotation, new(-profile.MaxSpeed), new(profile.MaxSpeed)) * (strength * dt);
            // Never correct farther than the visible error in either axis.
            rotation.X = Math.Clamp(rotation.X, -Math.Abs(error.X), Math.Abs(error.X));
            rotation.Y = Math.Clamp(rotation.Y, -Math.Abs(error.Y), Math.Abs(error.Y));
            Vector2 output = adjusted + rotation;
            state.PreviousError = target.BodyError; state.PreviousOutput = output;
            return new(output.X, output.Y, target.Slot, friction, strength,
                state.HeadBlend > 0 ? AimAssistPointType.Head : target.BodyPointType, state.HeadBlend, best == retained ? retainedScore : bestScore);
        }
    }
}
