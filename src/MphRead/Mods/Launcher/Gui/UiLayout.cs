using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace MphRead.Mods.Launcher.Gui
{
    /// <summary>
    /// The one layout every screen in the launcher is built from.
    ///
    /// There used to be nine screens and each had picked its own answer to
    /// where things go: a card 400 wide over here, a 600-wide table over
    /// there, a bar across the top on one, a corner link on another. The
    /// screens were all the same product and none of them looked like it.
    ///
    /// So the answers live here and nowhere else -- the picture, the two
    /// washes over it, the column of words in the bottom-left corner, the line
    /// under it, and the two marks in the bottom corners that mean yes and no.
    /// A screen is a choice of what goes in those places, which is why there
    /// are four of them now instead of nine.
    ///
    /// The numbers are the front screen's, unchanged: it was the one screen
    /// that already read the way the rest were meant to.
    /// </summary>
    internal static class UiLayout
    {
        /// <summary>Where the column of words starts, and how far it clears the bottom edge.</summary>
        public const double ColumnLeft = 72;
        public const double ColumnBottom = 96;

        /// <summary>The corner marks, and the dim line under the column.</summary>
        public const double CornerX = 64;
        public const double CornerY = 38;
        public const double FooterBottom = 18;

        /// <summary>A menu word, and the one word a screen is named by.</summary>
        public const double WordSize = 20;
        public const double HeadingSize = 15;

        /// <summary>Where a screen's own content sits: clear of the tabs and of both corners.</summary>
        public static Thickness BodyMargin => new(ColumnLeft, 104, CornerX, 96);

        /// <summary>Where the tab strip sits: the top-left corner, above the body.</summary>
        public static Thickness TabMargin => new(ColumnLeft, 52, 0, 0);

        // ------------------------------------------------------- the well
        //
        // Everything behind the front screen is laid out in one column down
        // the middle of the frame, with the cross and the tick together at
        // its foot. The front screen is the exception and keeps its corner:
        // it is three words over a photograph and has nothing to hold.
        //
        // The well has a *fixed* width, which is the whole argument for it.
        // A layout that fills the window puts a settings row's label at one
        // edge and its control at the other, so on a wide screen the two ends
        // of one row are a foot apart and reading it means crossing the
        // monitor -- and the wider the display, the worse it gets. Here a
        // wider window gives the *photograph* more room and the content
        // exactly as much as it had, so a row is the same shape on a laptop
        // and on an ultrawide.
        //
        // The marks move for the same kind of reason. A cross in one corner
        // and a tick in the other are two things to find; side by side under
        // the content they are one thing to read, in the order they are read
        // in -- no, then yes -- and they sit directly under where the eye
        // already is rather than in the two places it is not.

        /// <summary>
        /// How wide the well is, per kind of screen.
        ///
        /// Chosen against the smallest layout box the surface will hand out
        /// (960 by 600 -- see <c>UiSurface.Factor</c>): a well as wide as that
        /// box is a well with no margin, which is a full-width layout wearing
        /// a centred heading. These leave room either side at every size the
        /// program allows.
        /// </summary>
        // In *ems*, not points, since the deck theme: the reference's panel is
        // `width: min(44em, 100%)` and the em is the frame's own (see Deck).
        // Every sheet in it is the same 44 -- a browser and a settings page
        // are the same object with different things in them -- and only the
        // small dialogs are narrower.
        public const double WellPlay = 44;
        public const double WellSettings = 44;

        /// <summary>A question, a menu, a progress log: content, not a table.</summary>
        public const double WellShort = 19;

        /// <summary>What the well clears at the top, and at the foot for the marks.</summary>
        public const double WellTop = 44;
        public const double WellBottom = 84;

        /// <summary>
        /// The least ground either side of the well.
        ///
        /// The widths above are fixed on purpose and a wider box gives the
        /// photograph the difference, not the content -- but a box *narrower*
        /// than the well is a different question, and it has one answer: the
        /// well has to give way, or it is drawn off both edges of the screen.
        /// That is not a case the desktop reaches (the surface never hands out
        /// a box under 960 points and the widest well is 820), and it is the
        /// ordinary case on a phone, which is about 830 points across in
        /// landscape. So the width is a maximum rather than a size, and this
        /// is what is kept clear when it binds.
        /// </summary>
        public const double WellGutter = 20;

        /// <summary>Where the pair of marks sits, and how far apart.</summary>
        public const double MarksBottom = 28;
        public const double MarksGap = 64;

        /// <summary>
        /// The wash the well is read against: darkest through the middle band
        /// where the content is, fading out top and bottom so the photograph
        /// is still a photograph.
        ///
        /// Not a panel and not a card -- it has no edge anywhere, which is the
        /// one rule these screens have always kept. And it is never laid over
        /// a match: the scrim is already the ground there, and a second wash
        /// on top of it takes the match away, which is the thing the pause
        /// menu exists to keep visible.
        /// </summary>
        public static Border Wash(byte core = 228, byte edge = 120)
        {
            return new Border
            {
                Background = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
                    GradientStops =
                    {
                        new GradientStop(Color.FromArgb(edge, 10, 12, 16), 0),
                        new GradientStop(Color.FromArgb(core, 10, 12, 16), 0.16),
                        new GradientStop(Color.FromArgb(core, 10, 12, 16), 0.88),
                        new GradientStop(Color.FromArgb(edge, 10, 12, 16), 1)
                    }
                }
            };
        }

        /// <summary>
        /// The front screen's wash: the same gradient, a third of the weight.
        ///
        /// The default is set by the densest thing any screen has to carry --
        /// fourteen settings rows, which have to be legible over whatever the
        /// photograph is doing. The front screen carries three words, and
        /// three words need almost nothing; painting them against the settings
        /// page's ground would throw the picture away to solve a problem this
        /// screen does not have.
        /// </summary>
        public static Border LightWash() => Wash(core: 140, edge: 55);

        /// <summary>`--void` at an alpha: #05070a, the reference's own ground colour.</summary>
        private static Color Void(double alpha) =>
            Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255), 5, 7, 10);

        /// <summary>
        /// One of `#ground`'s two gradients.
        ///
        /// The vertical one is the picture's: dark at the top so the build
        /// line reads, almost nothing across the third where the photograph is
        /// worth looking at, and back down to nearly opaque at the foot where
        /// the bar of buttons sits. The sideways one is a shorter fall from
        /// the left edge, which is where the front screen's profile chip and
        /// the support mark are.
        /// </summary>
        private static Border Ground(bool horizontal)
        {
            var brush = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(horizontal ? 0 : 0.5, horizontal ? 0.5 : 0,
                    RelativeUnit.Relative),
                EndPoint = new RelativePoint(horizontal ? 1 : 0.5, horizontal ? 0.5 : 1,
                    RelativeUnit.Relative)
            };
            if (horizontal)
            {
                brush.GradientStops.Add(new GradientStop(Void(0.55), 0));
                brush.GradientStops.Add(new GradientStop(Void(0), 0.38));
                brush.GradientStops.Add(new GradientStop(Void(0), 1));
            }
            else
            {
                brush.GradientStops.Add(new GradientStop(Void(0.72), 0));
                brush.GradientStops.Add(new GradientStop(Void(0.10), 0.30));
                brush.GradientStops.Add(new GradientStop(Void(0.35), 0.62));
                brush.GradientStops.Add(new GradientStop(Void(0.90), 1));
            }
            return new Border { Background = brush };
        }

        /// <summary>
        /// Which wash a backdrop carries, so that it can be baked into the
        /// same bitmap as the photograph under it.
        ///
        /// A wash is a full-window gradient with alpha and costs about what
        /// the photograph does to rasterise, so leaving it live would leave
        /// a third of <see cref="BakedBackdrop"/>'s saving on the table.
        /// </summary>
        public enum BackdropWash
        {
            /// <summary>Nothing over the photograph. The design studies, which draw their own.</summary>
            None,
            /// <summary>What every screen behind the front one is read against.</summary>
            Standard,
            /// <summary>The front screen's: three words need almost no ground.</summary>
            Light
        }

        /// <summary>
        /// Which slice of the backdrop a bake holds.
        ///
        /// One bitmap is the ordinary case and is what the desktop shell uses.
        /// The two halves exist for the head that draws its own moving layer
        /// (<see cref="MovingBackdrop"/>), which has to sit between the
        /// photograph and the washes rather than over both.
        /// </summary>
        public enum BackdropPart
        {
            /// <summary>Everything this head is responsible for.</summary>
            All,
            /// <summary>The photograph alone.</summary>
            Photo,
            /// <summary>The two gradients alone.</summary>
            Washes
        }

        /// <summary>
        /// Whether something under these screens is already drawing the
        /// photograph -- and, with it, the moving layer over the photograph.
        ///
        /// True only in the desktop shell, which puts both on the screen as GL
        /// quads at the window's own resolution (see
        /// <c>Mods/Render/LauncherPhoto.cs</c>) because the bake below is
        /// capped at 1080p and magnified, and a photograph is the one layer
        /// that shows it. False everywhere else: the standalone captures, the
        /// design studies, and the Android head, which has no window under the
        /// screens at all.
        /// </summary>
        private static bool PhotoDrawnBelow
        {
            get
            {
#if MPHREAD_SHELL
                return Mods.Render.LauncherPhoto.Enabled;
#else
                return false;
#endif
            }
        }

        /// <summary>
        /// How many device pixels one layout point is, for whoever is cutting
        /// a bitmap rather than drawing into the frame.
        ///
        /// <c>UiSurface</c> scales the whole screen with a layout
        /// transform, so a control's own <c>Bounds</c> are in points and
        /// nothing in the visual tree can see what those land on. The
        /// backdrop has to know: baked at the point size it would be blown up
        /// by this much and the photograph would be visibly soft. One is the
        /// right answer everywhere else -- the capture commands render at the
        /// size they are given, with no transform in the way.
        /// </summary>
        public static double BakeScale { get; set; } = 1;

        /// <summary>
        /// How much bigger than its own layout a box this tall draws the
        /// screens.
        ///
        /// Here rather than in <c>UiSurface</c>, where it was written,
        /// because it is not the desktop's rule -- it is the launcher's, and
        /// there are two heads. The desktop asks about the game window and
        /// scales the surface it composites; Android asks about the view the
        /// activity was given and scales that. A phone that skips this gets
        /// laid out in its own ~400 point height, which is a third of what
        /// every screen here is authored for: the well is wider than the
        /// screen, the server list is arranged off the side of it, and the
        /// result looks nothing like the same program.
        ///
        /// One rule for every screen -- the front screen, the pause menu, the
        /// settings, in a match or not. Two of them had different scales for a
        /// build and the difference is exactly what got reported: the menus in
        /// a match were readable and the front screen that came back when the
        /// match ended "went small again". A menu is a menu.
        ///
        /// It is not linear in the window's height, and that is deliberate.
        /// Straight proportion keeps text the same *fraction* of the picture,
        /// which is right for a HUD and wrong for something you read: a
        /// 1280x768 window sits an arm's length away on a desk, and a
        /// fullscreen 1080p or 4K picture is usually a bigger screen further
        /// off, wanting more than proportionally larger type. The exponent is
        /// what carries that -- a window twice as tall draws the screens about
        /// 2.8 times as large -- and 720 is the height at which they are drawn
        /// as they were authored.
        ///
        /// Both ends were reported, from the same build, in the same
        /// sentence: too big in the window it opens in, too small in
        /// fullscreen. The numbers below are the two anchors that came out of
        /// that -- about 1.1 at 1280x768, about 1.85 at 1080p.
        ///
        /// Eighth steps: a drag would otherwise relayout the whole screen on
        /// every pixel, and quarters were a visible jump at the boundary.
        ///
        /// The height is what the curve is drawn from, but it is not the only
        /// thing that decides: the screens also have to *fit*. A window wider
        /// than it is tall is the ordinary case and it is the one the height
        /// alone got wrong -- 1440p asked for 2.875, which leaves the screens
        /// 890 by 500 points to lay themselves out in, and they are authored
        /// for something near <see cref="MinBoxWidth"/> by <see cref="MinBoxHeight"/>. The type then looks
        /// enormous because everything around it has been squeezed, and the
        /// column of settings beside the list runs off the bottom of its own
        /// grid row and is drawn straight over the tick in the corner. So the
        /// curve is capped by what the window can actually hold, and the
        /// layout box never goes below the size the screens were drawn for.
        /// </summary>
        public static double Factor(double width, double height)
        {
            double room = Math.Max(height, 1) / 720.0;
            double raw = Math.Pow(room, 1.5);
            double fits = Math.Min(Math.Max(width, 1) / MinBoxWidth,
                Math.Max(height, 1) / MinBoxHeight);
            // The curve rounds to the nearest eighth and the cap rounds down
            // to one: a cap rounded to the nearest is a cap that can be
            // exceeded, which is the one thing it is there to stop.
            double stepped = Math.Min(Math.Round(raw * 8), Math.Floor(fits * 8)) / 8;
            // Down to 0.6, because the smallest window this program allows is
            // shorter than the space the screens are drawn in and the menu has
            // to fit inside it; up to four, past which nothing is legible for
            // a different reason.
            return Math.Clamp(stepped, 0.6, 4);
        }

        /// <summary>
        /// The smallest layout box the screens are allowed to be given, in
        /// their own points -- roughly the launcher window that used to open.
        ///
        /// It is the floor under <see cref="Factor"/> and nothing else: a
        /// window smaller than this in real pixels still gets the 0.6 clamp
        /// and a box smaller than this, because there is nothing else to do
        /// with it. A phone in landscape is exactly that case and is why the
        /// clamp matters on the other head: 829 by 393 points asks for 0.375
        /// off the curve and gets 0.6, which still fits a 1382 by 655 box.
        /// </summary>
        public const double MinBoxWidth = 960;
        public const double MinBoxHeight = 600;

        /// <summary>
        /// How short a box has to be before the well stops spending a third of
        /// it on its own margins.
        ///
        /// A desktop window is 600 points tall or more and 44 above plus 84
        /// below is a comfortable seventh of it. A phone in landscape is about
        /// 390 (see <c>UiScaleHost</c>), where the same two numbers are a
        /// third of the screen given to nothing -- and the thing they take it
        /// from is the list, which is what the screen is for.
        /// </summary>
        public const double ShortBox = 560;

        /// <summary>
        /// Leaving on the left, everything else on the right.
        ///
        /// They used to sit together in the middle, which reads as a row of
        /// equals -- and they are not: one of them undoes the screen and the
        /// others do what the screen is for. Pushed apart, the thumb has a
        /// side for each and neither is ever pressed by mistake for the other.
        /// </summary>
        public static Panel Marks(params UiMark?[] marks)
        {
            var row = new GapDock(0.6)
            {
                LastChildFill = false,
                VerticalAlignment = VerticalAlignment.Center
            };
            // Docked right in reverse, so the order they were passed in reads
            // left to right along the right-hand side: extra, then the tick.
            for (int i = marks.Length - 1; i >= 1; i--)
            {
                if (marks[i] is not UiMark mark)
                {
                    continue;
                }
                mark.VerticalAlignment = VerticalAlignment.Center;
                DockPanel.SetDock(mark, Dock.Right);
                row.Children.Add(mark);
            }
            if (marks.Length > 0 && marks[0] is UiMark cancel)
            {
                cancel.VerticalAlignment = VerticalAlignment.Center;
                DockPanel.SetDock(cancel, Dock.Left);
                row.Children.Add(cancel);
            }
            return row;
        }

        /// <summary>
        /// A Grid whose row gap is an em rather than a number.
        ///
        /// Avalonia has <c>RowSpacing</c> and it takes points, so the one
        /// place the em can be read is a measure pass -- and it is written
        /// only when it has moved, or an inherited property assigned on every
        /// measure invalidates every control that reads it, on every measure.
        /// </summary>
        private sealed class GapGrid : Grid
        {
            private readonly double _gapEms;

            public GapGrid(double gapEms, string rows)
            {
                _gapEms = gapEms;
                RowDefinitions = new RowDefinitions(rows);
            }

            protected override Size MeasureOverride(Size availableSize)
            {
                double gap = Math.Round(Deck.GetEm(this) * _gapEms);
                if (Math.Abs(gap - RowSpacing) > 0.01)
                {
                    RowSpacing = gap;
                }
                return base.MeasureOverride(availableSize);
            }
        }

        /// <summary>The same, for the row of marks: a flex gap in ems.</summary>
        private sealed class GapDock : DockPanel
        {
            private readonly double _gapEms;

            public GapDock(double gapEms)
            {
                _gapEms = gapEms;
            }

            protected override Size MeasureOverride(Size availableSize)
            {
                double gap = Math.Round(Deck.GetEm(this) * _gapEms);
                foreach (Control child in Children)
                {
                    Thickness want = GetDock(child) == Dock.Right
                        ? new Thickness(gap, 0, 0, 0)
                        : new Thickness(0);
                    if (child.Margin != want)
                    {
                        child.Margin = want;
                    }
                }
                return base.MeasureOverride(availableSize);
            }
        }

        /// <summary>
        /// The scrim a sheet lays over whatever is behind it:
        /// <c>rgba(5,7,10,.72)</c>.
        ///
        /// Not a wash and not a vignette -- flat, edge to edge, so the panel
        /// on top of it is the only thing with a shape. The photograph is
        /// still visible through it, which is the point: these screens sit on
        /// a table rather than replacing it.
        /// </summary>
        /// <summary>
        /// `.sheet`'s ground, <c>rgba(5,7,10,.72)</c>.
        ///
        /// It is now drawn inside the bake rather than as a live layer over
        /// it (see <see cref="BackdropLayers"/>): it is a flat rectangle the
        /// size of the window, it never changes while a screen is up, and
        /// rasterising it again on every redraw bought nothing. Kept as a
        /// brush because the value is the reference's and belongs somewhere
        /// nameable.
        /// </summary>
        public static readonly IBrush SheetBrush =
            new SolidColorBrush(Color.FromArgb(184, 5, 7, 10));

        /// <summary>
        /// A whole screen: the backdrop, the sheet over it, and one panel
        /// centred on that.
        ///
        /// <para>
        /// Every screen behind the front one is built from this and nothing
        /// else, so none of them can invent its own answer to where a heading
        /// goes -- which is what nine screens with nine layouts was, and what
        /// this file exists to stop happening again.
        /// </para>
        ///
        /// <para>
        /// The panel is three rows and they are always the same three: the
        /// strip of faces, the content, and the foot. The reference writes it
        /// <c>grid-template-rows: auto minmax(0, 1fr) auto</c> and the middle
        /// term is the load-bearing one -- <c>minmax(0, 1fr)</c>, not
        /// <c>1fr</c>, is what lets a list of thirteen servers scroll inside
        /// the panel instead of growing it off the bottom of the frame.
        /// </para>
        /// </summary>
        /// <param name="widthEms">
        /// The panel's cap, in frame ems. 44 for a screen, 19 for a dialog.
        /// </param>
        /// <param name="centreBody">
        /// Centre the content for what is shorter than the well -- a menu, a
        /// question. A list or a page of settings wants the height it is
        /// given, so it stretches.
        /// </param>
        /// <param name="extra">
        /// A third mark, between the two, for a screen whose foot carries an
        /// act that is neither leaving nor committing. Deliberately awkward to
        /// reach: the pair is the rule, and a screen that wants a third has to
        /// say so.
        /// </param>
        /// <param name="note">
        /// The line under the foot. Part of the foot in the reference rather
        /// than the last row of the content, which is what keeps it in the
        /// same place whether the content scrolled or not.
        /// </param>
        public static Panel Page(bool overGame, double widthEms, string heading,
            Control? strip, Control body, UiMark? no = null, UiMark? yes = null,
            bool centreBody = false, UiMark? extra = null, Control? note = null)
        {
            // The wash goes into the backdrop rather than over it: baked
            // together they are one blit a frame instead of four full-window
            // rasterisations. See BakedBackdrop for what that was costing.
            Panel root = Backdrop(overGame,
                overGame ? BackdropWash.None : BackdropWash.Standard);
            root.SetValue(ControllerNav.NavScopeProperty, "page");
            if (no != null) ControllerNav.Identify(no, "page.back");
            if (yes != null) ControllerNav.Identify(yes, "page.accept");
            if (extra != null) ControllerNav.Identify(extra, "page.extra");
            if (no != null && yes != null)
            {
                no.SetValue(ControllerNav.NavRightProperty, "page.accept");
                yes.SetValue(ControllerNav.NavLeftProperty, "page.back");
            }
            bool hasMarks = no != null || yes != null || extra != null;

            // The heading, for the screens with no strip -- where it is the
            // only thing saying which screen this is. A strip of named faces
            // says it already, and the word above it was saying it twice.
            Control? title = strip == null && heading.Length > 0
                ? Heading(heading.ToLowerInvariant())
                : null;

            var inside = new GapGrid(0.65, "Auto,*,Auto");
            if (title != null)
            {
                title.HorizontalAlignment = HorizontalAlignment.Center;
                Grid.SetRow(title, 0);
                inside.Children.Add(title);
            }
            else if (strip != null)
            {
                strip.HorizontalAlignment = HorizontalAlignment.Center;
                Grid.SetRow(strip, 0);
                inside.Children.Add(strip);
            }
            Grid.SetRow(body, 1);
            if (centreBody)
            {
                body.VerticalAlignment = VerticalAlignment.Center;
            }
            inside.Children.Add(body);

            if (hasMarks || note != null)
            {
                // `.foot`: the row of marks, and the line under it, .45em apart.
                var foot = new GapGrid(0.45, hasMarks ? "Auto,Auto" : "Auto");
                if (hasMarks)
                {
                    Panel marks = Marks(no, extra, yes);
                    Grid.SetRow(marks, 0);
                    foot.Children.Add(marks);
                }
                if (note != null)
                {
                    Grid.SetRow(note, hasMarks ? 1 : 0);
                    foot.Children.Add(note);
                }
                Grid.SetRow(foot, 2);
                inside.Children.Add(foot);
            }

            // The panel: `min(44em, 100%)` wide, its own height, centred in
            // what the sheet's padding leaves. See DeckCard.
            var card = new DeckCard
            {
                Child = inside,
                MaxWidthEms = widthEms
            };
            // `.sheet`: the scrim and the panel on it, fading in together,
            // with the panel springing up out of `scale(.9) translateY(14px)`
            // underneath that. Both halves are one object here for the same
            // reason they are one element there -- darkening the frame
            // instantly and then floating a panel onto it is two events where
            // the reference has one.
            var sheet = new DeckSheet();
            sheet.Children.Add(new Border { Background = SheetBrush });
            sheet.Children.Add(new SheetPad { Child = card });
            root.Children.Add(sheet);
            return root;
        }

        /// <summary>
        /// The sheet's own padding: <c>1.1em .9em</c>, in ems, around the one
        /// panel it holds.
        /// </summary>
        private sealed class SheetPad : Decorator
        {
            protected override Size MeasureOverride(Size availableSize)
            {
                double em = Deck.GetEm(this);
                var want = new Thickness(Math.Round(em * 0.9), Math.Round(em * 1.1),
                    Math.Round(em * 0.9), Math.Round(em * 1.1));
                if (Padding != want)
                {
                    Padding = want;
                }
                return base.MeasureOverride(availableSize);
            }
        }

        private static readonly Lazy<Bitmap?> _background =
            new(() => Load("Backgrounds/launcher-bg.jpg"));

        private static readonly Lazy<Bitmap?> _wordmark =
            new(() => Load("fruity-prime-logo.png"));

        private static Bitmap? Load(string asset)
        {
            try
            {
                using Stream stream = AssetLoader.Open(
                    new Uri($"avares://FruityPrime/Assets/{asset}"));
                return new Bitmap(stream);
            }
            catch (Exception)
            {
                // A build missing the asset gets no picture rather than no
                // launcher.
                return null;
            }
        }

        /// <summary>
        /// What every screen is painted on: the photo, a wash that carries the
        /// left third dark enough to read a menu over, and a corner vignette
        /// for the wordmark.
        ///
        /// Over a match it is the scrim alone. The match behind is still
        /// running -- a networked one cannot be paused -- and covering it with
        /// a photograph would be a lie about what the program is doing.
        /// </summary>
        public static Panel Backdrop(bool overGame = false,
            BackdropWash wash = BackdropWash.None)
        {
            // A DeckStage, not a bare Panel: this is the frame, and the frame
            // is where the em comes from. A plain Panel here left every screen
            // on Deck's default em, which happens to be exactly right at the
            // capture's 940 points and wrong everywhere else -- so a phone
            // laid its rows out as if it were a monitor and kept the columns
            // the reference drops below 560.
            var root = new DeckStage();
            if (overGame)
            {
                root.Children.Add(new Border { Background = GuiTheme.ScrimBrush });
                // One stamp at the root of every screen: these are attached
            // properties and they flow down the visual tree, so the whole
            // launcher is aliased from here rather than control by control --
            // and the control that would have been forgotten is the one that
            // shows.
            GuiTheme.PixelPerfect(root);
            return root;
            }
            if (PhotoDrawnBelow)
            {
                // GL has the photograph and the moving layer over it (see
                // LauncherPhoto and LauncherNoise), so this bake is the two
                // gradients and nothing else.
                root.Children.Add(new BakedBackdrop(wash));
            }
            else
            {
                // Nothing underneath is drawing any of it, so the whole
                // backdrop is the toolkit's -- and it goes down in three
                // pieces rather than one so that the moving layer lands
                // *between* the photograph and the washes, which is where the
                // reference puts it and where GL puts it on the desktop. Both
                // bakes are cached bitmaps and a blit apiece; only the middle
                // layer costs anything a frame. See MovingBackdrop.
                root.Children.Add(new BakedBackdrop(wash, BackdropPart.Photo));
                root.Children.Add(new MovingBackdrop());
                root.Children.Add(new BakedBackdrop(wash, BackdropPart.Washes));
            }
            // One stamp at the root of every screen: these are attached
            // properties and they flow down the visual tree, so the whole
            // launcher is aliased from here rather than control by control --
            // and the control that would have been forgotten is the one that
            // shows.
            GuiTheme.PixelPerfect(root);
            return root;
        }

        /// <summary>
        /// The layers themselves, for <see cref="BakedBackdrop"/> to render
        /// once into the bitmap everything else draws.
        ///
        /// This is the backdrop as it has always been -- it is only ever
        /// rasterised now instead of being kept in the tree.
        /// </summary>
        public static Panel BackdropLayers(BackdropWash wash,
            BackdropPart part = BackdropPart.All)
        {
            var root = new Panel();
            bool photoBelow = PhotoDrawnBelow;
            if (!photoBelow && part != BackdropPart.Washes)
            {
                // Only where nothing else is drawing it. The desktop shell
                // puts the photograph on the screen as a GL quad at the
                // window's own resolution -- see LauncherPhoto -- because this
                // bitmap is capped at 1080p and magnified, and a photograph is
                // the one layer that shows it. The washes below stay here
                // either way: they are gradients, and a gradient magnifies for
                // nothing.
                root.Children.Add(new Image
                {
                    Source = _background.Value,
                    Stretch = Stretch.UniformToFill
                });
            }
            // `#ground`, both gradients, in the order CSS paints them -- a
            // background list is drawn last-first, so the sideways one goes
            // down before the vertical one.
            //
            // These were a 215-to-0 fall from the top, a vignette in the
            // bottom-right corner and a full-window wash over both, and
            // together they put about sixty per cent of black over the middle
            // of the photograph against the reference's twenty-six. That is
            // the whole of "the backdrop is too dark": not one layer too
            // strong, three layers where there are two, and neither of them
            // shaped like these. It is `--void` as well, not black -- #05070a
            // has a blue in it that plain black does not, and over a picture
            // that is mostly lava it is the difference between shade and soot.
            if (part != BackdropPart.Photo)
            {
                root.Children.Add(Ground(horizontal: true));
                root.Children.Add(Ground(horizontal: false));
            }
            // `.sheet`'s own `rgba(5,7,10,.72)` is *not* here, even though it
            // is a flat rectangle that would bake for nothing: it fades in
            // with the panel it belongs to (see DeckSheet), and a layer inside
            // the bake cannot fade on its own.
            return root;
        }

        /// <summary>The column of words, anchored where the front screen's is.</summary>
        public static StackPanel Column(double spacing = 16)
        {
            return new StackPanel
            {
                Spacing = spacing,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(ColumnLeft, 0, 0, ColumnBottom)
            };
        }

        /// <summary>
        /// The dim line under the column: the build on the front screen, what
        /// match you are in on the pause screen. One line, one corner, and
        /// never anything a player came here to read.
        /// </summary>
        public static TextBlock Footer(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontFamily = GuiTheme.Display,
                FontSize = 12,
                Foreground = GuiTheme.TextDimBrush,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(ColumnLeft - 50, 0, 0, FooterBottom)
            };
        }

        /// <summary>The mark in the opposite corner, at the size the front screen uses.</summary>
        public static Image Wordmark()
        {
            return new Image
            {
                Source = _wordmark.Value,
                Stretch = Stretch.Uniform,
                Width = 220,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 32, 28),
                Opacity = 0.92
            };
        }

        /// <summary>
        /// What a screen is called, in the top-left corner over the tabs.
        /// Lower case and dim on purpose: it says where you are, and a title
        /// that shouts is a title competing with the thing it titles.
        /// </summary>
        public static TextBlock Heading(string text)
        {
            return new TextBlock
            {
                Text = text.ToLowerInvariant(),
                FontFamily = GuiTheme.Display,
                FontSize = HeadingSize,
                Foreground = GuiTheme.TextDimBrush,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(ColumnLeft, 26, 0, 0)
            };
        }
    }
}
