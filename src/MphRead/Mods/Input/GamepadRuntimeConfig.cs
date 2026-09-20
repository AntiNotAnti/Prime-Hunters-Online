namespace MphRead.Mods.Input
{
    public sealed class GamepadRuntimeConfig
    {
        internal static GamepadRuntimeConfig Fallback = new();
        private static GamepadRuntimeConfig _selected = Fallback;
        [System.ThreadStatic] internal static GamepadRuntimeConfig? Frame;
        // Android may publish another pad while the simulation is consuming a frame.
        // Compatibility readers use the runtime captured with that frame's state.
        internal static GamepadRuntimeConfig Current
        {
            get => Frame ?? System.Threading.Volatile.Read(ref _selected);
            set { System.Threading.Volatile.Write(ref _selected, value); Frame = null; }
        }
        public GamepadOptionState Options { get; }
        public PadBindingState Bindings { get; }
        public ControllerLayoutState Layout { get; }
        internal string ProfileName { get; set; } = "Custom settings";
        public GamepadRuntimeConfig() : this(new(), new()) { }
        private GamepadRuntimeConfig(GamepadOptionState options, PadBindingState bindings)
        { Options = options; Bindings = bindings; Layout = new(options, bindings); }
        public GamepadRuntimeConfig Clone() => new(Options.Clone(), Bindings.Clone()) { ProfileName = ProfileName };
    }
}
