using MphRead.Formats;

namespace MphRead.Entities
{
    public partial class PlayerEntity
    {
        private void ModClearAltFlick()
        {
            SwipeBoostRequested = false;
            SwipeBoostX = 0;
            SwipeBoostY = 0;
        }

        /// <summary>
        /// Read this frame's desktop mouse/pen movement as a possible alt-form
        /// flick. Android performs its own per-finger recognition and queues the
        /// same legacy request fields before this input pass.
        /// </summary>
        private void ModCheckMouseFlick(bool buttonBoostDown)
        {
            if (!IsMainPlayer || IsBot)
            {
                return;
            }
            bool movementBoostEnabled = Mods.Input.PointerDevice.Active
                ? Mods.InputSettings.StylusMovementBoost
                : Mods.InputSettings.MouseMovementBoost;
            if (!movementBoostEnabled || !Controls.MouseAim || buttonBoostDown
                || Flags1.TestFlag(PlayerFlags1.NoAimInput)
                || Flags1.TestFlag(PlayerFlags1.WeaponMenuOpen)
                || Mods.SpectatorMode.IsSpectating
                || _scene.FrameAdvance || _scene.FrameAdvanceLastFrame
                || CameraSequence.Current?.Flags.TestFlag(CamSeqFlags.BlockInput) == true)
            {
                Mods.Input.MouseFlick.Reset();
                return;
            }
            if (Mods.Input.MouseFlick.Check(Input.MouseDeltaX, Input.MouseDeltaY,
                _scene.FrameCount, out float dirX, out float dirY))
            {
                SwipeBoostRequested = true;
                SwipeBoostX = dirX;
                SwipeBoostY = dirY;
            }
        }

        /// <summary>
        /// Feed an active desktop pen drag into the same Roll binds used by
        /// keyboard/controller/touch. Ordinary mouse movement remains aiming and
        /// transformed aim-capable hunters keep their existing pointer behavior.
        /// </summary>
        private void ModApplyStylusAltMove()
        {
            if (!IsMainPlayer || IsBot || !IsAltForm || IsMorphing || IsUnmorphing
                || !Mods.Input.AltFormGesture.UsesRollMovement(Hunter)
                || !Mods.Input.PointerDevice.Active
                || Mods.Input.PointerDevice.Current.Device == Mods.Input.PointerDeviceType.Mouse
                || !Mods.Input.PointerDevice.Current.InContact
                || Flags1.TestFlag(PlayerFlags1.NoAimInput)
                || Flags1.TestFlag(PlayerFlags1.WeaponMenuOpen)
                || Mods.SpectatorMode.IsSpectating
                || _scene.FrameAdvance || _scene.FrameAdvanceLastFrame
                || CameraSequence.Current?.Flags.TestFlag(CamSeqFlags.BlockInput) == true)
            {
                return;
            }

            // An explicit movement bind already has ownership for this frame.
            if (Controls.RollUp.IsDown || Controls.RollDown.IsDown
                || Controls.RolltLeft.IsDown || Controls.RollRight.IsDown)
            {
                return;
            }

            Mods.Input.AltMoveDirection direction = Mods.Input.AltFormGesture.Direction(
                Input.MouseDeltaX, Input.MouseDeltaY, deadZone: 2f);
            if (direction == Mods.Input.AltMoveDirection.None)
            {
                return;
            }

            void Apply(Keybind bind, Mods.Input.AltMoveDirection flag)
            {
                if ((direction & flag) == 0)
                {
                    return;
                }
                bind.IsPressed |= !bind.IsDown;
                bind.IsDown = true;
                Input.HasInput = true;
            }

            Apply(Controls.RollUp, Mods.Input.AltMoveDirection.Up);
            Apply(Controls.RollDown, Mods.Input.AltMoveDirection.Down);
            Apply(Controls.RolltLeft, Mods.Input.AltMoveDirection.Left);
            Apply(Controls.RollRight, Mods.Input.AltMoveDirection.Right);
        }

        /// <summary>
        /// Resolve the one-shot alt-form gesture before network press history is
        /// captured. Samus leaves the event for the boost simulation; Spire
        /// emits the existing AltAttack edge. Unsupported/invalid states clear it.
        /// </summary>
        private void ModPrepareAltFlick()
        {
            Mods.Input.AltFlickAction action = Mods.Input.AltFormGesture.FlickAction(Hunter);
            bool acceptsFlick = IsAltForm && !IsMorphing && !IsUnmorphing
                && _health > 0 && _frozenTimer == 0
                && action != Mods.Input.AltFlickAction.None;
            if (!acceptsFlick)
            {
                ModClearAltFlick();
                Mods.Input.MouseFlick.Reset();
                return;
            }

            // Android already recognized the swipe against real touch timing.
            // Desktop mouse/pen uses the shared delta recognizer here.
            if (!global::System.OperatingSystem.IsAndroid())
            {
                bool suppressForCharge = action == Mods.Input.AltFlickAction.SamusBoost
                    && (Controls.Boost.IsDown || Controls.Zoom.IsDown);
                ModCheckMouseFlick(suppressForCharge);
            }

            if (action == Mods.Input.AltFlickAction.SpireAttack && SwipeBoostRequested)
            {
                Controls.AltAttack.IsPressed = true;
                Input.HasInput = true;
                ModClearAltFlick();
            }
        }
    }
}
