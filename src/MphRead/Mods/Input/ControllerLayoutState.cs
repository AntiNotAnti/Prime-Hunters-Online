namespace MphRead.Mods.Input
{
    // Layout identity includes bindings and stick assignment, never sensitivity/calibration.
    public sealed class ControllerLayoutState
    {
        private readonly GamepadOptionState _options;
        public PadBindingState Bindings { get; }
        internal ControllerLayoutState(GamepadOptionState options, PadBindingState bindings)
        { _options = options; Bindings = bindings; }
        public string Name => Bindings.Preset;
        public bool Southpaw { get => _options.Southpaw; set { _options.Southpaw = value; Bindings.Preset = "Custom"; } }
        public void Apply(string name)
        { Bindings.ApplyPreset(name); if (name != "Custom") _options.Southpaw = name == "Southpaw"; }
    }
}
