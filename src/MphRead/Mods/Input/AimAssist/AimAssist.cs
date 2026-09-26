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
            AimAssistWeaponProfile profile, bool firing = false,
            AimAssistShotPhase shotPhase = AimAssistShotPhase.None)
        {
            // Legacy/test callers only supplied a firing boolean. Production
            // supplies the exact phase; treat an otherwise-unspecified firing
            // edge as a press so old callers retain their commitment semantics.
            if (shotPhase == AimAssistShotPhase.None && firing)
                shotPhase = AimAssistShotPhase.Pressed;
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
            bool haveCameraHistory = state.PreviousDeltaTime > 0;
            Vector2 fittedCameraVelocity = haveCameraHistory
                ? AimAssistMath.FittedCameraVelocity(cameraVelocity, state.CameraVelocity0,
                    state.CameraVelocity1, state.CameraVelocity2)
                : cameraVelocity;
            Vector2 cameraAcceleration = haveCameraHistory
                ? AimAssistMath.FittedCameraAcceleration(cameraVelocity, state.CameraVelocity0,
                    state.CameraVelocity1, dt)
                : Vector2.Zero;
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
            bool commitEvent = shotPhase is AimAssistShotPhase.Pressed
                or AimAssistShotPhase.Released or AimAssistShotPhase.Fired;
            if (commitEvent && state.TargetSlot >= 0)
            {
                bool nearCommit = profile.Precision
                    ? state.PreviousInsideHead
                        || state.PreviousHeadError.Length() <= .65f
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

            Vector2 trajectoryTravel = fittedCameraVelocity * AimAssistTuning.TrajectoryHorizon;
            bool flickSelecting = state.FlickActive && state.FlickTarget < 0;
            int best = -1, retained = -1, occludedRetained = -1;
            float bestScore = -1, retainedScore = -1, bestAlignment = 0;
            for (int i = 0; i < targets.Length; i++)
            {
                ref readonly var t = ref targets[i];
                bool keep = t.Slot == state.TargetSlot && t.Life == state.TargetLife;
                if (intent == 0 && !keep) continue;
                if (state.FlickActive && state.FlickTarget >= 0 && !keep) continue;
                if (shotCommitted && state.TargetSlot >= 0 && !keep) continue;
                float rangeScale = 1 - .4f * AimAssistMath.Smooth(25, 60, t.Distance);
                float cone = (keep ? profile.ReleaseCone : profile.Cone) * rangeScale;
                float normalizedLimit = keep ? profile.NormalizedRelease : profile.NormalizedAcquire;
                Vector2 selectionError = AimAssistMath.SelectionError(t, profile);
                float angle = selectionError.Length();
                float normalizedDistance = AimAssistMath.NormalizedSelectionDistance(t, profile);
                if (!t.Eligible || !AimAssistMath.Finite(selectionError) || !AimAssistMath.Finite(t.BodyError)
                    || !float.IsFinite(t.Distance) || t.Distance < .2f || t.Distance > 60
                    || angle > cone * 1.5f || normalizedDistance > normalizedLimit)
                {
                    continue;
                }

                bool candidateHeadVisible = AimAssistMath.VisibleHead(t, profile);
                if (!t.BodyVisible && !candidateHeadVisible)
                {
                    if (keep) occludedRetained = i;
                    continue;
                }
                if (!keep && Math.Max(t.BodyVisibility, t.HeadVisibility) < .20f)
                    continue;

                float alignment = AimAssistMath.Alignment(physicalStick, selectionError);
                float closeness = 1 - Math.Clamp(normalizedDistance / Math.Max(.1f, normalizedLimit), 0, 1);
                float score = .50f * closeness + (keep ? .15f : 0)
                    + .10f * (1 - Math.Clamp(t.Distance / 60, 0, 1)) + .08f
                    + .05f * (keep ? Math.Min(state.AngularVelocity.Length() / 45, 1) : 0)
                    + AimAssistTuning.InputAlignmentWeight * Math.Clamp(alignment, 0, 1)
                    + (normalizedDistance == 0 ? .30f : .12f * (1 - AimAssistMath.Smooth(0, 1, normalizedDistance)))
                    + (keep && firing ? .18f : 0)
                    - (!keep && state.TargetSlot >= 0 ? .12f * (1 - alignment) : 0);

                AimAssistRegion? trajectoryRegion = candidateHeadVisible
                    && t.HeadRegion is { } hr
                    && AimAssistMath.HeadError(t).LengthSquared() <= AimAssistMath.BodyError(t).LengthSquared()
                        ? hr : t.BodyRegion;
                if (trajectoryRegion is { } pathRegion)
                {
                    score += AimAssistTuning.TrajectoryScoreWeight
                        * AimAssistMath.TrajectoryRegionScore(pathRegion, trajectoryTravel);
                }

                if (flickSelecting)
                {
                    if (candidateHeadVisible)
                    {
                        // Trajectory selection needs a direction even after the
                        // reticle has entered the valid band, so use the head
                        // center for direction and the projected region for landing.
                        Vector2 flickHead = AimAssistMath.Finite(t.HeadError)
                            ? t.HeadError : AimAssistMath.HeadError(t);
                        float candidateFlickAlignment = AimAssistMath.Alignment(state.FlickDirection, flickHead);
                        if (!keep && candidateFlickAlignment < AimAssistTuning.FlickTargetAlignment) continue;
                        float headDistance = flickHead.Length();
                        float candidateSpeedT = AimAssistMath.Smooth(AimAssistTuning.FlickDirectionalSpeed,
                            45f, state.FlickSpeed);
                        float horizon = AimAssistTuning.FlickLandingMaxSeconds
                            + (AimAssistTuning.FlickLandingMinSeconds - AimAssistTuning.FlickLandingMaxSeconds)
                            * candidateSpeedT;
                        Vector2 candidatePredictedTurn = fittedCameraVelocity * horizon
                            + cameraAcceleration * (.5f * horizon * horizon);
                        Vector2 candidateTargetMotion = keep
                            ? state.HeadAngularVelocity * horizon : Vector2.Zero;
                        float landing = t.HeadRegion is { } flickRegion
                            ? AimAssistMath.NormalizedLandingMiss(flickRegion,
                                candidateTargetMotion - candidatePredictedTurn)
                            : (flickHead + candidateTargetMotion - candidatePredictedTurn).Length();
                        score += .65f * candidateFlickAlignment
                            + .20f * (1 - AimAssistMath.Smooth(0, Math.Max(.25f, Math.Min(2, cone)), headDistance))
                            + .35f * (1 - AimAssistMath.Smooth(0, 1.25f, landing));
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
                    state.BodyTrackingConfidence = Math.Max(0, state.BodyTrackingConfidence
                        - AimAssistTuning.TrackingConfidenceDecayRate * dt);
                    state.HeadTrackingConfidence = Math.Max(0, state.HeadTrackingConfidence
                        - AimAssistTuning.HeadConfidenceDecayRate * dt);
                    state.HeadBlend = state.HeadCandidateSeconds = 0;
                    state.PreviousBodyVisible = state.PreviousHeadVisible = false;
                    // Remember how the target was moving, but never update that
                    // estimate from hidden positions and never output correction
                    // through cover. It simply decays until visibility returns.
                    float decay = MathF.Exp(-AimAssistTuning.OccludedMotionDecayRate * dt);
                    state.AngularVelocity *= decay;
                    state.HeadAngularVelocity *= decay;
                    state.AngularAcceleration *= decay;
                    state.HeadAngularAcceleration *= decay;
                    state.PreviousOutput = raw;
                    state.PreviousDeltaTime = dt;
                    state.MotionPhase = AimAssistMotionPhase.None;
                    state.PreviousRaw = raw;
                    state.PreviousCameraVelocity = cameraVelocity;
                    state.PushCameraVelocity(cameraVelocity);
                    return new(raw.X, raw.Y, hidden.Slot, 1, 0, hidden.BodyPointType, 0, 0,
                        AimAssistMath.Alignment(physicalStick, hidden.BodyError), 0, true, false,
                        state.RetainedSeconds, AimAssistTrackingState.OccludedRetention,
                        StickIntent: physicalStick, Firing: firing,
                        BodyTrackingConfidence: state.BodyTrackingConfidence,
                        HeadTrackingConfidence: state.HeadTrackingConfidence,
                        VisibilityCoverage: 0);
                }
                // Target loss clears target motion/confidence, but input history is
                // not target state. Preserve recent physical-stick/camera samples so
                // a flick that begins in empty space can still be fitted when it
                // reaches a candidate a frame later.
                bool pendingFlick = state.FlickActive && state.FlickTarget < 0;
                Vector2 pendingDirection = state.FlickDirection;
                float pendingAge = state.FlickAge, pendingSpeed = state.FlickSpeed;
                float pendingPeak = state.FlickPeak;
                bool pendingBraking = state.FlickBraking;
                Vector2 stick0 = state.StickHistory0, stick1 = state.StickHistory1;
                Vector2 stick2 = state.StickHistory2, stick3 = state.StickHistory3;
                Vector2 camera0 = state.CameraVelocity0, camera1 = state.CameraVelocity1;
                Vector2 camera2 = state.CameraVelocity2, camera3 = state.CameraVelocity3;
                float savedBudget = Math.Max(0, state.CorrectionBudgetUsed
                    - profile.CorrectionBudgetRecovery * dt);
                float savedScope = state.ScopeBlend;
                state.Reset();
                state.PreviousStick = physicalStick;
                state.FlickActive = pendingFlick;
                state.FlickDirection = pendingDirection;
                state.FlickAge = pendingAge;
                state.FlickSpeed = pendingSpeed;
                state.FlickPeak = pendingPeak;
                state.FlickBraking = pendingBraking;
                state.StickHistory0 = stick0; state.StickHistory1 = stick1;
                state.StickHistory2 = stick2; state.StickHistory3 = stick3;
                state.CameraVelocity0 = camera0; state.CameraVelocity1 = camera1;
                state.CameraVelocity2 = camera2; state.CameraVelocity3 = camera3;
                state.CorrectionBudgetUsed = savedBudget;
                state.ScopeBlend = savedScope;
                state.PreviousCameraVelocity = cameraVelocity;
                state.PushCameraVelocity(cameraVelocity);
                return new(raw.X, raw.Y, StickIntent: physicalStick, FlickActive: pendingFlick,
                    FlickAge: pendingAge, Firing: firing, ScopeBlend: profile.ScopeBlend,
                    CorrectionBudget: profile.CorrectionBudgetDegrees <= 0 ? 0
                        : savedBudget / profile.CorrectionBudgetDegrees,
                    ShotPhase: shotPhase,
                    PlayerContribution: raw.Length());
            }

            bool wasOccluded = state.OccludedSeconds > 0;
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
                float age = state.FlickAge, flickSpeed = state.FlickSpeed, flickPeak = state.FlickPeak;
                bool flickBraking = state.FlickBraking, previousFiring = state.PreviousFiring;
                int flickTarget = state.FlickTarget;
                Vector2 history0 = state.StickHistory0, history1 = state.StickHistory1;
                Vector2 history2 = state.StickHistory2, history3 = state.StickHistory3;
                Vector2 previousRaw = state.PreviousRaw, previousCameraVelocity = state.PreviousCameraVelocity;
                Vector2 camera0 = state.CameraVelocity0, camera1 = state.CameraVelocity1;
                Vector2 camera2 = state.CameraVelocity2, camera3 = state.CameraVelocity3;
                float savedCorrectionBudget = state.CorrectionBudgetUsed, scopeBlend = state.ScopeBlend;
                state.Reset();
                state.PreviousStick = physicalStick; state.FlickActive = active;
                state.FlickDirection = direction; state.FlickAge = age; state.FlickTarget = flickTarget;
                state.FlickSpeed = flickSpeed; state.FlickPeak = flickPeak; state.FlickBraking = flickBraking;
                state.StickHistory0 = history0; state.StickHistory1 = history1;
                state.StickHistory2 = history2; state.StickHistory3 = history3;
                state.PreviousRaw = previousRaw; state.PreviousCameraVelocity = previousCameraVelocity;
                state.CameraVelocity0 = camera0; state.CameraVelocity1 = camera1;
                state.CameraVelocity2 = camera2; state.CameraVelocity3 = camera3;
                state.CorrectionBudgetUsed = savedCorrectionBudget; state.ScopeBlend = scopeBlend;
                state.PreviousFiring = previousFiring;
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
            float measuredBodyCoverage = Math.Clamp(target.BodyVisibility, 0, 1);
            float measuredHeadCoverage = visibleHead ? Math.Clamp(target.HeadVisibility, 0, 1) : 0;
            float bodyCoverageRate = measuredBodyCoverage < state.SmoothedBodyVisibility
                ? AimAssistTuning.VisibilityDecayRate : AimAssistTuning.VisibilityRiseRate;
            float headCoverageRate = measuredHeadCoverage < state.SmoothedHeadVisibility
                ? AimAssistTuning.VisibilityDecayRate : AimAssistTuning.VisibilityRiseRate;
            state.SmoothedBodyVisibility += (measuredBodyCoverage - state.SmoothedBodyVisibility)
                * (1 - MathF.Exp(-bodyCoverageRate * dt));
            state.SmoothedHeadVisibility += (measuredHeadCoverage - state.SmoothedHeadVisibility)
                * (1 - MathF.Exp(-headCoverageRate * dt));
            float bodyCoverage = state.SmoothedBodyVisibility;
            float confidenceGoal = 0;
            float normalizedSelection = AimAssistMath.NormalizedSelectionDistance(target, profile);
            if (intent > 0)
            {
                bool alreadyOverTarget = normalizedSelection <= profile.NormalizedInner;
                confidenceGoal = (alreadyOverTarget ? .9f : Math.Clamp(.25f + .75f * bestAlignment, 0, 1))
                    * (.70f + .30f * bodyCoverage);
                float rate = confidenceGoal > state.BodyTrackingConfidence
                    ? AimAssistTuning.TrackingConfidenceRiseRate
                    : AimAssistTuning.TrackingConfidenceDecayRate;
                state.BodyTrackingConfidence += (confidenceGoal - state.BodyTrackingConfidence)
                    * (1 - MathF.Exp(-rate * dt));
            }
            else if (strafe)
            {
                state.BodyTrackingConfidence = Math.Max(0, state.BodyTrackingConfidence
                    - AimAssistTuning.TrackingConfidenceStrafeDecayRate * dt);
            }

            Vector2 bodyAcceleration = state.AngularAcceleration;
            state.AngularVelocity = TrackMotion(state.AngularVelocity, bodyAcceleration, target.BodyError,
                state.PreviousError, state.PreviousOutput, state.PreviousDeltaTime, dt,
                hasHistory && state.PreviousBodyVisible && target.BodyVisible, wasOccluded,
                AimAssistTuning.VelocityFilterRate, ref state.MotionDirection,
                out bodyAcceleration, out bool bodyTransition);
            state.AngularAcceleration = bodyAcceleration;
            Vector2 headAcceleration = state.HeadAngularAcceleration;
            state.HeadAngularVelocity = TrackMotion(state.HeadAngularVelocity, headAcceleration, target.HeadError,
                state.PreviousHeadError, state.PreviousOutput, state.PreviousDeltaTime, dt,
                hasHistory && state.PreviousHeadVisible && visibleHead, wasOccluded,
                AimAssistTuning.HeadVelocityFilterRate, ref state.HeadMotionDirection,
                out headAcceleration, out bool headTransition);
            state.HeadAngularAcceleration = headAcceleration;
            state.MotionTransition = bodyTransition || headTransition;
            if (state.MotionTransition)
            {
                state.MotionTransitionSeconds = AimAssistTuning.MotionTransitionSeconds;
                state.ServoVelocity *= .35f;
            }
            else
            {
                state.MotionTransitionSeconds = Math.Max(0, state.MotionTransitionSeconds - dt);
                state.MotionTransition = state.MotionTransitionSeconds > 0;
            }

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
            bool headInside = visibleHead && AimAssistMath.InsideHead(target);
            float normalizedHead = visibleHead ? AimAssistMath.NormalizedHeadError(target).Length() : float.MaxValue;
            float normalizedHeadLimit = state.HeadBlend > .01f
                ? profile.NormalizedRelease : profile.NormalizedAcquire;
            bool headCandidate = visibleHead && headAngle < headCone
                && normalizedHead <= normalizedHeadLimit && !opposingHead
                && (normalizedHead < AimAssistMath.NormalizedBodyError(target).Length() * .95f
                    || intentionalHead || headOnly || (strafe && state.HeadBlend > .5f));
            state.HeadCandidateSeconds = headCandidate ? state.HeadCandidateSeconds + dt : 0;
            bool head = headCandidate && same && state.HeadCandidateSeconds >= delay;

            float headCoverage = state.SmoothedHeadVisibility;
            float headConfidenceGoal = headCandidate
                ? (headInside ? 1f : intentionalHead ? .9f : .65f) * (.65f + .35f * headCoverage)
                : 0;
            float headConfidenceRate = headConfidenceGoal > state.HeadTrackingConfidence
                ? AimAssistTuning.HeadConfidenceRiseRate : AimAssistTuning.HeadConfidenceDecayRate;
            state.HeadTrackingConfidence += (headConfidenceGoal - state.HeadTrackingConfidence)
                * (1 - MathF.Exp(-headConfidenceRate * dt));
            if (!visibleHead)
                state.HeadTrackingConfidence = Math.Max(0, state.HeadTrackingConfidence
                    - AimAssistTuning.HeadConfidenceDecayRate * dt);

            float predictionAmount = 0;
            // Hitscan precision uses the current region. Motion is applied only as
            // feed-forward camera velocity below, never as an impact-point lead.
            float proximity = 1 - AimAssistMath.Smooth(.35f,
                Math.Max(.5f, normalizedHeadLimit), normalizedHead);
            float maxHead = intentionalHead ? AimAssistTuning.IntentionalMaxHeadBlend : AimAssistTuning.MaxHeadBlend;
            if (headInside) maxHead = 1;
            float desiredHead = head ? maxHead * (.35f + .65f * proximity)
                * (.45f + .55f * state.HeadTrackingConfidence) : 0;
            if (!visibleHead) state.HeadBlend = 0;
            else if (headOnly)
            {
                state.HeadBlend = 1;
                state.HeadTrackingConfidence = Math.Max(state.HeadTrackingConfidence, .8f);
            }
            else
            {
                float blendRate = head ? AimAssistTuning.HeadBlendRate : AimAssistTuning.HeadFallbackRate;
                state.HeadBlend += (desiredHead - state.HeadBlend) * (1 - MathF.Exp(-blendRate * dt));
                if (state.HeadBlend < .001f) state.HeadBlend = 0;
            }

            if (headInside && !opposingHead) state.HeadBlend = 1;

            // The weak head pocket moves slightly with target angular motion.
            // This is not projectile lead: it stays fully inside the real headshot
            // band and only gives a moving band more room before its edge escapes.
            Vector2 headPositionError = headError;
            if (headInside && target.HeadRegion is { } headRegion)
            {
                Vector2 safe = AimAssistMath.MotionSafeRegionError(headRegion,
                    state.HeadAngularVelocity);
                if (safe.LengthSquared() > .000001f)
                    headPositionError = safe * AimAssistTuning.HeadSafePositionScale;
            }

            Vector2 error = Vector2.Lerp(bodyError,
                visibleHead ? headPositionError : bodyError, state.HeadBlend);
            Vector2 trackedVelocity = Vector2.Lerp(state.AngularVelocity,
                state.HeadAngularVelocity, state.HeadBlend);
            Vector2 trackedAcceleration = Vector2.Lerp(state.AngularAcceleration,
                state.HeadAngularAcceleration, state.HeadBlend);
            Vector2 targetServoVelocity = AimAssistMath.ClampLength(
                trackedVelocity + trackedAcceleration * AimAssistTuning.MotionServoLookahead,
                AimAssistTuning.MaxTrackedSpeed);

            float visibilityCoverage = Math.Clamp(bodyCoverage * (1 - state.HeadBlend)
                + headCoverage * state.HeadBlend, 0, 1);
            float coverageScale = visibilityCoverage <= 0 ? 0
                : .25f + .75f * MathF.Sqrt(visibilityCoverage);
            float distanceStrength = 1 - .15f * AimAssistMath.Smooth(35, 60, target.Distance);

            AimAssistRegion? activeRegion = state.HeadBlend > .35f && visibleHead
                ? target.HeadRegion : target.BodyRegion;
            float normalizedError = activeRegion is { } normalizedRegion
                ? AimAssistMath.NormalizeToRegion(error, normalizedRegion).Length()
                : error.Length();
            float bubble = 1 - AimAssistMath.Smooth(profile.NormalizedInner,
                profile.NormalizedRelease, normalizedError);

            // Control phase is based on the error velocity after the player's own
            // camera turn. Approaching should feel free; braking and overshoot
            // receive precision damping; deliberate escape always wins.
            bool activeInside = state.HeadBlend > .35f && visibleHead
                ? headInside : AimAssistMath.InsideBody(target);
            float closingSpeed = 0;
            if (error.LengthSquared() > .000001f)
            {
                Vector2 relativeErrorVelocity = targetServoVelocity - cameraVelocity;
                closingSpeed = -Vector2.Dot(Vector2.Normalize(error), relativeErrorVelocity);
            }
            bool escaping = stickIntent > .20f && error.LengthSquared() > .000001f
                && Vector2.Dot(physicalStick, error) < 0;
            bool braking = state.FlickBraking
                || (state.PreviousClosingSpeed > AimAssistTuning.MotionPhaseSpeed
                    && closingSpeed < state.PreviousClosingSpeed * .55f
                    && error.LengthSquared() > .000001f
                    && Vector2.Dot(cameraVelocity, error) > 0);
            AimAssistMotionPhase phase = escaping ? AimAssistMotionPhase.Escaping
                : activeInside || Math.Abs(closingSpeed) <= AimAssistTuning.MotionMatchedSpeed
                    ? AimAssistMotionPhase.Matched
                : braking ? AimAssistMotionPhase.Braking
                : closingSpeed > AimAssistTuning.MotionPhaseSpeed ? AimAssistMotionPhase.Approaching
                : closingSpeed < -AimAssistTuning.MotionPhaseSpeed ? AimAssistMotionPhase.Overshooting
                : AimAssistMotionPhase.None;
            state.MotionPhase = phase;
            state.PreviousClosingSpeed = closingSpeed;

            Vector2 stickDirection = physicalStick;
            float opposeX = AimAssistMath.Opposition(stickDirection.X, error.X);
            float opposeY = AimAssistMath.Opposition(stickDirection.Y, error.Y);
            float phaseFriction = phase switch
            {
                AimAssistMotionPhase.Approaching => .35f,
                AimAssistMotionPhase.Braking => 1.25f,
                AimAssistMotionPhase.Matched => .55f,
                AimAssistMotionPhase.Overshooting => 1.35f,
                AimAssistMotionPhase.Escaping => 0,
                _ => 1
            };
            float frictionStrength = profile.FrictionStrength * bubble * distanceStrength
                * intent * coverageScale * phaseFriction;
            if (shotCommitted) frictionStrength *= AimAssistTuning.ShotCommitFrictionScale;
            frictionStrength = Math.Clamp(frictionStrength, 0, 1 - AimAssistTuning.MinimumFriction);
            float friction = 1 - frictionStrength;
            Vector2 adjusted = raw;
            if (activeRegion is { } region && activeInside)
            {
                adjusted.X *= AimAssistMath.EdgeFrictionFactor(raw.X, physicalStick.X,
                    region.MinYaw, region.MaxYaw, frictionStrength);
                adjusted.Y *= AimAssistMath.EdgeFrictionFactor(raw.Y, physicalStick.Y,
                    region.MinPitch, region.MaxPitch, frictionStrength);
                if (raw.LengthSquared() > .000001f)
                    friction = Math.Clamp(adjusted.Length() / raw.Length(),
                        AimAssistTuning.MinimumFriction, 1);
                else friction = 1;
            }
            else if (error.LengthSquared() > .000001f && phase != AimAssistMotionPhase.Escaping)
            {
                Vector2 normal = Vector2.Normalize(error);
                Vector2 radial = normal * Vector2.Dot(raw, normal);
                Vector2 tangent = raw - radial;
                adjusted = radial * (Vector2.Dot(raw, normal) > 0
                    ? 1 - (1 - friction) * .25f : friction) + tangent * friction;
            }

            Vector2 positionHeadGain = target.HeadRegion is { } positionHeadRegion
                ? AimAssistMath.HeadGeometryGain(positionHeadRegion,
                    AimAssistTuning.HeadHorizontalPositionGain,
                    AimAssistTuning.HeadVerticalPositionGain)
                : new(AimAssistTuning.HeadHorizontalPositionGain,
                    AimAssistTuning.HeadVerticalPositionGain);
            Vector2 trackingHeadGain = target.HeadRegion is { } trackingHeadRegion
                ? AimAssistMath.HeadGeometryGain(trackingHeadRegion,
                    AimAssistTuning.HeadHorizontalTrackingGain,
                    AimAssistTuning.HeadVerticalTrackingGain)
                : new(AimAssistTuning.HeadHorizontalTrackingGain,
                    AimAssistTuning.HeadVerticalTrackingGain);

            float strength = intent * distanceStrength * bubble * profile.Rotation
                * AimAssistTuning.RotationAssistMultiplier;
            Vector2 position = new(error.X * (1 + state.HeadBlend
                    * (positionHeadGain.X - 1)),
                error.Y * (1 + state.HeadBlend * (positionHeadGain.Y - 1)));
            position = AimAssistMath.ClampLength(position * profile.PositionGain * strength,
                profile.MaxPositionSpeed) * dt;

            // Supply only target motion the player is not already matching.
            Vector2 relativeTrackingVelocity = AimAssistMath.RelativeTrackingVelocity(
                targetServoVelocity, cameraVelocity);
            Vector2 tracking = new(relativeTrackingVelocity.X * (1 + state.HeadBlend
                    * (trackingHeadGain.X - 1)),
                relativeTrackingVelocity.Y * (1 + state.HeadBlend
                    * (trackingHeadGain.Y - 1)));
            float retentionConfidence = state.BodyTrackingConfidence * (1 - state.HeadBlend)
                + state.HeadTrackingConfidence * state.HeadBlend;
            float trackingIntent = strafe
                ? AimAssistTuning.StrafeTrackingMinimum
                    + (AimAssistTuning.StrafeTrackingMaximum - AimAssistTuning.StrafeTrackingMinimum)
                    * retentionConfidence
                : same ? intent : 0;
            tracking = AimAssistMath.ClampLength(tracking * profile.TrackingGain * bubble
                * trackingIntent * coverageScale, profile.MaxTrackingSpeed) * dt;

            // Once the target is genuinely retained, replace the loosely coupled
            // position+velocity terms with a normalized critically damped follower.
            bool servoActive = same && state.RetainedSeconds >= .075f && intent > 0
                && !strafe && phase != AimAssistMotionPhase.Escaping;
            if (servoActive)
            {
                Vector2 servoError = new(error.X * (1 + state.HeadBlend * (positionHeadGain.X - 1)),
                    error.Y * (1 + state.HeadBlend * (positionHeadGain.Y - 1)));
                float frequency = profile.ServoFrequency * (state.MotionTransition ? 1.25f : 1f);
                Vector2 servoStep = AimAssistMath.CriticallyDampedServo(ref state.ServoVelocity,
                    servoError, relativeTrackingVelocity, activeRegion, frequency, dt,
                    profile.MaxTrackingSpeed);
                float servoScale = Math.Clamp(profile.TrackingGain * bubble * coverageScale
                    * (.70f + .30f * intent), 0, 1.2f);
                position = servoStep * servoScale;
                tracking = Vector2.Zero;
            }
            else
            {
                state.ServoVelocity *= MathF.Exp(-10f * dt);
            }

            if (strafe) position = Vector2.Zero;
            position *= new Vector2(opposeX, opposeY);
            tracking *= new Vector2(opposeX, opposeY);

            // Predict where the unassisted flick is landing using a short fitted
            // camera trajectory rather than a single noisy velocity sample.
            float flickAlignment = AimAssistMath.Alignment(state.FlickDirection,
                AimAssistMath.Finite(target.HeadError) ? target.HeadError : headError);
            float flickHorizon = AimAssistMath.FlickLandingHorizon(state.FlickSpeed);
            Vector2 fittedVelocity = haveCameraHistory ? fittedCameraVelocity : cameraVelocity;
            Vector2 predictedTurn = fittedVelocity * flickHorizon
                + cameraAcceleration * (.5f * flickHorizon * flickHorizon);
            Vector2 predictedTargetMotion = state.HeadAngularVelocity * flickHorizon
                + state.HeadAngularAcceleration * (.5f * flickHorizon * flickHorizon);
            float speedT = AimAssistMath.Smooth(AimAssistTuning.FlickDirectionalSpeed, 45f,
                state.FlickSpeed);
            float captureRadii = AimAssistTuning.FlickRadiusMinScale
                + (AimAssistTuning.FlickRadiusMaxScale - AimAssistTuning.FlickRadiusMinScale) * speedT;
            if (profile.ScopeBlend > 0) captureRadii *= 1 - .25f * profile.ScopeBlend;
            float flickLandingError = target.HeadRegion is { } landingRegion
                ? AimAssistMath.NormalizedLandingMiss(landingRegion,
                    predictedTargetMotion - predictedTurn)
                : (headError + predictedTargetMotion - predictedTurn).Length();
            if (state.FlickSpeed > 45 && flickLandingError > captureRadii * 1.5f)
                captureRadii *= .75f;
            bool naturalLanding = flickLandingError <= captureRadii;
            // Normalized target radii keep flick behavior consistent across distance,
            // but a very small projected head can make a tiny sub-degree miss look
            // numerically huge. Preserve the existing precision behavior: an aligned
            // flick may finish the last 0.30 degrees into the mechanical head region.
            bool currentCapture = normalizedHead > 0
                && (normalizedHead <= captureRadii || headAngle <= .30f);
            bool capture = state.FlickActive && !state.FlickConsumed && visibleHead && !opposingHead
                && state.FlickTarget == target.Slot
                && (currentCapture || state.FlickBraking && naturalLanding)
                && flickAlignment >= .75f && headAlignment >= .35f;
            if (capture)
            {
                Vector2 safe = target.HeadRegion is { } r
                    ? AimAssistMath.MotionSafeRegionError(r, state.HeadAngularVelocity) : headError;
                Vector2 flickCorrection = safe
                    * (1 - MathF.Exp(-AimAssistTuning.HeadFlickSnapGain * dt));
                position = AimAssistMath.ClampLength(flickCorrection,
                    profile.MaxPositionSpeed * dt);
                tracking = Vector2.Zero;
                state.ServoVelocity = Vector2.Zero;
                error = safe; state.HeadBlend = 1;
                state.HeadTrackingConfidence = Math.Max(state.HeadTrackingConfidence, .85f);
            }
            if (headInside) state.FlickConsumed = true;

            // Near a target edge, let the next frame's stick filter and the
            // outer-stick acceleration get out of the player's way.
            float filterRelease = 0;
            if (activeRegion is { } filterRegion)
            {
                if (activeInside)
                {
                    float edgeDistance = Math.Min(
                        Math.Min(Math.Abs(filterRegion.MinYaw), Math.Abs(filterRegion.MaxYaw)),
                        Math.Min(Math.Abs(filterRegion.MinPitch), Math.Abs(filterRegion.MaxPitch)));
                    filterRelease = 1 - AimAssistMath.Smooth(0, .6f, edgeDistance);
                }
                else
                {
                    filterRelease = .65f * (1 - AimAssistMath.Smooth(0, .5f, error.Length()));
                }
                if (phase is AimAssistMotionPhase.Braking or AimAssistMotionPhase.Overshooting)
                    filterRelease = Math.Max(filterRelease, .85f);
            }
            float turnAccelerationBrake = phase switch
            {
                AimAssistMotionPhase.Braking => .95f,
                AimAssistMotionPhase.Overshooting => .90f,
                AimAssistMotionPhase.Matched => .45f,
                AimAssistMotionPhase.Escaping => 0,
                _ => 0
            };
            if (state.FlickBraking) turnAccelerationBrake = Math.Max(turnAccelerationBrake, .9f);
            if (shotCommitted) turnAccelerationBrake = Math.Max(turnAccelerationBrake, .85f);
            if (state.HeadBlend > .6f) turnAccelerationBrake = Math.Max(turnAccelerationBrake, .55f);
            turnAccelerationBrake = Math.Max(turnAccelerationBrake, filterRelease * .55f);

            Vector2 rotation = position + tracking;
            Vector2 remaining = error + targetServoVelocity * dt - adjusted;
            Vector2 bounded = new(AimAssistMath.LimitCorrection(rotation.X, remaining.X),
                AimAssistMath.LimitCorrection(rotation.Y, remaining.Y));
            bool saturated = bounded != rotation;

            // Leaky correction budget: tiny rescues can be sharp, but sustained
            // automatic pull quickly spends the budget and has to recover.
            state.CorrectionBudgetUsed = Math.Max(0,
                state.CorrectionBudgetUsed - profile.CorrectionBudgetRecovery * dt);
            float budgetRemaining = Math.Max(0,
                profile.CorrectionBudgetDegrees - state.CorrectionBudgetUsed);
            float correctionLength = bounded.Length();
            if (correctionLength > budgetRemaining && correctionLength > .000001f)
            {
                bounded *= budgetRemaining / correctionLength;
                correctionLength = budgetRemaining;
                saturated = true;
            }
            state.CorrectionBudgetUsed += correctionLength;
            float correctionBudget = profile.CorrectionBudgetDegrees <= 0 ? 0
                : Math.Clamp(state.CorrectionBudgetUsed / profile.CorrectionBudgetDegrees, 0, 1);

            Vector2 output = adjusted + bounded;
            float playerContribution = raw.Length();
            float assistContribution = (output - raw).Length();

            state.TrackingState = capture ? AimAssistTrackingState.FlickCapturingHead
                : state.HeadBlend > .01f
                    ? headInside ? AimAssistTrackingState.TrackingHead : AimAssistTrackingState.RefiningHead
                    : state.BodyTrackingConfidence >= AimAssistTuning.TrackingConfidenceMin
                        ? AimAssistTrackingState.TrackingBody : AimAssistTrackingState.AcquiringBody;
            state.PreviousError = target.BodyError;
            state.PreviousHeadError = target.HeadError;
            state.PreviousOutput = output;
            state.PreviousDeltaTime = dt;
            state.PreviousBodyVisible = target.BodyVisible;
            state.PreviousHeadVisible = visibleHead;
            state.PreviousInsideBody = AimAssistMath.InsideBody(target);
            state.PreviousInsideHead = headInside;
            state.PreviousRaw = raw;
            state.PreviousCameraVelocity = cameraVelocity;
            state.PushCameraVelocity(cameraVelocity);
            return new(output.X, output.Y, target.Slot, friction, strength,
                state.HeadBlend > 0 ? AimAssistPointType.Head : target.BodyPointType,
                state.HeadBlend, bestScore, bestAlignment, predictionAmount, false, saturated,
                state.RetainedSeconds, state.TrackingState, position, tracking, physicalStick,
                state.FlickActive, state.FlickAge, flickAlignment, strafe, false, firing,
                phase, state.BodyTrackingConfidence, state.HeadTrackingConfidence,
                flickLandingError, state.FlickBraking, shotCommitted,
                visibilityCoverage, filterRelease, turnAccelerationBrake,
                normalizedError, playerContribution, assistContribution,
                correctionBudget, profile.ScopeBlend, shotPhase, state.MotionTransition);
        }

        private static Vector2 TrackMotion(Vector2 filtered, Vector2 acceleration,
            Vector2 error, Vector2 previous, Vector2 cameraDelta, float previousDt,
            float dt, bool history, bool preserveHistory, float rate,
            ref Vector2 direction, out Vector2 nextAcceleration, out bool transition)
        {
            transition = false;
            if (preserveHistory)
            {
                nextAcceleration = acceleration;
                return filtered;
            }
            nextAcceleration = Vector2.Zero;
            if (!history || previousDt <= 0)
            {
                direction = Vector2.Zero;
                return Vector2.Zero;
            }
            Vector2 motion = AimAssistMath.AngularDelta(error, previous) + cameraDelta;
            if (!AimAssistMath.Finite(motion) || motion.Length() > AimAssistTuning.MotionDiscontinuity)
            {
                direction = Vector2.Zero;
                return Vector2.Zero;
            }

            Vector2 measured = AimAssistMath.ClampLength(motion / previousDt,
                AimAssistTuning.MaxTrackedSpeed);
            float filteredSpeedBefore = filtered.Length();
            float measuredSpeedBefore = measured.Length();
            transition = filteredSpeedBefore > 2 && measuredSpeedBefore > 2
                    && Vector2.Dot(filtered, measured) < -.15f * filteredSpeedBefore * measuredSpeedBefore
                || (measured - filtered).Length() >= AimAssistTuning.MotionTransitionVelocityDelta;
            if (transition)
            {
                // A strafe reversal, jump apex/landing or impulse should not drag
                // the previous acceleration estimate into the new motion phase.
                acceleration = Vector2.Zero;
                if (measuredSpeedBefore > .05f) direction = measured / measuredSpeedBefore;
                rate *= 2.5f;
            }

            // Couple yaw/pitch through a persistent target-motion direction. This
            // suppresses minor-axis corkscrew noise on diagonal strafe+jump motion
            // while still changing direction quickly on a real reversal.
            float measuredSpeed = measured.Length();
            if (measuredSpeed > .05f)
            {
                Vector2 measuredDirection = measured / measuredSpeed;
                if (direction.LengthSquared() < .0001f
                    || Vector2.Dot(direction, measuredDirection) < -.25f)
                {
                    direction = measuredDirection;
                }
                else
                {
                    float directionRate = AimAssistTuning.MotionDirectionRate
                        * (Vector2.Dot(direction, measuredDirection) < .25f ? 1.8f : 1f);
                    Vector2 blended = Vector2.Lerp(direction, measuredDirection,
                        1 - MathF.Exp(-directionRate * dt));
                    direction = blended.LengthSquared() > .0001f
                        ? Vector2.Normalize(blended) : measuredDirection;
                }
                float coupling = .35f * AimAssistMath.Smooth(3f, 30f, measuredSpeed);
                measured = Vector2.Lerp(measured, direction * measuredSpeed, coupling);
            }

            float xRate = filtered.X * measured.X < 0 ? rate * 2.5f : rate;
            float yRate = filtered.Y * measured.Y < 0 ? rate * 2.5f : rate;
            Vector2 velocity = new(
                filtered.X + (measured.X - filtered.X) * (1 - MathF.Exp(-xRate * dt)),
                filtered.Y + (measured.Y - filtered.Y) * (1 - MathF.Exp(-yRate * dt)));
            velocity = AimAssistMath.ClampLength(velocity, AimAssistTuning.MaxTrackedSpeed);

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
