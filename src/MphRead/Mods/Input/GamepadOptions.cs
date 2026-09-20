using System.Collections.Generic;
namespace MphRead.Mods.Input
{
    // Compatibility view of the selected controller runtime.
    public static class GamepadOptions
    {
        private static GamepadOptionState State => GamepadRuntimeConfig.Current.Options;
        public static float LeftInner { get => State.LeftInner; set => State.LeftInner = value; }
        public static float RightInner { get => State.RightInner; set => State.RightInner = value; }
        public static float LeftOuter { get => State.LeftOuter; set => State.LeftOuter = value; }
        public static float RightOuter { get => State.RightOuter; set => State.RightOuter = value; }
        public static float LookX { get => State.LookX; set => State.LookX = value; }
        public static float LookY { get => State.LookY; set => State.LookY = value; }
        public static float TriggerThreshold { get => State.TriggerThreshold; set => State.TriggerThreshold = value; }
        public static float ActivityThreshold { get => State.ActivityThreshold; set => State.ActivityThreshold = value; }
        public static bool InvertX { get => State.InvertX; set => State.InvertX = value; }
        public static bool InvertY { get => State.InvertY; set => State.InvertY = value; }
        public static bool Southpaw { get => State.Southpaw; set => GamepadRuntimeConfig.Current.Layout.Southpaw = value; }
        public static bool Vibration { get => State.Vibration; set => State.Vibration = value; }
        public static float VibrationStrength { get => State.VibrationStrength; set => State.VibrationStrength = value; }
        public static GamepadCurve Curve { get => State.Curve; set => State.Curve = value; }
        public static GamepadFamily GlyphStyle { get => State.GlyphStyle; set => State.GlyphStyle = value; }
        public static float ScopedX { get => State.ScopedX; set => State.ScopedX = value; }
        public static float ScopedY { get => State.ScopedY; set => State.ScopedY = value; }
        public static float WheelThreshold { get => State.WheelThreshold; set => State.WheelThreshold = value; }
        public static bool WheelToggle { get => State.WheelToggle; set => State.WheelToggle = value; }
        public static GamepadButtons BindingModifier { get => State.BindingModifier; set => State.BindingModifier = value; }
        public static float LeftTriggerMin { get => State.LeftTriggerMin; set => State.LeftTriggerMin = value; }
        public static float RightTriggerMin { get => State.RightTriggerMin; set => State.RightTriggerMin = value; }
        public static float LeftTriggerMax { get => State.LeftTriggerMax; set => State.LeftTriggerMax = value; }
        public static float RightTriggerMax { get => State.RightTriggerMax; set => State.RightTriggerMax = value; }
        public static StickCalibration LeftCalibration { get => State.LeftCalibration; set => State.LeftCalibration = value; }
        public static StickCalibration RightCalibration { get => State.RightCalibration; set => State.RightCalibration = value; }
        public static int[] WheelOrder => State.WheelOrder;
        public static void SetWheelSlot(int position, int slot) => State.SetWheelSlot(position, slot);
        public static void Load(IEnumerable<string> lines) => State.Load(lines);
        public static void Write(List<string> lines) => State.Write(lines);
        public static void Reset() => State.Reset();
    }
}
