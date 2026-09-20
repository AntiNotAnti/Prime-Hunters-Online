#if MPHREAD_AVALONIA
using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace MphRead.Mods.Launcher.Gui
{
    /// <summary>
    /// A label and the thing it names, in one small slab.
    ///
    /// The command bar's ends: what profile this is on the left, and on the
    /// right the one thing that is not a menu entry. They are not buttons --
    /// there is no lip and nothing to press on the profile one -- which is the
    /// point: the row reads as three faces between two pieces of information
    /// rather than five things to click.
    /// </summary>
    internal sealed class DeckChip : Control
    {
        private string _key;
        private string _value;

        /// <summary>
        /// The dim word above the value, or nothing.
        ///
        /// Empty draws the value alone, which is the reference's `.code` --
        /// the badge at the top of the `.side` drawer is a bare chip there
        /// ("mp3", "host", a flag), not a labelled one, and a key printed over
        /// it was this control answering a question the drawer had not asked.
        /// </summary>
        public string Label
        {
            get => _key;
            set
            {
                string upper = value.ToUpperInvariant();
                if (_key == upper)
                {
                    return;
                }
                _key = upper;
                InvalidateMeasure();
                InvalidateVisual();
            }
        }

        /// <summary>
        /// What the chip reads, for the one caller that refills a chip rather
        /// than building a new one: the play screen's drawer, which carries
        /// whichever map was just pressed.
        /// </summary>
        public string Text
        {
            get => _value;
            set
            {
                string upper = value.ToUpperInvariant();
                if (_value == upper)
                {
                    return;
                }
                _value = upper;
                InvalidateMeasure();
                InvalidateVisual();
            }
        }

        public DeckChip(string key, string value)
        {
            _key = key.ToUpperInvariant();
            _value = value.ToUpperInvariant();
            IsHitTestVisible = false;
            Avalonia.Media.RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        }

        private static FormattedText Key(string s)
        {
            return new FormattedText(s, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, GuiTheme.Face(false), 10,
                GuiTheme.TextDimBrush);
        }

        private static FormattedText Value(string s)
        {
            return new FormattedText(s, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(GuiTheme.PixelSemi, FontStyle.Normal, FontWeight.Normal),
                GuiTheme.PixelSize(17), GuiTheme.TextBrush);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            FormattedText v = Value(_value);
            if (_key.Length == 0)
            {
                return new Size(v.Width + 22, v.Height + 14);
            }
            FormattedText k = Key(_key);
            return new Size(Math.Max(k.Width, v.Width) + 22,
                k.Height + v.Height + 18);
        }

        /// <summary>
        /// The keycap on a button's face: the reference's <c>.key</c>.
        ///
        /// A well sunk into the face with the key in it, at half the label's
        /// size, in the <i>body</i> face rather than the display one -- a
        /// keycap is read, not scanned, and "ESC" set in a pixel font at seven
        /// points is three smudges. Sunk rather than tinted: on brass a chip at
        /// a third opacity reads as a lighter brass rectangle, not as a hole.
        /// </summary>
        public static void DrawKey(DrawingContext context, string key, double labelSize,
            double x, double height)
        {
            if (key.Length == 0)
            {
                return;
            }
            double size = labelSize * 0.5;
            FormattedText text = DeckText.Run(key, Deck.Body(bold: true), size,
                GuiTheme.AccentBrush);
            double w = Math.Max(size * 1.5, text.Width + size * 0.7);
            double h = Math.Max(size * 1.5, text.Height);
            var box = new Rect(x, Math.Round((height - h) / 2), w, h);
            context.DrawRectangle(new SolidColorBrush(Color.FromArgb(107, 0, 0, 0)), null,
                new RoundedRect(box, size * 0.3));
            context.DrawText(text, new Point(
                Math.Round(box.X + (w - text.Width) / 2),
                Math.Round(box.Y + (h - text.Height) / 2)));
        }

        public override void Render(DrawingContext context)
        {
            double w = Bounds.Width, h = Bounds.Height - 4;
            FormattedText v = Value(_value);

            context.DrawRectangle(new SolidColorBrush(Color.FromArgb(210, 18, 21, 28)), null,
                new RoundedRect(new Rect(0, 0, w, h), 7));
            context.DrawRectangle(new SolidColorBrush(Color.FromArgb(128, 0, 0, 0)), null,
                new RoundedRect(new Rect(0, h, w, 4), 2));

            if (_key.Length == 0)
            {
                // `.code`: the value alone, centred in the chip.
                context.DrawText(v, new Point(Math.Round((w - v.Width) / 2),
                    Math.Round((h - v.Height) / 2)));
                return;
            }
            FormattedText k = Key(_key);
            context.DrawText(k, new Point(Math.Round((w - k.Width) / 2), 5));
            // The value sits in its own well, the way the profile chip's does
            // on the screen this is from.
            double vy = Math.Round(k.Height + 7);
            context.DrawRectangle(new SolidColorBrush(GuiTheme.PanelLight), null,
                new RoundedRect(new Rect(5, vy - 2, w - 10, v.Height + 4), 4));
            context.DrawText(v, new Point(Math.Round((w - v.Width) / 2), vy));
        }
    }

    /// <summary>
    /// The one thing in the bar that is not a menu entry: a pixel heart that
    /// opens the support page.
    ///
    /// Drawn from rows rather than a path, each row centred on the same axis
    /// by construction -- a heart traced by hand comes out with one lobe wider
    /// than the other and nobody sees it until it is on a screenshot.
    /// </summary>
    internal sealed class DeckHeart : Control
    {
        public event EventHandler? Click;

        private readonly Tap _tap = new();

        /// <summary>
        /// The pixel heart, drawn into a box, for whoever is carrying it.
        ///
        /// It is a `.btn` in the reference -- `class="btn f-rust heart"` --
        /// with an SVG where the word goes, so the mark is now a
        /// <see cref="DeckButton"/> with this as its glyph rather than a
        /// control of its own. As its own control it had no bevel, no spring,
        /// no lean towards the pointer and no press that travels by the lip it
        /// loses, which beside QUIT read as a different program's button.
        /// </summary>
        public static void DrawHeart(DrawingContext context, Rect area, IBrush ink)
        {
            // Whole pixels, so the heart stays a pixel heart rather than a
            // smoothed one. The grid is twelve wide and eight tall.
            double cell = Math.Max(1, Math.Floor(Math.Min(area.Width / 12, area.Height / 8)));
            double ox = Math.Round(area.X + (area.Width - 12 * cell) / 2);
            double oy = Math.Round(area.Y + (area.Height - 8 * cell) / 2);
            foreach ((int y, int x, int w) in _rows)
            {
                context.FillRectangle(ink, new Rect(ox + x * cell, oy + y * cell, w * cell, cell));
            }
        }

        /// <summary>y, x, width -- on a 12-wide grid, mirrored about x = 6.</summary>
        private static readonly (int Y, int X, int W)[] _rows =
        {
            (0, 2, 3), (0, 7, 3), (1, 1, 10), (2, 1, 10), (3, 1, 10),
            (4, 2, 8), (5, 3, 6), (6, 4, 4), (7, 5, 2)
        };

        static DeckHeart()
        {
            AffectsMeasure<DeckHeart>(Deck.EmProperty);
            AffectsRender<DeckHeart>(Deck.EmProperty);
        }

        public DeckHeart()
        {
            Focusable = true;
            Cursor = new Cursor(StandardCursorType.Hand);
            Avalonia.Media.RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        }

        /// <summary>
        /// The mark is a <c>.btn</c> like every other: <c>font-size: 1.55em</c>
        /// of the frame, and its own padding and glyph measured against that.
        ///
        /// It was a flat 52 x 44, which is the same mistake
        /// <see cref="DeckWordmark"/> had -- a number that is only right at one
        /// window size, on a screen where everything beside it grows with the
        /// frame. The reference's own numbers, all of them in this em:
        /// <c>padding: .7em .9em</c>, a glyph <c>1.9em</c> by <c>1.27em</c>,
        /// and a five-point edge that stays five points because
        /// <c>--lip: 5px</c> is a length there too.
        /// </summary>
        private double Em => Deck.Px(this, 1.55);

        /// <summary>The solid edge under the face. `--lip: 5px`.</summary>
        private const double Lip = 5;

        private const double GlyphWide = 1.9, GlyphTall = 1.27;
        private const double PadX = 0.9, PadY = 0.7;

        /// <summary>
        /// What the mark says when the pointer is on it: the reference's
        /// <c>data-tip</c>, drawn rather than left to a platform tooltip.
        ///
        /// The reference draws its own for a stated reason -- a browser's own
        /// tip arrives about a second late and in the operating system's
        /// colours, which on a screen made of painted controls is the one
        /// thing on it from somewhere else. There was none here at all, so the
        /// only hint that the mark was a link was the hand cursor.
        /// </summary>
        public string Tip { get; set; } = "Support this project <3";

        /// <summary>`transition: opacity .16s, translate .24s var(--spring)`.</summary>
        private const double TipFade = 0.16, TipRise = 0.24;

        private readonly System.Diagnostics.Stopwatch _tip = new();
        private bool _tipShown;
        private bool _framePending;

        /// <summary>
        /// How far along the tip is, forwards on hover and backwards off it.
        /// One clock either way, so leaving mid-appearance rewinds from where
        /// it got to rather than snapping to the end and fading from there.
        /// </summary>
        private double TipAmount(double seconds)
        {
            double t = _tip.IsRunning ? _tip.Elapsed.TotalSeconds : double.MaxValue;
            double progress = seconds <= 0 ? 1 : Math.Clamp(t / seconds, 0, 1);
            return _tipShown ? progress : 1 - progress;
        }

        private void TipTo(bool shown)
        {
            if (_tipShown == shown)
            {
                return;
            }
            _tipShown = shown;
            if (Deck.Still)
            {
                return;
            }
            _tip.Restart();
            AskFrame();
        }

        private void AskFrame()
        {
            if (_framePending || Deck.Still)
            {
                return;
            }
            _framePending = true;
            Deck.NextFrame(this, () =>
            {
                _framePending = false;
                InvalidateVisual();
            });
        }

        /// <summary>
        /// The tip, above the mark and hanging off its right edge.
        ///
        /// Drawn from inside the mark's own Render and outside its bounds,
        /// which is what the lips under every button here already do -- a
        /// control's drawing is not clipped to its box unless something asks
        /// for that.
        /// </summary>
        private void Tip_(DrawingContext context, double w, double em)
        {
            DrawTip(context, Tip, w, em, TipAmount(TipFade), Deck.Spring(TipAmount(TipRise)));
            if (_tip.IsRunning && _tip.Elapsed.TotalSeconds >= Math.Max(TipFade, TipRise))
            {
                _tip.Reset();
            }
            else if (_tip.IsRunning)
            {
                AskFrame();
            }
        }

        /// <summary>
        /// The tip above a control, hanging off its right edge: the
        /// reference's `.heart::before`.
        ///
        /// Static, because the mark that carries it is a
        /// <see cref="DeckButton"/> now rather than a control of its own, and
        /// the drawing is the same either way. <paramref name="show"/> is the
        /// opacity and <paramref name="risen"/> is how much of the four-point
        /// slide it has done.
        /// </summary>
        public static void DrawTip(DrawingContext context, string tip, double w, double em,
            double show, double risen)
        {
            if (show <= 0.001 || tip.Length == 0)
            {
                return;
            }
            double size = GuiTheme.PixelSize(em * 0.4);
            FormattedText text = DeckText.Run(tip, Deck.Body(bold: false), size,
                new SolidColorBrush(GuiTheme.Text, show));
            double padX = Math.Round(size * 0.7), padY = Math.Round(size * 0.45);
            double tw = Math.Round(text.Width + padX * 2);
            double th = Math.Round(text.Height + padY * 2);
            // `bottom: calc(100% + .8em); right: 0`, and it slides the last
            // four points up as it arrives.
            double slide = 4 * (1 - risen);
            double x = w - tw;
            double y = -th - Math.Round(em * 0.8) + slide;
            var box = new RoundedRect(new Rect(x, y, tw, th), Math.Round(size * 0.35));
            context.DrawRectangle(new SolidColorBrush(GuiTheme.Panel, show),
                new Pen(new SolidColorBrush(GuiTheme.Edge, show), 1), box,
                new BoxShadows(
                    Deck.Shadow(0, 4, 0, 0, Color.FromArgb(
                        (byte)Math.Round(255 * show), GuiTheme.PanelDeep.R,
                        GuiTheme.PanelDeep.G, GuiTheme.PanelDeep.B)),
                    new[] { Deck.Shadow(0, 8, 16, 0, Deck.Fade(0, 0.6 * show)) }));
            context.DrawText(text, new Point(Math.Round(x + padX), Math.Round(y + padY)));
        }

        /// <summary>
        /// The face alone, like <see cref="DeckButton"/>'s.
        ///
        /// The lip was counted in, and that is the misalignment: it is a
        /// `box-shadow` in the reference and a shadow is outside the box, so
        /// a row aligned on its items' bottoms lines up the *faces* and lets
        /// every lip hang into the gap below. Counting this one in bottom-
        /// aligned the mark's lip against the buttons' faces, which left the
        /// mark five points high beside a row it is supposed to sit in.
        /// </summary>
        protected override Size MeasureOverride(Size availableSize)
        {
            double em = Em;
            return new Size(Math.Round(em * (GlyphWide + PadX * 2)),
                Math.Round(em * (GlyphTall + PadY * 2)));
        }

        protected override void OnPointerEntered(PointerEventArgs e)
        {
            TipTo(true);
            InvalidateVisual();
            base.OnPointerEntered(e);
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            TipTo(false);
            InvalidateVisual();
            base.OnPointerExited(e);
        }

        protected override void OnGotFocus(FocusChangedEventArgs e)
        {
            // `:focus-visible` carries the tip too, so the mark is reachable
            // without a pointer.
            TipTo(Deck.KeyboardDriving);
            InvalidateVisual();
            base.OnGotFocus(e);
        }

        protected override void OnLostFocus(FocusChangedEventArgs e)
        {
            if (!IsPointerOver)
            {
                TipTo(false);
            }
            InvalidateVisual();
            base.OnLostFocus(e);
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
            if (_tap.Release(e, this))
            {
                e.Handled = true;
                Click?.Invoke(this, EventArgs.Empty);
            }
            InvalidateVisual();
            base.OnPointerReleased(e);
        }

        public override void Render(DrawingContext context)
        {
            double w = Bounds.Width, h = Bounds.Height;
            if (w <= 0 || h <= 0)
            {
                return;
            }
            bool hot = IsPointerOver || IsFocused;
            double em = Em;
            Tip_(context, w, em);

            context.DrawRectangle(new SolidColorBrush(
                hot ? Color.FromRgb(0x84, 0x42, 0x42) : Color.FromRgb(0x6b, 0x36, 0x36)), null,
                new RoundedRect(new Rect(0, 0, w, h), em * 0.55));
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x38, 0x1b, 0x1b)), null,
                new RoundedRect(new Rect(0, h, w, Lip), em * 0.3));

            // 12 x 8 cells, centred, at whole-pixel scale so the heart stays a
            // pixel heart rather than a smoothed one. The cell comes off the
            // glyph the reference asks for -- `width: 1.9em` over a twelve-wide
            // viewBox -- not off the face it sits on, which is how the mark
            // ended up filling its own button.
            double cell = Math.Max(1, Math.Floor(em * GlyphWide / 12));
            double ox = Math.Round((w - 12 * cell) / 2);
            double oy = Math.Round((h - 8 * cell) / 2);
            var ink = new SolidColorBrush(hot
                ? Color.FromRgb(0xff, 0xd0, 0xd0) : Color.FromRgb(0xe8, 0xa0, 0xa0));
            foreach ((int y, int x, int rw) in _rows)
            {
                context.FillRectangle(ink,
                    new Rect(ox + x * cell, oy + y * cell, rw * cell, cell));
            }
        }
    }
}
#endif
