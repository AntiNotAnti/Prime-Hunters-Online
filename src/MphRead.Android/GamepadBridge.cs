using System.Collections.Generic;
using Android.Content;
using Android.Hardware.Input;
using Android.OS;
using Android.Views;
using MphRead.Mods.Input;

namespace MphRead.Droid
{
    internal static class GamepadBridge
    {
        private sealed class Pad
        {
            public required string Id, Name;
            public required AndroidGamepadProfile Profile;
            public GamepadFamily Family;
            public readonly GamepadEventState Input = new();
        }
        private static readonly Dictionary<int, Pad> Pads = new();
        private static InputManager? _manager;
        private static Listener? _listener;
        private static int _generation;

        private sealed class Listener : Java.Lang.Object, InputManager.IInputDeviceListener
        {
            public void OnInputDeviceAdded(int id) => Ensure(id);
            public void OnInputDeviceChanged(int id) { Remove(id); Ensure(id); }
            public void OnInputDeviceRemoved(int id) => Remove(id);
        }
        public static void Start(Context context)
        {
            Stop();
            _manager = context.GetSystemService(Context.InputService) as InputManager;
            _listener = new Listener();
            _manager?.RegisterInputDeviceListener(_listener, new Handler(Looper.MainLooper!));
            foreach (int id in InputDevice.GetDeviceIds() ?? System.Array.Empty<int>()) Ensure(id);
        }
        public static void Stop()
        {
            if (_listener != null) _manager?.UnregisterInputDeviceListener(_listener);
            foreach (var pad in Pads.Values) { GamepadHaptics.Unregister(pad.Id); GamepadManager.RemoveDevice(pad.Id); }
            Pads.Clear();
            _listener?.Dispose(); _listener = null; _manager = null;
        }
        public static void Clear()
        {
            foreach (var pad in Pads.Values) { pad.Input.Clear(); GamepadManager.ClearDevice(pad.Id); }
            GamepadHaptics.Stop();
        }
        private static void Remove(int id)
        {
            if (Pads.Remove(id, out var pad))
            {
                pad.Input.Clear(); GamepadHaptics.Unregister(pad.Id); GamepadManager.RemoveDevice(pad.Id);
            }
        }
        private static Pad? Ensure(int id)
        {
            if (Pads.TryGetValue(id, out var pad)) return pad;
            using var device = InputDevice.GetDevice(id);
            if (device == null || !IsGamepad(device.Sources)) return null;
            pad = new Pad
            {
                Id = $"android:{device.Descriptor}:{id}:{++_generation}",
                Name = device.Name ?? "gamepad", Family = GamepadGlyphs.Detect(device.Name ?? "", vendorId: device.VendorId), Profile = new AndroidGamepadProfile(device)
            };
            Pads.Add(id, pad);
            var haptics = new AndroidGamepadHaptics(id);
            if (haptics.Available) GamepadHaptics.Register(pad.Id, haptics);
            Publish(pad);
            return pad;
        }
        private static void Publish(Pad pad)
        {
            var state = pad.Input.Snapshot;
            state.Connected = true; state.Name = pad.Name;
            GamepadManager.UpdateDevice(pad.Id, state, true, pad.Family,
                capabilities: (pad.Profile.LeftTrigger.HasValue && pad.Profile.RightTrigger.HasValue ? GamepadCapabilities.AnalogTriggers : 0)
                    | (pad.Profile.HasLeftStick ? GamepadCapabilities.AnalogLeftStick : 0)
                    | (pad.Profile.RightX.HasValue && pad.Profile.RightY.HasValue ? GamepadCapabilities.AnalogRightStick : 0)
                    | (GamepadHaptics.Available(pad.Id) ? GamepadCapabilities.Rumble : 0));
        }
        private static bool IsGamepad(InputSourceType source)
            => (source & InputSourceType.Gamepad) == InputSourceType.Gamepad
                || (source & InputSourceType.Joystick) == InputSourceType.Joystick;

        public static bool HandleKey(Keycode keyCode, KeyEvent? e, bool down)
        {
            if (e == null) return false;
            var pad = Ensure(e.DeviceId);
            if (pad == null) return false;
            GamepadButtons button = Map(keyCode);
            if (button == 0) return false;
            if (down && e.RepeatCount > 0) return true;
            pad.Input.Key(button, down); Publish(pad);
            return true;
        }
        public static bool HandleMotion(MotionEvent? e)
        {
            if (e == null || !IsGamepad(e.Source) || e.Action != MotionEventActions.Move) return false;
            var pad = Ensure(e.DeviceId);
            if (pad == null) return false;
            var profile = pad.Profile;
            var state = new GamepadState
            {
                LeftX = e.GetAxisValue(Axis.X), LeftY = -e.GetAxisValue(Axis.Y),
                RightX = AndroidGamepadProfile.Read(e, profile.RightX),
                RightY = -AndroidGamepadProfile.Read(e, profile.RightY),
                LeftTrigger = AndroidGamepadProfile.Read(e, profile.LeftTrigger),
                RightTrigger = AndroidGamepadProfile.Read(e, profile.RightTrigger)
            };
            float x = e.GetAxisValue(Axis.HatX), y = e.GetAxisValue(Axis.HatY);
            if (x < -.5f) state.Buttons |= GamepadButtons.DpadLeft;
            if (x > .5f) state.Buttons |= GamepadButtons.DpadRight;
            if (y < -.5f) state.Buttons |= GamepadButtons.DpadUp;
            if (y > .5f) state.Buttons |= GamepadButtons.DpadDown;
            pad.Input.Motion = state; Publish(pad);
            return true;
        }

        private static GamepadButtons Map(Keycode code)
        {
            return code switch
            {
                Keycode.ButtonA => GamepadButtons.A,
                Keycode.ButtonB => GamepadButtons.B,
                Keycode.ButtonX => GamepadButtons.X,
                Keycode.ButtonY => GamepadButtons.Y,
                Keycode.ButtonL1 => GamepadButtons.LeftBumper,
                Keycode.ButtonR1 => GamepadButtons.RightBumper,
                // Some pads report the triggers as buttons and never send an
                // axis for them; the axis path above sets the same flags.
                Keycode.ButtonL2 => GamepadButtons.LeftTrigger,
                Keycode.ButtonR2 => GamepadButtons.RightTrigger,
                Keycode.ButtonSelect => GamepadButtons.Back,
                Keycode.ButtonStart => GamepadButtons.Start,
                Keycode.ButtonThumbl => GamepadButtons.LeftThumb,
                Keycode.ButtonThumbr => GamepadButtons.RightThumb,
                Keycode.DpadUp => GamepadButtons.DpadUp,
                Keycode.DpadDown => GamepadButtons.DpadDown,
                Keycode.DpadLeft => GamepadButtons.DpadLeft,
                Keycode.DpadRight => GamepadButtons.DpadRight,
                _ => GamepadButtons.None
            };
        }
    }
}
