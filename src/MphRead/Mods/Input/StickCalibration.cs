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
}
