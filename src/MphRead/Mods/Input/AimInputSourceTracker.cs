using System;

namespace MphRead.Mods.Input
{
    public enum AimInputSource { None, Mouse, Touch, Gamepad }

    public static class AimInputSourceTracker
    {
        public static AimInputSource Current { get; private set; }
        public static long Revision { get; private set; }
        private static long _claimStart = -1;

        private static void Set(AimInputSource source)
        {
            if (Current == source) return;
            Current = source;
            Revision++;
        }

        public static void Pointer(float x, float y, bool touch, long milliseconds)
        {
            if (!float.IsFinite(x) || !float.IsFinite(y) || x * x + y * y < .0001f) return;
            Set(touch ? AimInputSource.Touch : AimInputSource.Mouse);
            _claimStart = -1;
        }

        public static void Stick(float x, float y, long milliseconds)
        {
            if (!float.IsFinite(x) || !float.IsFinite(y))
            {
                _claimStart = -1;
                return;
            }
            float magnitudeSquared = x * x + y * y;
            if (magnitudeSquared <= .08f * .08f)
            {
                _claimStart = -1;
                return;
            }
            if (Current == AimInputSource.Gamepad) return;
            if (Current == AimInputSource.None)
            {
                Set(AimInputSource.Gamepad);
                _claimStart = -1;
                return;
            }

            float magnitude = MathF.Sqrt(magnitudeSquared);
            long delay = magnitude >= .60f ? 0 : magnitude >= .30f ? 60 : 120;
            if (delay == 0)
            {
                Set(AimInputSource.Gamepad);
                _claimStart = -1;
                return;
            }
            if (_claimStart < 0) _claimStart = milliseconds;
            if (milliseconds - _claimStart >= delay)
            {
                Set(AimInputSource.Gamepad);
                _claimStart = -1;
            }
        }

        public static void Reset()
        {
            Set(AimInputSource.None);
            _claimStart = -1;
        }
    }
}
