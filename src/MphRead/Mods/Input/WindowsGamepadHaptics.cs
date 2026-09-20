using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace MphRead.Mods.Input
{
    // GLFW does not expose the XInput index. Only attach when the physical pairing is unambiguous.
    internal sealed class WindowsGamepadHaptics : IGamepadHaptics
    {
        [StructLayout(LayoutKind.Sequential)] private struct State
        {
            public uint Packet; public ushort Buttons; public byte LeftTrigger, RightTrigger;
            public short LeftX, LeftY, RightX, RightY;
        }
        [StructLayout(LayoutKind.Sequential)] private struct Vibration { public ushort Low, High; }
        [DllImport("xinput1_4.dll")] private static extern uint XInputGetState(uint index, out State state);
        [DllImport("xinput1_4.dll")] private static extern uint XInputSetState(uint index, ref Vibration vibration);
        private static string? _id;
        private static WindowsGamepadHaptics? _current;
        private readonly uint _index;
        private bool _disposed;
        private readonly object _gate = new();
        private readonly Timer _timer;
        private WindowsGamepadHaptics(uint index)
        {
            _index = index; _timer = new Timer(_ => Stop(), null, Timeout.Infinite, Timeout.Infinite);
        }
        public static void Synchronize(string? uniqueId)
        {
            if (!OperatingSystem.IsWindows()) return;
            uint? index = null;
            try
            {
                for (uint i = 0; i < 4; i++) if (XInputGetState(i, out _) == 0)
                {
                    if (index.HasValue) { uniqueId = null; break; }
                    index = i;
                }
            }
            catch (DllNotFoundException) { uniqueId = null; }
            catch (EntryPointNotFoundException) { uniqueId = null; }
            if (!index.HasValue) uniqueId = null;
            if (_id == uniqueId && (_current == null || _current._index == index)) return;
            if (_id != null) GamepadHaptics.Unregister(_id);
            _current?.Dispose(); _current = null; _id = uniqueId;
            if (_id != null && index.HasValue)
            {
                _current = new WindowsGamepadHaptics(index.Value);
                GamepadHaptics.Register(_id, _current);
            }
        }
        public void Rumble(float lowFrequency, float highFrequency, TimeSpan duration)
        {
            lock (_gate)
            {
                if (_disposed) return;
                var vibration = new Vibration { Low = (ushort)(GamepadAnalog.Finite(lowFrequency, 0, 1) * ushort.MaxValue),
                    High = (ushort)(GamepadAnalog.Finite(highFrequency, 0, 1) * ushort.MaxValue) };
                XInputSetState(_index, ref vibration);
                _timer.Change(Math.Clamp((int)duration.TotalMilliseconds, 1, 500), Timeout.Infinite);
            }
        }
        private void Dispose()
        {
            lock (_gate) { Stop(); _disposed = true; _timer.Dispose(); }
        }
        public void Stop()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _timer.Change(Timeout.Infinite, Timeout.Infinite);
                var vibration = new Vibration(); XInputSetState(_index, ref vibration);
            }
        }
    }
}
