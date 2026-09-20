using System;

namespace MphRead.Mods.Input
{
    public enum AimInputSource { None, Mouse, Touch, Gamepad }
    public static class AimInputSourceTracker
    {
        public static AimInputSource Current { get; private set; }
        public static long Revision { get; private set; }
        private static long _claimStart = -1;
        public static void Pointer(float x, float y, bool touch, long milliseconds)
        {
            if (!float.IsFinite(x) || !float.IsFinite(y) || x * x + y * y < .0001f) return;
            Current = touch ? AimInputSource.Touch : AimInputSource.Mouse;
            _claimStart = -1;
        }
        public static void Stick(float x, float y, long milliseconds)
        {
            if (!float.IsFinite(x) || !float.IsFinite(y) || x * x + y * y <= .08f * .08f)
            { _claimStart = -1; return; }
            if (Current == AimInputSource.Gamepad) return;
            if (_claimStart < 0) _claimStart = milliseconds;
            if (Current == AimInputSource.None || milliseconds - _claimStart >= 120) Current = AimInputSource.Gamepad;
        }
        public static void Reset() { Current = AimInputSource.None; _claimStart = -1; Revision++; }
    }
}
