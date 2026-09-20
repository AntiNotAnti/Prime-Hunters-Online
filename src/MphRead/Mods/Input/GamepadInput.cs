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

        /// <summary>
        /// Degrees of turn per frame at full stick deflection, before the
        /// player's sensitivity multiplier. 3.5 is 210 degrees a second, which
        /// is where console shooters have sat since they settled the question.
        /// </summary>
        private const float TurnRate = 3.5f;

        /// <summary>
        /// How far a stick has to go before it counts as movement. The walk
        /// keys are on or off, so this is where a stick becomes a direction.
        /// Larger than the aim dead zone below it, because a thumb resting on
        /// the stick should not walk you off a ledge.
        /// </summary>
        private const float WalkThreshold = 0.5f;

        /// <summary>
        /// Called once a frame, before the pad is read for anything. Works out
        /// the rising edges and this frame's aim.
        /// </summary>
        public static void BeginFrame()
        {
            var snapshot = GamepadManager.Snapshot;
            FrameSnapshot = snapshot;
            GamepadRuntimeConfig.Frame = snapshot.Runtime;
            _frame = snapshot.State;
            var context = GamepadContexts.Current;
            _pressed = Edges.Update(snapshot);
            long contextRevision = GamepadContexts.Revision;
            if (_context != context || _revision != snapshot.Revision || _contextRevision != contextRevision || _bindingsRevision != PadBindings.Revision)
            {
                Actions.Reset();
                _bindingsRevision = PadBindings.Revision;
                _blocked = _frame.Buttons;
                _pressed = 0;
            }
            _context = context; _revision = snapshot.Revision; _contextRevision = contextRevision;
            _blocked &= _frame.Buttons;
            _frame.Buttons &= ~_blocked;
            AimDeltaX = AimDeltaY = 0;
            if (context != GamepadContext.Gameplay || !GamepadContexts.Focused || !_frame.Connected
                || (PlayerEntity.MainPlayerIndex >= 0 && PlayerEntity.MainPlayerIndex < PlayerEntity.Players.Count
                    && PlayerEntity.Players[PlayerEntity.MainPlayerIndex] is { Health: 0 })) AimInputSourceTracker.Reset();
            if (!GamepadContexts.Focused) { _frame = default; _pressed = 0; return; }
            if (!_frame.Connected) { Actions.Reset(); return; }
            Actions.Update(_frame.Buttons);
            if (context != GamepadContext.Gameplay || WheelHeld) return;
            var (x, y) = AimStick;
            AimDeltaX = -GamepadAnalog.ApplyResponseCurve(x, GamepadOptions.Curve) * TurnRate * GamepadOptions.LookX
                * (GamepadOptions.InvertX ? -1 : 1);
            AimDeltaY = GamepadAnalog.ApplyResponseCurve(y, GamepadOptions.Curve) * TurnRate * GamepadOptions.LookY
                * (GamepadOptions.InvertY ? -1 : 1);
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
            if (!GamepadContexts.Focused || _context != GamepadContext.Gameplay || player == null || !Active || player.IsBot
                || !player.LoadFlags.TestFlag(LoadFlags.Active))
            {
                return;
            }
            if (player.Health == 0 || player.IsAltForm) Actions.CloseWheel();
            PlayerControls controls = player.Controls;
            var move = GamepadOptions.Southpaw
                ? GamepadAnalog.ApplyRadialDeadZone(_frame.RightX, _frame.RightY, GamepadOptions.RightInner, GamepadOptions.RightOuter)
                : GamepadAnalog.ApplyRadialDeadZone(_frame.LeftX, _frame.LeftY, GamepadOptions.LeftInner, GamepadOptions.LeftOuter);
            (int moveX, int moveY) = GamepadAnalog.QuantizeMovement(move.X, move.Y);
            // Both sets, as the touch controls do: walking reads Move and the
            // morph ball reads Roll, and a player who has bound them to
            // different keys expects the stick to drive whichever form they
            // are in.
            Hold(controls.MoveUp, moveY > WalkThreshold);
            Hold(controls.RollUp, moveY > WalkThreshold);
            Hold(controls.MoveDown, moveY < -WalkThreshold);
            Hold(controls.RollDown, moveY < -WalkThreshold);
            Hold(controls.MoveLeft, moveX < -WalkThreshold);
            Hold(controls.RolltLeft, moveX < -WalkThreshold);
            Hold(controls.MoveRight, moveX > WalkThreshold);
            Hold(controls.RollRight, moveX > WalkThreshold);

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
