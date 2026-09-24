using System;

namespace MphRead.Mods.Input
{
    public enum GamepadCurve { Linear, Classic, Precision, Dynamic }

    public static class GamepadAnalog
    {
        private const long TriggerReleaseDebounceMs = 6;

        public static float Finite(float value, float min = -1, float max = 1)
            => float.IsFinite(value) ? Math.Clamp(value, min, max) : 0;

        public static (float X, float Y) ApplyRadialDeadZone(float x, float y,
            float inner, float outer = 0)
        {
            x = Finite(x);
            y = Finite(y);
            inner = Finite(inner, 0, 0.9f);
            outer = Finite(outer, 0, Math.Min(0.5f, 0.99f - inner));
            float length = MathF.Sqrt(x * x + y * y);
            if (length <= inner || length == 0) return (0, 0);
            float magnitude = Math.Clamp((length - inner) / (1 - inner - outer), 0, 1);
            return (x / length * magnitude, y / length * magnitude);
        }

        public static float ApplyResponseCurve(float value, GamepadCurve curve)
        {
            float x = MathF.Abs(Finite(value));
            if (curve == GamepadCurve.Linear) return Finite(value);
            float micro = curve == GamepadCurve.Precision ? .07f : curve == GamepadCurve.Dynamic ? .16f : .11f;
            float tracking = curve == GamepadCurve.Precision ? .58f : curve == GamepadCurve.Dynamic ? .72f : .65f;
            float result = x <= .35f ? micro * (x / .35f) * (x / .35f)
                : x <= .8f ? micro + (tracking - micro) * (x - .35f) / .45f
                : tracking + (1 - tracking) * (x - .8f) / .2f;
            return MathF.CopySign(result, value);
        }

        public static (float X, float Y) ApplyRadialResponseCurve(float x, float y, GamepadCurve curve)
        {
            x = Finite(x);
            y = Finite(y);
            float length = MathF.Sqrt(x * x + y * y);
            if (length <= 0) return (0, 0);
            float magnitude = Math.Clamp(length, 0, 1);
            float curved = ApplyResponseCurve(magnitude, curve);
            float scale = curved / length;
            return (x * scale, y * scale);
        }

        public static System.Numerics.Vector2 FilterAim(System.Numerics.Vector2 previous,
            System.Numerics.Vector2 sample, float dt)
        {
            if (sample == System.Numerics.Vector2.Zero || System.Numerics.Vector2.Dot(previous, sample) < 0
                || sample.Length() >= .65f) return sample;
            float velocity = (sample - previous).Length() / Math.Max(dt, .0001f);
            float rate = 35 + 165 * Math.Clamp(velocity / 8, 0, 1);
            return System.Numerics.Vector2.Lerp(previous, sample, 1 - MathF.Exp(-rate * dt));
        }

        private static float TriggerRelease(float press)
        {
            press = Finite(press, 0.05f, 0.95f);
            return Math.Max(0.01f, press - .08f);
        }

        public static bool Trigger(float value, bool held, float press = 0.60f)
        {
            press = Finite(press, 0.05f, 0.95f);
            return Finite(value, 0, 1) >= (held ? TriggerRelease(press) : press);
        }

        public static bool TriggerStable(float value, bool held, ref long belowSince,
            long milliseconds, float press = 0.60f)
        {
            press = Finite(press, 0.05f, 0.95f);
            value = Finite(value, 0, 1);
            if (!held)
            {
                belowSince = -1;
                return value >= press;
            }
            if (value >= TriggerRelease(press))
            {
                belowSince = -1;
                return true;
            }
            if (belowSince < 0 || milliseconds < belowSince)
            {
                belowSince = milliseconds;
                return true;
            }
            if (milliseconds - belowSince < TriggerReleaseDebounceMs) return true;
            belowSince = -1;
            return false;
        }

        public static (int X, int Y) QuantizeMovement(float x, float y, float threshold = 0.5f)
        {
            if (x * x + y * y < threshold * threshold || (x == 0 && y == 0)) return (0, 0);
            int sector = ((int)MathF.Round(MathF.Atan2(y, x) / (MathF.PI / 4)) + 8) % 8;
            return sector switch
            {
                0 => (1, 0), 1 => (1, 1), 2 => (0, 1), 3 => (-1, 1),
                4 => (-1, 0), 5 => (-1, -1), 6 => (0, -1), _ => (1, -1)
            };
        }
    }

    public sealed class GamepadEventState
    {
        public GamepadButtons KeyButtons;
        public GamepadState Motion;
        public GamepadState Snapshot
        {
            get
            {
                var state = Motion;
                state.Buttons |= KeyButtons;
                return state;
            }
        }

        public void Key(GamepadButtons button, bool down)
        {
            if (down) KeyButtons |= button;
            else KeyButtons &= ~button;
        }

        public void Clear()
        {
            KeyButtons = 0;
            Motion = default;
        }
    }
}
