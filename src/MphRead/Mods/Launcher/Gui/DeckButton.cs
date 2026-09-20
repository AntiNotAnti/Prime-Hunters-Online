#if MPHREAD_AVALONIA
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace MphRead.Mods.Launcher.Gui
{
    /// <summary>
    /// A button with a thickness.
    ///
    /// <para>
    /// The whole of the "deck" look is in one detail, and it is not the
    /// colour: a solid, unblurred edge below the face, in the face's own hue
    /// two stops down. That is what makes the control read as an object with a
    /// depth rather than a rectangle with a gradient, and it is why the press
    /// works -- the face travels down by exactly the edge it loses, so it
    /// lands where the edge's bottom was. Anything else is a rectangle moving.
    /// </para>
    ///
    /// <para>
    /// Every measurement is an em. The font size is in the <i>frame's</i> ems
    /// (<see cref="Deck.EmProperty"/>) and the padding and the radius are in
    /// the <i>button's own</i>, which is exactly how the reference nests them:
    /// <c>font-size: 1.05em</c> on the tab, then <c>padding: .38em .8em</c>
    /// against that. Getting the two levels the wrong way round is how a tab
    /// strip comes out the size of a menu entry.
    /// </para>
    ///
    /// <para>
    /// Avalonia has no CSS transitions and this codebase paints its own
    /// controls, so the motion is a spring integrated in <see cref="Render"/>:
    /// a position, a velocity, a stiffness and a damping, stepped by the time
    /// since the last frame and re-invalidating until it settles. That gives
    /// the overshoot a cubic-bezier cannot, costs one float of state, and
    /// stops drawing the moment it stops moving -- which matters here, because
    /// these are composited into the game window every frame it draws.
    /// </para>
    ///
    /// <para>
    /// The hovered face also leans toward the pointer. Seven degrees is where
    /// it stops reading as a card and starts reading as a wobble; a bare
    /// rotation is the whole of it, since a real perspective divide on a
    /// control this size is invisible next to the cost of doing it.
    /// </para>
    /// </summary>
    internal sealed class DeckButton : Control
    {
        public event EventHandler? Click;

        private string _text;
        private Deck.Face _face;
        private bool _selected;

        /// <summary>Font size, in the frame's ems.</summary>
        private readonly double _sizeEms;

        /// <summary>Padding and radius, in this button's own ems.</summary>
        private readonly double _padXEms, _padYEms;

        /// <summary>The edge, in points. Flat, like the reference's <c>--lip</c>.</summary>
        private readonly double _lip;

        /// <summary>
        /// The key that does the same thing, in a chip on the face.
        ///
        /// A mark used to carry a drawn glyph -- a tick, a cross, a plus --
        /// which said what kind of thing it was and nothing a player did not
        /// already know from the word beside it, at four times the width. The
        /// chip says something they cannot know instead.
        /// </summary>
        public string KeyCap { get; set; } = "";

        /// <summary>
        /// Something drawn on the face instead of a word, in the box it is
        /// given, in the colour the label would have been.
        ///
        /// For the support mark, which is a `.btn` in the reference like every
        /// other -- `class="btn f-rust heart"` -- with an SVG where the word
        /// goes. It had been its own control, so it had none of what makes
        /// these buttons what they are: no bevel, no spring, no lean towards
        /// the pointer, no press that travels by the lip it loses. Beside QUIT
        /// it read as a different program's button.
        /// </summary>
        public Action<DrawingContext, Rect, IBrush>? Glyph { get; set; }

        /// <summary>The glyph's box, in this button's own ems.</summary>
        public Size GlyphEms { get; set; }

        /// <summary>
        /// The glyph's own colour, before the face's filter.
        ///
        /// `fill: currentColor` under `.btn:hover { filter: brightness(1.22)
        /// saturate(1.15) }`: the picture is brightened with the face it sits
        /// on rather than being repainted, which is what makes the two look
        /// like one object. Unset means the label's ink.
        /// </summary>
        public Color? GlyphColour { get; set; }

        /// <summary>
        /// What this says when the pointer is on it, drawn above it.
        ///
        /// `data-tip` on the reference's support mark. Empty on every other
        /// button, which is most of them: a button whose word already says
        /// what it does needs nothing above it.
        /// </summary>
        public string Tip { get; set; } = "";

        /// <summary>
        /// The reference's <c>.btn.idle</c>: two and a half points of bob on a
        /// 3.4 second cycle, on the one button the screen wants looked at.
        ///
        /// It is off by default and has to be asked for, because it is the one
        /// thing on these screens that redraws when nothing has happened --
        /// and a redraw here costs the whole launcher surface. One button on
        /// one screen is the budget.
        /// </summary>
        public bool Idle { get; set; }

        private readonly Tap _tap = new();

        private const double LipPressed = 2;
        private const double RadiusEms = 0.55;
        private const double MaxTilt = 7;

        /// <summary>Where the spring is, where it is going, and how fast.</summary>
        private double _pop = 1, _popVelocity;
        private double _popTarget = 1;
        private double _tilt, _tiltTarget;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private TimeSpan _last;

        /// <summary>
        /// Stiffness and damping. 220 and 18 overshoot by about six per cent
        /// and settle inside a fifth of a second, which is Balatro's pop as
        /// near as watching it frame by frame can place it.
        /// </summary>
        private const double Stiffness = 220;
        private const double Damping = 18;

        // ------------------------------------------------ the word's own hop
        //
        // `.btn:hover .ch { animation: chhop .42s var(--spring) var(--d) 1 }`,
        // staggered `i * .022s` down the label and leaning alternate ways.
        // It is the one animation on these screens that happens to the *word*
        // rather than to the object under it, and it is what a Balatro button
        // does that a scaled rectangle does not.
        //
        // One shot per hover, so the clock is started when the pointer arrives
        // and stopped when the run is over rather than left running: a button
        // the pointer is resting on has finished animating, and a clock nobody
        // reads is a frame nobody needs.

        /// <summary>One character's run, in seconds.</summary>
        private const double HopSeconds = 0.42;

        /// <summary>How far down the label each character starts, in seconds.</summary>
        private const double HopStagger = 0.022;

        /// <summary>Where the lift peaks, as a fraction of one character's run.</summary>
        private const double HopPeak = 0.38;

        /// <summary>The lift at the peak, in ems of the label's own size.</summary>
        private const double HopLift = 0.18;

        /// <summary>The lean at the peak, in degrees, alternating down the word.</summary>
        private const double HopTilt = 3;

        private readonly Stopwatch _hop = new();

        /// <summary>
        /// Whether the ring should be drawn: the reference's
        /// <c>:focus-visible</c>, not <c>:focus</c>.
        ///
        /// A browser shows that ring when the keyboard put the focus there and
        /// hides it when a pointer did -- which is the whole point of the
        /// pseudo-class, and why a mouse user never sees one. This drew on
        /// <c>IsFocused</c> instead, and every control here calls
        /// <c>Focus()</c> from its own press handler, so clicking anything
        /// left a two-point amber ring around it until something else was
        /// clicked. Avalonia says which way the focus arrived, so this asks.
        /// </summary>
        private bool _ringVisible;

        static DeckButton()
        {
            AffectsRender<DeckButton>(IsEnabledProperty);
            AffectsMeasure<DeckButton>(Deck.EmProperty);
            AffectsRender<DeckButton>(Deck.EmProperty);
        }

        public DeckButton(string text, Deck.Face face, double sizeEms = 1.55,
            double padXEms = 1.5, double padYEms = 0.85, double lip = 6)
        {
            _text = text;
            _face = face;
            _sizeEms = sizeEms;
            _padXEms = padXEms;
            _padYEms = padYEms;
            _lip = lip;
            Focusable = true;
            Cursor = new Cursor(StandardCursorType.Hand);
            // Pixel perfect: no edge feathering, so the face and the type land
            // on the same grid. Fully qualified: the engine has its own
            // RenderOptions and it wins the name here.
            Avalonia.Media.RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        }

        /// <summary>The label, for a button whose word changes with the face it is on.</summary>
        public string Text
        {
            get => _text;
            set
            {
                if (_text == value)
                {
                    return;
                }
                _text = value;
                InvalidateMeasure();
                InvalidateVisual();
            }
        }

        /// <summary>
        /// Change face without rebuilding. A tab strip swaps colours on every
        /// press, and a control rebuilt on every press loses the spring it was
        /// in the middle of.
        /// </summary>
        public void Wear(Deck.Face face, bool selected)
        {
            if (_face == face && _selected == selected)
            {
                return;
            }
            _face = face;
            _selected = selected;
            InvalidateVisual();
        }

        /// <summary>Whether this is the tab that is up: the wedge over it says so.</summary>
        public bool Selected
        {
            get => _selected;
            set
            {
                if (_selected == value)
                {
                    return;
                }
                _selected = value;
                InvalidateVisual();
            }
        }

        /// <summary>This button's own em: the frame's, times its font size.</summary>
        private double Size => Deck.GetEm(this) * _sizeEms;

        private double KeyGap => Size * 0.5;

        private double KeyWidth(double size)
        {
            if (KeyCap.Length == 0)
            {
                return 0;
            }
            double cap = size * 0.5;
            double text = DeckText.Run(KeyCap, Deck.Body(bold: true), cap,
                GuiTheme.AccentBrush).Width;
            return Math.Max(cap * 1.5, text + cap * 0.7);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            double size = Size;
            if (Glyph != null)
            {
                return new Size(
                    Math.Round(size * (GlyphEms.Width + _padXEms * 2)),
                    Math.Round(size * (GlyphEms.Height + _padYEms * 2)));
            }
            double width = DeckText.MeasureTracked(_text, Deck.Label(), size,
                DeckText.LabelTracking) + size * _padXEms * 2;
            if (KeyCap.Length > 0)
            {
                width += KeyGap + KeyWidth(size);
            }
            double line = DeckText.Run("Hg", Deck.Label(), size, GuiTheme.TextBrush).Height;
            // The face alone. The edge is a `box-shadow`, and a shadow is
            // outside the box: a row of buttons lines up on the bottom of the
            // *faces* and the edges hang into the gap under them. Counting the
            // edge here instead pushed everything below a tab strip down by
            // six points and everything below a foot by another six, which is
            // most of what "close, but everything is a bit low" was.
            return new Size(Math.Min(Math.Round(width), availableSize.Width),
                Math.Round(line + size * _padYEms * 2));
        }

        protected override void OnPointerEntered(PointerEventArgs e)
        {
            _popTarget = 1.055;
            StartHop();
            InvalidateVisual();
            base.OnPointerEntered(e);
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            _popTarget = 1;
            _tiltTarget = 0;
            _hop.Reset();
            InvalidateVisual();
            base.OnPointerExited(e);
        }

        /// <summary>
        /// Run the word once, from the beginning.
        ///
        /// Restarted rather than resumed: the reference declares one
        /// iteration on <c>:hover</c>, so leaving and coming back plays it
        /// again, and a pointer that crosses the same button twice should not
        /// get half a hop the second time.
        /// </summary>
        private void StartHop()
        {
            if (Deck.Still)
            {
                return;
            }
            _hop.Restart();
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            if (_tap.Down && _tap.Moved(e, this))
            {
                InvalidateVisual();
            }
            if (IsPointerOver)
            {
                double half = Bounds.Width / 2;
                if (half > 0)
                {
                    double dx = (e.GetPosition(this).X - half) / half;
                    _tiltTarget = Math.Clamp(dx, -1, 1) * MaxTilt;
                    InvalidateVisual();
                }
            }
            base.OnPointerMoved(e);
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            _tap.Press(e, this);
            Focus();
            e.Pointer.Capture(this);
            e.Handled = true;
            InvalidateVisual();
            base.OnPointerPressed(e);
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            bool tapped = _tap.Release(e, this);
            InvalidateVisual();
            if (tapped)
            {
                e.Handled = true;
                Click?.Invoke(this, EventArgs.Empty);
            }
            base.OnPointerReleased(e);
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            _tap.Cancel();
            _popTarget = 1;
            _tiltTarget = 0;
            InvalidateVisual();
            base.OnPointerCaptureLost(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Enter || e.Key == Key.Space)
            {
                e.Handled = true;
                Click?.Invoke(this, EventArgs.Empty);
                return;
            }
            base.OnKeyDown(e);
        }

        protected override void OnGotFocus(FocusChangedEventArgs e)
        {
            // Unspecified is what a programmatic Focus() reports, and every
            // screen here focuses something as it opens; that is the keyboard
            // arriving as far as the player is concerned, so it rings. A
            // pointer does not.
            // A browser's rule, not "did something focus me": the ring
            // follows whichever device the player is currently driving with,
            // so a panel that focuses its Back button as it opens rings it
            // when Tab opened the panel and does not when a click did.
            _ringVisible = e.NavigationMethod != NavigationMethod.Pointer
                && Deck.KeyboardDriving;
            _popTarget = 1.055;
            // `:focus-visible` carries the same rule as `:hover` there, which
            // is what keeps the keyboard and the mouse the same screen.
            StartHop();
            InvalidateVisual();
            base.OnGotFocus(e);
        }

        protected override void OnLostFocus(FocusChangedEventArgs e)
        {
            _ringVisible = false;
            if (!IsPointerOver)
            {
                _popTarget = 1;
                _hop.Reset();
            }
            InvalidateVisual();
            base.OnLostFocus(e);
        }

        /// <summary>
        /// Step the spring by real time rather than by frame, so the motion is
        /// the same whether this is drawn at 60Hz in the game window or once
        /// by <see cref="UiCapture"/>. A capture therefore lands on the
        /// resting pose, which is the one worth photographing -- and the idle
        /// bob starts at zero for the same reason.
        /// </summary>
        private bool Settle()
        {
            TimeSpan now = _clock.Elapsed;
            double dt = Math.Min(0.05, (now - _last).TotalSeconds);
            _last = now;
            if (Deck.Still)
            {
                _pop = _popTarget;
                _popVelocity = 0;
                _tilt = _tiltTarget;
                return false;
            }
            if (dt <= 0)
            {
                return false;
            }
            double accel = (_popTarget - _pop) * Stiffness - _popVelocity * Damping;
            _popVelocity += accel * dt;
            _pop += _popVelocity * dt;
            _tilt += (_tiltTarget - _tilt) * Math.Min(1, dt * 14);
            bool moving = Math.Abs(_popTarget - _pop) > 0.0005
                || Math.Abs(_popVelocity) > 0.0005
                || Math.Abs(_tiltTarget - _tilt) > 0.05;
            if (!moving)
            {
                _pop = _popTarget;
                _popVelocity = 0;
                _tilt = _tiltTarget;
            }
            return moving;
        }

        /// <summary>
        /// Where character <paramref name="index"/> is in its hop: a lift in
        /// points and a lean in degrees.
        ///
        /// Two eased segments, not one curve through three points: CSS applies
        /// the timing function <i>between each pair of keyframes</i>, so the
        /// rise to 38% and the fall back are each a whole spring. Running one
        /// bezier over the pair instead loses the snap at the top, which is
        /// the part anybody actually sees.
        /// </summary>
        private (double Lift, double Degrees) Hop(int index, double size)
        {
            if (!_hop.IsRunning)
            {
                return (0, 0);
            }
            double t = _hop.Elapsed.TotalSeconds - index * HopStagger;
            if (t <= 0)
            {
                return (0, 0);
            }
            if (t >= HopSeconds)
            {
                return (0, 0);
            }
            double phase = t / HopSeconds;
            double amount = phase < HopPeak
                ? Deck.Spring(phase / HopPeak)
                : 1 - Deck.Spring((phase - HopPeak) / (1 - HopPeak));
            double tilt = (index % 2 == 0 ? -HopTilt : HopTilt) * amount;
            return (-size * HopLift * amount, tilt);
        }

        /// <summary>
        /// Is any character still moving? The last one starts
        /// <c>(characters - 1) * HopStagger</c> late and runs for
        /// <see cref="HopSeconds"/>, and the clock is stopped at the end so a
        /// resting button asks for no more frames.
        /// </summary>
        private bool Hopping()
        {
            if (!_hop.IsRunning)
            {
                return false;
            }
            int characters = 0;
            foreach (char c in _text)
            {
                if (c != ' ')
                {
                    characters++;
                }
            }
            double total = HopSeconds + Math.Max(0, characters - 1) * HopStagger;
            if (_hop.Elapsed.TotalSeconds >= total)
            {
                _hop.Reset();
                return false;
            }
            return true;
        }

        /// <summary>The bob, in points. Zero unless this is the one idle button.</summary>
        private double Bob()
        {
            if (!Idle || Deck.Still || IsPointerOver || (IsFocused && _ringVisible))
            {
                return 0;
            }
            // `@keyframes bob { 0%,100% { --ty: 0 } 50% { --ty: -2.5px } }`
            // over 3.4s, eased. A raised cosine is the same curve to the eye
            // and is one call.
            double phase = _clock.Elapsed.TotalSeconds % 3.4 / 3.4;
            return -1.25 * (1 - Math.Cos(phase * Math.PI * 2));
        }

        /// <summary>Whether a frame has already been asked for and not yet drawn.</summary>
        private bool _framePending;

        /// <summary>
        /// Ask for the next frame of an animation, from inside the pass
        /// drawing this one.
        ///
        /// <b>Not <c>InvalidateVisual</c> directly.</b> Avalonia's compositing
        /// renderer walks a list of dirty visuals and refuses to have one
        /// added while it is walking it: the call throws "Visual was
        /// invalidated during the render pass", the exception comes out of the
        /// dispatcher rather than out of this method, and what the player sees
        /// is the launcher failing on its first frame -- which on a Windows
        /// build, a GUI binary with no console, is a program that starts and
        /// shows nothing at all.
        ///
        /// Handed to <see cref="UiSurface.RequestFrame"/> instead, which runs
        /// it at the top of the next tick -- between passes, and on a surface
        /// it has marked dirty. One at a time: a button that bobs would
        /// otherwise queue an operation per frame per button, and the queue is
        /// what the renderer drains.
        ///
        /// <b>The surface has to be told, not just the control.</b>
        /// <c>InvalidateVisual</c> alone marks this visual dirty inside
        /// Avalonia and says nothing to the thing that decides whether the
        /// screens are rasterised at all: an untouched surface is redrawn only
        /// on its backstop, which is 50 ms while something has happened
        /// recently and 250 ms once it has been still for three seconds. A
        /// spring stepped at four frames a second, with <see cref="Settle"/>
        /// clamping each step to 50 ms, runs at a fifth of its real speed --
        /// which is a fifth-of-a-second pop taking a second to arrive, and is
        /// what "the buttons answer a second late" was. Scrolling never showed
        /// it because a wheel event invalidates the surface itself.
        /// </summary>
        /// <param name="idling">
        /// True when the bob is the only thing left moving. It is the one
        /// animation on these screens that never stops, and a redraw here is
        /// the whole window rasterised on the CPU -- so it gets a slower
        /// cadence of its own. See UiSurface.IdleAnimGap.
        /// </param>
        private void RequestAnotherFrame(bool idling = false)
        {
            if (_framePending)
            {
                return;
            }
            _framePending = true;
            Deck.NextFrame(this, () =>
            {
                _framePending = false;
                InvalidateVisual();
            }, idling);
        }

        public override void Render(DrawingContext context)
        {
            bool moving = Settle();
            bool down = _tap.Down;
            bool on = IsEnabled;

            double lip = down ? LipPressed : _lip;
            // The face drops by exactly what the lip loses, so its bottom edge
            // stays where the lip's bottom edge was.
            double drop = _lip - lip + Bob();

            double w = Bounds.Width;
            double h = Bounds.Height;
            if (w <= 0 || h <= 0)
            {
                return;
            }

            double size = Size;
            double radius = size * RadiusEms;
            double scale = down ? 1 : _pop;
            Color fill = _face.Fill, lipColour = _face.Lip;
            if (!on)
            {
                // `filter: grayscale(.7) brightness(.55)`.
                fill = DeckPaint.Brightness(DeckPaint.Saturate(fill, 0.3), 0.55);
                lipColour = DeckPaint.Brightness(DeckPaint.Saturate(lipColour, 0.3), 0.55);
            }
            else if (IsPointerOver || (IsFocused && _ringVisible))
            {
                // `filter: brightness(1.22) saturate(1.15)`. Not a blend
                // towards white: that washes the hue out, and the hue is what
                // says which of five things the button is.
                fill = DeckPaint.Saturate(DeckPaint.Brightness(fill, 1.22), 1.15);
                lipColour = DeckPaint.Saturate(DeckPaint.Brightness(lipColour, 1.22), 1.15);
            }

            using (context.PushTransform(
                Avalonia.Matrix.CreateTranslation(-w / 2, -h / 2)
                * Avalonia.Matrix.CreateScale(scale, scale)
                * Avalonia.Matrix.CreateRotation(_tilt * Math.PI / 180 * 0.06)
                * Avalonia.Matrix.CreateTranslation(w / 2, h / 2 + drop)))
            {
                var faceRect = new RoundedRect(new Rect(0, 0, w, h), radius);

                // The wedge over the tab that is up, drawn under the face so
                // it reads as part of it: the strip has no other way to say
                // which page is showing once the dots and the arrows are gone.
                // `.tab[aria-selected]::before`, a .4em triangle .72em above.
                if (_selected)
                {
                    double half = size * 0.4;
                    double top = -size * 0.72;
                    var wedge = new StreamGeometry();
                    using (StreamGeometryContext g = wedge.Open())
                    {
                        double mid = Math.Round(w / 2);
                        g.BeginFigure(new Point(mid - half, top), true);
                        g.LineTo(new Point(mid + half, top));
                        g.LineTo(new Point(mid, top + half));
                        g.EndFigure(true);
                    }
                    context.DrawGeometry(new SolidColorBrush(fill), null, wedge);
                }

                // The face and its shadows, exactly as the reference declares
                // them: `0 var(--lip) 0 var(--face-lip), 0 calc(var(--lip) +
                // 4px) 12px rgba(0,0,0,.55)`, with `0 0 0 2px var(--accent)`
                // in front of both while it has the keyboard.
                //
                // The edge is a box-shadow there and is one here. It used to
                // be a second rounded rect drawn behind the face and the cast
                // was three translucent rects stepped outward, which is a fair
                // likeness of a blur and the wrong object for a ring: a `0 0 0
                // 2px` ring is a *spread*, and drawing it as a stroke puts
                // half of it inside the face and takes two points off the
                // label.
                var cast = new List<BoxShadow>
                {
                    Deck.Shadow(0, lip, 0, 0, lipColour),
                    Deck.Shadow(0, lip + 4, 12, 0, Deck.Fade(0, 0.55))
                };
                bool ring = IsFocused && _ringVisible;
                BoxShadow first = ring
                    ? Deck.Shadow(0, 0, 0, 2, GuiTheme.Accent)
                    : cast[0];
                if (ring)
                {
                    context.DrawRectangle(new SolidColorBrush(fill), null, faceRect,
                        new BoxShadows(first, cast.ToArray()));
                }
                else
                {
                    context.DrawRectangle(new SolidColorBrush(fill), null, faceRect,
                        new BoxShadows(first, new[] { cast[1] }));
                }

                // One hairline of light along the top, not a gradient over the
                // whole face: a gradient makes it a web button.
                var bevel = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                    GradientStops =
                    {
                        new GradientStop(Color.FromArgb(0x17, 255, 255, 255), 0),
                        new GradientStop(Color.FromArgb(0, 255, 255, 255), 1)
                    }
                };
                context.DrawRectangle(bevel, null,
                    new RoundedRect(new Rect(1, 1, w - 2, h * 0.4), radius - 1));

                IBrush ink = on ? GuiTheme.TextBrush : GuiTheme.TextDimBrush;
                if (Glyph != null)
                {
                    IBrush paint = ink;
                    if (GlyphColour is Color own)
                    {
                        Color tint = own;
                        if (!on)
                        {
                            tint = DeckPaint.Brightness(DeckPaint.Saturate(tint, 0.3), 0.55);
                        }
                        else if (IsPointerOver || (IsFocused && _ringVisible))
                        {
                            tint = DeckPaint.Saturate(DeckPaint.Brightness(tint, 1.22), 1.15);
                        }
                        paint = new SolidColorBrush(tint);
                    }
                    double gw = size * GlyphEms.Width, gh = size * GlyphEms.Height;
                    Glyph(context, new Rect(Math.Round((w - gw) / 2),
                        Math.Round((h - gh) / 2), gw, gh), paint);
                    DrawTip(context, w, size,
                        IsPointerOver || (IsFocused && _ringVisible) || Deck.Phone);
                    bool glyphBusy = moving || _hop.IsRunning || _tipMoving;
                    if ((glyphBusy || Idle) && !Deck.Still)
                    {
                        RequestAnotherFrame(idling: !glyphBusy);
                    }
                    return;
                }
                double keyWidth = KeyWidth(size);
                double labelWidth = DeckText.MeasureTracked(_text, Deck.Label(), size,
                    DeckText.LabelTracking);
                double content = labelWidth + (keyWidth > 0 ? KeyGap + keyWidth : 0);
                double x = Math.Round((w - content) / 2);
                bool hopping = Hopping();
                DeckText.DrawTracked(context, _text, Deck.Label(), size, ink, x, 0, h,
                    DeckText.LabelTracking,
                    hopping ? i => Hop(i, size) : null);
                if (keyWidth > 0 && on)
                {
                    DeckChip.DrawKey(context, KeyCap, size,
                        Math.Round(x + labelWidth + KeyGap), h);
                }
            }

            // Not while the screen is being photographed: a control that asks
            // for another frame from inside a render pass is one a
            // RenderTargetBitmap refuses to finish. See Deck.Still.
            bool busy = moving || _hop.IsRunning || _tipMoving;
            if ((busy || Idle) && !Deck.Still)
            {
                RequestAnotherFrame(idling: !busy);
            }
        }

        /// <summary>How far the tip has arrived, 0 to 1.</summary>
        private double _tipShow;

        /// <summary>
        /// Whether it is still on its way. Not "is it showing": on a
        /// touchscreen the tip is up for as long as the mark is, and a control
        /// that asks for another frame while anything is visible would redraw
        /// the whole launcher for ever.
        /// </summary>
        private bool _tipMoving;

        /// <summary>
        /// Step the tip and draw it. Outside the button's own transform, so a
        /// hovered button's lean does not tip the label above it over with it.
        /// </summary>
        private void DrawTip(DrawingContext context, double w, double size, bool hot)
        {
            if (Tip.Length == 0)
            {
                _tipMoving = false;
                return;
            }
            double target = hot ? 1 : 0;
            if (Deck.Still)
            {
                _tipShow = target;
            }
            else
            {
                // `.16s` to fade, on the settle curve; near enough at this
                // size to move a sixth of the way a frame.
                _tipShow += (target - _tipShow) * 0.25;
                if (Math.Abs(target - _tipShow) < 0.004)
                {
                    _tipShow = target;
                }
            }
            _tipMoving = !Deck.Still && Math.Abs(target - _tipShow) > 0.0001;
            DeckHeart.DrawTip(context, Tip, w, size, _tipShow, _tipShow);
        }
    }

    /// <summary>
    /// The two filters the reference puts on a hovered or a disabled face,
    /// done as arithmetic rather than as a blend towards a colour.
    ///
    /// <c>Shade</c> -- what this replaces -- moves a colour towards white,
    /// which lightens and desaturates at once. The reference brightens
    /// <i>and</i> saturates, so brass gets warmer rather than paler, and on a
    /// screen with five faces on it the hue is what says which button is which.
    /// </summary>
    internal static class DeckPaint
    {
        public static Color Brightness(Color c, double k)
        {
            return Color.FromArgb(c.A, Clamp(c.R * k), Clamp(c.G * k), Clamp(c.B * k));
        }

        /// <summary>The sRGB luma matrix, which is what a CSS saturate() filter is.</summary>
        public static Color Saturate(Color c, double s)
        {
            double luma = c.R * 0.2126 + c.G * 0.7152 + c.B * 0.0722;
            return Color.FromArgb(c.A,
                Clamp(luma + (c.R - luma) * s),
                Clamp(luma + (c.G - luma) * s),
                Clamp(luma + (c.B - luma) * s));
        }

        private static byte Clamp(double v) => (byte)Math.Clamp(v, 0, 255);
    }
}
#endif
