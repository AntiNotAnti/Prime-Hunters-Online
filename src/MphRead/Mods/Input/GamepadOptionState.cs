using System;
using System.Collections.Generic;
using System.Globalization;

namespace MphRead.Mods.Input
{
    // Independent of the engine so migration and calibration can be tested without a window.
    public sealed class GamepadOptionState
    {
        public StickCalibration LeftCalibration = StickCalibration.Default, RightCalibration = StickCalibration.Default;
        public float LeftInner = 0.2f, RightInner = 0.2f, LeftOuter, RightOuter;
        public float LookX = 1, LookY = 1, TriggerThreshold = 0.60f, ActivityThreshold = 0.35f;
        public bool InvertX, InvertY, Southpaw, Vibration = true;
        public float VibrationStrength = 0.65f;
        public GamepadCurve Curve = GamepadCurve.Classic;
        public GamepadFamily GlyphStyle;
        public float ScopedX = 1, ScopedY = 1, WheelThreshold = .45f;
        public bool WheelToggle;
        public GamepadButtons BindingModifier;
        public float LeftTriggerMin, RightTriggerMin, LeftTriggerMax = 1, RightTriggerMax = 1;
        public int[] WheelOrder { get; } = { 0, 1, 2, 3, 4, 5 };
        public void SetWheelSlot(int position, int slot)
        {
            int previous = Array.IndexOf(WheelOrder, slot);
            if (position < 0 || position >= 6 || previous < 0) return;
            (WheelOrder[position], WheelOrder[previous]) = (WheelOrder[previous], WheelOrder[position]);
        }

        public void Load(IEnumerable<string> lines)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in lines)
            {
                int split = line.IndexOf('=');
                if (split > 0) values[line[..split].Trim()] = line[(split + 1)..].Trim();
            }
            float Number(string key, float fallback, float min, float max)
                => values.TryGetValue(key, out var text) && float.TryParse(text, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var n) && float.IsFinite(n) ? Math.Clamp(n, min, max) : fallback;
            bool Flag(string key, bool fallback) => values.TryGetValue(key, out var text)
                && bool.TryParse(text, out var b) ? b : fallback;
            StickCalibration Calibration(string side) => new(
                Number("gamepad_" + side + "_center_x", 0, -.3f, .3f), Number("gamepad_" + side + "_center_y", 0, -.3f, .3f),
                Number("gamepad_" + side + "_min_x", -1, -1, -.4f), Number("gamepad_" + side + "_max_x", 1, .4f, 1),
                Number("gamepad_" + side + "_min_y", -1, -1, -.4f), Number("gamepad_" + side + "_max_y", 1, .4f, 1));
            LeftCalibration = Calibration("left"); RightCalibration = Calibration("right");
            ScopedX = Number("gamepad_scoped_x", 1, .1f, 3);
            ScopedY = Number("gamepad_scoped_y", 1, .1f, 3);
            WheelThreshold = Number("gamepad_wheel_threshold", .45f, .1f, .95f);
            WheelToggle = Flag("gamepad_wheel_toggle", false);
            BindingModifier = values.TryGetValue("gamepad_binding_modifier", out var m)
                && Enum.TryParse<GamepadButtons>(m, out var modifier) && PadBindings.Single(modifier) ? modifier : 0;
            LeftTriggerMin = Number("gamepad_lt_min", 0, 0, .8f);
            RightTriggerMin = Number("gamepad_rt_min", 0, 0, .8f);
            LeftTriggerMax = Number("gamepad_lt_max", 1, LeftTriggerMin + .1f, 1);
            RightTriggerMax = Number("gamepad_rt_max", 1, RightTriggerMin + .1f, 1);
            for (int i = 0; i < 6; i++) WheelOrder[i] = i;
            if (values.TryGetValue("gamepad_wheel_order", out var order))
            {
                var parts = order.Split(','); var parsed = new int[6]; int used = 0;
                if (parts.Length == 6)
                {
                    for (int i = 0; i < 6; i++)
                        if (int.TryParse(parts[i], out int n) && n >= 0 && n < 6 && (used & (1 << n)) == 0)
                        { parsed[i] = n; used |= 1 << n; } else break;
                    if (used == 63) Array.Copy(parsed, WheelOrder, 6);
                }
            }
            float legacyDead = Number("gamepad_deadzone", 0.2f, 0, 0.9f);
            float legacyLook = Number("gamepad_look", 1, 0.1f, 5);
            LeftInner = Number("gamepad_left_inner_deadzone", legacyDead, 0, 0.9f);
            RightInner = Number("gamepad_right_inner_deadzone", legacyDead, 0, 0.9f);
            LeftOuter = Number("gamepad_left_outer_deadzone", 0, 0, 0.5f);
            RightOuter = Number("gamepad_right_outer_deadzone", 0, 0, 0.5f);
            LookX = Number("gamepad_look_x", legacyLook, 0.1f, 5);
            LookY = Number("gamepad_look_y", legacyLook, 0.1f, 5);
            TriggerThreshold = Number("gamepad_trigger_threshold", 0.60f, 0.05f, 0.95f);
            ActivityThreshold = Number("gamepad_activity_threshold", 0.35f, 0.2f, 0.95f);
            VibrationStrength = Number("gamepad_vibration_strength", 0.65f, 0, 1);
            InvertX = Flag("gamepad_invert_x", false); InvertY = Flag("gamepad_invert_y", false);
            Southpaw = Flag("gamepad_southpaw", false); Vibration = Flag("gamepad_vibration", true);
            Curve = values.TryGetValue("gamepad_curve", out var c) && Enum.TryParse<GamepadCurve>(c, out var curve)
                && Enum.IsDefined(curve) ? curve : GamepadCurve.Classic;
            GlyphStyle = values.TryGetValue("gamepad_glyph_style", out var g) && Enum.TryParse<GamepadFamily>(g, out var glyph)
                && Enum.IsDefined(glyph) ? glyph : GamepadFamily.Unknown;
        }
        public void Write(List<string> lines)
        {
            void Number(string key, float n) => lines.Add(key + "=" + n.ToString(CultureInfo.InvariantCulture));
            void Calibration(string side, StickCalibration c)
            {
                Number("gamepad_" + side + "_center_x", c.CenterX); Number("gamepad_" + side + "_center_y", c.CenterY);
                Number("gamepad_" + side + "_min_x", c.MinX); Number("gamepad_" + side + "_max_x", c.MaxX);
                Number("gamepad_" + side + "_min_y", c.MinY); Number("gamepad_" + side + "_max_y", c.MaxY);
            }
            Calibration("left", LeftCalibration); Calibration("right", RightCalibration);
            Number("gamepad_scoped_x", ScopedX); Number("gamepad_scoped_y", ScopedY);
            Number("gamepad_wheel_threshold", WheelThreshold);
            Number("gamepad_lt_min", LeftTriggerMin); Number("gamepad_lt_max", LeftTriggerMax);
            Number("gamepad_rt_min", RightTriggerMin); Number("gamepad_rt_max", RightTriggerMax);
            lines.Add("gamepad_invert_y=" + InvertY);
            lines.Add("gamepad_wheel_toggle=" + WheelToggle);
            lines.Add("gamepad_binding_modifier=" + BindingModifier);
            lines.Add("gamepad_wheel_order=" + string.Join(",", WheelOrder));
            Number("gamepad_left_inner_deadzone", LeftInner); Number("gamepad_right_inner_deadzone", RightInner);
            Number("gamepad_left_outer_deadzone", LeftOuter); Number("gamepad_right_outer_deadzone", RightOuter);
            Number("gamepad_look_x", LookX); Number("gamepad_look_y", LookY);
            Number("gamepad_trigger_threshold", TriggerThreshold); Number("gamepad_activity_threshold", ActivityThreshold);
            Number("gamepad_vibration_strength", VibrationStrength);
            lines.Add("gamepad_invert_x=" + InvertX); lines.Add("gamepad_southpaw=" + Southpaw);
            lines.Add("gamepad_vibration=" + Vibration); lines.Add("gamepad_curve=" + Curve);
            lines.Add("gamepad_glyph_style=" + GlyphStyle);
        }
        public GamepadOptionState Clone() { var copy = new GamepadOptionState(); var lines = new List<string>(); Write(lines); copy.Load(lines); return copy; }
        public void Reset() => Load(Array.Empty<string>());
    }
}
