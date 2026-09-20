using System;
using System.Numerics;

namespace MphRead.Mods.Input.AimAssist
{
    public static class AimAssistMath
    {
        public static float Smooth(float a, float b, float value)
        { float t = Math.Clamp((value - a) / (b - a), 0, 1); return t * t * (3 - 2 * t); }
        public static bool Finite(Vector2 v) => float.IsFinite(v.X) && float.IsFinite(v.Y);
        public static float Opposition(float input, float error)
            => input * error < 0 ? 1 - Smooth(.02f, .8f, Math.Abs(input)) : 1;
        public static float Score(float angle, float cone, float distance, bool retained, float motion)
            => .60f * (1 - Math.Clamp(angle / cone, 0, 1)) + (retained ? .15f : 0)
                + .10f * (1 - Math.Clamp(distance / 60, 0, 1)) + .10f + .05f * Math.Clamp(motion, 0, 1);
    }
}
