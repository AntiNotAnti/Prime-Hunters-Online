using System;

namespace MphRead.Mods.Input
{
    public enum GamepadCurve { Linear, Classic, Precision, Dynamic }

    public static class GamepadAnalog
    {
        public static float Finite(float value, float min = -1, float max = 1)
            => float.IsFinite(value) ? Math.Clamp(value, min, max) : 0;

        public static (float X, float Y) ApplyRadialDeadZone(float x, float y,
            float inner, float outer = 0)
        {
            x = Finite(x); y = Finite(y);
            inner = Finite(inner, 0, 0.9f);
            outer = Finite(outer, 0, Math.Min(0.5f, 0.99f - inner));
            float length = MathF.Sqrt(x * x + y * y);
            if (length <= inner || length == 0) return (0, 0);
            float magnitude = Math.Clamp((length - inner) / (1 - inner - outer), 0, 1);
            return (x / length * magnitude, y / length * magnitude);
        }

        public static float ApplyResponseCurve(float value, GamepadCurve curve)
        {
            float exponent = curve switch
            {
                GamepadCurve.Linear => 1, GamepadCurve.Precision => 2.4f,
                GamepadCurve.Dynamic => 1.5f, _ => 2
            };
            return MathF.CopySign(MathF.Pow(MathF.Abs(Finite(value)), exponent), value);
        }

        public static bool Trigger(float value, bool held, float press = 0.60f)
        {
            press = Finite(press, 0.05f, 0.95f);
            float release = Math.Max(0.01f, press - 0.15f);
            return Finite(value, 0, 1) >= (held ? release : press);
        }

        // Eight equal angular sectors: diagonals engage at the same magnitude as cardinals.
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

    // Android keys and motion remain independent, including duplicate key/axis triggers.
    public sealed class GamepadEventState
    {
        public GamepadButtons KeyButtons;
        public GamepadState Motion;
        public GamepadState Snapshot
        {
            get { var state = Motion; state.Buttons |= KeyButtons; return state; }
        }
        public void Key(GamepadButtons button, bool down)
        {
            if (down) KeyButtons |= button; else KeyButtons &= ~button;
        }
        public void Clear() { KeyButtons = 0; Motion = default; }
    }
}
