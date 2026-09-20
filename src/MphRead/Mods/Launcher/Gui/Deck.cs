#if MPHREAD_AVALONIA
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MphRead.Mods.Launcher.Gui
{
    /// <summary>
    /// Responsive sizing and transitional face colours used by the shared launcher controls.
    ///
    /// <para>
    /// Everything in these screens is sized in <b>ems</b>, and the em is not a
    /// constant: the reference layout is a container query, <c>clamp(9px,
    /// 1.15cqw, 15px)</c> on the frame's own width, so one rule draws a 16:9
    /// desktop, a phone held upright and the same phone turned. That is why
    /// there is no second layout for Android in this folder and why there must
    /// never be one -- a phone is not a different design, it is the same
    /// design asked about a narrower box.
    /// </para>
    ///
    /// <para>
    /// The em is published as an <i>inherited attached property</i> so a
    /// control deep in a list can ask for it without every layer in between
    /// having to pass it down. <see cref="DeckStage"/> is what puts it there,
    /// once, from the box the screen is actually handed.
    /// </para>
    ///
    /// <para>
    /// Two typography roles are kept deliberately separate. Inter is the
    /// player-facing label/heading face; JetBrains Mono is reserved for
    /// technical values such as ping, build, counts and status metadata.
    /// </para>
    /// </summary>
    internal sealed class Deck
    {
        private Deck()
        {
        }

        // ------------------------------------------------------------ faces

        /// <summary>A face colour and the solid edge under it, two stops down.</summary>
        internal readonly record struct Face(Color Fill, Color Lip)
        {
            public static Face Blue => new(Rgb(0x12354a), Rgb(0x071c29));
            public static Face Brass => new(Rgb(0x5a4528), Rgb(0x2c2114));
            public static Face Rust => new(Rgb(0x563038), Rgb(0x29171c));
            public static Face Moss => new(Rgb(0x21483b), Rgb(0x10251f));
            public static Face Slate => new(Rgb(0x14212e), Rgb(0x09121b));
            /// <summary>The stepper and tab strip neutral, one shade above Slate.</summary>
            public static Face Step => new(Rgb(0x1b2b3a), Rgb(0x0b1722));
        }

        public static Color Rgb(int hex) => Color.FromRgb(
            (byte)(hex >> 16), (byte)((hex >> 8) & 0xff), (byte)(hex & 0xff));

        /// <summary>A colour at a CSS alpha: <c>rgba(0,0,0,.55)</c> is <c>Fade(0, .55)</c>.</summary>
        public static Color Fade(int hex, double alpha) => Color.FromArgb(
            (byte)Math.Clamp(Math.Round(alpha * 255), 0, 255),
            (byte)(hex >> 16), (byte)((hex >> 8) & 0xff), (byte)(hex & 0xff));

        /// <summary>
        /// One CSS shadow: <c>offsetX offsetY blur spread colour</c>, and
        /// <paramref name="inset"/> for the ones that go the other way.
        ///
        /// Avalonia has the whole of <c>box-shadow</c> -- stacked shadows in
        /// paint order, a real blur, a spread, and inset -- and a
        /// <c>DrawingContext</c> overload that takes them, which means a
        /// control that paints itself can carry the reference's declarations
        /// across <i>verbatim</i> instead of approximating them. Everything
        /// here used to be three or four translucent rounded rects stepped
        /// outward by hand; that was a decent likeness of a blur and the
        /// wrong answer to a `0 0 0 2px` ring, which is a spread and not a
        /// stroke -- a stroke sits half in and half out and eats two points
        /// of whatever is inside it.
        /// </summary>
        public static BoxShadow Shadow(double x, double y, double blur, double spread,
            Color colour, bool inset = false)
        {
            return new BoxShadow
            {
                OffsetX = x,
                OffsetY = y,
                Blur = blur,
                Spread = spread,
                Color = colour,
                IsInset = inset
            };
        }

        // ------------------------------------------------------------- type

        /// <summary>
        /// The body face. Two files rather than one and a synthesised bold:
        /// a smeared mono is exactly the failure the mono was brought in to
        /// avoid, and both weights are used (400 for a cell, 700 for a
        /// keycap).
        /// </summary>
        public static readonly FontFamily Mono =
            new("avares://FruityPrime/Assets/Fonts/JetBrainsMono-Regular.ttf#JetBrains Mono");

        public static readonly FontFamily MonoBold =
            new("avares://FruityPrime/Assets/Fonts/JetBrainsMono-Bold.ttf#JetBrains Mono");

        /// <summary>The body face at a weight. Never synthesised; a real file either way.</summary>
        public static Typeface Body(bool bold) =>
            new(bold ? MonoBold : Mono, FontStyle.Normal, FontWeight.Normal);

        /// <summary>
        /// The interface face: labels, names and headings.
        ///
        /// Two weights, and which one is not decoration. A <c>.btn</c> is
        /// <c>font-weight: 600</c> and a server row is a bare
        /// <c>&lt;button&gt;</c> that sets only its family -- so its name and
        /// its map inherit the browser's own 400. Setting the whole screen in
        /// 600 made every row read as a heading, which is the opposite of what
        /// a list wants.
        /// </summary>
        public static Typeface Label(bool strong = true) =>
            new(GuiTheme.Interface, FontStyle.Normal,
                strong ? FontWeight.SemiBold : FontWeight.Normal);

        // -------------------------------------------------------- the frame

        /// <summary>
        /// Whether the frame is a phone's.
        ///
        /// Android on the real head; a capture sets it by hand so the two
        /// orientations can be photographed from a desktop. It changes the
        /// <i>curve</i> the em is read off and nothing else -- there is one
        /// layout.
        /// </summary>
        public static bool Phone { get; set; } = OperatingSystem.IsAndroid();

        /// <summary>
        /// Draw everything at rest: no spring in flight, no idle bob.
        ///
        /// For <see cref="UiCapture"/>, and it is not only about determinism.
        /// A spring re-invalidates from inside <c>Render</c> until it settles,
        /// and a <c>RenderTargetBitmap</c> refuses to finish a pass that
        /// invalidated something -- so a screen with a focused button on it
        /// could not be photographed at all ("Visual was invalidated during
        /// the render pass"). The resting pose is also the one worth
        /// photographing.
        /// </summary>
        public static bool Still
        {
            get => _still || Asleep;
            set => _still = value;
        }

        private static bool _still;

        /// <summary>
        /// The screens are not on the glass, so nothing on them need move.
        ///
        /// Unlike <see cref="Still"/>, which a capture sets once for the life
        /// of the process, this goes on and off: on Android a match hides the
        /// launcher's Android view, and a view going <c>Gone</c> is not a
        /// detach, so every animation on the front screen carried on asking
        /// the compositor for frames -- on the UI thread of the process
        /// running the match, for the whole match. Read through
        /// <see cref="Still"/>, which every animation here already checks.
        /// </summary>
        public static bool Asleep { get; set; }

        /// <summary>
        /// The reference's three clamps, verbatim.
        ///
        /// <c>1.15cqw</c> on a desktop, <c>3.15cqw</c> on a phone upright and
        /// <c>1.55cqw</c> on the same phone turned -- three numbers rather
        /// than one because the frames are 1180, 360 and 800 points wide and
        /// a single percentage across that range is either unreadable at one
        /// end or a toy at the other. The ceiling is lower on a phone (13
        /// against 15) for the same reason.
        /// </summary>
        /// <summary>
        /// The reference's <c>--spring</c>, <c>cubic-bezier(.18, 1.55, .35,
        /// 1)</c>, solved for a progress in 0..1.
        ///
        /// A real cubic Bezier rather than something that overshoots by about
        /// the right amount: the overshoot past 1 is the whole character of
        /// this curve -- it is what makes a hovered button *pop* instead of
        /// growing -- and it happens at a particular moment, which an
        /// approximation gets wrong in a way that reads as a different
        /// animation beside the page it is a port of.
        ///
        /// Newton on x(t) for the parameter, then y(t). Four iterations is
        /// inside a thousandth over the whole span; the bisection after it is
        /// the guard for the one region where the derivative goes flat.
        /// </summary>
        /// <summary>
        /// Run something on the UI thread before the next frame these screens
        /// are drawn in, and mark that frame as wanted.
        ///
        /// The one hook every self-driving animation here goes through. Two
        /// things have to happen and neither is <c>InvalidateVisual</c> on its
        /// own: the step must land *between* render passes (Avalonia refuses
        /// to have a visual invalidated during one, and the exception comes
        /// out of the dispatcher rather than out of the caller), and the
        /// surface has to be told the picture changed, or the step runs and is
        /// then not drawn until the backstop comes round.
        /// </summary>
        /// <param name="asker">
        /// The control the animation belongs to. It is how the head with a
        /// real compositor finds the clock to step on; the shell ignores it,
        /// having exactly one surface.
        /// </param>
        public static void NextFrame(Visual asker, Action step, bool idling = false)
        {
#if MPHREAD_SHELL
            UiSurface.RequestFrame(step, idling);
#else
            // The compositor's own animation tick, not a bare dispatcher post.
            // A post at Render priority is run whenever the queue reaches that
            // priority, which is not a frame: a step that asks for the next
            // one -- and the idle bob asks for ever -- goes round again as
            // fast as the dispatcher can turn, ahead of the touch events a
            // drag is made of. RequestAnimationFrame is throttled on the
            // compositor and its callbacks are drained from a queue that has
            // already been swapped out, so re-asking from inside one lands on
            // the frame after rather than on this one.
            TopLevel? top = TopLevel.GetTopLevel(asker);
            if (top != null)
            {
                top.RequestAnimationFrame(_ => step());
                return;
            }
            // Not in a tree yet: no clock to ask, and the step still has to
            // land between passes rather than inside one.
            Avalonia.Threading.Dispatcher.UIThread.Post(step,
                Avalonia.Threading.DispatcherPriority.Render);
#endif
        }

        /// <summary>
        /// Whether the keyboard, rather than a pointer, is what the player is
        /// currently driving these screens with.
        ///
        /// This is the state a browser keeps behind <c>:focus-visible</c>, and
        /// it is why a mouse user never sees a focus ring and somebody on Tab
        /// always does. The pseudo-class is not "has focus" -- the ring
        /// follows the *input*, so a panel that focuses its Back button as it
        /// opens rings it when Tab opened the panel and does not when a click
        /// did. A control that asked <c>IsFocused</c> instead put a two-point
        /// amber ring around everything anybody clicked, which is what
        /// "un outerline jaune s'affiche quand on click dessus" was.
        ///
        /// Set from the input entry points rather than worked out per control,
        /// because it is one fact about the session and not a property of any
        /// one button.
        /// </summary>
        public static bool KeyboardDriving { get; private set; }

        /// <summary>A key arrived: rings from here on.</summary>
        public static void DrivingByKeyboard() => KeyboardDriving = true;

        /// <summary>A pointer or a finger arrived: no rings from here on.</summary>
        public static void DrivingByPointer() => KeyboardDriving = false;

        public static double Spring(double progress) => Bezier(progress, 0.18, 1.55, 0.35, 1);

        public static double Bezier(double progress, double x1, double y1, double x2, double y2)
        {
            if (progress <= 0)
            {
                return 0;
            }
            if (progress >= 1)
            {
                return 1;
            }
            double t = progress;
            for (int i = 0; i < 4; i++)
            {
                double slope = Slope(t, x1, x2);
                if (Math.Abs(slope) < 1e-6)
                {
                    break;
                }
                t -= (Curve(t, x1, x2) - progress) / slope;
                t = Math.Clamp(t, 0, 1);
            }
            return Curve(t, y1, y2);
        }

        /// <summary>One axis of a unit cubic Bezier, the two ends pinned at 0 and 1.</summary>
        private static double Curve(double t, double a, double b)
        {
            double u = 1 - t;
            return 3 * u * u * t * a + 3 * u * t * t * b + t * t * t;
        }

        private static double Slope(double t, double a, double b)
        {
            double u = 1 - t;
            return 3 * u * u * a + 6 * u * t * (b - a) + 3 * t * t * (1 - b);
        }

        public static double EmFor(double width, double height)
        {
            if (width <= 0)
            {
                return 9;
            }
            if (Phone)
            {
                bool landscape = width > height;
                return Math.Clamp(width * (landscape ? 0.0155 : 0.0315), 9, 13);
            }
            return Math.Clamp(width * 0.0115, 9, 15);
        }

        /// <summary>
        /// The row's own em, which is <b>not</b> the stage's.
        ///
        /// A server row is a <c>&lt;button&gt;</c> in the reference and a
        /// button does not inherit font-size, so every row in the browser is
        /// laid out against the browser's own 13.333px whatever the frame is
        /// doing. That reads as an accident and is not one: it is what keeps a
        /// list of servers the same physical size on a phone as on a monitor,
        /// where the text around it shrinks. Reproduced deliberately.
        /// </summary>
        public const double RowEm = 13.3333;

        /// <summary>
        /// Below this the frame drops a row's middle columns.
        ///
        /// The reference's <c>@container (max-width: 560px)</c>, and the
        /// container is the <i>frame</i>, not the panel: a phone in landscape
        /// has a narrow panel and a wide frame, and it keeps every column.
        /// </summary>
        public const double NarrowFrame = 560;

        // ------------------------------------------- the em, down the tree

        public static readonly AttachedProperty<double> EmProperty =
            AvaloniaProperty.RegisterAttached<Deck, Control, double>(
                "Em", defaultValue: 10.81, inherits: true);

        /// <summary>The frame's width, for the one container query there is.</summary>
        public static readonly AttachedProperty<double> FrameWidthProperty =
            AvaloniaProperty.RegisterAttached<Deck, Control, double>(
                "FrameWidth", defaultValue: 940, inherits: true);

        public static double GetEm(AvaloniaObject o) => o.GetValue(EmProperty);

        public static void SetEm(AvaloniaObject o, double value) => o.SetValue(EmProperty, value);

        public static double GetFrameWidth(AvaloniaObject o) => o.GetValue(FrameWidthProperty);

        public static void SetFrameWidth(AvaloniaObject o, double value) =>
            o.SetValue(FrameWidthProperty, value);

        /// <summary>Ems to points, on this control's own frame.</summary>
        public static double Px(AvaloniaObject o, double ems) => GetEm(o) * ems;

        /// <summary>True where the frame has dropped to a phone's width.</summary>
        public static bool Narrow(AvaloniaObject o) => GetFrameWidth(o) <= NarrowFrame;
    }

    /// <summary>
    /// The frame every screen is laid out in, and the one place the em is
    /// worked out.
    ///
    /// It is a <see cref="Panel"/> and draws nothing: what it does is read the
    /// box it is being measured with and stamp <see cref="Deck.EmProperty"/>
    /// on itself before its children measure, so the whole tree under it is
    /// already in the right unit on the first pass rather than a pass behind.
    /// A second pass is avoided by only writing the value when it has actually
    /// moved -- an inherited property assigned on every measure invalidates
    /// every control that reads it, on every measure.
    /// </summary>
    internal sealed class DeckStage : Panel
    {
        private double _em = -1;
        private double _width = -1;

        protected override Size MeasureOverride(Size availableSize)
        {
            double w = availableSize.Width, h = availableSize.Height;
            if (!Double.IsInfinity(w) && !Double.IsInfinity(h) && w > 0 && h > 0)
            {
                double em = Deck.EmFor(w, h);
                if (Math.Abs(em - _em) > 0.001)
                {
                    _em = em;
                    Deck.SetEm(this, em);
                    // One line when it moves, which is once per screen and
                    // once more per orientation change. It is the number every
                    // "the phone lays it out as if it were a monitor" report
                    // is really about.
                    Mods.DebugLog.Line("deck", $"stage em {em:0.###} at {w:0}x{h:0}");
                }
                if (Math.Abs(w - _width) > 0.001)
                {
                    _width = w;
                    Deck.SetFrameWidth(this, w);
                }
            }
            return base.MeasureOverride(availableSize);
        }
    }
}
#endif
