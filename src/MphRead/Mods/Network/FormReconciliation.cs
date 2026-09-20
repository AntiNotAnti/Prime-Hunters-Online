using System;

namespace MphRead.Mods.Network
{
    internal enum FormCorrection { None, Start, Force }

    /// <summary>Per-slot timing for reconciling a puppet's form with its source.</summary>
    internal struct FormReconciliation
    {
        private const uint MismatchGrace = 8;
        private const uint FailedSwitchGrace = 12;
        // The model's morph/unmorph animation length is asset dependent.
        // This is an emergency ceiling, not the normal mismatch grace.
        private const uint MaximumTransition = 90;
        private uint _mismatchSince;
        private uint _attemptSince;
        private uint _transitionSince;
        private uint _transitionLastSeen;
        private bool _mismatching;
        private bool _attempted;
        private bool _transitionSeen;
        private bool _transitionActive;
        private bool _transitionTarget;

        public void Reset() => this = default;

        public FormCorrection Step(uint frame, bool desiredAlt, bool actualAlt,
            bool morphing, bool unmorphing, int pingMilliseconds)
        {
            bool transitioning = morphing || unmorphing;
            if (transitioning)
            {
                bool target = morphing;
                if (!_transitionActive || _transitionTarget != target)
                {
                    _transitionSince = frame;
                }
                _transitionSeen = true;
                _transitionTarget = target;
                _transitionLastSeen = frame;
            }
            // Keep the last transition for latency grace, but do not charge a
            // later same-direction animation for time spent settled in between.
            _transitionActive = transitioning;

            if (actualAlt == desiredAlt && !transitioning)
            {
                _mismatching = false;
                _attempted = false;
                return FormCorrection.None;
            }

            // A morph only changes IsAltForm at animation end; an unmorph changes
            // it immediately. In both cases older snapshots/intents can still
            // describe the form from before the relayed press.
            if (transitioning)
            {
                if (frame - _transitionSince >= MaximumTransition)
                {
                    Reset();
                    return FormCorrection.Force;
                }
                return FormCorrection.None;
            }
            uint latencyGrace = (uint)Math.Clamp(pingMilliseconds * 60 / 1000 + 8, 8, 32);
            if (_transitionSeen && !_attempted && _transitionTarget == actualAlt
                && frame - _transitionLastSeen <= latencyGrace)
            {
                return FormCorrection.None;
            }

            if (_attempted)
            {
                if (frame - _attemptSince < FailedSwitchGrace)
                {
                    return FormCorrection.None;
                }
                Reset();
                return FormCorrection.Force;
            }
            if (!_mismatching)
            {
                _mismatchSince = frame;
                _mismatching = true;
            }
            if (frame - _mismatchSince < MismatchGrace)
            {
                return FormCorrection.None;
            }
            _attempted = true;
            _attemptSince = frame;
            return FormCorrection.Start;
        }
    }
}
