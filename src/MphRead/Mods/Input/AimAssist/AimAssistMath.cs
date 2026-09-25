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
            float distance = cameraInput > 0 ? Math.Max(0, -min) : Math.Max(0, max);
            float width = Math.Max(.12f, (max - min) * .35f);
            float edge = 1 - Smooth(0, width, distance);
            float deliberate = Smooth(.45f, .85f, Math.Abs(physicalInput));
            float amount = Math.Clamp(strength * edge * (1 - .85f * deliberate), 0, .75f);
            return 1 - amount;
        }
        public static Vector2 BodyError(in AimAssistTarget target)
            => target.BodyRegion is { } r ? RegionError(r) : target.BodyError;
        public static Vector2 HeadError(in AimAssistTarget target)
            => target.HeadRegion is { } r ? RegionError(r) : target.HeadError;
        public static bool CanHeadshotAtDistance(BeamType weapon, float distance)
            => float.IsFinite(distance) && distance >= 0 && (weapon == BeamType.Imperialist
                || (weapon is BeamType.PowerBeam or BeamType.VoltDriver && distance <= 15));

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
