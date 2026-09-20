#if MPHREAD_AVALONIA
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace MphRead.Mods.Launcher.Gui
{
    /// <summary>
    /// One map, as a card with the map on it: the reference's <c>.pcard</c>.
    ///
    /// <para>
    /// The offline face was a list of names down the left with a preview of
    /// whichever one was highlighted in the corner. That is a file picker. The
    /// reference makes the *picture* the control -- every map at once, square,
    /// its archive code in the corner, and a word along the bottom that says
    /// whether this is the one -- because the answer to "which map is that" is
    /// the map, and a column of truncated names beside one thumbnail asks the
    /// player to remember what the other twenty look like.
    /// </para>
    ///
    /// <para>
    /// Three layers under the type, in the reference's own order: the render,
    /// a drift of two coloured gradients over it, and a scrim that darkens the
    /// bottom two thirds so the word can be read. The drift is drawn at a
    /// fixed phase per card rather than animated -- it is a 26-second cycle on
    /// a soft-light gradient, worth about nothing to the eye, and twenty-one
    /// of them animating would pin the whole surface at the frame rate for
    /// ever. See the note in LAUNCHER-DESIGN.md about what a redraw costs
    /// here.
    /// </para>
    /// </summary>
    internal sealed class DeckTile : Control
    {
        public event EventHandler? Click;

        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private TimeSpan _last;

        /// <summary>
        /// Hover, as <see cref="DeckGrid"/> resolved it rather than as the
        /// toolkit's hit test answered it. The two disagree here -- see the
        /// grid's note -- so <c>IsPointerOver</c> is the wrong card.
        /// </summary>
        private bool _over;

        /// <summary>The room this card stands for, and what the tag says.</summary>
        public string RoomKey { get; }

        public string Code { get; }

        /// <summary>
        /// Optional image supplied by non-map cards, such as Replay Studio.
        /// The room render remains the fallback when this is null or missing.
        /// </summary>
        public string? ImagePath { get; set; }

        /// <summary>Optional payload for a card owned by a higher-level picker.</summary>
        public object? Choice { get; set; }

        /// <summary>The line along the bottom. Empty on a map, used by a clip.</summary>
        public string Blurb { get; set; } = "";

        /// <summary>What the word along the bottom says when this is not the one.</summary>
        public string Verb { get; set; } = "Select";

        public string ChosenVerb { get; set; } = "Selected";

        /// <summary>
        /// How many people have picked this map, and whether it is winning.
        ///
        /// The reference's `.tally`, `.leader` and `.mine`: a badge in the
        /// corner, an accent ring on whatever is ahead, and a brass word on
        /// your own pick. Negative means this card is not part of a ballot and
        /// draws no badge at all, which is every card on the offline face.
        /// </summary>
        public int Tally { get; set; } = -1;

        public bool Leader { get; set; }

        private bool _chosen;

        public bool Chosen
        {
            get => _chosen;
            set
            {
                if (_chosen == value)
                {
                    return;
                }
                _chosen = value;
                InvalidateVisual();
            }
        }

        /// <summary>`.pcard:hover { --sc: 1.03 }`, and 3.5 degrees of lean.</summary>
        private const double MaxTilt = 3.5;

        private const double Stiffness = 220;
        private const double Damping = 18;

        private double _pop = 1, _popVelocity;
        private double _popTarget = 1;
        private double _tilt, _tiltTarget;
        private double _tiltX, _tiltXTarget;
        private bool _framePending;

        static DeckTile()
        {
            AffectsRender<DeckTile>(Deck.EmProperty);
        }

        public DeckTile(string roomKey, string code)
        {
            RoomKey = roomKey;
            Code = code;
            Focusable = true;
            Cursor = new Cursor(StandardCursorType.Hand);
        }

        private double Em => Deck.GetEm(this);

        /// <summary>
        /// Width over height. One on the offline grid, `16/9` in the results
        /// panel's compact ballot -- where the reference uses the map's own
        /// shape so that a column of cards is a column of pictures rather than
        /// a column of squares.
        /// </summary>
        public double Ratio { get; set; } = 1;

        /// <summary>The grid gives the width; the ratio gives the height.</summary>
        protected override Size MeasureOverride(Size availableSize)
        {
            double side = Double.IsInfinity(availableSize.Width)
                ? Em * 10 : availableSize.Width;
            return new Size(side, Math.Round(side / Math.Max(0.1, Ratio)));
        }

        /// <summary>
        /// Hover and lean, decided by <see cref="DeckGrid"/> and given in this
        /// card's own coordinates.
        ///
        /// The card has no pointer handlers of its own any more: the toolkit
        /// delivers them to the wrong card, so a card that listened lit up
        /// under a pointer that was over its neighbour.
        /// </summary>
        internal void Hover(bool over, Point at)
        {
            _over = over;
            _popTarget = over || (IsFocused && Deck.KeyboardDriving) ? 1.03 : 1;
            if (over && Bounds.Width > 0 && Bounds.Height > 0)
            {
                double halfW = Bounds.Width / 2, halfH = Bounds.Height / 2;
                _tiltTarget = Math.Clamp((at.X - halfW) / halfW, -1, 1) * MaxTilt;
                _tiltXTarget = -Math.Clamp((at.Y - halfH) / halfH, -1, 1) * MaxTilt;
            }
            else
            {
                _tiltTarget = 0;
                _tiltXTarget = 0;
            }
            InvalidateVisual();
        }

        /// <summary>The grid decided this card was tapped.</summary>
        internal void Fire() => Click?.Invoke(this, EventArgs.Empty);

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
            _popTarget = 1.03;
            InvalidateVisual();
            base.OnGotFocus(e);
        }

        protected override void OnLostFocus(FocusChangedEventArgs e)
        {
            if (!_over)
            {
                _popTarget = 1;
            }
            InvalidateVisual();
            base.OnLostFocus(e);
        }

        /// <summary>The same spring <see cref="DeckButton"/> uses, on a bigger object.</summary>
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
                _tiltX = _tiltXTarget;
                return false;
            }
            if (dt <= 0)
            {
                return false;
            }
            double accel = (_popTarget - _pop) * Stiffness - _popVelocity * Damping;
            _popVelocity += accel * dt;
            _pop += _popVelocity * dt;
            double ease = Math.Min(1, dt * 14);
            _tilt += (_tiltTarget - _tilt) * ease;
            _tiltX += (_tiltXTarget - _tiltX) * ease;
            bool moving = Math.Abs(_popTarget - _pop) > 0.0005
                || Math.Abs(_popVelocity) > 0.0005
                || Math.Abs(_tiltTarget - _tilt) > 0.02
                || Math.Abs(_tiltXTarget - _tiltX) > 0.02;
            if (!moving)
            {
                _pop = _popTarget;
                _popVelocity = 0;
                _tilt = _tiltTarget;
                _tiltX = _tiltXTarget;
            }
            return moving;
        }

        private void Ask()
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

        public override void Render(DrawingContext context)
        {
            bool moving = Settle();
            double w = Bounds.Width, h = Bounds.Height;
            if (w <= 0 || h <= 0)
            {
                return;
            }
            double em = Em;
            double radius = em * 0.55;
            bool hot = _over || (IsFocused && Deck.KeyboardDriving);

            using (context.PushTransform(
                Avalonia.Matrix.CreateTranslation(-w / 2, -h / 2)
                * Avalonia.Matrix.CreateScale(_pop, _pop)
                // A rotation about the card's own middle stands in for the
                // reference's two-axis 3D lean: a real perspective divide on a
                // card this size is invisible beside what it costs, and what
                // reads is that the card tips towards the pointer at all.
                * Avalonia.Matrix.CreateRotation((_tilt + _tiltX) * Math.PI / 180 * 0.12)
                * Avalonia.Matrix.CreateTranslation(w / 2, h / 2)))
            {
                var face = new RoundedRect(new Rect(0, 0, w, h), radius);
                Color ring = _chosen || Leader ? GuiTheme.Accent
                    : hot ? Color.FromRgb(0x4a, 0x6f, 0x8c) : GuiTheme.Edge;
                bool raised = hot || _chosen;
                // The shadow, out of the shared cut rather than blurred again.
                // Three shadows, one of them a 26-point blur, times the twenty
                // cards a 1080p grid shows, was 48% of the whole frame --
                // measured, and measured on the Android head too, which is
                // GPU-accelerated and was reported just as sluggish. A blur is
                // expensive wherever it runs; the answer is not to run it. The
                // cut depends on the card's size and state and never on which
                // map it is, so every card on the screen shares four of them.
                Bitmap? chrome = Chrome(w, h, radius, ring, raised);
                if (chrome != null)
                {
                    context.DrawImage(chrome,
                        new Rect(-Bleed, -Bleed, w + Bleed * 2, h + Bleed * 2));
                }
                else
                {
                    context.DrawRectangle(new SolidColorBrush(GuiTheme.PanelDeep), null,
                        face, Shadows(ring, raised));
                }

                using (context.PushClip(face))
                {
                    // The three layers under the type are baked, for the
                    // reason BakedBackdrop gives about the backdrop: they
                    // never change for a given card at a given size, and
                    // resampling a map render and laying two radial gradients
                    // and a linear one over it -- twenty-one times -- is the
                    // whole of why the grid did not scroll. Everything below
                    // this line is what actually changes: the ring, the lip,
                    // and the word.
                    Bitmap? ground = _ground;
                    if (ground != null)
                    {
                        context.DrawImage(ground, new Rect(0, 0, w, h));
                    }
                    Info(context, w, h, em);
                    Badge(context, w, em);
                }
            }

            if (moving)
            {
                Ask();
            }
        }

        private Avalonia.Media.Imaging.RenderTargetBitmap? _ground;
        private Bitmap? _customShot;
        private string? _customShotPath;
        private int _groundWidth, _groundHeight;

        /// <summary>
        /// The render, the drift and the scrim in one bitmap, cut once per
        /// size.
        ///
        /// Rounded up to a multiple of eight so that dragging the window's
        /// edge re-cuts every eight points rather than every one -- the same
        /// bargain <see cref="BakedBackdrop"/> strikes with its grain.
        /// </summary>
        /// <summary>
        /// Cut the bitmap for the size just arrived at.
        ///
        /// In the arrange pass and never in <see cref="Render"/>: baking means
        /// rendering into a second target, and doing that from inside the
        /// render pass is how Avalonia is made to throw *Visual was
        /// invalidated during the render pass*. <see cref="BakedBackdrop"/>
        /// carries the same note for the same reason.
        /// </summary>
        protected override Size ArrangeOverride(Size finalSize)
        {
            Bake(finalSize.Width, finalSize.Height);
            PrimeChrome(finalSize.Width, finalSize.Height, Em * 0.55);
            return base.ArrangeOverride(finalSize);
        }

        // ------------------------------------------------- the card's shadow

        /// <summary>
        /// The card's own three shadows, which are what <see cref="Chrome"/>
        /// bakes and what the fallback path draws directly.
        /// </summary>
        private static BoxShadows Shadows(Color ring, bool raised)
        {
            return new BoxShadows(
                Deck.Shadow(0, 0, 0, 2, ring),
                new[]
                {
                    Deck.Shadow(0, raised ? 7 : 5, 0, 0, Deck.Fade(0, 0.45)),
                    Deck.Shadow(0, raised ? 16 : 10, raised ? 26 : 18, 0,
                        Deck.Fade(0, 0.5))
                });
        }

        /// <summary>
        /// How far past the card the shadows reach, in points: the furthest
        /// offset (16) plus the widest blur (26) plus the ring's spread (2),
        /// with a little over. None of the three is scaled by the em, so this
        /// is a constant rather than a calculation.
        /// </summary>
        private const double Bleed = 48;

        private readonly record struct Cut(int Width, int Height, int Radius,
            uint Ring, bool Raised);

        /// <summary>
        /// Two sizes' worth of the four states a card can be in: the grid as
        /// it is now, and the grid as it was before the window was dragged.
        /// </summary>
        private const int ChromeKept = 8;

        private static readonly List<KeyValuePair<Cut, RenderTargetBitmap>> _chrome = new();

        /// <summary>How many cuts have been taken and asked for, for -uibench.</summary>
        internal static int ChromeBakes { get; private set; }

        internal static int ChromeAsks { get; private set; }

        /// <summary>
        /// Off puts the blur back on every card on every frame, which is what
        /// this replaced. For measuring the difference and nothing else.
        /// </summary>
        internal static bool CacheChrome { get; set; } = true;

        /// <summary>
        /// Cut every state this card could be drawn in, before it is drawn in
        /// any of them.
        ///
        /// In the arrange pass and never in <see cref="Render"/>, for the
        /// reason <see cref="Bake"/> gives -- and because the state that
        /// decides which cut is wanted (the pointer arriving) changes between
        /// arrange passes, so the one that is about to be needed has to have
        /// been taken already.
        /// </summary>
        private static void PrimeChrome(double w, double h, double radius)
        {
            // The reachable combinations, and only those: hot always raises a
            // card, and a card that is neither chosen nor led nor hot is never
            // raised, so two of the six do not exist.
            Chrome(w, h, radius, GuiTheme.Edge, raised: false);
            Chrome(w, h, radius, GuiTheme.Accent, raised: false);
            Chrome(w, h, radius, GuiTheme.Accent, raised: true);
            Chrome(w, h, radius, Color.FromRgb(0x4a, 0x6f, 0x8c), raised: true);
        }

        private static Bitmap? Chrome(double w, double h, double radius, Color ring,
            bool raised)
        {
            if (!CacheChrome)
            {
                return null;
            }
            int width = (int)Math.Ceiling(w / 8) * 8;
            int height = (int)Math.Ceiling(h / 8) * 8;
            if (width <= 0 || height <= 0)
            {
                return null;
            }
            var key = new Cut(width, height, (int)Math.Round(radius), ring.ToUInt32(), raised);
            ChromeAsks++;
            for (int i = 0; i < _chrome.Count; i++)
            {
                if (_chrome[i].Key == key)
                {
                    KeyValuePair<Cut, RenderTargetBitmap> hit = _chrome[i];
                    _chrome.RemoveAt(i);
                    _chrome.Add(hit);
                    return hit.Value;
                }
            }
            try
            {
                ChromeBakes++;
                // At the resolution it will be drawn at rather than at its own
                // point size: the ring is a two-point hairline with no blur on
                // it, and a hairline cut at 1:1 and blown up by the layout
                // transform is a smear. Same bargain as BakedBackdrop.
                double scale = UiLayout.BakeScale <= 0 ? 1 : UiLayout.BakeScale;
                double boxWidth = width + Bleed * 2, boxHeight = height + Bleed * 2;
                var cut = new RenderTargetBitmap(
                    new PixelSize(Math.Max((int)Math.Ceiling(boxWidth * scale), 1),
                        Math.Max((int)Math.Ceiling(boxHeight * scale), 1)),
                    new Vector(96, 96));
                using (DrawingContext into = cut.CreateDrawingContext())
                using (into.PushTransform(Avalonia.Matrix.CreateScale(scale, scale)))
                {
                    into.DrawRectangle(new SolidColorBrush(GuiTheme.PanelDeep), null,
                        new RoundedRect(new Rect(Bleed, Bleed, width, height), radius),
                        Shadows(ring, raised));
                }
                while (_chrome.Count >= ChromeKept)
                {
                    _chrome[0].Value.Dispose();
                    _chrome.RemoveAt(0);
                }
                _chrome.Add(new KeyValuePair<Cut, RenderTargetBitmap>(key, cut));
                return cut;
            }
            catch (Exception)
            {
                // A cut that could not be taken means the card blurs its own
                // shadow this frame, which is what it always did.
                return null;
            }
        }

        private Bitmap? Bake(double w, double h)
        {
            int width = (int)Math.Ceiling(w / 8) * 8;
            int height = (int)Math.Ceiling(h / 8) * 8;
            if (width <= 0 || height <= 0)
            {
                return null;
            }
            if (_ground != null && _groundWidth == width && _groundHeight == height)
            {
                return _ground;
            }
            _ground?.Dispose();
            _ground = null;
            try
            {
                var cut = new Avalonia.Media.Imaging.RenderTargetBitmap(
                    new PixelSize(width, height), new Vector(96, 96));
                using (DrawingContext into = cut.CreateDrawingContext())
                {
                    Bitmap? shot = SourceShot();
                    if (shot != null)
                    {
                        // `object-fit: cover`: fill the square, losing whatever
                        // hangs off whichever axis has the spare picture.
                        double sw = shot.PixelSize.Width, sh = shot.PixelSize.Height;
                        if (sw > 0 && sh > 0)
                        {
                            double scale = Math.Max(width / sw, height / sh);
                            double dw = sw * scale, dh = sh * scale;
                            into.DrawImage(shot,
                                new Rect((width - dw) / 2, (height - dh) / 2, dw, dh));
                        }
                    }
                    Drift(into, width, height);
                    Scrim(into, width, height);
                }
                _ground = cut;
                _groundWidth = width;
                _groundHeight = height;
                return _ground;
            }
            catch (Exception)
            {
                // A cut that could not be taken is a card with no picture,
                // not a dead screen.
                _ground = null;
                return null;
            }
        }

        private Bitmap? SourceShot()
        {
            if (!String.IsNullOrWhiteSpace(ImagePath) && File.Exists(ImagePath))
            {
                if (!String.Equals(_customShotPath, ImagePath, StringComparison.OrdinalIgnoreCase))
                {
                    _customShot?.Dispose();
                    _customShot = null;
                    _customShotPath = ImagePath;
                    try { _customShot = new Bitmap(ImagePath); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                        or ArgumentException)
                    {
                        _customShotPath = null;
                    }
                }
                if (_customShot != null) return _customShot;
            }
            return MapShot.For(RoomKey);
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            _ground?.Dispose();
            _ground = null;
            _customShot?.Dispose();
            _customShot = null;
            _customShotPath = null;
            _groundWidth = _groundHeight = 0;
            base.OnDetachedFromVisualTree(e);
        }

        /// <summary>
        /// `.tally`: how many have picked this, in the top-right corner.
        /// Brass once anybody has, which is what lets the count be read at a
        /// glance across a grid of twenty.
        /// </summary>
        private void Badge(DrawingContext context, double w, double em)
        {
            if (Tally < 0)
            {
                return;
            }
            double size = GuiTheme.PixelSize(em);
            FormattedText count = DeckText.Run(
                Tally.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Deck.Label(), size,
                new SolidColorBrush(Tally > 0 ? Colors.White : GuiTheme.TextDim));
            double side = Math.Max(em * 1.7, count.Width + em * 0.7);
            double high = em * 1.7;
            var box = new Rect(Math.Round(w - em * 0.4 - side), Math.Round(em * 0.4),
                Math.Round(side), Math.Round(high));
            context.DrawRectangle(
                new SolidColorBrush(Tally > 0 ? Deck.Face.Brass.Fill : Deck.Fade(0x0a0c10, 0.85)),
                new Pen(new SolidColorBrush(Tally > 0
                    ? Color.FromRgb(0xc9, 0xa2, 0x27) : GuiTheme.Edge), 1),
                new RoundedRect(box, Math.Round(em * 0.35)));
            context.DrawText(count, new Point(
                Math.Round(box.X + (box.Width - count.Width) / 2),
                Math.Round(box.Y + (box.Height - count.Height) / 2)));
        }

        /// <summary>
        /// `.drift`: two coloured blobs in soft-light over the render, at this
        /// card's own phase. Static -- see the note on the class.
        /// </summary>
        private void Drift(DrawingContext context, double w, double h)
        {
            // The phase the reference would be at, from the room key, so two
            // cards side by side are not the same picture twice.
            double phase = (Math.Abs(RoomKey.GetHashCode()) % 1000) / 1000.0;
            double dx = (phase - 0.5) * 0.08 * w;
            double dy = (phase - 0.5) * 0.06 * h;
            var warm = new RadialGradientBrush
            {
                Center = new RelativePoint(0.30, 0.40, RelativeUnit.Relative),
                GradientOrigin = new RelativePoint(0.30, 0.40, RelativeUnit.Relative),
                RadiusX = new RelativeScalar(0.45, RelativeUnit.Relative),
                RadiusY = new RelativeScalar(0.55, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(56, 0xff, 0xb3, 0x47), 0),
                    new GradientStop(Color.FromArgb(0, 0xff, 0xb3, 0x47), 1)
                }
            };
            var cool = new RadialGradientBrush
            {
                Center = new RelativePoint(0.72, 0.65, RelativeUnit.Relative),
                GradientOrigin = new RelativePoint(0.72, 0.65, RelativeUnit.Relative),
                RadiusX = new RelativeScalar(0.50, RelativeUnit.Relative),
                RadiusY = new RelativeScalar(0.60, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(80, 0x2b, 0x4e, 0x6b), 0),
                    new GradientStop(Color.FromArgb(0, 0x2b, 0x4e, 0x6b), 1)
                }
            };
            var box = new Rect(dx - w * 0.25, dy - h * 0.25, w * 1.5, h * 1.5);
            context.FillRectangle(warm, box);
            context.FillRectangle(cool, box);
        }

        private static void Scrim(DrawingContext context, double w, double h)
        {
            var brush = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(77, 10, 12, 16), 0),
                    new GradientStop(Color.FromArgb(51, 10, 12, 16), 0.40),
                    new GradientStop(Color.FromArgb(199, 10, 12, 16), 0.72),
                    new GradientStop(Color.FromArgb(245, 10, 12, 16), 1)
                }
            };
            context.FillRectangle(brush, new Rect(0, 0, w, h));
        }

        /// <summary>
        /// `.pinfo`: the tag at the top, the blurb above the foot, and the
        /// word that commits along the bottom.
        /// </summary>
        private void Info(DrawingContext context, double w, double h, double em)
        {
            double pad = Math.Round(em * 0.5);

            // `.tag`, with its code in the accent.
            double tagSize = GuiTheme.PixelSize(em * 0.72);
            FormattedText code = DeckText.Run(Code.ToUpperInvariant(),
                Deck.Body(bold: true), tagSize, GuiTheme.AccentBrush);
            double tagPadX = Math.Round(tagSize * 0.45), tagPadY = Math.Round(tagSize * 0.12);
            var tag = new Rect(pad, pad,
                Math.Round(code.Width + tagPadX * 2), Math.Round(code.Height + tagPadY * 2));
            context.DrawRectangle(new SolidColorBrush(Deck.Fade(0, 0.6)),
                new Pen(new SolidColorBrush(Deck.Fade(0xe6eaf2, 0.14)), 1),
                new RoundedRect(tag, Math.Round(tagSize * 0.25)));
            context.DrawText(code, new Point(tag.X + tagPadX, tag.Y + tagPadY));

            // `.picked`: a four-point lip, the full width of the card's inside,
            // moss until it is the chosen one and brass after. An empty verb
            // draws no slab at all -- the whole card is the button, so a
            // ballot of small cards need not spend a third of each one saying
            // so twice.
            double wordSize = GuiTheme.PixelSize(em * 0.9);
            double lip = 4;
            double wordH = Math.Round(wordSize * 1.7);
            double wordY = h - pad - wordH - lip;
            string word = _chosen ? ChosenVerb : Verb;
            if (word.Length == 0)
            {
                wordY = h - pad + Math.Round(em * 0.3);
            }
            else
            {
                var slab = new Rect(pad, wordY, Math.Max(0, w - pad * 2), wordH);
                Deck.Face faceColour = _chosen ? Deck.Face.Brass : Deck.Face.Moss;
                Color fill = faceColour.Fill, lipColour = faceColour.Lip;
                if (_over)
                {
                    fill = DeckPaint.Saturate(DeckPaint.Brightness(fill, 1.22), 1.15);
                    lipColour = DeckPaint.Saturate(DeckPaint.Brightness(lipColour, 1.22), 1.15);
                }
                context.DrawRectangle(new SolidColorBrush(fill), null,
                    new RoundedRect(slab, Math.Round(wordSize * 0.55)),
                    new BoxShadows(Deck.Shadow(0, lip, 0, 0, lipColour)));
                double wordWidth = DeckText.MeasureTracked(word, Deck.Label(), wordSize,
                    DeckText.LabelTracking);
                DeckText.DrawTracked(context, word, Deck.Label(), wordSize, GuiTheme.TextBrush,
                    Math.Round(slab.X + (slab.Width - wordWidth) / 2), slab.Y, slab.Height,
                    DeckText.LabelTracking);
            }

            if (Blurb.Length == 0)
            {
                return;
            }
            // `.blurb`: one line, above the word, trimmed to the card.
            double blurbSize = GuiTheme.PixelSize(em * 0.76);
            FormattedText blurb = DeckText.Run(Blurb, Deck.Body(bold: false), blurbSize,
                new SolidColorBrush(Color.FromRgb(0xc7, 0xcf, 0xdd)),
                maxWidth: Math.Max(10, w - pad * 2));
            context.DrawText(blurb,
                new Point(pad, Math.Round(wordY - Math.Round(em * 0.3) - blurb.Height)));
        }
    }

    /// <summary>
    /// The reference's <c>.grid</c>: three cards across, two when the frame is
    /// a phone's, half an em between them.
    ///
    /// A <see cref="UniformGrid"/> cannot do it -- the column count is a
    /// container query, not a constant, and the cards are square so the rows
    /// have to be as tall as the columns are wide.
    /// </summary>
    internal sealed class DeckGrid : Panel
    {
        /// <summary>`@container (max-width: 640px)` -- and the container is the frame.</summary>
        private const double TwoColumnFrame = 640;

        private readonly Tap _tap = new();
        private DeckTile? _down;
        private DeckTile? _over;

        /// <summary>
        /// The grid resolves its own pointer, against the rectangles it
        /// arranged the cards into, and the cards have no pointer handlers of
        /// their own.
        ///
        /// The toolkit hit-tests a card against <i>what it drew</i>, not
        /// against its rectangle -- and a card draws three shadows, one of
        /// them a 26-point blur, well outside its own box. So neighbouring
        /// cards overlap by tens of points and the one drawn later wins,
        /// which means a press near a card's edge picks the next one along.
        /// Measured on the offline grid, in the grid's own units, one row of
        /// press points across three columns:
        ///
        /// <code>
        ///                        column 1 ends   column 2 ends
        ///   the rectangles            208             415
        ///   the toolkit               155             380
        /// </code>
        ///
        /// 480 of 1470 swept points -- 33% -- landed on the wrong card, and
        /// the gaps between rows answered a card at all. It is not Android's:
        /// those numbers are the desktop's, which is why the offline grid
        /// picked the wrong map there too and why fixing the results ballot
        /// alone fixed neither.
        ///
        /// Clipping the cards would fix the hit test and take the shadows
        /// with it, so the rectangles are consulted instead and the picture
        /// is left alone.
        ///
        /// In the tunnel, so the card the toolkit chose never sees it. On the
        /// release rather than the press, because acting on the press is what
        /// CLAUDE.md records as having made scrolling the settings on a phone
        /// toggle every row a drag passed over: the press records, the release
        /// decides, and the release must land on the same card. Nothing is
        /// captured and nothing is marked handled before the release -- a card
        /// holding the pointer is what stopped the ballot being scrolled by
        /// dragging it, and the scroll gesture lives above this in the same
        /// route.
        /// </summary>
        public DeckGrid()
        {
            AddHandler(PointerPressedEvent, Pressed, RoutingStrategies.Tunnel);
            AddHandler(PointerMovedEvent, Moved, RoutingStrategies.Tunnel);
            AddHandler(PointerReleasedEvent, Released, RoutingStrategies.Tunnel);
        }

        /// <summary>The card whose own rectangle holds that point, or none.</summary>
        private DeckTile? TileAt(Point at)
        {
            foreach (Control child in Children)
            {
                if (child is DeckTile tile && tile.Bounds.Contains(at))
                {
                    return tile;
                }
            }
            return null;
        }

        private void Hover(DeckTile? tile, Point at)
        {
            if (!ReferenceEquals(_over, tile))
            {
                _over?.Hover(false, default);
                _over = tile;
            }
            tile?.Hover(true, at - tile.Bounds.Position);
        }

        private void Pressed(object? sender, PointerPressedEventArgs e)
        {
            Point at = e.GetPosition(this);
            _down = TileAt(at);
            Hover(_down, at);
            if (_down != null)
            {
                _tap.Press(e, this);
                _down.Focus();
            }
        }

        private void Moved(object? sender, PointerEventArgs e)
        {
            Point at = e.GetPosition(this);
            Hover(TileAt(at), at);
            if (_tap.Down)
            {
                _tap.Moved(e, this);
            }
        }

        private void Released(object? sender, PointerReleasedEventArgs e)
        {
            DeckTile? tile = _down;
            _down = null;
            bool tapped = _tap.Release(e, this);
            if (tile == null)
            {
                return;
            }
            // Ours either way: the card the toolkit would have sent this to is
            // not the one under the pointer, so letting it through picks the
            // wrong map.
            e.Handled = true;
            if (tapped && TileAt(e.GetPosition(this)) == tile)
            {
                tile.Fire();
            }
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            Hover(null, default);
            base.OnPointerExited(e);
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            _tap.Cancel();
            _down = null;
            base.OnPointerCaptureLost(e);
        }

        /// <summary>
        /// Fixed number of columns, or zero for the container query below.
        ///
        /// The results panel's ballot is two across whatever the frame is
        /// doing -- it is 22 ems wide and the frame's width says nothing about
        /// it -- where the offline grid is three, or two on a phone.
        /// </summary>
        public int FixedColumns { get; set; }

        /// <summary>Every card's shape, handed down as they are added.</summary>
        public double Ratio { get; set; } = 1;

        private int Columns => FixedColumns > 0 ? FixedColumns
            : Deck.GetFrameWidth(this) <= TwoColumnFrame ? 2 : 3;

        private double Gap => Math.Round(Deck.GetEm(this) * 0.5);

        static DeckGrid()
        {
            AffectsMeasure<DeckGrid>(Deck.EmProperty);
            AffectsMeasure<DeckGrid>(Deck.FrameWidthProperty);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            int columns = Columns;
            double gap = Gap;
            double width = Double.IsInfinity(availableSize.Width)
                ? Deck.GetEm(this) * 40 : availableSize.Width;
            double cell = Math.Max(1, (width - gap * (columns - 1)) / columns);
            double high = Math.Round(cell / Math.Max(0.1, Ratio));
            var slot = new Size(cell, high);
            foreach (Control child in Children)
            {
                if (child is DeckTile tile)
                {
                    tile.Ratio = Ratio;
                }
                child.Measure(slot);
            }
            int rows = (Children.Count + columns - 1) / columns;
            return new Size(width, rows * high + Math.Max(0, rows - 1) * gap);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            int columns = Columns;
            double gap = Gap;
            double cell = Math.Max(1, (finalSize.Width - gap * (columns - 1)) / columns);
            double high = Math.Round(cell / Math.Max(0.1, Ratio));
            for (int i = 0; i < Children.Count; i++)
            {
                int row = i / columns, column = i % columns;
                Children[i].Arrange(new Rect(
                    column * (cell + gap), row * (high + gap), cell, high));
            }
            return finalSize;
        }
    }
}
#endif
