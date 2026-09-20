using System.Collections.Generic;
namespace MphRead.Mods.Input
{
    public static class PadBindings
    {
        private static PadBindingState State => GamepadRuntimeConfig.Current.Bindings;
        public static string Preset { get => State.Preset; internal set => State.Preset = value; }
        public static long Revision => State.Revision;
        public static IReadOnlyList<PadAction> Actions => State.Actions;
        public static GamepadButtons Get(PadAction action) => State.Get(action);
        public static void Set(PadAction action, GamepadButtons buttons) => State.Set(action, buttons);
        public static GamepadButtons Default(PadAction action) => State.Default(action);
        public static GamepadButtons Slot(PadAction action, int slot) => State.Slot(action, slot);
        public static void SetSlot(PadAction action, int slot, GamepadButtons button, GamepadButtons modifier = 0) => State.SetSlot(action, slot, button, modifier);
        public static bool Single(GamepadButtons button) => State.Single(button);
        public static GamepadButtons Modifier(PadAction action, int slot) => State.Modifier(action, slot);
        public static string DescribeSlot(PadAction action, int slot) => State.DescribeSlot(action, slot);
        public static ulong Evaluate(GamepadButtons buttons, GamepadButtons suppressed = 0) => State.Evaluate(buttons, suppressed);
        public static GamepadButtons ChordButtons(GamepadButtons buttons) => State.ChordButtons(buttons);
        public static void Write(List<string> lines) => State.Write(lines);
        public static void LoadSlots(IEnumerable<string> lines) => State.LoadSlots(lines);
        public static IReadOnlyList<PadAction> Conflicts(PadAction action, GamepadButtons button, GamepadButtons modifier = 0) => State.Conflicts(action, button, modifier);
        public static void Assign(PadAction action, int slot, GamepadButtons button, string resolution, GamepadButtons modifier = 0) => State.Assign(action, slot, button, resolution, modifier);
        public static void ApplyPreset(string name) => GamepadRuntimeConfig.Current.Layout.Apply(name);
        public static void Reset() => State.Reset();
        public static string Name(PadAction action) => State.Name(action);
        public static string Describe(GamepadButtons buttons) => State.Describe(buttons);
        public static string ButtonName(GamepadButtons button) => State.ButtonName(button);
        public static string SettingKey(PadAction action) => State.SettingKey(action);
        public static bool TryLoad(string key, string value) => State.TryLoad(key, value);
    }
}
