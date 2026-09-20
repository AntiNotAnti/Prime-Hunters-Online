using System;
using System.Collections.Generic;

namespace MphRead.Mods.Input
{
    public enum GamepadFamily { Unknown, Xbox, PlayStation, Nintendo, Generic }
    [Flags]
    public enum GamepadCapabilities { None = 0, Rumble = 1, Gyro = 2, Touchpad = 4, AnalogTriggers = 8, AnalogLeftStick = 16, AnalogRightStick = 32 }

    internal sealed class GamepadDevice
    {
        public string DeviceId { get; init; } = "";
        public string Name { get; internal set; } = "";
        public string ProfileKey { get; init; } = "";
        public GamepadFamily Family { get; internal set; }
        public GamepadCapabilities Capabilities { get; internal set; }
        public bool IsMapped { get; internal set; }
        public string Mapping { get; internal set; } = "";
        public GamepadState State { get; internal set; }
        public GamepadState RawState { get; internal set; }
        internal bool LeftTriggerHeld, RightTriggerHeld;
        internal GamepadRuntimeConfig Runtime = new();
        internal long Revision;
        internal GamepadDeviceSnapshot Snapshot => new() { DeviceId = DeviceId, Name = Name, ProfileKey = ProfileKey,
            Family = Family, Capabilities = Capabilities, IsMapped = IsMapped, Mapping = Mapping,
            State = State, RawState = RawState, Revision = Revision };
    }

    public readonly record struct GamepadSnapshot(string? DeviceId, GamepadState State, long Revision)
    {
        internal GamepadRuntimeConfig? Runtime { get; init; }
    }

    public static class GamepadManager
    {
        private static readonly object Gate = new();
        private static readonly List<GamepadDevice> Known = new();
        private static GamepadDevice? _active;
        private static string? _selected;
        private static bool _used;
        private static long _revision;
        private static GamepadSnapshot _snapshot = new(null, default, 0);
        public static GamepadSnapshot Snapshot { get { lock (Gate) return _snapshot; } }
        public static GamepadState ActiveState => Snapshot.State;
        public static GamepadDeviceSnapshot? ActiveDevice { get { lock (Gate) return _active?.Snapshot; } }
        public static string? SelectedDeviceId { get { lock (Gate) return _selected; } }
        public static string? LastInputDevice { get; private set; }
        public static IReadOnlyList<GamepadDeviceSnapshot> Devices { get { lock (Gate) return Known.ConvertAll(d => d.Snapshot).ToArray(); } }
        public static event Action<GamepadDeviceSnapshot>? DeviceAdded;
        public static event Action<GamepadDeviceSnapshot>? DeviceRemoved;
        public static event Action? ActiveChanged;

        private static GamepadDevice? Find(string id)
        {
            foreach (var device in Known) if (device.DeviceId == id) return device;
            return null;
        }

        private static void Activate(GamepadDevice? device)
        {
            if (_active == device) return;
            _active = device;
            _revision++;
            Publish();

        }
        private static void Publish()
        {
            GamepadRuntimeConfig.Current = _active?.Runtime ?? GamepadRuntimeConfig.Fallback;
            GamepadProfiles.NoteActive(GamepadRuntimeConfig.Current.ProfileName);
            _snapshot = new(_active?.DeviceId, _active?.State ?? default, _revision) { Runtime = _active?.Runtime ?? GamepadRuntimeConfig.Fallback };
        }

        public static void UpdateDevice(string id, GamepadState state, bool mapped,
            GamepadFamily family = GamepadFamily.Unknown,
            GamepadCapabilities capabilities = GamepadCapabilities.None, string? mapping = null)
        {
            GamepadDeviceSnapshot? notification = null;
            bool activeChanged;
            lock (Gate)
            {
                long oldRevision = _revision;
                var device = Find(id);
                bool added = device == null;
                if (device == null)
                {
                    device = new GamepadDevice { DeviceId = id, ProfileKey = GamepadProfiles.DeviceKey(id) };
                    device.Runtime = GamepadProfiles.Resolve(device.ProfileKey);
                    Known.Add(device);
                }
                var previous = device.State;
                var options = device.Runtime.Options;
                state.LeftX = GamepadAnalog.Finite(state.LeftX);
                state.LeftY = GamepadAnalog.Finite(state.LeftY);
                state.RightX = GamepadAnalog.Finite(state.RightX);
                state.RightY = GamepadAnalog.Finite(state.RightY);
                state.LeftTrigger = GamepadAnalog.Finite(state.LeftTrigger, 0, 1);
                state.RightTrigger = GamepadAnalog.Finite(state.RightTrigger, 0, 1);
                state.Connected = true;
                device.RawState = state;
                (state.LeftX, state.LeftY) = options.LeftCalibration.Normalize(state.LeftX, state.LeftY);
                (state.RightX, state.RightY) = options.RightCalibration.Normalize(state.RightX, state.RightY);
                state.LeftTrigger = GamepadCalibration.Trigger(state.LeftTrigger, options.LeftTriggerMin, options.LeftTriggerMax);
                state.RightTrigger = GamepadCalibration.Trigger(state.RightTrigger, options.RightTriggerMin, options.RightTriggerMax);
                device.LeftTriggerHeld = GamepadAnalog.Trigger(state.LeftTrigger, device.LeftTriggerHeld, options.TriggerThreshold);
                device.RightTriggerHeld = GamepadAnalog.Trigger(state.RightTrigger, device.RightTriggerHeld, options.TriggerThreshold);
                if (device.LeftTriggerHeld) state.Buttons |= GamepadButtons.LeftTrigger;
                if (device.RightTriggerHeld) state.Buttons |= GamepadButtons.RightTrigger;
                state.Connected = true;
                device.State = state;
                string name = state.Name ?? "gamepad";
                if (added || device.Name != name || family != GamepadFamily.Unknown)
                    device.Family = family == GamepadFamily.Unknown ? GamepadGlyphs.Detect(name) : family;
                device.Name = name;
                device.IsMapped = mapped;
                device.Mapping = mapping ?? (mapped ? "Platform mapping" : "Unmapped fallback");
                device.Capabilities = capabilities;
                bool activity = (state.Buttons & ~previous.Buttons) != 0
                    || StickActivity(state.LeftX, state.LeftY, previous.LeftX, previous.LeftY, options.ActivityThreshold)
                    || StickActivity(state.RightX, state.RightY, previous.RightX, previous.RightY, options.ActivityThreshold);
                if (_selected == id || (_selected == null && (_active == null
                    || (!_used && mapped && !_active.IsMapped)))) Activate(device);
                if (activity)
                {
                    LastInputDevice = id;

                    if (_selected == null || _selected == id) { _used = true; Activate(device); InputSourceTracker.Note(InputSource.Gamepad); }
                }
                Publish();
                device.Revision++;
                if (added) notification = device.Snapshot;
                activeChanged = oldRevision != _revision;
            }
            if (activeChanged) ActiveChanged?.Invoke();
            if (notification.HasValue) DeviceAdded?.Invoke(notification.Value);
        }

        internal static void ReplaceRuntime(GamepadRuntimeConfig runtime)
        {
            lock (Gate)
            {
                if (_active != null) _active.Runtime = runtime;
                else GamepadRuntimeConfig.Fallback = runtime;
                _revision++; Publish();
            }
        }
        internal static void RefreshProfiles()
        {
            lock (Gate)
            {
                foreach (var device in Known) device.Runtime = GamepadProfiles.Resolve(device.ProfileKey);
                _revision++; Publish();
            }
        }

        private static bool StickActivity(float x, float y, float oldX, float oldY, float threshold)
            => x * x + y * y > threshold * threshold
                && (Math.Abs(x - oldX) > 0.08f || Math.Abs(y - oldY) > 0.08f);

        public static void SelectDevice(string? id)
        {
            bool changed;
            lock (Gate)
            {
                long previous = _revision;
                _selected = string.IsNullOrEmpty(id) ? null : id;
                _used = false;
                if (_selected != null) Activate(Find(_selected));
                else if (_active == null) Activate(Known.Find(d => d.IsMapped) ?? Known.Find(_ => true));
                changed = previous != _revision;
            }
            if (changed) ActiveChanged?.Invoke();
        }
        public static void ClearDevice(string id)
        {
            lock (Gate)
            {
                var device = Find(id);
                if (device == null) return;
                device.State = new GamepadState { Connected = true, Name = device.Name };
                device.RawState = device.State;
                device.LeftTriggerHeld = device.RightTriggerHeld = false;
                if (_active == device) { _revision++; Publish(); }
            }
        }
        public static void RemoveDevice(string id)
        {
            GamepadDeviceSnapshot removed;
            bool changed;
            lock (Gate)
            {
                var device = Find(id);
                if (device == null) return;
                removed = device.Snapshot;
                changed = _active == device;
                device.State = default;
                Known.Remove(device);
                if (_selected == id) _selected = null;
                if (_active == device) { _used = false; Activate(null); }

            }
            if (changed) ActiveChanged?.Invoke();
            DeviceRemoved?.Invoke(removed);
        }
        public static void ClearAll()
        {
            lock (Gate) foreach (var device in Known) ClearDevice(device.DeviceId);
        }
    }
}
