using System;

namespace MphRead.Mods.Input
{
    public readonly record struct StickCalibration(float CenterX, float CenterY, float MinX, float MaxX, float MinY, float MaxY)
    {
        public static StickCalibration Default => new(0, 0, -1, 1, -1, 1);
        public (float X, float Y) Normalize(float x, float y)
            => (Axis(x, CenterX, MinX, MaxX), Axis(y, CenterY, MinY, MaxY));
        private static float Axis(float value, float center, float min, float max)
            => Math.Clamp((value - center) / Math.Max(.1f, value >= center ? max - center : center - min), -1, 1);
    }

    /// <summary>
    /// Eight setup-only outer-radius samples around the stick gate. Axis
    /// calibration fixes center/min/max; this fixes controllers whose diagonal
    /// gate is an ellipse, rounded square or simply worn unevenly.
    /// </summary>
    public readonly record struct StickRadialCalibration(float R0, float R1, float R2, float R3,
        float R4, float R5, float R6, float R7)
    {
        public static StickRadialCalibration Default => new(1, 1, 1, 1, 1, 1, 1, 1);

        public float Radius(int sector) => (sector & 7) switch
        {
            0 => R0, 1 => R1, 2 => R2, 3 => R3,
            4 => R4, 5 => R5, 6 => R6, _ => R7
        };

        public (float X, float Y) Normalize(float x, float y)
        {
            if (!float.IsFinite(x) || !float.IsFinite(y)) return (0, 0);
            float length = MathF.Sqrt(x * x + y * y);
            if (length <= .000001f) return (0, 0);
            float turns = MathF.Atan2(y, x) / (MathF.PI * 2);
            if (turns < 0) turns += 1;
            float position = turns * 8;
            int a = (int)MathF.Floor(position) & 7;
            int b = (a + 1) & 7;
            float t = position - MathF.Floor(position);
            float radius = Radius(a) + (Radius(b) - Radius(a)) * t;
            radius = Math.Clamp(radius, .65f, 1.45f);
            float corrected = Math.Clamp(length / radius, 0, 1);
            float scale = corrected / length;
            return (x * scale, y * scale);
        }
    }
}
