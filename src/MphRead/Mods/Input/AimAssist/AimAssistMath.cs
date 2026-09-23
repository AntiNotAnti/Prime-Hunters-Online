using System;
using System.Numerics;

namespace MphRead.Mods.Input.AimAssist
{
    public static class AimAssistMath
    {
        public static float Smooth(float a, float b, float value)
        {
            float t = Math.Clamp((value - a) / (b - a), 0, 1);
            return t * t * (3 - 2 * t);
        }

        public static bool Finite(Vector2 v) => float.IsFinite(v.X) && float.IsFinite(v.Y);

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
