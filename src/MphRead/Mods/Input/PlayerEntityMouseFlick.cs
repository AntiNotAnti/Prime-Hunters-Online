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
        private void ModCheckMouseFlick(bool dedicatedBoostDown)
        {
            if (!IsMainPlayer || IsBot)
            {
                return;
            }
            bool movementBoostEnabled = Mods.Input.PointerDevice.Active
                ? Mods.InputSettings.StylusMovementBoost
                : Mods.InputSettings.MouseMovementBoost;
            if (!movementBoostEnabled || !Controls.MouseAim || dedicatedBoostDown
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

        internal void ModSetAltSwipeDrive(bool engaged, float x, float y)
        {
            if (!global::System.Single.IsFinite(x) || !global::System.Single.IsFinite(y))
            {
                engaged = false;
                x = y = 0;
            }
            bool wasEngaged = Input.AltSwipeEngaged;
            if (!engaged && wasEngaged)
            {
                Input.AltSwipeStopRequested = true;
            }
            Input.AltSwipeEngaged = engaged;
            Input.AltSwipeX = engaged ? x : 0;
            Input.AltSwipeY = engaged ? y : 0;
        }

        private void ModResetAltSwipeDrive()
        {
            Input.ResetAltSwipe();
        }

        private void ModMarkAltSwipeInput()
        {
            if (Input.AltSwipeEngaged
                && (Input.AltSwipeX != 0 || Input.AltSwipeY != 0))
            {
                Input.HasInput = true;
            }
        }

        /// <summary>
        /// Desktop Stylus Mode uses an anchored virtual stick for rolling alt
        /// forms. Ordinary mouse movement remains aiming and Trace/Sylux/Weavel
        /// keep their transformed pointer aim.
        /// </summary>
        private void ModApplyStylusAltMove()
        {
            // Android queues its anchored multi-touch sample in GameView before
            // the shared hardware-input pass.
            if (global::System.OperatingSystem.IsAndroid())
            {
                return;
            }

            bool valid = IsMainPlayer && !IsBot && IsAltForm
                && !IsMorphing && !IsUnmorphing
                && Mods.Input.AltFormGesture.UsesRollMovement(Hunter)
                && Mods.Input.PointerDevice.Active
                && Mods.Input.PointerDevice.Current.Device != Mods.Input.PointerDeviceType.Mouse
                && Mods.Input.PointerDevice.Current.InContact
                && (!Mods.Input.StylusZone.Enabled || Mods.Input.StylusZone.Aiming)
                && !Flags1.TestFlag(PlayerFlags1.NoAimInput)
                && !Flags1.TestFlag(PlayerFlags1.WeaponMenuOpen)
                && !Mods.SpectatorMode.IsSpectating
                && !_scene.FrameAdvance && !_scene.FrameAdvanceLastFrame
                && CameraSequence.Current?.Flags.TestFlag(CamSeqFlags.BlockInput) != true;
            if (!valid)
            {
                Input.StylusAltTracking = false;
                ModSetAltSwipeDrive(false, 0, 0);
                return;
            }

            Mods.Input.PointerSample sample = Mods.Input.PointerDevice.Current;
            if (!Input.StylusAltTracking)
            {
                Input.StylusAltTracking = true;
                Input.StylusAltOriginX = sample.X;
                Input.StylusAltOriginY = sample.Y;
                ModSetAltSwipeDrive(true, 0, 0);
                return;
            }

            (float X, float Y) drive = Mods.Input.AltFormGesture.Drive(
                sample.X - Input.StylusAltOriginX,
                sample.Y - Input.StylusAltOriginY,
                deadZone: 6f, fullScale: 96f,
                sensitivity: Mods.InputSettings.AltSwipeSensitivity);
            ModSetAltSwipeDrive(true, drive.X, drive.Y);
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
            // Spire needs its edge before network press history is captured, so
            // desktop mouse/pen detection runs here only for Spire. Samus keeps
            // its existing simulation-time detector where controller-held boost
            // is already visible and can suppress an accidental release.
            if (action == Mods.Input.AltFlickAction.SpireAttack
                && !global::System.OperatingSystem.IsAndroid())
            {
                ModCheckMouseFlick(dedicatedBoostDown: false);
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
