using System;

namespace MphRead.Mods.Input
{
    /// <summary>
    /// Which part of the DS's bottom screen the pen is on.
    /// </summary>
    public enum StylusRegion
    {
        /// <summary>Outside the zone entirely. The pointer is a mouse again.</summary>
        None,
        /// <summary>The middle, which is the map on the DS and the aim here.</summary>
        Aim,
        PowerBeam,
        Missile,
        Weapons,
        WeaponSelect,
        AltForm
    }

    /// <summary>
    /// The DS's bottom screen, drawn faintly on the top one and mapped to a
    /// tablet.
    ///
    /// A pen tablet is an absolute device: a point on the tablet is a point on
    /// the screen, and it stays that point. That is what the DS's touch screen
    /// was, and it is why a tablet is the one desktop device that can play
    /// this game the way it was actually played -- the four weapon buttons
    /// along the top, the morph ball in the corner, and aiming by dragging in
    /// the middle. Reaching for a button is a movement of the hand to a place,
    /// not a search with a cursor.
    ///
    /// So the player marks out a rectangle on their screen that stands for the
    /// bottom screen, and maps their tablet to it. Inside it, the layout is
    /// the DS's; outside it, nothing here applies and the pointer is an
    /// ordinary mouse. The rectangle keeps the DS's 4:3, because the layout
    /// is a picture and stretching it would put the buttons somewhere the hand
    /// has not learned.
    ///
    /// It is drawn on the top screen at very low opacity: enough that the hand
    /// can find a button without looking away from the game, not enough to be
    /// part of the picture. The DS player could glance down; this is the
    /// nearest thing to that on one screen.
    ///
    /// Positions below are in the DS's own 256x192 units and are taken off the
    /// game's bottom screen: four buttons across the top -- the Power Beam,
    /// the missile with its ammo count, the weapon and the weapon select --
    /// the alt form in the bottom right corner, and the map filling the
    /// middle, which is where aiming happens.
    ///
    /// <para>
    /// Inside the zone the pen is a pen and not a mouse, and that is three
    /// rules rather than one. A touch is owned by whatever it went down on
    /// until it lifts (<see cref="Held"/>), so a drag may wander anywhere
    /// without becoming something else. Aiming is that drag on the map and
    /// nothing else (<see cref="Aiming"/>) -- not the pointer's raw movement,
    /// which for a tablet is the distance the hand travelled to reach the
    /// weapon it was going for. And the tip never fires
    /// (<see cref="CapturingPrimaryButton"/>): the DS put the trigger on a shoulder button,
    /// this screen has no trigger on it, and here the tip arrives as the left
    /// mouse button, which is the fire bind. A touch that begins *outside* the
    /// zone is an ordinary click and is left alone, which is what a tablet
    /// player shoots with.
    /// </para>
    /// </summary>
    public static class StylusZone
    {
        public const float DsWidth = 256;
        public const float DsHeight = 192;

        /// <summary>
        /// A button on the bottom screen: where it is and how big, in DS
        /// units, and what it does.
        /// </summary>
        public readonly struct Button
        {
            public readonly StylusRegion Region;
            public readonly float X;
            public readonly float Y;
            public readonly float Radius;
            public readonly string Label;

            public Button(StylusRegion region, float x, float y, float radius, string label)
            {
                Region = region;
                X = x;
                Y = y;
                Radius = radius;
                Label = label;
            }
        }

        /// <summary>
        /// The layout, off the DS screen. Order matters only in that the
        /// first one containing the point wins, and they do not overlap.
        /// </summary>
        public static readonly Button[] Buttons =
        {
            new Button(StylusRegion.PowerBeam, 26, 26, 22, "BEAM"),
            new Button(StylusRegion.Missile, 80, 24, 20, "MSL"),
            new Button(StylusRegion.Weapons, 150, 28, 30, "WPN"),
            new Button(StylusRegion.WeaponSelect, 222, 28, 26, "SEL"),
            new Button(StylusRegion.AltForm, 228, 166, 22, "ALT")
        };

        /// <summary>
        /// Whether the zone is being used at all.
        ///
        /// Two switches, one answer: the player's own
        /// (<see cref="Wanted"/>, "DS bottom screen for a pen tablet") and the
        /// master above it (<see cref="PointerInput.StylusMode"/>, the
        /// settings screen's "Stylus mode"). The zone is a pen tablet's bottom
        /// screen and means nothing without a pen, so the master wins -- and
        /// it has to, because the zone's own row is *hidden* while stylus mode
        /// is off. Left ungated, turning stylus mode off changed nothing the
        /// player could see: the aim was still a drag on the map, the tip
        /// still did not fire, the wheel was still drawn in a rectangle, and
        /// the only control that would have undone any of it was no longer on
        /// the screen.
        ///
        /// The player's own answer is kept rather than cleared, so turning
        /// stylus mode back on restores the zone they drew instead of asking
        /// them to draw it again.
        /// </summary>
        public static bool Enabled
        {
            get => Wanted && PointerInput.StylusMode;
            set => Wanted = value;
        }

        /// <summary>
        /// What the player asked for, whatever the master switch says. This is
        /// what the settings row shows and what is written to the file.
        /// </summary>
        public static bool Wanted { get; private set; }

        // Where the zone is, as fractions of the window, so it survives a
        // resize and a change of monitor. Width alone: the height follows
        // from the DS's shape, which the layout depends on.
        public static float Left { get; private set; } = 0.62f;
        public static float Top { get; private set; } = 0.60f;
        public static float Width { get; private set; } = 0.34f;

        public static float Height => Width * (DsHeight / DsWidth) * AspectCorrection;

        /// <summary>
        /// Window shape, so a zone given as a fraction of the width is still
        /// the DS's shape on screen. Set once a frame by the renderer, which
        /// is the only thing that knows the window.
        /// </summary>
        public static float AspectCorrection { get; set; } = 16f / 9f;

        /// <summary>How solid the overlay is drawn. Barely there by design --
        /// see the class summary -- but a slider's worth of a question, since
        /// screens and eyes differ.</summary>
        public static float Opacity { get; set; } = 0.22f;

        public static void SetRect(float left, float top, float width)
        {
            Width = Math.Clamp(width, 0.10f, 1f);
            Left = Math.Clamp(left, 0, 1 - Width);
            Top = Math.Clamp(top, 0, Math.Max(0, 1 - Height));
        }

        /// <summary>
        /// The player is drawing the zone right now, so the overlay is drawn
        /// solidly and the game takes no input from the pointer.
        /// </summary>
        public static bool Placing { get; private set; }

        private static float _placeAnchorX;
        private static float _placeAnchorY;
        private static bool _placeAnchored;

        /// <summary>
        /// One button in the settings, and then the whole of it: the player
        /// drags a rectangle on the screen where the bottom screen should be,
        /// and that is both the position and the size. Nothing to type, and
        /// nothing that can be set to a shape the layout does not fit.
        /// </summary>
        public static void BeginPlacement()
        {
            Placing = true;
            _placeAnchored = false;
        }

        public static void CancelPlacement()
        {
            Placing = false;
            _placeAnchored = false;
        }

        /// <summary>A press while placing: the first corner.</summary>
        public static void PlacementDown(float x, float y)
        {
            if (!Placing)
            {
                return;
            }
            _placeAnchorX = Math.Clamp(x, 0, 1);
            _placeAnchorY = Math.Clamp(y, 0, 1);
            _placeAnchored = true;
        }

        /// <summary>
        /// A drag while placing: the far corner, so far.
        ///
        /// The corner the pen went down on stays where it was put, and the
        /// drag decides only how wide the box is and which way it grows --
        /// down from that corner, or up onto it. That is not what this did:
        /// it took the top edge from whichever of the two points was higher
        /// and then let <see cref="SetRect"/> clamp it, and since the height
        /// is derived from the width and the width was still growing, a box
        /// dragged out anywhere near the bottom of the window slid upwards
        /// under the hand for the whole of the drag. Which is the whole of
        /// "I cannot put it where I want it".
        /// </summary>
        public static void PlacementDrag(float x, float y)
        {
            if (!Placing || !_placeAnchored)
            {
                return;
            }
            x = Math.Clamp(x, 0, 1);
            y = Math.Clamp(y, 0, 1);
            Width = Math.Clamp(Math.Abs(x - _placeAnchorX), 0.10f, 1f);
            float height = Height;
            float left = Math.Min(_placeAnchorX, x);
            // Up or down from the anchor, rather than "the higher of the two":
            // the anchored corner is the one the hand chose and it is the one
            // that must not move.
            float top = y >= _placeAnchorY ? _placeAnchorY : _placeAnchorY - height;
            Left = Math.Clamp(left, 0, Math.Max(0, 1 - Width));
            Top = Math.Clamp(top, 0, Math.Max(0, 1 - height));
        }

        /// <summary>
        /// Move or resize the zone from the keyboard while it is being placed.
        ///
        /// A drag puts the rectangle roughly where it goes; a tablet is an
        /// absolute device and "roughly" is the one thing it cannot live with,
        /// since the hand learns the position once and then stops looking. The
        /// arrows move it and the brackets resize it, both by a step small
        /// enough to land on a pixel and with a finer one on Shift.
        /// </summary>
        public static void Nudge(float dx, float dy)
        {
            if (!Placing)
            {
                return;
            }
            Left = Math.Clamp(Left + dx, 0, Math.Max(0, 1 - Width));
            Top = Math.Clamp(Top + dy, 0, Math.Max(0, 1 - Height));
        }

        public static void Resize(float by)
        {
            if (!Placing)
            {
                return;
            }
            SetRect(Left, Top, Width + by);
        }

        /// <summary>Release: that is the zone. Enabling it is the point of
        /// having drawn one.</summary>
        public static void PlacementUp()
        {
            if (!Placing)
            {
                return;
            }
            Placing = false;
            if (_placeAnchored)
            {
                Enabled = true;
            }
            _placeAnchored = false;
        }

        /// <summary>
        /// Keep what is on screen and stop placing, for a player who moved an
        /// existing zone with the keys rather than drawing a new one -- there
        /// is no release to end that, and Escape means "leave it as it was".
        /// </summary>
        public static void CommitPlacement()
        {
            if (!Placing)
            {
                return;
            }
            Placing = false;
            _placeAnchored = false;
            Enabled = true;
        }

        // ------------------------------------------------------------- input

        /// <summary>Where the pen is, this frame.</summary>
        public static StylusRegion Region { get; private set; }

        /// <summary>Whether the pen is in contact -- the left button, which
        /// is what a pen tip reports.</summary>
        public static bool Contact { get; private set; }

        /// <summary>
        /// What the touch on the screen right now belongs to.
        ///
        /// A touch is owned by whatever it went down on, the way every
        /// touchscreen in the world works and the way the DS's did: the pen
        /// may then be dragged anywhere at all and the thing it started on is
        /// still the thing it is doing. That is what makes the two gestures
        /// this screen actually has work at all -- dragging the map to aim,
        /// which wanders over a button constantly, and holding the weapon
        /// select while taking the pen up to the wheel to choose off it.
        ///
        /// The one exception is the row of plain buttons, which stay
        /// slide-sensitive: the hand runs along BEAM-MSL-WPN with the tip
        /// down far more often than it taps each one, and none of those three
        /// is a gesture with an end to protect.
        /// </summary>
        public static StylusRegion Held { get; private set; }

        /// <summary>
        /// A button that was touched and has not been acted on yet.
        ///
        /// A latch rather than a flag that is true for one frame, because the
        /// frames that matter are not the same frames: this is set once per
        /// *picture* and read once per *simulation step*, and at 144 Hz most
        /// pictures have no step behind them. The press that landed on one of
        /// those used to be overwritten by the next frame's None and reach the
        /// game not at all. Taken with <see cref="TakePressed"/>.
        /// </summary>
        public static StylusRegion Pressed { get; private set; }

        /// <summary>
        /// Read the pending press and clear it, so one touch is one press
        /// however many steps or pictures follow it.
        /// </summary>
        public static StylusRegion TakePressed()
        {
            StylusRegion pressed = Pressed;
            Pressed = StylusRegion.None;
            return pressed;
        }

        /// <summary>
        /// The pen is dragging the map, which is this game's aiming.
        ///
        /// False on the frame the tip lands, which is the frame that carries
        /// the whole of the distance between wherever the pen was hovering and
        /// where it was put down -- integrated, that is the view spinning
        /// round on every touch, which is the fault
        /// <see cref="PointerInput"/> guards against for a pen that has no
        /// zone.
        /// </summary>
        public static bool Aiming => Enabled && !Placing && Contact
            && Held == StylusRegion.Aim && _aimReady;

        /// <summary>
        /// The weapon select is being held down.
        ///
        /// The DS's weapon menu is a hold: the wheel is up for as long as the
        /// tip is on the screen and the weapon under it when the tip lifts is
        /// the one taken. Pressing the button for a single frame -- which is
        /// what this did -- opened the wheel and closed it again before the
        /// pen could be moved anywhere near it, so the menu flickered and
        /// nothing was ever chosen.
        /// </summary>
        public static bool MenuHeld => Enabled && !Placing && Contact
            && Held == StylusRegion.WeaponSelect;

        /// <summary>
        /// The zone owns the pointer gesture, including placement. Independent
        /// keyboard, controller and mouse-button input remains available.
        /// A contact that begins outside the zone remains an ordinary click.
        /// </summary>
        public static bool CapturingPointer => Placing || CapturingPrimaryButton;

        /// <summary>Only the tip's primary button is consumed, never an independent action.</summary>
        public static bool CapturingPrimaryButton =>
            Enabled && Contact && Held != StylusRegion.None;

        private static bool _loggedContact;
        private static bool _loggedCapture;

        private static void LogTransitions()
        {
            if (DebugLog.Active)
            {
                if (Contact != _loggedContact)
                {
                    DebugLog.Line("input", Contact ? $"stylus contact began region={Region}" : "stylus contact ended");
                }
                if (CapturingPointer != _loggedCapture)
                {
                    DebugLog.Line("input", $"stylus primary button captured={CapturingPrimaryButton} "
                        + $"pointer={CapturingPointer} contact={Contact} region={Region} held={Held} "
                        + $"aiming={Aiming} jumps={PointerInput.JumpsIgnored}");
                }
            }
            _loggedContact = Contact;
            _loggedCapture = CapturingPointer;
        }

        private static bool _lastContact;
        private static bool _aimReady;

        /// <summary>
        /// One frame of pointer, in window fractions. Called by the renderer
        /// whether or not the zone is on, because it is also what drives the
        /// placement drag.
        /// </summary>
        public static void Update(float x, float y, bool contact)
        {
            if (Placing)
            {
                Region = StylusRegion.None;
                Held = StylusRegion.None;
                Pressed = StylusRegion.None;
                Contact = contact;
                _lastContact = contact;
                _aimReady = false;
                LogTransitions();
                return;
            }
            if (!Enabled)
            {
                Region = StylusRegion.None;
                Held = StylusRegion.None;
                Pressed = StylusRegion.None;
                Contact = false;
                _lastContact = false;
                _aimReady = false;
                LogTransitions();
                return;
            }
            StylusRegion under = RegionAt(x, y);
            if (!contact)
            {
                // Nothing is owed to a touch that is over. A press nobody took
                // is dropped with it rather than being handed to the game
                // whenever it next looks.
                Held = StylusRegion.None;
                Pressed = StylusRegion.None;
                _aimReady = false;
            }
            else if (!_lastContact)
            {
                Held = under;
                _aimReady = false;
                if (under != StylusRegion.None && under != StylusRegion.Aim
                    && under != StylusRegion.WeaponSelect)
                {
                    Pressed = under;
                }
            }
            else
            {
                // A drag that began on one of the plain buttons may walk onto
                // another; a drag that began on the map or on the select owns
                // the touch to the end of it.
                if (Held != StylusRegion.None && Held != StylusRegion.Aim
                    && Held != StylusRegion.WeaponSelect
                    && under != Held && under != StylusRegion.None
                    && under != StylusRegion.Aim)
                {
                    Held = under;
                    if (under != StylusRegion.WeaponSelect)
                    {
                        Pressed = under;
                    }
                }
                _aimReady = true;
            }
            Region = contact ? Held : under;
            Contact = contact;
            _lastContact = contact;
            LogTransitions();
        }

        /// <summary>Which part of the zone a window position falls in.</summary>
        public static StylusRegion RegionAt(float x, float y)
        {
            float height = Height;
            if (height <= 0 || x < Left || x >= Left + Width || y < Top || y >= Top + height)
            {
                return StylusRegion.None;
            }
            // Into DS units, where the layout is written.
            float dsX = (x - Left) / Width * DsWidth;
            float dsY = (y - Top) / height * DsHeight;
            for (int i = 0; i < Buttons.Length; i++)
            {
                Button button = Buttons[i];
                float dx = dsX - button.X;
                float dy = dsY - button.Y;
                if (dx * dx + dy * dy <= button.Radius * button.Radius)
                {
                    return button.Region;
                }
            }
            return StylusRegion.Aim;
        }

        public static void Reset()
        {
            Region = StylusRegion.None;
            Held = StylusRegion.None;
            Pressed = StylusRegion.None;
            Contact = false;
            _lastContact = false;
            _aimReady = false;
            Placing = false;
            _placeAnchored = false;
            _loggedContact = false;
            _loggedCapture = false;
        }
    }
}
