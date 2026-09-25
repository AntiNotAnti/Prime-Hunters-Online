using System;
using MphRead.Entities;

namespace MphRead.Mods.Input
{
    /// <summary>
    /// A gamepad, on any platform, driving the game.
    ///
    /// The same trick the Android touch controls use, from the other end:
    /// rather than fork <c>ProcessAllInput</c>, this waits until it has run
    /// and then *adds* the pad's contribution to the same <see cref="Keybind"/>
    /// flags the keyboard just filled in. Everything downstream -- firing,
    /// morphing, the weapon wheel, what goes on the wire as an intent -- reads
    /// those flags and cannot tell where they came from, so a pad and a
    /// keyboard work at once and neither had to be special-cased.
    ///
    /// Aim is the one thing that cannot go through a keybind, because a stick
    /// is analogue and a key is not. It goes in where the mouse's does, at
    /// <c>ApplyModAim</c>, in the same units (degrees of turn per frame) and
    /// at the same point in the frame -- an aim applied at a different moment
    /// from the mouse's would feel different for reasons nobody could name.
    ///
    /// Where the state comes from is the platform's business:
    /// <see cref="GamepadDesktop"/> polls GLFW, and the Android head adds up
    /// the events its window receives. Both publish independent devices through <see cref="GamepadManager"/>.
    /// </summary>
    public static class GamepadInput
    {
        /// <summary>The pad as of this frame.</summary>
        public static GamepadState State => GamepadManager.ActiveState;
        private static GamepadState _frame;
        internal static GamepadSnapshot FrameSnapshot { get; private set; }
        private static readonly GamepadEdges Edges = new();
        private static GamepadContext _context;
        private static GamepadButtons _blocked;
        private static long _revision = -1, _contextRevision = -1;
        private static readonly GamepadActions Actions = new();
        private static long _bindingsRevision = -1;
        public static bool WheelHeld => _context == GamepadContext.Gameplay && Actions.WheelOpen;
        public static (float X, float Y) AimStick => GamepadOptions.Southpaw
            ? GamepadAnalog.ApplyRadialDeadZone(_frame.LeftX, _frame.LeftY, GamepadOptions.LeftInner, GamepadOptions.LeftOuter)
            : GamepadAnalog.ApplyRadialDeadZone(_frame.RightX, _frame.RightY, GamepadOptions.RightInner, GamepadOptions.RightOuter);


        /// <summary>Buttons that went down this frame, for the one-shot actions.</summary>
        private static GamepadButtons _pressed;

        /// <summary>
        /// True while a pad is connected.
        ///
        /// There used to be a setting beside this -- "Use a connected gamepad"
        /// -- and it is gone. It could only ever matter to somebody who had a
        /// pad attached and did not want it, which the automatic handover
        /// answers on its own: nothing on screen changes until a pad button is
        /// actually pressed, and a finger takes the game straight back. What
        /// it cost was a row in the settings that read like it might be the
        /// reason the pad was not working, in the one screen somebody with a
        /// pad that is not working will go looking.
        /// </summary>
        public static bool Active => State.Connected;

        /// <summary>
        /// Whether the pad is being *held*, as opposed to merely connected.
        ///
        /// The dead zone rather than zero, and the trigger threshold rather
        /// than zero, because every pad's sticks and triggers drift at rest
        /// and a pad lying on a table would otherwise look like a pad in
        /// somebody's hands. That is the whole question the Android head asks
        /// before it puts the touch controls away.
        /// </summary>
        public static bool InUse
        {
            get
            {
                var state = State;
                if (!state.Connected) return false;
                var left = GamepadAnalog.ApplyRadialDeadZone(state.LeftX, state.LeftY, GamepadOptions.LeftInner, GamepadOptions.LeftOuter);
                var right = GamepadAnalog.ApplyRadialDeadZone(state.RightX, state.RightY, GamepadOptions.RightInner, GamepadOptions.RightOuter);
                return state.Buttons != 0 || left != (0, 0) || right != (0, 0);
            }
        }

        /// <summary>
        /// What the right stick asked for this frame, in degrees of turn --
        /// the same unit <c>UpdateAimX</c> and <c>UpdateAimY</c> take, and the
        /// same unit the mouse arrives in after its own division.
        /// </summary>
        public static float AimDeltaX { get; private set; }
        public static float AimDeltaY { get; private set; }
        private static (float X, float Y)? _appliedCameraAim;
        private static long _appliedAimContext;
        private static GamepadSnapshot? _presentationSample;
        private static long _presentationContext;
        private static float _acceptedAimDeltaX, _acceptedAimDeltaY;

        internal static void RecordCameraAim(float x, float y)
        {
            _appliedCameraAim = (x, y);
            _appliedAimContext = GamepadContexts.Revision;
        }

        // Presentation projects the accepted assisted camera turn. A newer raw
        // stick sample is exposed separately below so the player can transform it
        // through the exact scoped/FOV/inversion path used by simulation.
        internal static bool TryRenderCameraAim(double alpha, out float x, out float y)
        {
            x = y = 0;
            if (_appliedCameraAim is not { } aim || !FrameSnapshot.State.Connected
                || !GamepadContexts.Focused || GamepadContexts.MenuVisible || WheelHeld
                || GamepadContexts.Current != GamepadContext.Gameplay
                || _appliedAimContext != GamepadContexts.Revision || !double.IsFinite(alpha)) return false;
            float fraction = (float)Math.Clamp(alpha, 0, 1);
            x = aim.X * fraction;
            y = aim.Y * fraction;
            return true;
        }

        /// <summary>
        /// Capture the newest hardware axes for render-only preview. This never
        /// advances button edges, actions, aim-assist state or source ownership.
        /// The aim axes from this exact sample are consumed by the next
        /// <see cref="BeginFrame"/> so presentation cannot preview one turn and
        /// gameplay later accept a different one.
        /// </summary>
        public static void CapturePresentationSample()
        {
            if (!GamepadContexts.Focused || GamepadContexts.MenuVisible
                || GamepadContexts.Current != GamepadContext.Gameplay || WheelHeld)
            {
                _presentationSample = null;
                return;
            }
            GamepadSnapshot snapshot = GamepadManager.PresentationSnapshot
                ?? GamepadManager.Snapshot;
            if (!snapshot.State.Connected)
            {
                _presentationSample = null;
                return;
            }
            _presentationSample = snapshot;
            _presentationContext = GamepadContexts.Revision;
        }

        internal static bool TryRenderRawAimDelta(out float x, out float y)
        {
            x = y = 0;
            if (_presentationSample is not { } sample
                || _presentationContext != GamepadContexts.Revision
                || sample.DeviceId != FrameSnapshot.DeviceId
                || sample.Revision != FrameSnapshot.Revision)
            {
                return false;
            }
            (float previewX, float previewY) = PreviewAim(sample);
            x = previewX - _acceptedAimDeltaX;
            y = previewY - _acceptedAimDeltaY;
            return float.IsFinite(x) && float.IsFinite(y);
        }

        /// <summary>
        /// Degrees of turn per frame at full stick deflection, before the
        /// player's sensitivity multiplier. 3.5 is 210 degrees a second, which
        /// is where console shooters have sat since they settled the question.
        /// </summary>
        private const float TurnRate = 3.5f;
        private static System.Numerics.Vector2 _filteredAimStick;
        private const float TurnAccelerationMax = 1.5f;
        private static float _turnRateScale = 1;

        private static void ResetAimRamp()
        {
            _filteredAimStick = default;
            _turnRateScale = 1;
        }

        private static float NextTurnRateScale(float magnitude, float current)
        {
            float outer = Math.Clamp((magnitude - .8f) / .2f, 0, 1);
            float desired = 1 + (TurnAccelerationMax - 1) * outer * outer;
            return current + (desired - current) * (1 - MathF.Exp(-8f / 60));
        }

        private static void UpdateAimRamp(float magnitude)
            => _turnRateScale = NextTurnRateScale(magnitude, _turnRateScale);

        private static (float X, float Y) PreviewAim(GamepadSnapshot snapshot)
        {
            GamepadOptionState options = (snapshot.Runtime ?? GamepadRuntimeConfig.Current).Options;
            GamepadState state = snapshot.State;
            (float x, float y) = options.Southpaw
                ? GamepadAnalog.ApplyRadialDeadZone(state.LeftX, state.LeftY,
                    options.LeftInner, options.LeftOuter)
                : GamepadAnalog.ApplyRadialDeadZone(state.RightX, state.RightY,
                    options.RightInner, options.RightOuter);
            float magnitude = MathF.Sqrt(x * x + y * y);
            float scale = NextTurnRateScale(magnitude, _turnRateScale);
            var filtered = GamepadAnalog.FilterAim(_filteredAimStick,
                new System.Numerics.Vector2(x, y), 1f / 60);
            (x, y) = GamepadAnalog.ApplyRadialResponseCurve(filtered.X, filtered.Y, options.Curve);
            return (-x * TurnRate * scale * options.LookX * (options.InvertX ? -1 : 1),
                y * TurnRate * scale * options.LookY * (options.InvertY ? -1 : 1));
        }

        private static GamepadSnapshot ConsumePresentationAim(GamepadSnapshot snapshot)
        {
            if (_presentationSample is not { } pending)
            {
                return snapshot;
            }
            _presentationSample = null;
            if (_presentationContext != GamepadContexts.Revision
                || pending.DeviceId != snapshot.DeviceId || pending.Revision != snapshot.Revision
                || !pending.State.Connected)
            {
                return snapshot;
            }

            GamepadOptionState options = (pending.Runtime ?? GamepadRuntimeConfig.Current).Options;
            GamepadState state = snapshot.State;
            if (options.Southpaw)
            {
                state.LeftX = pending.State.LeftX;
                state.LeftY = pending.State.LeftY;
            }
            else
            {
                state.RightX = pending.State.RightX;
                state.RightY = pending.State.RightY;
            }
            return snapshot with { State = state };
        }

        /// <summary>
        /// Render-only projection of the currently held aim stick through the
        /// fractional part of a simulation frame. Unlike BeginFrame this does
        /// not update edges, actions or aim-assist state.
        /// </summary>
        internal static (float X, float Y) RenderAim(double alpha)
        {
            if (GamepadContexts.Current != GamepadContext.Gameplay || !GamepadContexts.Focused
                || WheelHeld || alpha <= 0)
            {
                return (0, 0);
            }
            GamepadSnapshot snapshot = _presentationSample is { } pending
                && _presentationContext == GamepadContexts.Revision
                && pending.DeviceId == FrameSnapshot.DeviceId
                && pending.Revision == FrameSnapshot.Revision ? pending : FrameSnapshot;
            if (!snapshot.State.Connected) return (0, 0);
            (float x, float y) = PreviewAim(snapshot);
            float fraction = (float)Math.Clamp(alpha, 0.0, 1.0);
            return (x * fraction, y * fraction);
        }

        /// <summary>
        /// Called once a frame, before the pad is read for anything. Works out
        /// the rising edges and this frame's aim.
        /// </summary>
        public static void BeginFrame()
        {
            _appliedCameraAim = null;
            var snapshot = ConsumePresentationAim(GamepadManager.Snapshot);
            FrameSnapshot = snapshot;
            GamepadRuntimeConfig.Frame = snapshot.Runtime;
            _frame = snapshot.State;
            GamepadButtons gameplayButtons = snapshot.GameplayButtons;
            var context = GamepadContexts.Current;
            _pressed = Edges.Update(snapshot);
            long contextRevision = GamepadContexts.Revision;
            if (_context != context || _revision != snapshot.Revision || _contextRevision != contextRevision || _bindingsRevision != PadBindings.Revision)
            {
                Actions.Reset();
                ResetAimRamp();
                _bindingsRevision = PadBindings.Revision;
                _blocked = gameplayButtons;
                _pressed = 0;
            }
            _context = context; _revision = snapshot.Revision; _contextRevision = contextRevision;
            _blocked &= gameplayButtons;
            _frame.Buttons &= ~_blocked;
            gameplayButtons &= ~_blocked;
            AimDeltaX = AimDeltaY = 0;
            _acceptedAimDeltaX = _acceptedAimDeltaY = 0;
            if (context != GamepadContext.Gameplay || !GamepadContexts.Focused || !_frame.Connected
                || (PlayerEntity.MainPlayerIndex >= 0 && PlayerEntity.MainPlayerIndex < PlayerEntity.Players.Count
                    && PlayerEntity.Players[PlayerEntity.MainPlayerIndex] is { Health: 0 }))
            {
                AimInputSourceTracker.Reset();
                ResetAimRamp();
            }
            if (!GamepadContexts.Focused) { _frame = default; _pressed = 0; return; }
            if (!_frame.Connected) { Actions.Reset(); ResetAimRamp(); return; }
            Actions.Update(gameplayButtons,
                replayContext: MphRead.Mods.Network.DemoPlayback.IsActive);
            if (context != GamepadContext.Gameplay || WheelHeld)
            {
                ResetAimRamp();
                return;
            }
            var (x, y) = AimStick;
            float magnitude = MathF.Sqrt(x * x + y * y);
            UpdateAimRamp(magnitude);
            _filteredAimStick = GamepadAnalog.FilterAim(_filteredAimStick, new(x, y), 1f / 60);
            (x, y) = (_filteredAimStick.X, _filteredAimStick.Y);
            (x, y) = GamepadAnalog.ApplyRadialResponseCurve(x, y, GamepadOptions.Curve);
            AimDeltaX = -x * TurnRate * _turnRateScale * GamepadOptions.LookX
                * (GamepadOptions.InvertX ? -1 : 1);
            AimDeltaY = y * TurnRate * _turnRateScale * GamepadOptions.LookY
                * (GamepadOptions.InvertY ? -1 : 1);
            _acceptedAimDeltaX = AimDeltaX;
            _acceptedAimDeltaY = AimDeltaY;
        }

        /// <summary>
        /// True once for each press of Start, which opens and closes the pause
        /// menu.
        ///
        /// Taken rather than read, and not a keybind like everything else,
        /// because the pause menu is not a thing the *player* does: it is a
        /// window the host platform opens, so it has to be acted on by
        /// whatever owns a window rather than by the entity. The desktop
        /// consumes this after the frame updates and Android in its own loop.
        /// </summary>
        public static bool TakeMenuPress()
        {
            if (_context != GamepadContext.Gameplay && _context != GamepadContext.Results) return false;
            return Actions.Take(PadAction.Menu)
                || (_context == GamepadContext.Results && TakePress(GamepadButtons.B));
        }

        /// <summary>
        /// True once for each press of the chat button.
        ///
        /// Taken, not held, for the reason <see cref="TakeMenuPress"/> gives:
        /// it opens a line that then swallows the keyboard, and opening it
        /// twice from one press would open and immediately close it.
        /// </summary>
        public static bool TakeChatPress()
        {
            if (_context != GamepadContext.Gameplay) return false;
            return Actions.Take(PadAction.Chat);
        }

        /// <summary>
        /// True once for each press of one of these buttons, and consumed on
        /// the way out.
        ///
        /// For screens that read the pad directly rather than through a
        /// bind -- the results screen's hunter picker is the only one -- and
        /// taken rather than read for the reason <see cref="TakeMenuPress"/>
        /// is: a frame drawn twice must not step the choice twice.
        /// </summary>
        public static bool TakeActionPress(PadAction action)
        {
            if (_context != GamepadContext.Gameplay)
            {
                return false;
            }
            return Actions.Take(action);
        }

        public static bool TakePress(GamepadButtons buttons)
        {
            if ((_pressed & buttons) == 0)
            {
                return false;
            }
            _pressed &= ~buttons;
            return true;
        }

        /// <summary>
        /// Add the pad to what the keyboard and mouse already said, for the
        /// player this machine is driving.
        ///
        /// After <c>PlayerEntity.ProcessInput</c>, never instead of it: the
        /// binds are filled from the keyboard first and this only ever turns
        /// things on, so a player with a hand on each works, and a pad sitting
        /// on a desk contributes nothing.
        /// </summary>
        public static void Apply(PlayerEntity? player)
        {
            if (player == null)
            {
                return;
            }
            PlayerControls controls = player.Controls;
            // This state belongs to the current controller sample, not the
            // PlayerControls lifetime. Clear it before every local projection so
            // disconnects, menus and neutral sticks cannot leave stale magnitude.
            controls.ClearAnalogMovement();
            if (!GamepadContexts.Focused || _context != GamepadContext.Gameplay || !Active || player.IsBot
                || !player.LoadFlags.TestFlag(LoadFlags.Active))
            {
                return;
            }
            if (player.Health == 0 || player.IsAltForm) Actions.CloseWheel();
            var move = GamepadOptions.Southpaw
                ? GamepadAnalog.ApplyRadialDeadZone(_frame.RightX, _frame.RightY, GamepadOptions.RightInner, GamepadOptions.RightOuter)
                : GamepadAnalog.ApplyRadialDeadZone(_frame.LeftX, _frame.LeftY, GamepadOptions.LeftInner, GamepadOptions.LeftOuter);

            // Preserve the old additive keyboard/touch + controller behavior.
            // If a digital direction was already held, it remains full strength;
            // the other axis can still come from a partial controller deflection.
            // Right/up keep the same precedence the engine's existing else-if
            // movement branches have when opposite directions are both held.
            bool existingRight = controls.MoveRight.IsDown || controls.RollRight.IsDown;
            bool existingLeft = controls.MoveLeft.IsDown || controls.RolltLeft.IsDown;
            bool existingUp = controls.MoveUp.IsDown || controls.RollUp.IsDown;
            bool existingDown = controls.MoveDown.IsDown || controls.RollDown.IsDown;
            bool padMoving = move.X != 0 || move.Y != 0;
            if (padMoving)
            {
                float x = existingRight ? 1 : move.X > 0 ? move.X
                    : existingLeft ? -1 : move.X < 0 ? move.X : 0;
                float y = existingUp ? 1 : move.Y > 0 ? move.Y
                    : existingDown ? -1 : move.Y < 0 ? move.Y : 0;
                controls.SetAnalogMovement(x, y);
            }

            // Directional keybind state is still populated for animation,
            // jump-direction and legacy gameplay checks. Magnitude is no longer
            // quantized: the movement step reads AnalogMoveX/Y to scale traction.
            // The radial deadzone already turns resting-stick noise into exact zero.
            Hold(controls.MoveUp, move.Y > 0);
            Hold(controls.RollUp, move.Y > 0);
            Hold(controls.MoveDown, move.Y < 0);
            Hold(controls.RollDown, move.Y < 0);
            Hold(controls.MoveLeft, move.X < 0);
            Hold(controls.RolltLeft, move.X < 0);
            Hold(controls.MoveRight, move.X > 0);
            Hold(controls.RollRight, move.X > 0);

            // Which button each of these is on is the player's business now:
            // see PadBindings, which starts as the table that used to be
            // written out here. Two of them drive two binds apiece, which is
            // why PadAction has twelve entries and PlayerControls has more --
            // FIRE is both attacks, for the reason the touch button is (the DS
            // had one attack button, and the game's own defaults still bind
            // the gun and the alt form's attack to the same one), and JUMP is
            // also the ball's boost.
            ApplyBindings(controls);
            if (Actions.WasPressed(PadAction.LastWeapon) && player.PreviousWeapon != player.CurrentWeapon)
            {
                var last = GamepadActions.WeaponBind(controls, player.PreviousWeapon);
                if (last is not null) Hold(last, true, true);
            }

            // And say that somebody is playing. The binds above are ored on
            // after the pass that answers that question for the keyboard, so
            // without this a player holding nothing but a pad reads as idle --
            // which lowers their gun off the screen and leaves them unable to
            // fire. See PlayerEntity.ModNoteInput.
            if (InUse)
            {
                player.ModNoteInput();
            }
        }

        internal static void ApplyBindings(PlayerControls controls)
        {
            void Bind(Keybind bind, PadAction action) => Hold(bind, Actions.Down(action), Actions.WasPressed(action));
            Bind(controls.Shoot, PadAction.Shoot); Bind(controls.AltAttack, PadAction.Shoot);
            Bind(controls.Jump, PadAction.Jump); Bind(controls.Boost, PadAction.Jump);
            Bind(controls.Zoom, PadAction.Zoom); Bind(controls.Morph, PadAction.Morph);
            Bind(controls.Scan, PadAction.Scan); Bind(controls.ScanVisor, PadAction.ScanVisor);
            Hold(controls.WeaponMenu, WheelHeld, Actions.WasPressed(PadAction.WeaponWheel));
            Bind(controls.Pause, PadAction.Scoreboard);
            Bind(controls.NextWeapon, PadAction.NextWeapon); Bind(controls.PrevWeapon, PadAction.PrevWeapon);
            Bind(controls.Missile, PadAction.Missile); Bind(controls.PowerBeam, PadAction.PowerBeam);
            Bind(controls.VoltDriver, PadAction.VoltDriver); Bind(controls.Battlehammer, PadAction.Battlehammer);
            Bind(controls.Imperialist, PadAction.Imperialist); Bind(controls.Judicator, PadAction.Judicator);
            Bind(controls.Magmaul, PadAction.Magmaul); Bind(controls.ShockCoil, PadAction.ShockCoil);
            Bind(controls.OmegaCannon, PadAction.OmegaCannon); Bind(controls.AffinitySlot, PadAction.AffinitySlot);
        }

        private static void Hold(Keybind bind, bool down)
        {
            // No edge of its own: a stick direction is a state, and the things
            // that read IsPressed are all buttons.
            Hold(bind, down, pressed: false);
        }

        /// <summary>
        /// Turn a bind on, never off.
        ///
        /// The or is the whole point: <c>ProcessInput</c> has already written
        /// what the keyboard and mouse are doing, and a pad that assigned
        /// instead of adding would release a key somebody is holding every
        /// frame it did not have that button pressed.
        /// </summary>
        internal static void Hold(Keybind bind, bool down, bool pressed)
        {
            if (down)
            {
                bind.IsDown = true;
                // Keyboard processing sees the previous combined state. A held pad
                // must cancel the provisional release caused by an idle keyboard.
                bind.IsReleased = false;
            }
            if (pressed)
            {
                bind.IsPressed = true;
            }
        }
    }
}
