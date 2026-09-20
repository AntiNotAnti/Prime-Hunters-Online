using System;
using Android.OS;
using Android.App;
using Android.Views;
using MphRead.Mods.Input;

[assembly: UsesPermission(Android.Manifest.Permission.Vibrate)]

namespace MphRead.Droid
{
    internal sealed class AndroidGamepadHaptics : IGamepadHaptics
    {
        private readonly int _deviceId;
        public AndroidGamepadHaptics(int deviceId) { _deviceId = deviceId; }
#pragma warning disable CA1422
        private Vibrator? Vibrator => InputDevice.GetDevice(_deviceId)?.Vibrator;
#pragma warning restore CA1422
        public bool Available => Vibrator?.HasVibrator == true;
        public void Rumble(float lowFrequency, float highFrequency, TimeSpan duration)
        {
            var vibrator = Vibrator;
            if (vibrator?.HasVibrator != true) return;
            long ms = Math.Clamp((long)duration.TotalMilliseconds, 1, 500);
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
            {
                int amplitude = Math.Clamp((int)(Math.Max(lowFrequency, highFrequency) * 255), 1, 255);
                using var effect = VibrationEffect.CreateOneShot(ms, vibrator.HasAmplitudeControl ? amplitude : -1);
                vibrator.Vibrate(effect);
            }
            else
            {
#pragma warning disable CA1422
                vibrator.Vibrate(ms);
#pragma warning restore CA1422
            }
        }
        public void Stop() => Vibrator?.Cancel();
    }
}
