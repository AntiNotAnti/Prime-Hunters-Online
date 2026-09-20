using System;
namespace MphRead.Mods.Input
{
    public static class WeaponSelectionDirection
    {
        // The DS wheel is a six-sector quarter arc. Both pointer and stick supply
        // a direction in that arc; selecting a slot never changes an OS pointer.
        public static int Resolve(float x, float y)
        {
            if (x < 0 || y < 0 || x * x + y * y < .04f) return -1;
            float angle = MathF.Atan2(x, y);
            return Math.Clamp((int)(angle / (MathF.PI / 12)), 0, 5);
        }
        public static int ControllerSlot(float x, float y)
        {
            float threshold = GamepadOptions.WheelThreshold;
            if (x * x + y * y < threshold * threshold) return -1;
            float angle = MathF.Atan2(x, y);
            if (angle < 0) angle += 2 * MathF.PI;
            return GamepadOptions.WheelOrder[Math.Clamp((int)(angle / (MathF.PI / 3)), 0, 5)];
        }
        public static (float X, float Y) FromStick(float x, float y)
        {
            if (x * x + y * y < .20f) return (0, 0);
            // Full stick circle, clockwise from up, projected onto the displayed arc.
            float angle = MathF.Atan2(x, y);
            if (angle < 0) angle += 2 * MathF.PI;
            angle /= 4;
            return (MathF.Sin(angle), MathF.Cos(angle));
        }
    }
}
