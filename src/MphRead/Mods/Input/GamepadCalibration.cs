using System;
using System.Collections.Generic;

namespace MphRead.Mods.Input
{
    // Bounded setup-only samples. No calibration work occurs in the gameplay hot path.
    public sealed class GamepadCalibration
    {
        private readonly List<GamepadState> _rest = new(), _range = new();
        private bool _dirty = true, _valid;
        private StickCalibration _left, _right;
        private float _leftDead, _rightDead, _ltMin, _rtMin, _ltMax, _rtMax;
        public void Sample(GamepadState state, bool resting)
        {
            var samples = resting ? _rest : _range;
            if (samples.Count < 2048) { samples.Add(state); _dirty = true; }
        }
        private static float Percentile(List<GamepadState> samples, Func<GamepadState, float> value, float quantile)
        {
            float[] values = new float[samples.Count];
            for (int i = 0; i < values.Length; i++) values[i] = value(samples[i]);
            Array.Sort(values);
            return values[(int)Math.Clamp(MathF.Floor((values.Length - 1) * quantile), 0, values.Length - 1)];
        }
        private bool Measure(bool left, out StickCalibration calibration, out float dead)
        {
            float X(GamepadState s) => left ? s.LeftX : s.RightX;
            float Y(GamepadState s) => left ? s.LeftY : s.RightY;
            float cx = Percentile(_rest, X, .5f), cy = Percentile(_rest, Y, .5f);
            float radius = Percentile(_rest, s => MathF.Sqrt(MathF.Pow(X(s) - cx, 2) + MathF.Pow(Y(s) - cy, 2)), .99f);
            calibration = new(cx, cy, Percentile(_range, X, .02f), Percentile(_range, X, .98f),
                Percentile(_range, Y, .02f), Percentile(_range, Y, .98f));
            dead = Math.Clamp(radius + .04f, .04f, .3f);
            return float.IsFinite(radius) && radius < .25f && Math.Abs(cx) < .3f && Math.Abs(cy) < .3f
                && calibration.MinX < -.5f && calibration.MaxX > .5f && calibration.MinY < -.5f && calibration.MaxY > .5f;
        }
        private void Measure()
        {
            if (!_dirty) return;
            _dirty = false; _valid = false;
            if (_rest.Count < 10 || _range.Count < 10) return;
            bool left = Measure(true, out _left, out _leftDead), right = Measure(false, out _right, out _rightDead);
            _ltMin = Percentile(_rest, s => s.LeftTrigger, .99f); _rtMin = Percentile(_rest, s => s.RightTrigger, .99f);
            _ltMax = Percentile(_range, s => s.LeftTrigger, .98f); _rtMax = Percentile(_range, s => s.RightTrigger, .98f);
            _valid = left && right;
        }
        public bool Valid { get { Measure(); return _valid; } }
        public string Summary => !Valid ? "Insufficient range or movement during rest. Rotate both sticks in every direction and retry."
            : $"Center offsets measured. Dead zones: left {_leftDead:0.00}, right {_rightDead:0.00}. Apply to use these measurements. Triggers without enough travel retain their current calibration.";
        public void Apply()
        {
            if (!Valid) throw new InvalidOperationException("Calibration is incomplete.");
            GamepadOptions.LeftCalibration = _left; GamepadOptions.RightCalibration = _right;
            GamepadOptions.LeftInner = _leftDead; GamepadOptions.RightInner = _rightDead;
            GamepadOptions.LeftOuter = GamepadOptions.RightOuter = 0;
            if (_ltMax - _ltMin >= .4f) { GamepadOptions.LeftTriggerMin = _ltMin; GamepadOptions.LeftTriggerMax = _ltMax; }
            if (_rtMax - _rtMin >= .4f) { GamepadOptions.RightTriggerMin = _rtMin; GamepadOptions.RightTriggerMax = _rtMax; }
        }
        public static float Trigger(float value, float min, float max)
            => Math.Clamp((GamepadAnalog.Finite(value, 0, 1) - min) / Math.Max(.1f, max - min), 0, 1);
    }
}
