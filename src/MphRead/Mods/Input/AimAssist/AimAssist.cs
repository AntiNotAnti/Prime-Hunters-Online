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
            if (!eligible || !float.IsFinite(dt) || dt <= 0 || dt > .1f)
            {
                state.Reset();
                return new(raw.X, raw.Y);
            }

            float intent = stickIntent > .08f ? 1 : moveIntent > .20f ? .5f : 0;
            if (intent == 0)
            {
                state.Reset();
                return new(raw.X, raw.Y);
            }

            int best = -1, retained = -1, occludedRetained = -1;
            float bestScore = -1, retainedScore = -1, bestAlignment = 0;
            for (int i = 0; i < targets.Length; i++)
            {
                ref readonly var t = ref targets[i];
                bool keep = t.Slot == state.TargetSlot && t.Life == state.TargetLife;
                float rangeScale = 1 - .4f * AimAssistMath.Smooth(25, 60, t.Distance);
                float cone = (keep ? profile.ReleaseCone : profile.Cone) * rangeScale;
                float angle = t.BodyError.Length();
                if (!t.Eligible || !AimAssistMath.Finite(t.BodyError) || !float.IsFinite(t.Distance)
                    || t.Distance < .2f || t.Distance > 60 || angle > cone)
                {
                    continue;
                }
                if (!t.BodyVisible)
                {
                    if (keep) occludedRetained = i;
                    continue;
                }

                float alignment = AimAssistMath.Alignment(raw, t.BodyError);
                float score = AimAssistMath.Score(angle, cone, t.Distance, keep,
                    keep ? Math.Min(state.AngularVelocity.Length() / 45, 1) : 0, alignment);
                if (keep)
                {
                    retained = i;
                    retainedScore = score;
                }
                if (score > bestScore)
                {
                    best = i;
                    bestScore = score;
                    bestAlignment = alignment;
                }
            }

            if (best < 0)
            {
                if (occludedRetained >= 0 && state.OccludedSeconds + dt <= AimAssistTuning.OcclusionGrace)
                {
                    ref readonly var hidden = ref targets[occludedRetained];
                    state.OccludedSeconds += dt;
                    state.HeadBlend = 0;
                    state.AngularVelocity = state.HeadAngularVelocity = Vector2.Zero;
                    state.PreviousError = hidden.BodyError;
                    state.PreviousHeadError = hidden.HeadError;
                    state.PreviousOutput = raw;
                    return new(raw.X, raw.Y, hidden.Slot, 1, 0, hidden.BodyPointType, 0, 0,
                        AimAssistMath.Alignment(raw, hidden.BodyError), 0, true, false, state.RetainedSeconds);
                }
                state.Reset();
                return new(raw.X, raw.Y);
            }

            state.OccludedSeconds = 0;
            bool deliberate = raw.Length() / dt > 90;
            if (retained >= 0 && best != retained
                && bestScore < retainedScore * (deliberate ? 1 : AimAssistTuning.ChallengerRatio))
            {
                best = retained;
                bestScore = retainedScore;
                bestAlignment = AimAssistMath.Alignment(raw, targets[retained].BodyError);
            }

            ref readonly var target = ref targets[best];
            bool same = state.TargetSlot == target.Slot && state.TargetLife == target.Life;
            if (!same) state.Reset();
            state.TargetSlot = target.Slot;
            state.TargetLife = target.Life;
            bool hasHistory = same && state.RetainedSeconds > 0;
            state.RetainedSeconds += dt;

            Vector2 bodyVelocity = hasHistory
                ? (target.BodyError - state.PreviousError + state.PreviousOutput) / dt
                : default;
            bodyVelocity = Vector2.Clamp(bodyVelocity, new(-120), new(120));
            state.AngularVelocity = Vector2.Lerp(state.AngularVelocity, bodyVelocity,
                1 - MathF.Exp(-AimAssistTuning.VelocityFilterRate * dt));

            Vector2 headVelocity = hasHistory && target.HeadVisible && AimAssistMath.Finite(target.HeadError)
                ? (target.HeadError - state.PreviousHeadError + state.PreviousOutput) / dt
                : default;
            headVelocity = Vector2.Clamp(headVelocity, new(-120), new(120));
            state.HeadAngularVelocity = Vector2.Lerp(state.HeadAngularVelocity, headVelocity,
                1 - MathF.Exp(-AimAssistTuning.HeadVelocityFilterRate * dt));

            float bodyAngle = target.BodyError.Length();
            float headAngle = target.HeadError.Length();
            float headAlignment = AimAssistMath.Alignment(raw, target.HeadError);
            bool intentionalHead = stickIntent > .18f && headAlignment > .45f;
            bool opposingHead = stickIntent > .20f && AimAssistMath.Finite(target.HeadError)
                && Vector2.Dot(raw, target.HeadError) < 0;
            float headCone = state.HeadBlend > .01f ? AimAssistTuning.HeadReleaseCone : AimAssistTuning.HeadAcquireCone;
            float delay = intentionalHead ? AimAssistTuning.IntentionalHeadDelay : AimAssistTuning.HeadDelay;
            bool headCandidate = profile.Head && same && target.HeadVisible && AimAssistMath.Finite(target.HeadError)
                && target.Distance > 5 && headAngle < headCone && !opposingHead
                && (headAngle < bodyAngle * .95f || intentionalHead);
            bool head = headCandidate && state.RetainedSeconds >= delay;

            Vector2 predictedHead = target.HeadError;
            float predictionAmount = 0;
            if (head)
            {
                Vector2 prediction = AimAssistMath.ClampLength(
                    state.HeadAngularVelocity * AimAssistTuning.HeadPredictionSeconds,
                    AimAssistTuning.MaxHeadPrediction);
                predictedHead += prediction;
                predictionAmount = prediction.Length();
            }

            float proximity = 1 - AimAssistMath.Smooth(0, headCone, headAngle);
            float maxHead = intentionalHead ? AimAssistTuning.IntentionalMaxHeadBlend : AimAssistTuning.MaxHeadBlend;
            float desiredHead = head ? maxHead * (.15f + .85f * proximity) : 0;
            if (!target.HeadVisible)
            {
                state.HeadBlend = 0;
            }
            else
            {
                float blendRate = head ? AimAssistTuning.HeadBlendRate : AimAssistTuning.HeadFallbackRate;
                state.HeadBlend += (desiredHead - state.HeadBlend) * (1 - MathF.Exp(-blendRate * dt));
                if (state.HeadBlend < .001f) state.HeadBlend = 0;
            }

            Vector2 error = Vector2.Lerp(target.BodyError, predictedHead, state.HeadBlend);
            if (!AimAssistMath.Finite(error)) error = target.BodyError;
            Vector2 trackedVelocity = Vector2.Lerp(state.AngularVelocity, state.HeadAngularVelocity, state.HeadBlend);

            float distanceStrength = (.55f + .45f * AimAssistMath.Smooth(0, 5, target.Distance))
                * (1 - .5f * AimAssistMath.Smooth(25, 60, target.Distance));
            float bubble = 1 - AimAssistMath.Smooth(profile.Inner, profile.ReleaseCone, target.BodyError.Length());
            float opposeX = AimAssistMath.Opposition(raw.X / (dt * 60), error.X);
            float opposeY = AimAssistMath.Opposition(raw.Y / (dt * 60), error.Y);
            float friction = 1 - (1 - AimAssistTuning.MinimumFriction) * bubble * distanceStrength;
            Vector2 adjusted = new(raw.X * (1 - (1 - friction) * opposeX),
                raw.Y * (1 - (1 - friction) * opposeY));

            float strength = intent * distanceStrength * bubble * profile.Rotation
                * AimAssistTuning.RotationAssistMultiplier;
            Vector2 unrestricted = new(
                (error.X * AimAssistTuning.RotationErrorGain + trackedVelocity.X)
                    * AimAssistTuning.HorizontalRotation * opposeX,
                (error.Y * AimAssistTuning.RotationErrorGain + trackedVelocity.Y)
                    * AimAssistTuning.VerticalRotation * opposeY);
            Vector2 limited = Vector2.Clamp(unrestricted, new(-profile.MaxSpeed), new(profile.MaxSpeed));
            bool saturated = limited != unrestricted;
            Vector2 rotation = limited * (strength * dt);

            float beforeX = rotation.X, beforeY = rotation.Y;
            rotation.X = Math.Clamp(rotation.X, -Math.Abs(error.X), Math.Abs(error.X));
            rotation.Y = Math.Clamp(rotation.Y, -Math.Abs(error.Y), Math.Abs(error.Y));
            saturated |= rotation.X != beforeX || rotation.Y != beforeY;

            Vector2 output = adjusted + rotation;
            state.PreviousError = target.BodyError;
            state.PreviousHeadError = target.HeadError;
            state.PreviousOutput = output;
            return new(output.X, output.Y, target.Slot, friction, strength,
                state.HeadBlend > 0 ? AimAssistPointType.Head : target.BodyPointType,
                state.HeadBlend, bestScore, bestAlignment, predictionAmount, false, saturated, state.RetainedSeconds);
        }
    }
}
