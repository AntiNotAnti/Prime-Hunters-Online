using System;
namespace MphRead.Mods.Input
{
    internal sealed class HapticScheduler
    {
        private readonly long[] _last = new long[7];
        private long _until;
        private int _priority;
        public void Reset() { Array.Fill(_last, long.MinValue / 2); _until = 0; _priority = 0; }
        public HapticScheduler() => Reset();
        public bool Accept(GamepadFeedback feedback, long now, int duration)
        {
            int priority = feedback is GamepadFeedback.Death or GamepadFeedback.Explosion or GamepadFeedback.Damage ? 3
                : feedback == GamepadFeedback.Fire ? 1 : 2;
            int cooldown = feedback == GamepadFeedback.Fire ? 65 : 40;
            if (now - _last[(int)feedback] < cooldown || (now < _until && priority < _priority)) return false;
            _last[(int)feedback] = now; _until = now + duration; _priority = priority; return true;
        }
    }
}
