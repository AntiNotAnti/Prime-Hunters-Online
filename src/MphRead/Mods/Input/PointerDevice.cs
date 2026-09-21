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
        // contact to a new native pointer id. Do not let that one sample re-arm
        // one-shot DS buttons; a release must survive one complete update.
        private static bool _stylusReleasePending;

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
            if (!Active || !acceptsInput)
            {
                _stylusReleasePending = false;
            }
            else if (rawContact)
            {
                // A new down immediately after a one-frame up is the same physical
                // gesture. This is the common WM_POINTER handoff shape on tablets.
                _stylusReleasePending = false;
            }
            else if (wasActive && previous.Device == PointerDeviceType.Pen
                && previous.InContact && sample.Device == PointerDeviceType.Pen)
            {
                _stylusReleasePending = true;
                effectiveContact = true;
            }
            else if (hadPendingRelease)
            {
                // The up survived another update, so it is a real release and
                // may re-arm the next one-shot DS button touch.
                _stylusReleasePending = false;
                effectiveContact = false;
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
            PrimaryDown = acceptsInput && ResolvePrimary(sample.PrimaryDown, independentPrimaryDown,
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
