using System;
using System.Numerics;

namespace MphRead.Mods.Input.AimAssist
{
    // Pure, allocation-free camera intent processing. No access to networking or damage.
    public static class AimAssist
    {
        public static AimAssistResult Apply(AimAssistState state, ReadOnlySpan<AimAssistTarget> targets,
            Vector2 raw, float stickIntent, float moveIntent, float dt, bool eligible, AimAssistWeaponProfile profile)
            => Apply(state, targets, raw,
                raw.LengthSquared() > 0 ? Vector2.Normalize(raw) * stickIntent : new Vector2(stickIntent, 0),
                moveIntent, dt, eligible, profile);

        public static AimAssistResult Apply(AimAssistState state, ReadOnlySpan<AimAssistTarget> targets,
            Vector2 raw, Vector2 physicalStick, float moveIntent, float dt, bool eligible,
            AimAssistWeaponProfile profile, bool firing = false)
        {
            float stickIntent = physicalStick.Length();
            if (!AimAssistMath.Finite(raw)) raw = Vector2.Zero;
            if (!eligible || !AimAssistMath.Finite(physicalStick) || !float.IsFinite(dt) || dt <= 0 || dt > .1f)
            {
                state.Reset();
                return new(raw.X, raw.Y);
            }

            float intent = float.IsFinite(stickIntent)
                ? AimAssistMath.Smooth(AimAssistTuning.IntentStart, AimAssistTuning.IntentFull, stickIntent) : 0;
            state.SecondsSinceLookIntent = intent > 0 ? 0 : state.SecondsSinceLookIntent + dt;
            bool strafe = intent == 0 && state.TargetSlot >= 0 && state.RetainedSeconds >= .10f
                && moveIntent >= .15f && state.SecondsSinceLookIntent <= .30f;
            float previousMagnitude = state.PreviousStick.Length();
            bool flickStart = stickIntent >= .65f && (previousMagnitude < .35f
                || (stickIntent - previousMagnitude) / dt > 18);
            if (flickStart)
            {
                state.FlickActive = true; state.FlickConsumed = false; state.FlickAge = 0;
                state.FlickDirection = Vector2.Normalize(physicalStick); state.FlickPeak = stickIntent;
                state.FlickTarget = state.TargetSlot;
            }
            else state.FlickAge += dt;
            state.PreviousStick = physicalStick;
            state.FlickActive &= intent > 0 && state.FlickAge <= AimAssistTuning.HeadFlickCaptureSeconds;
            if (intent == 0 && !strafe)
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
                if ((intent == 0 || (state.FlickActive && state.FlickTarget >= 0)) && !keep) continue;
                float rangeScale = 1 - .4f * AimAssistMath.Smooth(25, 60, t.Distance);
                float cone = (keep ? profile.ReleaseCone : profile.Cone) * rangeScale;
                Vector2 selectionError = AimAssistMath.SelectionError(t, profile);
                float angle = selectionError.Length();
                if (!t.Eligible || !AimAssistMath.Finite(selectionError) || !AimAssistMath.Finite(t.BodyError) || !float.IsFinite(t.Distance)
                    || t.Distance < .2f || t.Distance > 60 || angle > cone)
                {
                    continue;
                }
                if (!t.BodyVisible && !AimAssistMath.VisibleHead(t, profile))
                {
                    if (keep) occludedRetained = i;
                    continue;
                }

                float alignment = AimAssistMath.Alignment(physicalStick, selectionError);
                float score = AimAssistMath.Score(angle, cone, t.Distance, keep,
                    keep ? Math.Min(state.AngularVelocity.Length() / 45, 1) : 0, alignment)
                    + (angle == 0 ? .30f : .12f * (1 - AimAssistMath.Smooth(0, 1, angle)))
                    + (keep && firing ? .18f : 0) - (!keep && state.TargetSlot >= 0 ? .12f * (1 - alignment) : 0);
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
                    state.TrackingState = AimAssistTrackingState.OccludedRetention;
                    state.FlickActive = false;
                    state.OccludedSeconds += dt;
                    state.HeadBlend = state.HeadCandidateSeconds = 0;
                    state.PreviousBodyVisible = state.PreviousHeadVisible = false;
                    state.AngularVelocity = state.HeadAngularVelocity = Vector2.Zero;
                    state.PreviousError = hidden.BodyError;
                    state.PreviousHeadError = hidden.HeadError;
                    state.PreviousOutput = raw;
                    state.PreviousDeltaTime = dt;
                    return new(raw.X, raw.Y, hidden.Slot, 1, 0, hidden.BodyPointType, 0, 0,
                        AimAssistMath.Alignment(physicalStick, hidden.BodyError), 0, true, false, state.RetainedSeconds, AimAssistTrackingState.OccludedRetention, StickIntent: physicalStick, Firing: firing);
                }
                // Target loss must not turn a held stick into a new flick next tick.
                bool pendingFlick = state.TargetSlot < 0 && state.FlickActive;
                Vector2 pendingDirection = state.FlickDirection;
                float pendingAge = state.FlickAge;
                state.Reset();
                state.PreviousStick = physicalStick;
                state.FlickActive = pendingFlick;
                state.FlickDirection = pendingDirection;
                state.FlickAge = pendingAge;
                return new(raw.X, raw.Y, StickIntent: physicalStick, FlickActive: pendingFlick,
                    FlickAge: pendingAge, Firing: firing);
            }

            state.OccludedSeconds = 0;
            bool deliberate = stickIntent > .65f && bestAlignment > .85f;
            float challengerRatio = profile.Scoped ? 1.6f : profile.Precision ? 1.45f : AimAssistTuning.ChallengerRatio;
            if (retained >= 0 && best != retained
                && (state.FlickActive || bestScore < retainedScore * (deliberate ? 1.05f : challengerRatio)
                    || bestScore - retainedScore < (deliberate ? .03f : .10f)))
            {
                best = retained;
                bestScore = retainedScore;
                bestAlignment = AimAssistMath.Alignment(physicalStick, AimAssistMath.SelectionError(targets[retained], profile));
            }

            ref readonly var target = ref targets[best];
            bool same = state.TargetSlot == target.Slot && state.TargetLife == target.Life;
            if (!same)
            {
                bool active = state.FlickActive; Vector2 direction = state.FlickDirection;
                float age = state.FlickAge;
                state.Reset();
                state.PreviousStick = physicalStick; state.FlickActive = active;
                state.FlickDirection = direction; state.FlickAge = age; state.FlickTarget = target.Slot;
            }
            Vector2 selection = AimAssistMath.SelectionError(target, profile);
            if (stickIntent > .20f && selection.LengthSquared() > .0001f
                && Vector2.Dot(physicalStick, Vector2.Normalize(selection)) < -.20f)
            {
                state.Reset();
                state.PreviousStick = physicalStick;
                return new(raw.X, raw.Y, StickIntent: physicalStick, OpposingBreak: true, Firing: firing);
            }
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

            Vector2 bodyError = AimAssistMath.BodyError(target), headError = AimAssistMath.HeadError(target);
            float bodyAngle = bodyError.Length();
            float headAngle = visibleHead ? headError.Length() : float.MaxValue;
            float headAlignment = AimAssistMath.Alignment(physicalStick, headError);
            bool intentionalHead = stickIntent > .18f && headAlignment > .45f;
            // Tiny counter-steering is part of tracking. Only a meaningful turn away
            // cancels head refinement; a sign change at the crosshair must not chatter.
            bool opposingHead = stickIntent > .20f && visibleHead
                && Vector2.Dot(physicalStick, headError) < 0 && (raw.Length() / dt > 12 || stickIntent > .60f);
            if (opposingHead && (state.HeadBlend > .01f || state.FlickActive))
            {
                state.Reset(); state.PreviousStick = physicalStick;
                return new(raw.X, raw.Y, StickIntent: physicalStick, OpposingBreak: true, Firing: firing);
            }
            float radius = float.IsFinite(target.HeadRadiusDegrees)
                ? Math.Clamp(target.HeadRadiusDegrees, .05f, 3f) : .6f;
            float headCone = Math.Max(AimAssistTuning.HeadAcquireCone, radius * 1.5f);
            if (state.HeadBlend > .01f) headCone *= AimAssistTuning.HeadReleaseCone / AimAssistTuning.HeadAcquireCone;
            headCone = Math.Min(headCone, profile.Cone);
            float delay = intentionalHead ? AimAssistTuning.IntentionalHeadDelay : AimAssistTuning.HeadDelay;
            bool headOnly = !target.BodyVisible && visibleHead;
            bool headCandidate = visibleHead && headAngle < headCone && !opposingHead
                && (headAngle < bodyAngle * .95f || intentionalHead || headOnly || (strafe && state.HeadBlend > .5f));
            state.HeadCandidateSeconds = headCandidate ? state.HeadCandidateSeconds + dt : 0;
            bool head = headCandidate && same && state.HeadCandidateSeconds >= delay;

            Vector2 predictedHead = visibleHead ? headError : bodyError;
            float predictionAmount = 0;
            // Hitscan precision uses the current region. Motion is applied only as
            // feed-forward camera velocity below, never as an impact-point lead.

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

            if (visibleHead && headAngle == 0 && !opposingHead) state.HeadBlend = 1;
            Vector2 error = Vector2.Lerp(bodyError, predictedHead, state.HeadBlend);
            Vector2 trackedVelocity = Vector2.Lerp(state.AngularVelocity, state.HeadAngularVelocity, state.HeadBlend);
            float distanceStrength = (.55f + .45f * AimAssistMath.Smooth(0, 5, target.Distance))
                * (1 - .5f * AimAssistMath.Smooth(25, 60, target.Distance));
            float targetRangeScale = 1 - .4f * AimAssistMath.Smooth(25, 60, target.Distance);
            float bubble = 1 - AimAssistMath.Smooth(profile.Inner * targetRangeScale, profile.ReleaseCone * targetRangeScale,
                AimAssistMath.SelectionError(target, profile).Length());
            // Zoom and sensitivity must not make a fully deflected stick unable to
            // escape the target. Respect physical stick intent as well as angular speed.
            Vector2 stickDirection = physicalStick;
            float opposeX = AimAssistMath.Opposition(stickDirection.X, error.X);
            float opposeY = AimAssistMath.Opposition(stickDirection.Y, error.Y);
            float friction = 1 - profile.FrictionStrength * bubble * distanceStrength * intent;
            Vector2 adjusted = raw;
            if (error.LengthSquared() > .000001f)
            {
                Vector2 normal = Vector2.Normalize(error);
                Vector2 radial = normal * Vector2.Dot(raw, normal);
                Vector2 tangent = raw - radial;
                // Approaching the region preserves most input; tangential motion gets precision damping.
                adjusted = radial * (Vector2.Dot(raw, normal) > 0 ? 1 - (1 - friction) * .25f : friction)
                    + tangent * friction;
            }
            else adjusted *= friction;
            if (visibleHead && headAngle == 0) adjusted.Y *= .85f;

            float strength = intent * distanceStrength * bubble * profile.Rotation
                * AimAssistTuning.RotationAssistMultiplier;
            Vector2 position = new(error.X * (1 + state.HeadBlend * (AimAssistTuning.HeadHorizontalPositionGain - 1)),
                error.Y * (1 + state.HeadBlend * (AimAssistTuning.HeadVerticalPositionGain - 1)));
            position = AimAssistMath.ClampLength(position * profile.PositionGain * strength,
                profile.MaxPositionSpeed) * dt;
            Vector2 tracking = new(trackedVelocity.X * (1 + state.HeadBlend * (AimAssistTuning.HeadHorizontalTrackingGain - 1)),
                trackedVelocity.Y * (1 + state.HeadBlend * (AimAssistTuning.HeadVerticalTrackingGain - 1)));
            tracking = AimAssistMath.ClampLength(tracking * profile.TrackingGain * bubble
                * (strafe ? .28f : same ? intent : 0), profile.MaxTrackingSpeed) * dt;
            if (strafe) position = Vector2.Zero;
            position *= new Vector2(opposeX, opposeY);
            tracking *= new Vector2(opposeX, opposeY);
            float flickAlignment = AimAssistMath.Alignment(state.FlickDirection, headError);
            float captureRadius = target.HeadRegion is { } region
                ? Math.Clamp((region.MaxPitch - region.MinPitch) * 1.5f, .35f, .8f) : .5f;
            if (profile.Scoped) captureRadius *= .65f;
            bool capture = state.FlickActive && !state.FlickConsumed && visibleHead && !opposingHead
                && state.FlickTarget == target.Slot && headAngle > 0 && headAngle <= captureRadius
                && flickAlignment >= .80f && headAlignment >= .80f;
            if (capture)
            {
                Vector2 safe = target.HeadRegion is { } r ? AimAssistMath.RegionError(r.Inset(.15f)) : headError;
                position = safe * (1 - MathF.Exp(-AimAssistTuning.HeadFlickSnapGain * dt));
                error = safe; state.HeadBlend = 1;
            }
            if (headAngle == 0) state.FlickConsumed = true;
            Vector2 rotation = AimAssistMath.ClampLength(position + tracking, profile.MaxSpeed * dt);
            Vector2 remaining = error + trackedVelocity * dt - adjusted;
            Vector2 bounded = new(AimAssistMath.LimitCorrection(rotation.X, remaining.X),
                AimAssistMath.LimitCorrection(rotation.Y, remaining.Y));
            bool saturated = bounded != rotation || rotation != position + tracking;
            Vector2 output = adjusted + bounded;
            state.TrackingState = capture ? AimAssistTrackingState.FlickCapturingHead
                : state.HeadBlend > .01f ? headAngle == 0 ? AimAssistTrackingState.TrackingHead : AimAssistTrackingState.RefiningHead
                : state.RetainedSeconds >= .10f ? AimAssistTrackingState.TrackingBody : AimAssistTrackingState.AcquiringBody;
            state.PreviousError = target.BodyError;
            state.PreviousHeadError = target.HeadError;
            state.PreviousOutput = output;
            state.PreviousDeltaTime = dt;
            state.PreviousBodyVisible = target.BodyVisible;
            state.PreviousHeadVisible = visibleHead;
            return new(output.X, output.Y, target.Slot, friction, strength,
                state.HeadBlend > 0 ? AimAssistPointType.Head : target.BodyPointType,
                state.HeadBlend, bestScore, bestAlignment, predictionAmount, false, saturated, state.RetainedSeconds,
                state.TrackingState, position, tracking, physicalStick, state.FlickActive, state.FlickAge,
                flickAlignment, strafe, false, firing);
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
            float xRate = filtered.X * velocity.X < 0 ? rate * 2.5f : rate;
            float yRate = filtered.Y * velocity.Y < 0 ? rate * 2.5f : rate;
            return new(filtered.X + (velocity.X - filtered.X) * (1 - MathF.Exp(-xRate * dt)),
                filtered.Y + (velocity.Y - filtered.Y) * (1 - MathF.Exp(-yRate * dt)));
        }
    }
}
