using Android.Views;

namespace MphRead.Droid
{
    internal sealed class AndroidGamepadProfile
    {
        public bool HasLeftStick { get; }
        public Axis? RightX { get; }
        public Axis? RightY { get; }
        public Axis? LeftTrigger { get; }
        public Axis? RightTrigger { get; }
        public AndroidGamepadProfile(InputDevice device)
        {
            Axis? Pick(Axis first, Axis second) => device.GetMotionRange(first) != null ? first
                : device.GetMotionRange(second) != null ? second : null;
            HasLeftStick = device.GetMotionRange(Axis.X) != null && device.GetMotionRange(Axis.Y) != null;
            RightX = Pick(Axis.Z, Axis.Rx); RightY = Pick(Axis.Rz, Axis.Ry);
            LeftTrigger = Pick(Axis.Ltrigger, Axis.Brake); RightTrigger = Pick(Axis.Rtrigger, Axis.Gas);
        }
        public static float Read(MotionEvent e, Axis? axis) => axis.HasValue ? e.GetAxisValue(axis.Value) : 0;
    }
}
