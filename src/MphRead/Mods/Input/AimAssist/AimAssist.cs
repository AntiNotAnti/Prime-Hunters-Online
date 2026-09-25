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
            bool strafe = intent == 0 && state.TargetSlot >= 0
                && state.BodyTrackingConfidence >= AimAssistTuning.TrackingConfidenceMin
                && moveIntent >= .15f && state.SecondsSinceLookIntent <= .45f;

            Vector2 cameraVelocity = raw / dt;
            Vector2 cameraAcceleration = AimAssistMath.ClampLength(
                (cameraVelocity - state.PreviousCameraVelocity) / dt, 900f);
            float previousMagnitude = state.PreviousStick.Length();
            Vector2 stickDelta = physicalStick - state.PreviousStick;
            float directionalSpeed = stickDelta.Length() / dt;
            float historySpeed = (physicalStick - state.StickHistory2).Length() / Math.Max(2 * dt, .0001f);
            float directionDot = previousMagnitude > .0001f && stickIntent > .0001f
                ? Vector2.Dot(state.PreviousStick, physicalStick) / (previousMagnitude * stickIntent) : 1;
            bool magnitudeFlick = stickIntent >= .65f && (previousMagnitude < .35f
                || (stickIntent - previousMagnitude) / dt > 18);
            bool directionalFlick = stickIntent >= AimAssistTuning.FlickDirectionalMinMagnitude
                && previousMagnitude >= .35f && directionalSpeed >= AimAssistTuning.FlickDirectionalSpeed
                && directionDot < .92f;
            if (!state.FlickActive && (magnitudeFlick || directionalFlick))
            {
                Vector2 flick = directionalFlick && stickDelta.LengthSquared() > .0001f
                    ? stickDelta : physicalStick;
                state.FlickActive = true; state.FlickConsumed = false; state.FlickAge = 0;
                state.FlickDirection = Vector2.Normalize(flick); state.FlickPeak = stickIntent;
                state.FlickSpeed = Math.Max(directionalSpeed, historySpeed);
                state.FlickBraking = false;
                // A new flick gets one trajectory-weighted selection pass.
                state.FlickTarget = -1;
            }
            else
            {
                state.FlickAge += dt;
                if (state.FlickActive)
                {
                    float speed = Math.Max(directionalSpeed, historySpeed);
                    state.FlickBraking = state.FlickAge > dt
                        && state.FlickSpeed > AimAssistTuning.FlickDirectionalSpeed
                        && speed <= state.FlickSpeed * AimAssistTuning.FlickBrakeRatio;
                    state.FlickSpeed = Math.Max(state.FlickSpeed, speed);
                }
            }
            state.PushStick(physicalStick);
            state.PreviousStick = physicalStick;
            state.FlickActive &= intent > 0 && state.FlickAge <= AimAssistTuning.HeadFlickCaptureSeconds;
            if (!state.FlickActive)
            {
                state.FlickTarget = -1;
                state.FlickBraking = false;
            }

            state.ShotCommitSeconds = Math.Max(0, state.ShotCommitSeconds - dt);
            bool firePressed = firing && !state.PreviousFiring;
            if (firePressed && state.TargetSlot >= 0)
            {
                bool nearCommit = profile.Precision
                    ? state.PreviousInsideHead || state.PreviousHeadError.Length() <= .65f
                    : state.PreviousInsideHead || state.PreviousInsideBody
                        || state.PreviousError.Length() <= .5f;
                if (nearCommit) state.ShotCommitSeconds = AimAssistTuning.ShotCommitSeconds;
            }
            state.PreviousFiring = firing;
            bool shotCommitted = state.ShotCommitSeconds > 0;

            if (intent == 0 && !strafe)
            {
                state.Reset();
                return new(raw.X, raw.Y);
            }

            Vector2 trajectoryTravel = cameraVelocity * AimAssistTuning.TrajectoryHorizon;
            bool flickSelecting = state.FlickActive && state.FlickTarget < 0;
            int best = -1, retained = -1, occludedRetained = -1;
            float bestScore = -1, retainedScore = -1, bestAlignment = 0;
            for (int i = 0; i < targets.Length; i++)
            {
                ref readonly var t = ref targets[i];
                bool keep = t.Slot == state.TargetSlot && t.Life == state.TargetLife;
                if (intent == 0 && !keep) continue;
                if (state.FlickActive && state.FlickTarget >= 0 && !keep) continue;
                float rangeScale = 1 - .4f * AimAssistMath.Smooth(25, 60, t.Distance);
                float cone = (keep ? profile.ReleaseCone : profile.Cone) * rangeScale;
                Vector2 selectionError = AimAssistMath.SelectionError(t, profile);
                float angle = selectionError.Length();
                if (!t.Eligible || !AimAssistMath.Finite(selectionError) || !AimAssistMath.Finite(t.BodyError)
                    || !float.IsFinite(t.Distance) || t.Distance < .2f || t.Distance > 60 || angle > cone)
                {
                    continue;
                }

                bool candidateHeadVisible = AimAssistMath.VisibleHead(t, profile);
                if (!t.BodyVisible && !candidateHeadVisible)
                {
                    if (keep) occludedRetained = i;
                    continue;
                }

                float alignment = AimAssistMath.Alignment(physicalStick, selectionError);
                float score = AimAssistMath.Score(angle, cone, t.Distance, keep,
                    keep ? Math.Min(state.AngularVelocity.Length() / 45, 1) : 0, alignment)
                    + (angle == 0 ? .30f : .12f * (1 - AimAssistMath.Smooth(0, 1, angle)))
                    + (keep && firing ? .18f : 0)
                    - (!keep && state.TargetSlot >= 0 ? .12f * (1 - alignment) : 0);

                if (flickSelecting)
                {
                    if (candidateHeadVisible)
                    {
                        // Trajectory selection needs a direction even after the
                        // reticle has already entered the valid headshot band.
                        // RegionError is zero there, so score the flick against
                        // the projected head center and use the region only for
                        // final capture/correction.
                        Vector2 flickHead = AimAssistMath.Finite(t.HeadError)
                            ? t.HeadError : AimAssistMath.HeadError(t);
                        float candidateFlickAlignment = AimAssistMath.Alignment(state.FlickDirection, flickHead);
                        if (!keep && candidateFlickAlignment < AimAssistTuning.FlickTargetAlignment) continue;
                        float headDistance = flickHead.Length();
                        score += .65f * candidateFlickAlignment
                            + .20f * (1 - AimAssistMath.Smooth(0, Math.Max(.25f, Math.Min(2, cone)), headDistance));
                    }
                    else if (!keep)
                    {
                        continue;
                    }
                }

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
                    state.TrackingConfidence = Math.Max(0, state.TrackingConfidence
                        - AimAssistTuning.TrackingConfidenceDecayRate * dt);
                    state.HeadBlend = state.HeadCandidateSeconds = 0;
                    state.PreviousBodyVisible = state.PreviousHeadVisible = false;
                    state.AngularVelocity = state.HeadAngularVelocity = Vector2.Zero;
                    state.AngularAcceleration = state.HeadAngularAcceleration = Vector2.Zero;
                    state.PreviousError = hidden.BodyError;
                    state.PreviousHeadError = hidden.HeadError;
                    state.PreviousOutput = raw;
                    state.PreviousDeltaTime = dt;
                    return new(raw.X, raw.Y, hidden.Slot, 1, 0, hidden.BodyPointType, 0, 0,
                        AimAssistMath.Alignment(physicalStick, hidden.BodyError), 0, true, false,
                        state.RetainedSeconds, AimAssistTrackingState.OccludedRetention,
                        StickIntent: physicalStick, Firing: firing);
                }
                // Target loss may preserve a not-yet-targeted flick for the remainder
                // of its tiny capture window, but never preserve target history.
                bool pendingFlick = state.FlickActive && state.FlickTarget < 0;
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
            if (!flickSelecting && retained >= 0 && best != retained
                && (bestScore < retainedScore * (deliberate ? 1.05f : challengerRatio)
                    || bestScore - retainedScore < (deliberate ? .03f : .10f)))
            {
                best = retained;
                bestScore = retainedScore;
                bestAlignment = AimAssistMath.Alignment(physicalStick,
                    AimAssistMath.SelectionError(targets[retained], profile));
            }

            ref readonly var target = ref targets[best];
            if (flickSelecting) state.FlickTarget = target.Slot;
            bool same = state.TargetSlot == target.Slot && state.TargetLife == target.Life;
            if (!same)
            {
                bool active = state.FlickActive; Vector2 direction = state.FlickDirection;
                float age = state.FlickAge; int flickTarget = state.FlickTarget;
                state.Reset();
                state.PreviousStick = physicalStick; state.FlickActive = active;
                state.FlickDirection = direction; state.FlickAge = age; state.FlickTarget = flickTarget;
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

            float confidenceGoal = 0;
            if (intent > 0)
            {
                bool alreadyOverTarget = selection.Length() <= profile.Inner;
                confidenceGoal = alreadyOverTarget ? .9f : Math.Clamp(.25f + .75f * bestAlignment, 0, 1);
                float rate = confidenceGoal > state.TrackingConfidence
                    ? AimAssistTuning.TrackingConfidenceRiseRate
                    : AimAssistTuning.TrackingConfidenceDecayRate;
                state.TrackingConfidence += (confidenceGoal - state.TrackingConfidence)
                    * (1 - MathF.Exp(-rate * dt));
            }
            else if (strafe)
            {
                state.TrackingConfidence = Math.Max(0, state.TrackingConfidence
                    - AimAssistTuning.TrackingConfidenceStrafeDecayRate * dt);
            }

            bool visibleHead = AimAssistMath.VisibleHead(target, profile);
            Vector2 bodyAcceleration = state.AngularAcceleration;
            state.AngularVelocity = TrackMotion(state.AngularVelocity, bodyAcceleration, target.BodyError,
                state.PreviousError, state.PreviousOutput, state.PreviousDeltaTime, dt,
                hasHistory && state.PreviousBodyVisible && target.BodyVisible,
                AimAssistTuning.VelocityFilterRate, out bodyAcceleration);
            state.AngularAcceleration = bodyAcceleration;
            Vector2 headAcceleration = state.HeadAngularAcceleration;
            state.HeadAngularVelocity = TrackMotion(state.HeadAngularVelocity, headAcceleration, target.HeadError,
                state.PreviousHeadError, state.PreviousOutput, state.PreviousDeltaTime, dt,
                hasHistory && state.PreviousHeadVisible && visibleHead,
                AimAssistTuning.HeadVelocityFilterRate, out headAcceleration);
            state.HeadAngularAcceleration = headAcceleration;

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
            if (state.HeadBlend > .01f)
                headCone *= AimAssistTuning.HeadReleaseCone / AimAssistTuning.HeadAcquireCone;
            headCone = Math.Min(headCone, profile.Cone);
            float delay = intentionalHead ? AimAssistTuning.IntentionalHeadDelay : AimAssistTuning.HeadDelay;
            bool headOnly = !target.BodyVisible && visibleHead;
            bool headCandidate = visibleHead && headAngle < headCone && !opposingHead
                && (headAngle < bodyAngle * .95f || intentionalHead || headOnly
                    || (strafe && state.HeadBlend > .5f));
            state.HeadCandidateSeconds = headCandidate ? state.HeadCandidateSeconds + dt : 0;
            bool head = headCandidate && same && state.HeadCandidateSeconds >= delay;

            float predictionAmount = 0;
            // Hitscan precision uses the current region. Motion is applied only as
            // feed-forward camera velocity below, never as an impact-point lead.
            float proximity = 1 - AimAssistMath.Smooth(radius, headCone, headAngle);
            float maxHead = intentionalHead ? AimAssistTuning.IntentionalMaxHeadBlend : AimAssistTuning.MaxHeadBlend;
            if (headAngle <= radius) maxHead = 1;
            float desiredHead = head ? maxHead * (.35f + .65f * proximity) : 0;
            if (!visibleHead) state.HeadBlend = 0;
            else if (headOnly) state.HeadBlend = 1;
            else
            {
                float blendRate = head ? AimAssistTuning.HeadBlendRate : AimAssistTuning.HeadFallbackRate;
                state.HeadBlend += (desiredHead - state.HeadBlend) * (1 - MathF.Exp(-blendRate * dt));
                if (state.HeadBlend < .001f) state.HeadBlend = 0;
            }

            if (visibleHead && headAngle == 0 && !opposingHead) state.HeadBlend = 1;

            // Inside the real headshot band, use a weak interior pocket rather than
            // continuing to pull toward exact head center. The outer band remains the
            // hit rule; the inset merely gives tracking room before an edge is crossed.
            Vector2 headPositionError = headError;
            if (visibleHead && headAngle == 0 && target.HeadRegion is { } headRegion)
            {
                Vector2 safe = AimAssistMath.SafeRegionError(headRegion, AimAssistTuning.HeadSafeInset);
                if (safe.LengthSquared() > .000001f)
                    headPositionError = safe * AimAssistTuning.HeadSafePositionScale;
            }

            Vector2 error = Vector2.Lerp(bodyError, visibleHead ? headPositionError : bodyError, state.HeadBlend);
            Vector2 trackedVelocity = Vector2.Lerp(state.AngularVelocity,
                state.HeadAngularVelocity, state.HeadBlend);
            Vector2 trackedAcceleration = Vector2.Lerp(state.AngularAcceleration,
                state.HeadAngularAcceleration, state.HeadBlend);
            Vector2 servoVelocity = AimAssistMath.ClampLength(
                trackedVelocity + trackedAcceleration * AimAssistTuning.MotionServoLookahead,
                AimAssistTuning.MaxTrackedSpeed);

            float distanceStrength = (.55f + .45f * AimAssistMath.Smooth(0, 5, target.Distance))
                * (1 - .5f * AimAssistMath.Smooth(25, 60, target.Distance));
            float targetRangeScale = 1 - .4f * AimAssistMath.Smooth(25, 60, target.Distance);
            float bubble = 1 - AimAssistMath.Smooth(profile.Inner * targetRangeScale,
                profile.ReleaseCone * targetRangeScale, AimAssistMath.SelectionError(target, profile).Length());

            Vector2 stickDirection = physicalStick;
            float opposeX = AimAssistMath.Opposition(stickDirection.X, error.X);
            float opposeY = AimAssistMath.Opposition(stickDirection.Y, error.Y);
            float frictionStrength = profile.FrictionStrength * bubble * distanceStrength * intent;
            float friction = 1 - frictionStrength;
            Vector2 adjusted = raw;
            AimAssistRegion? activeRegion = state.HeadBlend > .35f && visibleHead
                ? target.HeadRegion : target.BodyRegion;
            if (activeRegion is { } region && AimAssistMath.InsideRegion(region))
            {
                adjusted.X *= AimAssistMath.EdgeFrictionFactor(raw.X, physicalStick.X,
                    region.MinYaw, region.MaxYaw, frictionStrength);
                adjusted.Y *= AimAssistMath.EdgeFrictionFactor(raw.Y, physicalStick.Y,
                    region.MinPitch, region.MaxPitch, frictionStrength);
                if (raw.LengthSquared() > .000001f)
                    friction = Math.Clamp(adjusted.Length() / raw.Length(), AimAssistTuning.MinimumFriction, 1);
                else friction = 1;
            }
            else if (error.LengthSquared() > .000001f)
            {
                Vector2 normal = Vector2.Normalize(error);
                Vector2 radial = normal * Vector2.Dot(raw, normal);
                Vector2 tangent = raw - radial;
                // Approaching a region preserves most input; overshoot/tangential
                // movement gets precision damping until deliberate stick intent wins.
                adjusted = radial * (Vector2.Dot(raw, normal) > 0 ? 1 - (1 - friction) * .25f : friction)
                    + tangent * friction;
            }

            float strength = intent * distanceStrength * bubble * profile.Rotation
                * AimAssistTuning.RotationAssistMultiplier;
            Vector2 position = new(error.X * (1 + state.HeadBlend
                    * (AimAssistTuning.HeadHorizontalPositionGain - 1)),
                error.Y * (1 + state.HeadBlend * (AimAssistTuning.HeadVerticalPositionGain - 1)));
            position = AimAssistMath.ClampLength(position * profile.PositionGain * strength,
                profile.MaxPositionSpeed) * dt;

            Vector2 tracking = new(servoVelocity.X * (1 + state.HeadBlend
                    * (AimAssistTuning.HeadHorizontalTrackingGain - 1)),
                servoVelocity.Y * (1 + state.HeadBlend * (AimAssistTuning.HeadVerticalTrackingGain - 1)));
            float trackingIntent = strafe
                ? AimAssistTuning.StrafeTrackingMinimum
                    + (AimAssistTuning.StrafeTrackingMaximum - AimAssistTuning.StrafeTrackingMinimum)
                    * state.TrackingConfidence
                : same ? intent : 0;
            tracking = AimAssistMath.ClampLength(tracking * profile.TrackingGain * bubble
                * trackingIntent, profile.MaxTrackingSpeed) * dt;
            if (strafe) position = Vector2.Zero;
            position *= new Vector2(opposeX, opposeY);
            tracking *= new Vector2(opposeX, opposeY);

            float flickAlignment = AimAssistMath.Alignment(state.FlickDirection, headError);
            float captureRadius = target.HeadRegion is { } captureRegion
                ? Math.Clamp((captureRegion.MaxPitch - captureRegion.MinPitch) * 1.5f, .35f, .8f) : .5f;
            if (profile.Scoped) captureRadius *= .65f;
            bool capture = state.FlickActive && !state.FlickConsumed && visibleHead && !opposingHead
                && state.FlickTarget == target.Slot && headAngle > 0 && headAngle <= captureRadius
                && flickAlignment >= .80f && headAlignment >= .55f;
            if (capture)
            {
                Vector2 safe = target.HeadRegion is { } r
                    ? AimAssistMath.SafeRegionError(r, AimAssistTuning.HeadSafeInset) : headError;
                position = safe * (1 - MathF.Exp(-AimAssistTuning.HeadFlickSnapGain * dt));
                error = safe; state.HeadBlend = 1;
            }
            if (headAngle == 0) state.FlickConsumed = true;

            // Position correction and motion tracking have independent caps. Do not
            // re-clamp their sum through the legacy MaxSpeed ceiling: that made a
            // scoped 18 deg/s tracking budget silently become 8 deg/s again.
            Vector2 rotation = position + tracking;
            Vector2 remaining = error + servoVelocity * dt - adjusted;
            Vector2 bounded = new(AimAssistMath.LimitCorrection(rotation.X, remaining.X),
                AimAssistMath.LimitCorrection(rotation.Y, remaining.Y));
            bool saturated = bounded != rotation;
            Vector2 output = adjusted + bounded;

            state.TrackingState = capture ? AimAssistTrackingState.FlickCapturingHead
                : state.HeadBlend > .01f
                    ? headAngle == 0 ? AimAssistTrackingState.TrackingHead : AimAssistTrackingState.RefiningHead
                    : state.TrackingConfidence >= AimAssistTuning.TrackingConfidenceMin
                        ? AimAssistTrackingState.TrackingBody : AimAssistTrackingState.AcquiringBody;
            state.PreviousError = target.BodyError;
            state.PreviousHeadError = target.HeadError;
            state.PreviousOutput = output;
            state.PreviousDeltaTime = dt;
            state.PreviousBodyVisible = target.BodyVisible;
            state.PreviousHeadVisible = visibleHead;
            return new(output.X, output.Y, target.Slot, friction, strength,
                state.HeadBlend > 0 ? AimAssistPointType.Head : target.BodyPointType,
                state.HeadBlend, bestScore, bestAlignment, predictionAmount, false, saturated,
                state.RetainedSeconds, state.TrackingState, position, tracking, physicalStick,
                state.FlickActive, state.FlickAge, flickAlignment, strafe, false, firing);
        }

        private static Vector2 TrackMotion(Vector2 filtered, Vector2 acceleration,
            Vector2 error, Vector2 previous, Vector2 cameraDelta, float previousDt,
            float dt, bool history, float rate, out Vector2 nextAcceleration)
        {
            nextAcceleration = Vector2.Zero;
            if (!history || previousDt <= 0) return Vector2.Zero;
            Vector2 motion = AimAssistMath.AngularDelta(error, previous) + cameraDelta;
            if (!AimAssistMath.Finite(motion) || motion.Length() > AimAssistTuning.MotionDiscontinuity)
                return Vector2.Zero;

            Vector2 measured = AimAssistMath.ClampLength(motion / previousDt,
                AimAssistTuning.MaxTrackedSpeed);

            // Filter measured velocity directly. Feeding the previous acceleration
            // back into this estimate creates an unstable loop under alternating
            // frame intervals: a constant-speed target can ring above/below its
            // real velocity. Acceleration is feed-forward for the camera, not a
            // state predictor for the velocity estimator itself.
            float xRate = filtered.X * measured.X < 0 ? rate * 2.5f : rate;
            float yRate = filtered.Y * measured.Y < 0 ? rate * 2.5f : rate;
            Vector2 velocity = new(
                filtered.X + (measured.X - filtered.X) * (1 - MathF.Exp(-xRate * dt)),
                filtered.Y + (measured.Y - filtered.Y) * (1 - MathF.Exp(-yRate * dt)));
            velocity = AimAssistMath.ClampLength(velocity, AimAssistTuning.MaxTrackedSpeed);

            // Estimate target angular acceleration from the velocity estimate's
            // actual change over this observation. At constant measured speed
            // this naturally decays to zero even with variable dt.
            Vector2 observedAcceleration = AimAssistMath.ClampLength(
                (velocity - filtered) / Math.Max(dt, .001f),
                AimAssistTuning.MaxTrackedAcceleration);
            float accelRate = AimAssistTuning.MotionAccelerationRate;
            if (filtered.X * measured.X < 0 || filtered.Y * measured.Y < 0) accelRate *= 1.75f;
            nextAcceleration = acceleration + (observedAcceleration - acceleration)
                * (1 - MathF.Exp(-accelRate * dt));
            nextAcceleration = AimAssistMath.ClampLength(nextAcceleration,
                AimAssistTuning.MaxTrackedAcceleration);
            if (!AimAssistMath.Finite(nextAcceleration)) nextAcceleration = Vector2.Zero;
            return velocity;
        }
    }
}
