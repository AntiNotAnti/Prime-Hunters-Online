using MphRead.Entities;

namespace MphRead.Mods.Input
{
    // Actions, not physical edges: a chord must not also fire its component bindings.
    public sealed class GamepadActions
    {
        private ulong _down;
        private GamepadButtons _suppressed;
        public ulong Pressed { get; private set; }
        public bool WheelOpen { get; private set; }
        public bool Down(PadAction action) => (_down & (1UL << (int)action)) != 0;
        public bool WasPressed(PadAction action) => (Pressed & (1UL << (int)action)) != 0;
        public bool Take(PadAction action)
        {
            bool pressed = WasPressed(action); Pressed &= ~(1UL << (int)action); return pressed;
        }
        public void CloseWheel() => WheelOpen = false;
        public void Reset() { _down = Pressed = 0; _suppressed = 0; WheelOpen = false; }
        public void Update(GamepadButtons buttons)
        {
            _suppressed &= buttons;
            _suppressed |= PadBindings.ChordButtons(buttons);
            ulong down = PadBindings.Evaluate(buttons, _suppressed);
            Pressed = down & ~_down; _down = down;
            WheelOpen = GamepadOptions.WheelToggle
                ? WasPressed(PadAction.WeaponWheel) ? !WheelOpen : WheelOpen
                : Down(PadAction.WeaponWheel);
        }
        internal static Keybind? WeaponBind(PlayerControls controls, BeamType weapon) => weapon switch
        {
            BeamType.PowerBeam => controls.PowerBeam, BeamType.Missile => controls.Missile,
            BeamType.VoltDriver => controls.VoltDriver, BeamType.Battlehammer => controls.Battlehammer,
            BeamType.Imperialist => controls.Imperialist, BeamType.Judicator => controls.Judicator,
            BeamType.Magmaul => controls.Magmaul, BeamType.ShockCoil => controls.ShockCoil,
            BeamType.OmegaCannon => controls.OmegaCannon, _ => null
        };
    }
}
