using System;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace MphRead.Mods.Input
{
    // Raw profiles are platform/device-specific. Axis count alone cannot distinguish
    // Linux's interleaved Xbox axes from the macOS Bluetooth HID layout.
    internal readonly struct GamepadLayout
    {
        public readonly int AxisLeftX;
        public readonly int AxisLeftY;
        public readonly int AxisRightX;
        public readonly int AxisRightY;
        /// <summary>Trigger axis, or -1 where the triggers are buttons.</summary>
        public readonly int AxisLeftTrigger;
        public readonly int AxisRightTrigger;
        public readonly int ButtonA;
        public readonly int ButtonB;
        public readonly int ButtonX;
        public readonly int ButtonY;
        public readonly int ButtonLeftBumper;
        public readonly int ButtonRightBumper;
        /// <summary>Trigger button, or -1 where the triggers are axes.</summary>
        public readonly int ButtonLeftTrigger;
        public readonly int ButtonRightTrigger;
        public readonly int ButtonBack;
        public readonly int ButtonStart;
        public readonly int ButtonLeftThumb;
        public readonly int ButtonRightThumb;

        internal GamepadCapabilities Capabilities(int axes)
        {
            bool Has(int axis) => axis >= 0 && axis < axes;
            return (Has(AxisLeftX) && Has(AxisLeftY) ? GamepadCapabilities.AnalogLeftStick : 0)
                | (Has(AxisRightX) && Has(AxisRightY) ? GamepadCapabilities.AnalogRightStick : 0)
                | (Has(AxisLeftTrigger) && Has(AxisRightTrigger) ? GamepadCapabilities.AnalogTriggers : 0);
        }
        private GamepadLayout(int axisLeftX, int axisLeftY, int axisRightX, int axisRightY,
            int axisLeftTrigger, int axisRightTrigger, int buttonA, int buttonB, int buttonX,
            int buttonY, int buttonLeftBumper, int buttonRightBumper, int buttonLeftTrigger,
            int buttonRightTrigger, int buttonBack, int buttonStart, int buttonLeftThumb,
            int buttonRightThumb)
        {
            AxisLeftX = axisLeftX;
            AxisLeftY = axisLeftY;
            AxisRightX = axisRightX;
            AxisRightY = axisRightY;
            AxisLeftTrigger = axisLeftTrigger;
            AxisRightTrigger = axisRightTrigger;
            ButtonA = buttonA;
            ButtonB = buttonB;
            ButtonX = buttonX;
            ButtonY = buttonY;
            ButtonLeftBumper = buttonLeftBumper;
            ButtonRightBumper = buttonRightBumper;
            ButtonLeftTrigger = buttonLeftTrigger;
            ButtonRightTrigger = buttonRightTrigger;
            ButtonBack = buttonBack;
            ButtonStart = buttonStart;
            ButtonLeftThumb = buttonLeftThumb;
            ButtonRightThumb = buttonRightThumb;
        }

        /// <summary>The Xbox-shaped pad: analogue triggers on their own axes.</summary>
        private static readonly GamepadLayout Triggers = new GamepadLayout(
            axisLeftX: 0, axisLeftY: 1, axisRightX: 3, axisRightY: 4,
            axisLeftTrigger: 2, axisRightTrigger: 5,
            buttonA: 0, buttonB: 1, buttonX: 2, buttonY: 3,
            buttonLeftBumper: 4, buttonRightBumper: 5,
            buttonLeftTrigger: -1, buttonRightTrigger: -1,
            buttonBack: 6, buttonStart: 7, buttonLeftThumb: 9, buttonRightThumb: 10);

        /// <summary>The flat pad: four shoulder buttons and no analogue triggers.</summary>
        private static readonly GamepadLayout Buttons = new GamepadLayout(
            axisLeftX: 0, axisLeftY: 1, axisRightX: 2, axisRightY: 3,
            axisLeftTrigger: -1, axisRightTrigger: -1,
            buttonA: 0, buttonB: 1, buttonX: 2, buttonY: 3,
            buttonLeftBumper: 4, buttonRightBumper: 5,
            buttonLeftTrigger: 6, buttonRightTrigger: 7,
            buttonBack: 8, buttonStart: 9, buttonLeftThumb: 10, buttonRightThumb: 11);

        // SDL_GameControllerDB's macOS Series/updated Xbox Bluetooth HID profile.
        // Firmware changes alter the GUID without changing this physical layout.
        private static readonly GamepadLayout MacXboxBluetooth = new GamepadLayout(
            axisLeftX: 0, axisLeftY: 1, axisRightX: 2, axisRightY: 3,
            axisLeftTrigger: 5, axisRightTrigger: 4,
            buttonA: 0, buttonB: 1, buttonX: 3, buttonY: 4,
            buttonLeftBumper: 6, buttonRightBumper: 7,
            buttonLeftTrigger: -1, buttonRightTrigger: -1,
            buttonBack: 10, buttonStart: 11, buttonLeftThumb: 13, buttonRightThumb: 14);

        internal static bool IsMacXboxBluetooth(string guid, int axes, int buttons, int hats, bool macOS)
            => macOS && axes == 6 && buttons >= 15 && hats == 1 && guid.Length == 32
                && guid.AsSpan(0, 8).Equals("03000000", StringComparison.OrdinalIgnoreCase)
                && guid.AsSpan(8, 8).Equals("5e040000", StringComparison.OrdinalIgnoreCase)
                && (guid.AsSpan(16, 8).Equals("130b0000", StringComparison.OrdinalIgnoreCase)
                    || guid.AsSpan(16, 8).Equals("200b0000", StringComparison.OrdinalIgnoreCase));

        internal static GamepadLayout Select(string guid, int axes, int buttons, int hats, bool macOS)
            => IsMacXboxBluetooth(guid, axes, buttons, hats, macOS) ? MacXboxBluetooth
                : axes >= 6 ? Triggers : Buttons;

        public static GamepadLayout For(int slot)
            => Select(GLFW.GetJoystickGUID(slot) ?? "", GLFW.GetJoystickAxes(slot).Length,
                GLFW.GetJoystickButtons(slot).Length, GLFW.GetJoystickHats(slot).Length, OperatingSystem.IsMacOS());

        internal GamepadState Read(ReadOnlySpan<float> axes, ReadOnlySpan<JoystickInputAction> buttons,
            ReadOnlySpan<JoystickHats> hats, ref float leftFloor, ref float rightFloor)
        {
            var state = new GamepadState
            {
                Connected = true,
                LeftX = Axis(axes, AxisLeftX), LeftY = -Axis(axes, AxisLeftY),
                RightX = Axis(axes, AxisRightX), RightY = -Axis(axes, AxisRightY),
                LeftTrigger = Trigger(axes, AxisLeftTrigger, ref leftFloor),
                RightTrigger = Trigger(axes, AxisRightTrigger, ref rightFloor)
            };
            GamepadButtons flags = 0;
            Add(ref flags, buttons, ButtonA, GamepadButtons.A); Add(ref flags, buttons, ButtonB, GamepadButtons.B);
            Add(ref flags, buttons, ButtonX, GamepadButtons.X); Add(ref flags, buttons, ButtonY, GamepadButtons.Y);
            Add(ref flags, buttons, ButtonLeftBumper, GamepadButtons.LeftBumper);
            Add(ref flags, buttons, ButtonRightBumper, GamepadButtons.RightBumper);
            Add(ref flags, buttons, ButtonBack, GamepadButtons.Back); Add(ref flags, buttons, ButtonStart, GamepadButtons.Start);
            Add(ref flags, buttons, ButtonLeftThumb, GamepadButtons.LeftThumb); Add(ref flags, buttons, ButtonRightThumb, GamepadButtons.RightThumb);
            Add(ref flags, buttons, ButtonLeftTrigger, GamepadButtons.LeftTrigger);
            Add(ref flags, buttons, ButtonRightTrigger, GamepadButtons.RightTrigger);
            if (hats.Length > 0)
            {
                if ((hats[0] & JoystickHats.Up) != 0) flags |= GamepadButtons.DpadUp;
                if ((hats[0] & JoystickHats.Down) != 0) flags |= GamepadButtons.DpadDown;
                if ((hats[0] & JoystickHats.Left) != 0) flags |= GamepadButtons.DpadLeft;
                if ((hats[0] & JoystickHats.Right) != 0) flags |= GamepadButtons.DpadRight;
            }
            state.Buttons = flags;
            return state;
        }
        private static float Axis(ReadOnlySpan<float> axes, int index)
            => index >= 0 && index < axes.Length ? GamepadAnalog.Finite(axes[index]) : 0;
        private static float Trigger(ReadOnlySpan<float> axes, int index, ref float floor)
        {
            if (index < 0 || index >= axes.Length) return 0;
            float value = GamepadAnalog.Finite(axes[index]);
            floor = Math.Min(floor, value);
            return Math.Clamp((value - floor) / (1 - floor), 0, 1);
        }
        private static void Add(ref GamepadButtons flags, ReadOnlySpan<JoystickInputAction> buttons, int index, GamepadButtons flag)
        {
            if (index >= 0 && index < buttons.Length && buttons[index] == JoystickInputAction.Press) flags |= flag;
        }
    }
}
