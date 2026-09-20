using System.Numerics;

namespace MphRead.Mods.Input
{
    public readonly record struct GamepadAuxState(Vector3 Gyro, Vector3 Accelerometer,
        Vector2 Touch0, Vector2 Touch1, bool TouchpadPressed);

    public readonly record struct GamepadDeviceSnapshot
    {
        public GamepadDeviceSnapshot() { }
        public string DeviceId { get; init; } = "";
        public string Name { get; init; } = "";
        public string ProfileKey { get; init; } = "";
        public GamepadFamily Family { get; init; }
        public GamepadCapabilities Capabilities { get; init; }
        public bool IsMapped { get; init; }
        public string Mapping { get; init; } = "";
        public GamepadState State { get; init; }
        public GamepadState RawState { get; init; }
        public GamepadAuxState AuxState { get; init; }
        public long Revision { get; init; }
    }
}
