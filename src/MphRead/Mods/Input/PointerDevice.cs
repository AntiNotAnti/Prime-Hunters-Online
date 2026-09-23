using System;
using MphRead.Entities;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace MphRead.Mods.Input
{
    public enum PointerDeviceType { Unknown, Mouse, Pen, Touch }

    /// <summary>Client-area coordinates, independent of the platform API supplying them.</summary>
    public readonly record struct PointerSample(PointerDeviceType Device, uint Id,
        float X, float Y, bool PrimaryDown, bool InContact, bool InRange,
        float Pressure = 0, float TiltX = 0, float TiltY = 0);

    /// <summary>
    /// Owns the desktop pointer gesture before bindings are resolved. Android keeps
    /// its existing per-finger owners and synthetic keyboard/mouse input.
    /// </summary>
    public static class PointerDevice
    {
        public static PointerSample Current { get; private set; }
        public static bool Active { get; private set; }
        public static bool PrimaryDown { get; private set; }
        private static bool _acceptingInput;
        private static float _pendingX;
        private static float _pendingY;
        // Some tablet drivers briefly report pen-up while handing an active
        // contact to a new native pointer id. Render callbacks can run 2-4x
        // faster than gameplay, so "one more render update" is not a stable
        // debounce. A release must survive a 60 Hz simulation boundary before
        // it can re-arm a one-shot DS button.
        private static bool _stylusReleasePending;
        private static bool _stylusReleaseObservedBySimulation;

        // Input surfaces are allowed to close between simulation steps. If the
        // pointer is still held while a menu/settings surface owns it, keep that
        // contact quarantined until a real release instead of turning the UI
        // click into a gameplay press on the first frame back.
        private static bool _blockedContactUntilRelease;
        private static bool _blockedReleasePending;
        private static bool _blockedReleaseObservedBySimulation;

        public static void Update(PointerSample sample, int width, int height,
            bool independentPrimaryDown = false, bool acceptsInput = true)
        {
            bool wasActive = Active && _acceptingInput;
            Active = PointerInput.StylusMode && !OperatingSystem.IsAndroid();
            _acceptingInput = acceptsInput;
            PointerSample previous = Current;
            Current = sample;
            StylusZone.AspectCorrection = width / (float)Math.Max(height, 1);
            bool identityChanged = sample.Device != previous.Device || sample.Id != previous.Id;
            bool hadPendingRelease = _stylusReleasePending;
            bool rawContact = Active && acceptsInput && sample.InContact;
            bool effectiveContact = rawContact;
            if (!Active)
            {
                _stylusReleasePending = false;
                _stylusReleaseObservedBySimulation = false;
                _blockedContactUntilRelease = false;
                _blockedReleasePending = false;
                _blockedReleaseObservedBySimulation = false;
            }
            else if (!acceptsInput)
            {
                _stylusReleasePending = false;
                _stylusReleaseObservedBySimulation = false;
                effectiveContact = false;
                if (sample.InContact)
                {
                    _blockedContactUntilRelease = true;
                    _blockedReleasePending = false;
                    _blockedReleaseObservedBySimulation = false;
                }
                else if (_blockedContactUntilRelease)
                {
                    AdvanceBlockedRelease();
                }
            }
            else if (_blockedContactUntilRelease)
            {
                // A button/tip that was already down while a menu, settings page,
                // focus transition or dialog owned input must never become a new
                // gameplay press. Its release is debounced on the simulation clock
                // too, so a one-picture driver handoff cannot escape quarantine.
                if (sample.InContact)
                {
                    _blockedReleasePending = false;
                    _blockedReleaseObservedBySimulation = false;
                }
                else
                {
                    AdvanceBlockedRelease();
                }
                _stylusReleasePending = false;
                _stylusReleaseObservedBySimulation = false;
                effectiveContact = false;
            }
            else if (rawContact)
            {
                // A new down before the pending release crossed a simulation
                // boundary is the same physical gesture.
                _stylusReleasePending = false;
                _stylusReleaseObservedBySimulation = false;
            }
            else if (wasActive && previous.Device == PointerDeviceType.Pen
                && previous.InContact && sample.Device == PointerDeviceType.Pen)
            {
                _stylusReleasePending = true;
                _stylusReleaseObservedBySimulation = false;
                effectiveContact = true;
            }
            else if (hadPendingRelease)
            {
                if (_stylusReleaseObservedBySimulation)
                {
                    // The release survived gameplay's own clock. It is now safe
                    // to end the gesture and let the next contact create one edge.
                    _stylusReleasePending = false;
                    _stylusReleaseObservedBySimulation = false;
                    effectiveContact = false;
                }
                else
                {
                    // However many pictures happen before the next simulation
                    // step, this remains the same held gesture.
                    effectiveContact = true;
                }
            }

            // Some pen/tablet drivers rotate WM_POINTER identities while the tip
            // remains physically down. Treat that as one continuous gesture:
            // synthesising an up/down edge here re-arms one-shot DS buttons and
            // turns a single WPN/affinity tap into a weapon-cycling machine gun.
            // A one-frame pen-up during the handoff is folded into the same gesture
            // by the release debounce above. Movement is still discarded on every
            // identity-change frame, so the handoff cannot inject a camera teleport.
            bool contactContinues = identityChanged && wasActive && Active && acceptsInput
                && effectiveContact && (previous.InContact || hadPendingRelease);
            if (identityChanged && !contactContinues)
            {
                StylusZone.Update(0, 0, false);
            }
            StylusZone.Update(sample.X / Math.Max(width, 1), sample.Y / Math.Max(height, 1),
                effectiveContact);
            bool gameplayPrimary = effectiveContact && sample.PrimaryDown;
            PrimaryDown = acceptsInput && ResolvePrimary(gameplayPrimary, independentPrimaryDown,
                StylusZone.CapturingPrimaryButton || StylusZone.Placing);
            if (!Active || !acceptsInput || !wasActive || identityChanged)
            {
                _pendingX = _pendingY = 0;
                return;
            }
            if (StylusZone.Placing || (StylusZone.Enabled && !StylusZone.Aiming))
            {
                _pendingX = _pendingY = 0;
                return;
            }
            // Native contact transitions are known even without a DS zone.
            if (sample.Device == PointerDeviceType.Pen && sample.InContact != previous.InContact)
            {
                _pendingX = _pendingY = 0;
                return;
            }
            (float x, float y) = PointerInput.Filter(sample.X - previous.X, sample.Y - previous.Y);
            _pendingX += x;
            _pendingY += y;
        }

        public static bool ResolvePrimary(bool tipDown, bool independentDown, bool captured)
            => independentDown || (tipDown && !captured);

        /// <summary>
        /// Advance the pointer debounce on gameplay's fixed 60 Hz clock.
        /// Render frequency must never decide when a one-shot stylus action rearms.
        /// </summary>
        internal static void AdvanceSimulationStep()
        {
            if (_stylusReleasePending)
            {
                _stylusReleaseObservedBySimulation = true;
            }
            if (_blockedReleasePending)
            {
                _blockedReleaseObservedBySimulation = true;
            }
        }

        private static void AdvanceBlockedRelease()
        {
            if (!_blockedReleasePending)
            {
                _blockedReleasePending = true;
                _blockedReleaseObservedBySimulation = false;
            }
            else if (_blockedReleaseObservedBySimulation)
            {
                _blockedContactUntilRelease = false;
                _blockedReleasePending = false;
                _blockedReleaseObservedBySimulation = false;
            }
        }

        /// <summary>Consume once per simulation step, including when multiple steps share a picture.</summary>
        public static (float X, float Y) TakeDelta()
        {
            var result = (_pendingX, _pendingY);
            _pendingX = _pendingY = 0;
            return result;
        }

        public static void Reset()
        {
            Active = false;
            _acceptingInput = false;
            Current = default;
            PrimaryDown = false;
            _pendingX = _pendingY = 0;
            _stylusReleasePending = false;
            _stylusReleaseObservedBySimulation = false;
            _blockedContactUntilRelease = false;
            _blockedReleasePending = false;
            _blockedReleaseObservedBySimulation = false;
            StylusZone.Reset();
        }
    }

    /// <summary>Binding edges are calculated from the filtered source, before any stylus or pad actions.</summary>
    public sealed class PointerBindings
    {
        public bool Down { get; private set; }
        public bool PreviousDown { get; private set; }

        public void Update(bool rawDown, bool captured, bool independentDown = false)
        {
            PreviousDown = Down;
            Down = PointerDevice.ResolvePrimary(rawDown, independentDown, captured);
        }

        /// <summary>
        /// Move the primary-button baseline without manufacturing an edge.
        /// Used while a UI surface owns input so a held UI click cannot leak
        /// into gameplay when that surface closes.
        /// </summary>
        public void Synchronize(bool rawDown, bool captured, bool independentDown = false)
        {
            Down = PreviousDown = PointerDevice.ResolvePrimary(rawDown, independentDown, captured);
        }

        public bool Resolve(Keybind control)
        {
            if (control.Type != ButtonType.Mouse || control.MouseButton != MouseButton.Left)
            {
                return false;
            }
            control.IsDown = Down;
            control.IsPressed = Down && !PreviousDown;
            control.IsReleased = !Down && PreviousDown;
            return true;
        }
    }
}
