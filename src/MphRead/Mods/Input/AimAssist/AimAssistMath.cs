using System;
using System.Numerics;

namespace MphRead.Mods.Input.AimAssist
{
    public static class AimAssistMath
    {
        public static Vector2 RegionError(AimAssistRegion region) => RegionError(Vector2.Zero, region);
        public static Vector2 RegionError(Vector2 crosshair, AimAssistRegion region)
        {
            if (!Finite(region.Center) || region.MinYaw > region.MaxYaw || region.MinPitch > region.MaxPitch)
                return new(float.NaN, float.NaN);
            return new(Math.Clamp(crosshair.X, region.MinYaw, region.MaxYaw) - crosshair.X,
                Math.Clamp(crosshair.Y, region.MinPitch, region.MaxPitch) - crosshair.Y);
        }
        public static bool InsideRegion(AimAssistRegion region) => RegionError(region) == Vector2.Zero;
        public static float RegionDistance(AimAssistRegion region) => RegionError(region).Length();

        public static Vector2 RegionHalfExtents(AimAssistRegion region)
            => new(Math.Max(.05f, Math.Abs(region.Width) * .5f),
                Math.Max(.05f, Math.Abs(region.Height) * .5f));

        public static Vector2 NormalizeToRegion(Vector2 value, AimAssistRegion region)
        {
            Vector2 half = RegionHalfExtents(region);
            return new(value.X / half.X, value.Y / half.Y);
        }

        public static Vector2 DenormalizeFromRegion(Vector2 value, AimAssistRegion region)
        {
            Vector2 half = RegionHalfExtents(region);
            return new(value.X * half.X, value.Y * half.Y);
        }

        public static Vector2 NormalizedRegionError(AimAssistRegion region)
            => NormalizeToRegion(RegionError(region), region);

        public static Vector2 NormalizedBodyError(in AimAssistTarget target)
            => target.BodyRegion is { } r ? NormalizeToRegion(BodyError(target), r) : BodyError(target);

        public static Vector2 NormalizedHeadError(in AimAssistTarget target)
            => target.HeadRegion is { } r ? NormalizeToRegion(HeadError(target), r) : HeadError(target);

        public static float NormalizedSelectionDistance(in AimAssistTarget target,
            AimAssistWeaponProfile profile)
        {
            Vector2 bodyError = BodyError(target);
            float bodyDistance = target.BodyRegion is { } bodyRegion
                ? NormalizeToRegion(bodyError, bodyRegion).Length()
                : bodyError.Length();

            if (!VisibleHead(target, profile))
            {
                return bodyDistance;
            }

            Vector2 headError = HeadError(target);
            float headDistance = target.HeadRegion is { } headRegion
                ? NormalizeToRegion(headError, headRegion).Length()
                : headError.Length();

            // SelectionError uses the visible head when the chest is hidden and
            // whichever visible region is closer otherwise. Use that same region
            // for the normalized acquire gate. Requiring HeadRegion here made
            // head-only targets silently fall back to an occluded/far-away chest.
            return !target.BodyVisible ? headDistance : Math.Min(headDistance, bodyDistance);
        }

        /// <summary>
        /// Critically damped second-order follower in target-relative coordinates.
        /// Error is normalized by apparent target half-width/half-height so one
        /// target radius means the same thing at four units or forty.
        /// </summary>
        public static Vector2 CriticallyDampedServo(ref Vector2 servoVelocity,
            Vector2 error, Vector2 targetRelativeVelocity, AimAssistRegion? region,
            float frequency, float dt, float maxSpeed)
        {
            if (!Finite(error) || !Finite(targetRelativeVelocity) || !float.IsFinite(dt)
                || dt <= 0 || frequency <= 0 || maxSpeed <= 0)
            {
                servoVelocity = Vector2.Zero;
                return Vector2.Zero;
            }
            Vector2 half = region is { } r ? RegionHalfExtents(r) : Vector2.One;
            Vector2 normalizedError = new(error.X / half.X, error.Y / half.Y);
            Vector2 normalizedTargetVelocity = new(targetRelativeVelocity.X / half.X,
                targetRelativeVelocity.Y / half.Y);
            Vector2 normalizedServoVelocity = new(servoVelocity.X / half.X,
                servoVelocity.Y / half.Y);
            float omega = Math.Clamp(frequency, 2, 30);
            Vector2 acceleration = omega * omega * normalizedError
                + 2 * omega * (normalizedTargetVelocity - normalizedServoVelocity);
            servoVelocity += new Vector2(acceleration.X * half.X,
                acceleration.Y * half.Y) * dt;
            servoVelocity = ClampLength(servoVelocity, maxSpeed);
            if (!Finite(servoVelocity)) servoVelocity = Vector2.Zero;
            return servoVelocity * dt;
        }

        public static Vector2 FittedCameraVelocity(Vector2 newest, Vector2 oneBack,
            Vector2 twoBack, Vector2 threeBack)
            => (newest * 4 + oneBack * 3 + twoBack * 2 + threeBack) / 10f;

        public static Vector2 FittedCameraAcceleration(Vector2 newest, Vector2 oneBack,
            Vector2 twoBack, float dt)
        {
            if (!float.IsFinite(dt) || dt <= 0) return Vector2.Zero;
            Vector2 recent = (newest + oneBack) * .5f;
            Vector2 older = (oneBack + twoBack) * .5f;
            return ClampLength((recent - older) / dt, AimAssistTuning.MaxTrackedAcceleration);
        }

        public static float NormalizedLandingMiss(AimAssistRegion region, Vector2 relativeOffset)
        {
            AimAssistRegion shifted = region.Shift(relativeOffset.X, relativeOffset.Y);
            return NormalizeToRegion(RegionError(shifted), shifted).Length();
        }

        public static Vector2 HeadGeometryGain(AimAssistRegion region,
            float horizontalBase, float verticalBase)
        {
            Vector2 half = RegionHalfExtents(region);
            float aspect = Math.Clamp(half.X / half.Y, .35f, 3f);
            float x = horizontalBase / MathF.Sqrt(aspect);
            float y = verticalBase * MathF.Sqrt(aspect);
            return new(Math.Clamp(x, .65f, 1.35f), Math.Clamp(y, .8f, 2.25f));
        }

        public static Vector2 SafeRegionError(AimAssistRegion region, float inset)
        {
            inset = Math.Clamp(inset, 0, .49f);
            return RegionError(region.Inset(inset));
        }

        // When the crosshair is already inside a target region, slow only the
        // component that is about to leave through the edge it is travelling
        // toward. Strong deliberate stick input rapidly releases the guardrail.
        public static float EdgeFrictionFactor(float cameraInput, float physicalInput,
            float min, float max, float strength)
        {
            if (!float.IsFinite(cameraInput) || !float.IsFinite(physicalInput)
                || !float.IsFinite(min) || !float.IsFinite(max) || min > 0 || max < 0
                || cameraInput == 0 || strength <= 0)
            {
                return 1;
            }
            float distance = cameraInput > 0 ? Math.Max(0, max) : Math.Max(0, -min);
            float width = Math.Max(.12f, (max - min) * .35f);
            float edge = 1 - Smooth(0, width, distance);
            float deliberate = Smooth(.45f, .85f, Math.Abs(physicalInput));
            float amount = Math.Clamp(strength * edge * (1 - .85f * deliberate), 0, .75f);
            return 1 - amount;
        }
        public static Vector2 BodyError(in AimAssistTarget target)
            => target.BodySurface is { } s ? s.Error
                : target.BodyRegion is { } r ? RegionError(r) : target.BodyError;
        public static Vector2 HeadError(in AimAssistTarget target)
            => target.HeadSurface is { } s ? s.Error
                : target.HeadRegion is { } r ? RegionError(r) : target.HeadError;
        public static bool InsideBody(in AimAssistTarget target)
            => target.BodySurface is { } s ? s.Inside
                : target.BodyRegion is { } r && InsideRegion(r);
        public static bool InsideHead(in AimAssistTarget target)
            => target.HeadSurface is { } s ? s.Inside
                : target.HeadRegion is { } r && InsideRegion(r);
        public static bool CanHeadshotAtDistance(BeamType weapon, float distance)
            => float.IsFinite(distance) && distance >= 0 && (weapon == BeamType.Imperialist
                || (weapon is BeamType.PowerBeam or BeamType.VoltDriver && distance <= 15));

        public static AimAssistRegion MotionSafeRegion(AimAssistRegion region,
            Vector2 angularVelocity, float inset = AimAssistTuning.HeadSafeInset)
        {
            AimAssistRegion safe = region.Inset(inset);
            float bx = Math.Clamp(angularVelocity.X / AimAssistTuning.HeadSafeMotionSpeed, -1, 1)
                * region.Width * AimAssistTuning.HeadSafeMotionBias;
            float by = Math.Clamp(angularVelocity.Y / AimAssistTuning.HeadSafeMotionSpeed, -1, 1)
                * region.Height * AimAssistTuning.HeadSafeMotionBias;
            // Clamp the shifted pocket back inside the real mechanical region.
            bx = Math.Clamp(bx, region.MinYaw - safe.MinYaw, region.MaxYaw - safe.MaxYaw);
            by = Math.Clamp(by, region.MinPitch - safe.MinPitch, region.MaxPitch - safe.MaxPitch);
            return safe.Shift(bx, by);
        }

        public static Vector2 MotionSafeRegionError(AimAssistRegion region, Vector2 angularVelocity)
            => RegionError(MotionSafeRegion(region, angularVelocity));

        public static Vector2 RelativeTrackingVelocity(Vector2 targetVelocity, Vector2 cameraVelocity)
        {
            float speed = targetVelocity.Length();
            if (!Finite(targetVelocity) || !Finite(cameraVelocity) || speed < .0001f)
                return Vector2.Zero;
            Vector2 direction = targetVelocity / speed;
            float supplied = Math.Clamp(Vector2.Dot(cameraVelocity, direction), 0, speed);
            return targetVelocity - direction * supplied;
        }

        public static float FlickLandingHorizon(float flickSpeed)
        {
            float t = Smooth(AimAssistTuning.FlickDirectionalSpeed, 45f, flickSpeed);
            return AimAssistTuning.FlickLandingMaxSeconds
                + (AimAssistTuning.FlickLandingMinSeconds - AimAssistTuning.FlickLandingMaxSeconds) * t;
        }

        public static float DynamicFlickRadius(float baseRadius, float flickSpeed, bool scoped)
        {
            float t = Smooth(AimAssistTuning.FlickDirectionalSpeed, 45f, flickSpeed);
            float scale = AimAssistTuning.FlickRadiusMinScale
                + (AimAssistTuning.FlickRadiusMaxScale - AimAssistTuning.FlickRadiusMinScale) * t;
            return baseRadius * scale * (scoped ? .65f : 1f);
        }

        public static float TrajectoryRegionScore(AimAssistRegion region, Vector2 travel)
        {
            if (!Finite(region.Center) || !Finite(travel)) return 0;
            // Fixed samples are deterministic and allocation-free; an intersection
            // with the rectangle scores one, otherwise score closest approach.
            float best = float.MaxValue;
            for (int i = 0; i <= 8; i++)
            {
                Vector2 point = travel * (i / 8f);
                float distance = RegionError(point, region).Length();
                if (distance < best) best = distance;
                if (best == 0) return 1;
            }
            return 1 - Smooth(0, 2f, best);
        }

        public static float Smooth(float a, float b, float value)
        {
            float t = Math.Clamp((value - a) / (b - a), 0, 1);
            return t * t * (3 - 2 * t);
        }

        public static bool Finite(Vector2 v) => float.IsFinite(v.X) && float.IsFinite(v.Y);

        // The assist operates in actual camera degrees, after input preferences and zoom.
        public static Vector2 CameraDelta(Vector2 input, float zoomScale, bool invertX, bool invertY)
            => new(input.X * zoomScale * (invertX ? -1 : 1), input.Y * zoomScale * (invertY ? -1 : 1));

        public static Vector2 AngularDelta(Vector2 current, Vector2 previous)
            => new(MathF.IEEERemainder(current.X - previous.X, 360), current.Y - previous.Y);

        public static bool VisibleHead(in AimAssistTarget target, AimAssistWeaponProfile profile)
            => profile.Head && target.Distance <= profile.HeadRange && target.HeadVisible && Finite(target.HeadError) && Finite(HeadError(target));

        // Measure the visible chest-to-head region, so aiming at the head does not
        // lose a nearby hunter just because their chest is outside the acquire cone.
        public static Vector2 SelectionError(in AimAssistTarget target, AimAssistWeaponProfile profile)
        {
            Vector2 body = BodyError(target), head = HeadError(target);
            if (!VisibleHead(target, profile)) return body;
            if (!target.BodyVisible) return head;
            return head.LengthSquared() < body.LengthSquared() ? head : body;
        }

        // Only the assist is constrained: a deliberate stick overshoot remains the player's.
        public static float LimitCorrection(float correction, float remaining)
            => correction * remaining <= 0 ? 0 : MathF.CopySign(Math.Min(Math.Abs(correction), Math.Abs(remaining)), correction);

        public static float Opposition(float input, float error)
            => input * error < 0 ? 1 - Smooth(.02f, .8f, Math.Abs(input)) : 1;

        public static float Alignment(Vector2 input, Vector2 error)
        {
            if (!Finite(input) || !Finite(error)) return 0;
            float a = input.LengthSquared(), b = error.LengthSquared();
            if (a < .000001f || b < .000001f) return 0;
            return Math.Max(0, Vector2.Dot(input, error) / MathF.Sqrt(a * b));
        }

        public static Vector2 ClampLength(Vector2 value, float max)
        {
            if (!Finite(value) || max <= 0) return Vector2.Zero;
            float lengthSquared = value.LengthSquared();
            if (lengthSquared <= max * max) return value;
            return value * (max / MathF.Sqrt(lengthSquared));
        }

        public static float Score(float angle, float cone, float distance, bool retained, float motion, float alignment)
            => .50f * (1 - Math.Clamp(angle / cone, 0, 1)) + (retained ? .15f : 0)
                + .10f * (1 - Math.Clamp(distance / 60, 0, 1)) + .08f
                + .05f * Math.Clamp(motion, 0, 1)
                + AimAssistTuning.InputAlignmentWeight * Math.Clamp(alignment, 0, 1);
    }
}
