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

            float intent = float.IsFinite(stickIntent)
                ? AimAssistMath.Smooth(AimAssistTuning.IntentStart, AimAssistTuning.IntentFull, stickIntent) : 0;
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
                Vector2 selectionError = AimAssistMath.SelectionError(t, profile);
                float angle = selectionError.Length();
                if (!t.Eligible || !AimAssistMath.Finite(t.BodyError) || !float.IsFinite(t.Distance)
                    || t.Distance < .2f || t.Distance > 60 || angle > cone)
                {
                    continue;
                }
                if (!t.BodyVisible && !AimAssistMath.VisibleHead(t, profile))
                {
                    if (keep) occludedRetained = i;
                    continue;
                }

                float alignment = AimAssistMath.Alignment(raw, selectionError);
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
                    state.HeadBlend = state.HeadCandidateSeconds = 0;
                    state.PreviousBodyVisible = state.PreviousHeadVisible = false;
                    state.AngularVelocity = state.HeadAngularVelocity = Vector2.Zero;
                    state.PreviousError = hidden.BodyError;
                    state.PreviousHeadError = hidden.HeadError;
                    state.PreviousOutput = raw;
                    state.PreviousDeltaTime = dt;
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
                bestAlignment = AimAssistMath.Alignment(raw, AimAssistMath.SelectionError(targets[retained], profile));
            }

            ref readonly var target = ref targets[best];
            bool same = state.TargetSlot == target.Slot && state.TargetLife == target.Life;
            if (!same) state.Reset();
            state.TargetSlot = target.Slot;
            state.TargetLife = target.Life;
            bool hasHistory = same && state.RetainedSeconds > 0;
            state.RetainedSeconds += dt;

            bool visibleHead = AimAssistMath.VisibleHead(target, profile);
            state.AngularVelocity = TrackVelocity(state.AngularVelocity, target.BodyError,
                state.PreviousError, state.PreviousOutput, state.PreviousDeltaTime, dt,
                hasHistory && state.PreviousBodyVisible && target.BodyVisible, AimAssistTuning.VelocityFilterRate);
            state.HeadAngularVelocity = TrackVelocity(state.HeadAngularVelocity, target.HeadError,
                state.PreviousHeadError, state.PreviousOutput, state.PreviousDeltaTime, dt,
                hasHistory && state.PreviousHeadVisible && visibleHead, AimAssistTuning.HeadVelocityFilterRate);

            float bodyAngle = target.BodyError.Length();
            float headAngle = visibleHead ? target.HeadError.Length() : float.MaxValue;
            float headAlignment = AimAssistMath.Alignment(raw, target.HeadError);
            bool intentionalHead = stickIntent > .18f && headAlignment > .45f;
            // Tiny counter-steering is part of tracking. Only a meaningful turn away
            // cancels head refinement; a sign change at the crosshair must not chatter.
            bool opposingHead = stickIntent > .20f && visibleHead
                && Vector2.Dot(raw, target.HeadError) < 0 && (raw.Length() / dt > 12 || stickIntent > .60f);
            float radius = float.IsFinite(target.HeadRadiusDegrees)
                ? Math.Clamp(target.HeadRadiusDegrees, .05f, 3f) : .6f;
            float headCone = Math.Max(AimAssistTuning.HeadAcquireCone, radius * 1.5f);
            if (state.HeadBlend > .01f) headCone *= AimAssistTuning.HeadReleaseCone / AimAssistTuning.HeadAcquireCone;
            headCone = Math.Min(headCone, profile.Cone);
            float delay = intentionalHead ? AimAssistTuning.IntentionalHeadDelay : AimAssistTuning.HeadDelay;
            bool headOnly = !target.BodyVisible && visibleHead;
            bool headCandidate = visibleHead && headAngle < headCone && !opposingHead
                && (headAngle < bodyAngle * .95f || intentionalHead || headOnly);
            state.HeadCandidateSeconds = headCandidate ? state.HeadCandidateSeconds + dt : 0;
            bool head = headCandidate && same && state.HeadCandidateSeconds >= delay;

            Vector2 predictedHead = visibleHead ? target.HeadError : target.BodyError;
            float predictionAmount = 0;
            if (head || headOnly)
            {
                // A fixed angular lead can overshoot an entire distant head. Keep the
                // prediction inside its angular radius, including during jumps/reversals.
                Vector2 prediction = AimAssistMath.ClampLength(
                    state.HeadAngularVelocity * AimAssistTuning.HeadPredictionSeconds,
                    Math.Min(AimAssistTuning.MaxHeadPrediction, radius * .65f));
                predictedHead += prediction;
                predictionAmount = prediction.Length();
            }

            float proximity = 1 - AimAssistMath.Smooth(radius, headCone, headAngle);
            float maxHead = intentionalHead ? AimAssistTuning.IntentionalMaxHeadBlend : AimAssistTuning.MaxHeadBlend;
            // Once the player is on the head, allow full refinement instead of pulling
            // the crosshair back toward the chest on every horizontal strafe.
            if (headAngle <= radius) maxHead = 1;
            float desiredHead = head ? maxHead * (.35f + .65f * proximity) : 0;
            if (!visibleHead) state.HeadBlend = 0;
            else if (headOnly) state.HeadBlend = 1; // never aim at an occluded chest
            else
            {
                float blendRate = head ? AimAssistTuning.HeadBlendRate : AimAssistTuning.HeadFallbackRate;
                state.HeadBlend += (desiredHead - state.HeadBlend) * (1 - MathF.Exp(-blendRate * dt));
                if (state.HeadBlend < .001f) state.HeadBlend = 0;
            }

            Vector2 error = Vector2.Lerp(target.BodyError, predictedHead, state.HeadBlend);
            Vector2 trackedVelocity = Vector2.Lerp(state.AngularVelocity, state.HeadAngularVelocity, state.HeadBlend);
            float distanceStrength = (.55f + .45f * AimAssistMath.Smooth(0, 5, target.Distance))
                * (1 - .5f * AimAssistMath.Smooth(25, 60, target.Distance));
            float targetRangeScale = 1 - .4f * AimAssistMath.Smooth(25, 60, target.Distance);
            float bubble = 1 - AimAssistMath.Smooth(profile.Inner * targetRangeScale, profile.ReleaseCone * targetRangeScale,
                AimAssistMath.SelectionError(target, profile).Length());
            // Zoom and sensitivity must not make a fully deflected stick unable to
            // escape the target. Respect physical stick intent as well as angular speed.
            Vector2 stickDirection = raw.LengthSquared() > .00000001f
                ? Vector2.Normalize(raw) * Math.Clamp(stickIntent, 0, 1) : Vector2.Zero;
            float opposeX = Math.Min(AimAssistMath.Opposition(raw.X / (dt * 60), error.X),
                AimAssistMath.Opposition(stickDirection.X, error.X));
            float opposeY = Math.Min(AimAssistMath.Opposition(raw.Y / (dt * 60), error.Y),
                AimAssistMath.Opposition(stickDirection.Y, error.Y));
            float friction = 1 - (1 - AimAssistTuning.MinimumFriction) * bubble * distanceStrength * intent;
            Vector2 adjusted = new(raw.X * (1 - (1 - friction) * opposeX),
                raw.Y * (1 - (1 - friction) * opposeY));

            float strength = intent * distanceStrength * bubble * profile.Rotation
                * AimAssistTuning.RotationAssistMultiplier;
            Vector2 unrestricted = new(
                (error.X * AimAssistTuning.RotationErrorGain * AimAssistTuning.HorizontalRotation
                    + trackedVelocity.X * AimAssistTuning.MotionTracking) * opposeX * strength,
                (error.Y * AimAssistTuning.RotationErrorGain * AimAssistTuning.VerticalRotation
                    + trackedVelocity.Y * AimAssistTuning.MotionTracking) * opposeY * strength);
            // The profile speed is an actual degrees/second limit, including diagonals.
            Vector2 limited = AimAssistMath.ClampLength(unrestricted, profile.MaxSpeed);
            Vector2 rotation = limited * dt;
            Vector2 remaining = error + trackedVelocity * dt - adjusted;
            Vector2 bounded = new(AimAssistMath.LimitCorrection(rotation.X, remaining.X),
                AimAssistMath.LimitCorrection(rotation.Y, remaining.Y));
            bool saturated = limited != unrestricted || bounded != rotation;
            Vector2 output = adjusted + bounded;
            state.PreviousError = target.BodyError;
            state.PreviousHeadError = target.HeadError;
            state.PreviousOutput = output;
            state.PreviousDeltaTime = dt;
            state.PreviousBodyVisible = target.BodyVisible;
            state.PreviousHeadVisible = visibleHead;
            return new(output.X, output.Y, target.Slot, friction, strength,
                state.HeadBlend > 0 ? AimAssistPointType.Head : target.BodyPointType,
                state.HeadBlend, bestScore, bestAlignment, predictionAmount, false, saturated, state.RetainedSeconds);
        }
        private static Vector2 TrackVelocity(Vector2 filtered, Vector2 error, Vector2 previous,
            Vector2 cameraDelta, float previousDt, float dt, bool history, float rate)
        {
            if (!history || previousDt <= 0) return Vector2.Zero;
            Vector2 motion = AimAssistMath.AngularDelta(error, previous) + cameraDelta;
            if (!AimAssistMath.Finite(motion) || motion.Length() > AimAssistTuning.MotionDiscontinuity)
                return Vector2.Zero;
            Vector2 velocity = AimAssistMath.ClampLength(motion / previousDt, AimAssistTuning.MaxTrackedSpeed);
            // Catch changes in direction promptly instead of leading ahead of a target
            // that has already reversed its strafe or reached the apex of a jump.
            if (Vector2.Dot(filtered, velocity) < 0) rate *= 2.5f;
            return Vector2.Lerp(filtered, velocity, 1 - MathF.Exp(-rate * dt));
        }
    }
}
