using System;
using System.Collections.Generic;

namespace MphRead.Mods.Input
{
    public interface IGamepadHaptics
    {
        void Rumble(float lowFrequency, float highFrequency, TimeSpan duration);
        void Stop();
    }
    public enum GamepadFeedback { Fire, ChargedShot, Damage, Explosion, Boost, Landing, Death }
    public static class GamepadHaptics
    {
        private static readonly Dictionary<string, IGamepadHaptics> Backends = new();
        private static readonly object Gate = new();
        private static readonly HapticScheduler Scheduler = new();
        static GamepadHaptics() { GamepadManager.ActiveChanged += Stop; AppDomain.CurrentDomain.ProcessExit += (_, _) => Stop(); }
        public static void Register(string id, IGamepadHaptics backend) { lock (Gate) Backends[id] = backend; }
        public static bool Available(string id) { lock (Gate) return Backends.ContainsKey(id); }
        public static void Unregister(string id)
        {
            lock (Gate) if (Backends.Remove(id, out var backend)) backend.Stop();
        }
        public static void Stop() { lock (Gate) { Scheduler.Reset(); foreach (var backend in Backends.Values) backend.Stop(); } }
        public static void Play(GamepadFeedback feedback)
        {
            if (!GamepadContexts.Focused || GamepadContexts.MenuVisible || GamepadContexts.Capturing
                || !GamepadOptions.Vibration || InputSourceTracker.Current != InputSource.Gamepad) return;
            string? id = GamepadManager.Snapshot.DeviceId;
            if (id == null) return;
            long now = Environment.TickCount64;
            var (low, high, ms) = feedback switch
            {
                GamepadFeedback.Fire => (0.08f, 0.18f, 45),
                GamepadFeedback.ChargedShot => (0.22f, 0.30f, 95),
                GamepadFeedback.Damage => (0.32f, 0.15f, 100),
                GamepadFeedback.Explosion => (0.40f, 0.20f, 140),
                GamepadFeedback.Boost => (0.22f, 0.10f, 90),
                GamepadFeedback.Landing => (0.16f, 0.06f, 65),
                _ => (0.38f, 0.18f, 220)
            };
            lock (Gate) if (Backends.TryGetValue(id, out var backend))
            {
                if (!Scheduler.Accept(feedback, now, ms)) return;
                float scale = GamepadAnalog.Finite(GamepadOptions.VibrationStrength, 0, 1);
                if (scale <= 0) { backend.Stop(); return; }
                backend.Rumble(low * scale, high * scale, TimeSpan.FromMilliseconds(ms));
            }
        }
    }
}
