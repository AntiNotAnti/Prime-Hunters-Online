using System;
using System.Linq;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace MphRead.Mods.Input
{
    internal static class GamepadDesktop
    {
        private sealed class Slot
        {
            public string? Id;
            public int Generation;
            public float LeftFloor, RightFloor;
            public string Name = "gamepad", Mapping = "";
            public bool Mapped, XInput;
            public GamepadFamily Family;
            public GamepadLayout Layout;
            public GamepadCapabilities Capabilities;
        }
        private static readonly Slot[] Slots = CreateSlots();
        private static bool _unavailable;
        private static Slot[] CreateSlots()
        {
            var slots = new Slot[16];
            for (int i = 0; i < slots.Length; i++) slots[i] = new Slot();
            return slots;
        }

        // GLFW's own gamepad indices. Written out rather than taken from an
        // enum because OpenTK 4.9 binds `glfwGetGamepadState` without binding
        // the two enums that name its slots -- `GamepadState` is a pair of
        // fixed arrays and nothing else. These are GLFW_GAMEPAD_BUTTON_* and
        // GLFW_GAMEPAD_AXIS_*, which are part of its stable API.
        private const int ButtonA = 0;
        private const int ButtonB = 1;
        private const int ButtonX = 2;
        private const int ButtonY = 3;
        private const int ButtonLeftBumper = 4;
        private const int ButtonRightBumper = 5;
        private const int ButtonBack = 6;
        private const int ButtonStart = 7;
        private const int ButtonLeftThumb = 9;
        private const int ButtonRightThumb = 10;
        private const int ButtonDpadUp = 11;
        private const int ButtonDpadRight = 12;
        private const int ButtonDpadDown = 13;
        private const int ButtonDpadLeft = 14;

        private const int AxisLeftX = 0;
        private const int AxisLeftY = 1;
        private const int AxisRightX = 2;
        private const int AxisRightY = 3;
        private const int AxisLeftTrigger = 4;
        private const int AxisRightTrigger = 5;

        // OpenTK forwards lifecycle events without replacing GLFW's window-owned callback.
        public static void DeviceChanged(int index)
        {
            if ((uint)index >= Slots.Length) return;
            var slot = Slots[index];
            if (slot.Id != null) { GamepadHaptics.Unregister(slot.Id); GamepadManager.RemoveDevice(slot.Id); }
            slot.Id = null;
        }

        public static void Poll()
        {
            if (OperatingSystem.IsAndroid())
            {
                // The Android head has no GLFW at all -- its window is an
                // Android one and its pad arrives as key and motion events.
                // See GamepadBridge there.
                return;
            }
            try
            {
                PollUnsafe();
            }
            catch (Exception ex) when (ex is DllNotFoundException
                || ex is EntryPointNotFoundException || ex is BadImageFormatException)
            {
                // No GLFW in this process: the dedicated server, the map
                // audit, anything headless. Not an error -- there is no window
                // and there is nobody holding a pad.
                foreach (var slot in Slots) if (slot.Id != null) GamepadManager.RemoveDevice(slot.Id);
                _unavailable = true;
            }
        }

        private static void PollUnsafe()
        {
            if (_unavailable) return;
            if (GamepadMappings.ReloadRequested)
            {
                GamepadMappings.ReloadRequested = false;
                for (int slot = 0; slot < Slots.Length; slot++) DeviceChanged(slot);
            }
            GamepadMappings.EnsureLoaded();
            for (int i = 0; i < Slots.Length; i++)
            {
                Slot slot = Slots[i];
                if (!GLFW.JoystickPresent(i))
                {
                    if (slot.Id != null) GamepadManager.RemoveDevice(slot.Id);
                    slot.Id = null;
                    continue;
                }
                if (slot.Id == null)
                {
                    slot.Generation++;
                    string guid = GLFW.GetJoystickGUID(i);
                    slot.XInput = guid.StartsWith("78696e707574", StringComparison.OrdinalIgnoreCase);
                    slot.Id = $"glfw:{guid}:{i}:{slot.Generation}";
                    bool compatible = GamepadMappings.TryMapMacXbox(i);
                    slot.Mapped = GLFW.JoystickIsGamepad(i);
                    slot.Mapping = compatible ? "Xbox Bluetooth compatibility" : slot.Mapped ? "GLFW mapping" : "Unmapped fallback";
                    slot.Name = (slot.Mapped ? GLFW.GetGamepadName(i) : GLFW.GetJoystickName(i)) ?? "gamepad";
                    if (!slot.Mapped) slot.Name += " (unmapped)";
                    slot.Family = GamepadGlyphs.Detect(slot.Name, guid);
                    slot.Layout = GamepadLayout.For(i);
                    slot.Capabilities = GamepadMappings.Capabilities(guid, slot.Layout.Capabilities(GLFW.GetJoystickAxes(i).Length));
                    slot.LeftFloor = slot.RightFloor = 0;
                }
                if (GamepadMappingWizard.RequestedDevice == slot.Id)
                    GamepadMappingWizard.Latest = new(slot.Id, GLFW.GetJoystickGUID(i), slot.Name,
                        GLFW.GetJoystickAxes(i).ToArray(), GLFW.GetJoystickButtons(i).ToArray().Select(b => b == JoystickInputAction.Press).ToArray(),
                        GLFW.GetJoystickHats(i).ToArray().Select(h => (byte)h).ToArray());
                if (!(slot.Mapped ? TryRead(i) : TryReadRaw(i)))
                {
                    GamepadManager.RemoveDevice(slot.Id);
                    slot.Id = null;
                }
            }
            string? uniqueId = null;
            int xinputCount = 0;
            foreach (var slot in Slots) if (slot.Id != null && slot.XInput) { uniqueId = slot.Id; xinputCount++; }
            WindowsGamepadHaptics.Synchronize(xinputCount == 1 ? uniqueId : null);
        }

        private static unsafe bool TryRead(int slot)
        {
            if (!GLFW.JoystickIsGamepad(slot)
                || !GLFW.GetGamepadState(slot, out OpenTK.Windowing.GraphicsLibraryFramework
                    .GamepadState raw))
            {
                return false;
            }
            var state = new Mods.Input.GamepadState
            {
                Connected = true,
                Name = Slots[slot].Name,
                LeftX = raw.Axes[AxisLeftX],
                // Negated: GLFW reports a stick pushed forward as -1 and every
                // caller above wants forward to be positive. See GamepadState.
                LeftY = -raw.Axes[AxisLeftY],
                RightX = raw.Axes[AxisRightX],
                RightY = -raw.Axes[AxisRightY],
                // Triggers rest at -1 and go to 1, unlike the sticks, so they
                // are moved onto 0..1 here rather than at each use.
                LeftTrigger = (raw.Axes[AxisLeftTrigger] + 1) / 2,
                RightTrigger = (raw.Axes[AxisRightTrigger] + 1) / 2
            };
            GamepadButtons buttons = GamepadButtons.None;
            Add(ref buttons, raw.Buttons, ButtonA, GamepadButtons.A);
            Add(ref buttons, raw.Buttons, ButtonB, GamepadButtons.B);
            Add(ref buttons, raw.Buttons, ButtonX, GamepadButtons.X);
            Add(ref buttons, raw.Buttons, ButtonY, GamepadButtons.Y);
            Add(ref buttons, raw.Buttons, ButtonLeftBumper, GamepadButtons.LeftBumper);
            Add(ref buttons, raw.Buttons, ButtonRightBumper, GamepadButtons.RightBumper);
            Add(ref buttons, raw.Buttons, ButtonBack, GamepadButtons.Back);
            Add(ref buttons, raw.Buttons, ButtonStart, GamepadButtons.Start);
            Add(ref buttons, raw.Buttons, ButtonLeftThumb, GamepadButtons.LeftThumb);
            Add(ref buttons, raw.Buttons, ButtonRightThumb, GamepadButtons.RightThumb);
            Add(ref buttons, raw.Buttons, ButtonDpadUp, GamepadButtons.DpadUp);
            Add(ref buttons, raw.Buttons, ButtonDpadRight, GamepadButtons.DpadRight);
            Add(ref buttons, raw.Buttons, ButtonDpadDown, GamepadButtons.DpadDown);
            Add(ref buttons, raw.Buttons, ButtonDpadLeft, GamepadButtons.DpadLeft);
            state.Buttons = buttons;
            GamepadManager.UpdateDevice(Slots[slot].Id!, state, Slots[slot].Mapped, Slots[slot].Family,
                capabilities: Slots[slot].Capabilities
                    | (GamepadHaptics.Available(Slots[slot].Id!) ? GamepadCapabilities.Rumble : 0),
                mapping: Slots[slot].Mapping);
            return true;
        }

        private static bool TryReadRaw(int slot)
        {
            if (!GLFW.JoystickPresent(slot) || GLFW.JoystickIsGamepad(slot))
            {
                return false;
            }
            ReadOnlySpan<float> axes = GLFW.GetJoystickAxes(slot);
            ReadOnlySpan<JoystickInputAction> buttons = GLFW.GetJoystickButtons(slot);
            // A device with no axes and no buttons is not something anybody is
            // playing with -- and GLFW counts things that are not pads at all
            // as joysticks, from steering wheels to the accelerometer in a
            // laptop lid.
            if (axes.Length < 2 || buttons.Length < 4)
            {
                return false;
            }
            var state = Slots[slot].Layout.Read(axes, buttons, GLFW.GetJoystickHats(slot),
                ref Slots[slot].LeftFloor, ref Slots[slot].RightFloor);
            state.Name = Slots[slot].Name;
            GamepadManager.UpdateDevice(Slots[slot].Id!, state, Slots[slot].Mapped, Slots[slot].Family,
                capabilities: Slots[slot].Layout.Capabilities(axes.Length)
                    | (GamepadHaptics.Available(Slots[slot].Id!) ? GamepadCapabilities.Rumble : 0),
                mapping: Slots[slot].Mapping);
            return true;
        }

        private static unsafe void Add(ref GamepadButtons into,
            byte* buttons, int index, GamepadButtons flag)
        {
            if (buttons[index] == (byte)JoystickInputAction.Press)
            {
                into |= flag;
            }
        }
    }
}
