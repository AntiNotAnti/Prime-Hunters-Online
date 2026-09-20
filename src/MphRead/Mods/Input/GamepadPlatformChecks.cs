using System;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace MphRead.Mods.Input
{
    internal static class GamepadPlatformChecks
    {
        internal static void Run()
        {
            const string guid = "030000005e040000130b000099090000";
            var profile = GamepadLayout.Select(guid, 6, 19, 1, macOS: true);
            float leftFloor = 0, rightFloor = 0;
            float[] axes = { 0, 0, 0, 0, -1, -1 };
            var buttons = new JoystickInputAction[19];
            var hats = new[] { JoystickHats.Centered };
            GamepadState Read() => profile.Read(axes, buttons, hats, ref leftFloor, ref rightFloor);
            var state = Read();
            GamepadChecks.Check(state.RightX == 0 && state.RightY == 0 && state.RightTrigger == 0 && state.LeftTrigger == 0,
                "macOS Xbox Bluetooth rests with neutral sticks and released triggers");
            axes[4] = 1; state = Read();
            GamepadChecks.Check(state.RightTrigger == 1 && state.LeftTrigger == 0 && state.RightX == 0 && state.RightY == 0,
                "macOS Xbox right trigger cannot move the camera");
            GamepadManager.UpdateDevice("mac-fixture", state, false);
            GamepadChecks.Check(GamepadManager.ActiveState.Down(GamepadButtons.RightTrigger), "raw macOS trigger reaches bindable RT flag");
            axes[4] = -1; axes[5] = 1; state = Read();
            GamepadChecks.Check(state.LeftTrigger == 1 && state.RightTrigger == 0 && state.RightX == 0 && state.RightY == 0,
                "macOS Xbox left trigger cannot move either stick");
            axes[5] = -1; axes[2] = .75f; axes[3] = -.5f; state = Read();
            GamepadChecks.Check(state.RightX == .75f && state.RightY == .5f && state.LeftTrigger == 0 && state.RightTrigger == 0,
                "macOS Xbox right stick does not actuate triggers");
            axes[2] = axes[3] = 0;
            foreach (var (index, expected) in new[]
            {
                (0, GamepadButtons.A), (1, GamepadButtons.B), (3, GamepadButtons.X), (4, GamepadButtons.Y),
                (6, GamepadButtons.LeftBumper), (7, GamepadButtons.RightBumper), (10, GamepadButtons.Back),
                (11, GamepadButtons.Start), (13, GamepadButtons.LeftThumb), (14, GamepadButtons.RightThumb)
            })
            {
                buttons[index] = JoystickInputAction.Press;
                GamepadChecks.Check(Read().Buttons == expected, "macOS Xbox physical " + expected);
                buttons[index] = JoystickInputAction.Release;
            }
            hats[0] = JoystickHats.RightUp;
            GamepadChecks.Check(Read().Buttons == (GamepadButtons.DpadUp | GamepadButtons.DpadRight), "macOS Xbox diagonal hat");
            GamepadManager.RemoveDevice("mac-fixture");
            var mapping = GamepadMappings.CompatibleMacXboxMapping(guid, "Xbox Wireless Controller", 6, 19, 1, true);
            GamepadChecks.Check(mapping != null && mapping.StartsWith(guid + ",") && mapping.Contains("righttrigger:a4,")
                && mapping.Contains("lefttrigger:a5,") && mapping.Contains("rightx:a2,righty:a3,"), "unknown Xbox firmware receives correct macOS mapping");
            GamepadChecks.Check(GamepadMappings.CompatibleMacXboxMapping(guid, "Xbox", 6, 19, 1, false) == null,
                "macOS firmware compatibility never overrides Windows/Linux layouts");
            GamepadChecks.Check(GamepadMappings.CompatibleMacXboxMapping(guid, "Xbox", 7, 19, 1, true) == null
                && GamepadMappings.CompatibleMacXboxMapping(guid, "Xbox", 6, 10, 0, true) == null,
                "unrecognized HID shapes are not silently assigned a Bluetooth mapping");
            GamepadChecks.Check(GamepadLayout.Select(guid, 6, 19, 1, false).AxisRightX == 3,
                "existing Linux raw profile stays interleaved");
            GamepadChecks.Check(GamepadLayout.Select("generic", 4, 12, 1, false).ButtonRightTrigger == 7,
                "generic four-axis digital-trigger profile remains available");

            GamepadOptions.LookX = 1.75f;
            foreach (string preset in new[] { "Default", "Bumper Jumper", "Southpaw", "Classic" })
            {
                PadBindings.ApplyPreset(preset);
                GamepadChecks.Check(PadBindings.Get(PadAction.Shoot) == GamepadButtons.RightTrigger
                    && PadBindings.Get(PadAction.Zoom) == GamepadButtons.LeftTrigger, preset + " preserves trigger actions");
                GamepadChecks.Check(GamepadOptions.Southpaw == (preset == "Southpaw"), preset + " selects correct stick roles");
                GamepadChecks.Check(GamepadOptions.LookX == 1.75f, preset + " preserves personal calibration");
            }
            PadBindings.Reset();
            PadBindings.SetSlot(PadAction.Jump, 0, 0); PadBindings.SetSlot(PadAction.Jump, 1, GamepadButtons.A);
            PadBindings.Assign(PadAction.Shoot, 0, GamepadButtons.A, "Swap");
            GamepadChecks.Check(PadBindings.Slot(PadAction.Jump, 0) == 0
                && PadBindings.Slot(PadAction.Jump, 1) == GamepadButtons.RightTrigger, "conflict swaps preserve primary/secondary positions");
            PadBindings.Reset(); GamepadOptions.Reset();
        }
    }
}
