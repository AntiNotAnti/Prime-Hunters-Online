#if MPHREAD_AVALONIA
using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using MphRead.Mods.Network;

namespace MphRead.Mods.Launcher.Gui
{
    /// <summary>
    /// One server in the browser: a slab with its own map behind it.
    ///
    /// <para>
    /// The list used to be menu lines whose subtitle was a sentence --
    /// "1.2.3.4:27888 -- MP3 PROVING GROUND (Battle) 3/8 players, 41 ms".
    /// Everything was there and none of it was comparable: the map started at
    /// a different x on every row, so picking the emptiest server, or the
    /// nearest one, meant reading each line rather than scanning a column.
    /// </para>
    ///
    /// <para>
    /// So the row is a grid, and the grid is the reference's, term for term:
    /// <c>auto minmax(5em, 1.5fr) minmax(0, 1.3fr) minmax(0, 1fr) auto
    /// auto</c>. Fractions rather than the fixed widths measured from the
    /// right that were here before -- those were an attempt to stop a long map
    /// name wrapping, and what they actually did was hand a 565-point row the
    /// same 74 points of mode column as a 313-point one. A fraction gives
    /// ground everywhere at once, and the two columns that hold prose give it
    /// in proportion to how much prose they hold.
    /// </para>
    ///
    /// <para>
    /// <b>The row's em is not the screen's.</b> Every size here is a multiple
    /// of <see cref="Deck.RowEm"/>, which is fixed, so a list of servers is the
    /// same physical size on a phone as on a monitor while the panel around it
    /// shrinks. See the note on that constant: it is the reference's behaviour
    /// and it is deliberate.
    /// </para>
    ///
    /// <para>
    /// Two faces in one row, and that is the point of having two. The name and
    /// the map are <i>labels</i> and are set in the display face; the mode, the
    /// count and the ping are <i>read</i> and are set in the mono, right
    /// aligned on tabular figures so a column of pings is a column of numbers
    /// rather than a ragged edge.
    /// </para>
    /// </summary>
    internal sealed class ServerRow : Control
    {
        /// <summary>
        /// Where each column lands, from the reference's grid.
        ///
        /// Below <see cref="Deck.NarrowFrame"/> the mode and the player count
        /// come out and the two prose columns take what they leave -- the
        /// reference's <c>@container (max-width: 560px)</c>. The query is on
        /// the <b>frame</b>, not on the row: a phone in landscape has a narrow
        /// panel and a wide frame and keeps every column.
        /// </summary>
        internal readonly struct Columns
        {
            private const double PadX = 0.6 * Deck.RowEm;
            private const double Gap = 0.55 * Deck.RowEm;
            public const double FlagWidth = 1.5 * Deck.RowEm;
            public const double FlagHeight = 1.05 * Deck.RowEm;

            /// <summary>`min-width: 2.6em` on a column set at `.86em`.</summary>
            private const double NumberWidth = 2.6 * 0.86 * Deck.RowEm;

            public readonly double FlagX;
            public readonly double NameX, NameWidth;
            public readonly double MapX, MapWidth;
            public readonly double ModeX, ModeWidth;
            public readonly double PlayersX, PlayersWidth;
            public readonly double PingX, PingWidth;
            public readonly bool Narrow;

            public Columns(double width, bool narrow)
            {
                Narrow = narrow;
                FlagX = PadX;
                double x = PadX + FlagWidth + Gap;
                if (narrow)
                {
                    // auto | 1.4fr | 1fr | auto
                    double free = Math.Max(0,
                        width - PadX * 2 - Gap * 3 - FlagWidth - NumberWidth);
                    NameWidth = free * (1.4 / 2.4);
                    MapWidth = free - NameWidth;
                    ModeWidth = 0;
                    PlayersWidth = 0;
                    NameX = x;
                    MapX = NameX + NameWidth + Gap;
                    ModeX = PlayersX = 0;
                    PingWidth = NumberWidth;
                    PingX = MapX + MapWidth + Gap;
                    return;
                }
                // auto | 1.5fr | 1.3fr | 1fr | auto | auto
                double rest = Math.Max(0,
                    width - PadX * 2 - Gap * 5 - FlagWidth - NumberWidth * 2);
                NameWidth = rest * (1.5 / 3.8);
                MapWidth = rest * (1.3 / 3.8);
                ModeWidth = rest - NameWidth - MapWidth;
                NameX = x;
                MapX = NameX + NameWidth + Gap;
                ModeX = MapX + MapWidth + Gap;
                PlayersWidth = NumberWidth;
                PlayersX = ModeX + ModeWidth + Gap;
                PingWidth = NumberWidth;
                PingX = PlayersX + PlayersWidth + Gap;
            }
        }

        /// <summary>The slab, without its edge. `.45em` of padding either side of a 1.1 line.</summary>
        public const double SlabHeight = 29.6;

        /// <summary>At rest, and what the hovered and selected states grow it to.</summary>
        private const double Lip = 3;
        private const double LipLive = 5;
        private const double Radius = 0.4 * Deck.RowEm;

        public event EventHandler? Clicked;

        /// <summary>
        /// Chosen rather than merely pointed at: Enter, Space, or a second
        /// click on the row that is already selected.
        ///
        /// Separate from <see cref="Clicked"/> on purpose. A single click
        /// selects -- it opens the panel beside the list and fills the address
        /// box -- and joining is the mark in the corner. Pressing a row *was*
        /// the whole choice, which put a player in a match they had only meant
        /// to look at.
        /// </summary>
        public event EventHandler? Activated;

        private readonly string _name;
        private readonly string _endpoint;
        private string _map = "asking…";
        /// <summary>
        /// What the room is filed under, which is not what the row prints.
        ///
        /// The cell says "Combat Hall" and the thumbnail is
        /// <c>mp3_proving_ground.png</c>; looking the picture up by the
        /// printed name asked for <c>combat_hall.png</c>, found nothing, and
        /// drew a browser with no maps in it -- silently, since a missing
        /// thumbnail is an ordinary thing.
        /// </summary>
        private string _roomKey = "";
        private string _mode = "";
        private string _players = "—";
        private string _ping = "—";
        private IBrush _pingBrush = GuiTheme.TextDimBrush;
        private bool _answered;
        private bool _asking = true;
        private bool _hot;
        private bool _selected;
        private double _lean;
        private readonly Tap _tap = new();

        /// <summary>
        /// This is the row the address box and the tick are about.
        ///
        /// Its own state rather than hover or focus: neither of those exists
        /// under a finger, so on a phone the browser drew four identical rows
        /// and gave no sign which one was picked.
        /// </summary>
        public bool IsSelected
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

        public ServerRow(string name, string endpoint)
        {
            _name = name;
            _endpoint = endpoint;
            // The slab alone. Its edge is a box-shadow and hangs below it,
            // into the list's own `.32em` gap, exactly as the reference's
            // does -- so the pitch is 29.6 plus that gap and nothing else.
            // Reserving the edge inside the row's box instead added three
            // quarters of a point to every pitch, which over thirteen rows is
            // the ten points that had a phone's panel sitting too high.
            Height = SlabHeight;
            Focusable = true;
            Cursor = new Cursor(StandardCursorType.Hand);
            // A second click joins, as it does in the map list. See UiListRow.
            DoubleTapped += (_, _) =>
            {
                if (_asking || !_answered)
                {
                    return;
                }
                Clicked?.Invoke(this, EventArgs.Empty);
                Activated?.Invoke(this, EventArgs.Empty);
            };
        }

        /// <summary>Whether this row can be picked at all: it has answered.</summary>
        public bool IsLive => _answered && !_asking;
        public bool CanJoin { get; private set; }
        public bool Tactical { get; set; }
        private string _availability = "CHECKING";

        /// <summary>Fill the columns in once the server has answered.</summary>
        public void SetStatus(ServerStatus status)
        {
            CanJoin = status.Online && status.Protocol == NetConfig.ProtocolVersion
                && (status.MaxPlayers <= 0 || status.Players < status.MaxPlayers);
            _availability = !status.Online ? "OFFLINE" : status.Protocol != NetConfig.ProtocolVersion ? "BUILD MISMATCH"
                : CanJoin ? "JOINABLE" : "LOBBY FULL";
            _asking = false;
            _answered = status.Online;
            if (!status.Online)
            {
                // The reference's dead row: one phrase where the map was, a
                // dash in each number, and the name gone dim. Not an error
                // message -- a server that is down is an ordinary thing for a
                // browser to contain.
                _map = "did not answer";
                _mode = "";
                _players = "—";
                _ping = "—";
                _pingBrush = new SolidColorBrush(GuiTheme.Bad);
                Cursor = Cursor.Default;
                InvalidateVisual();
                return;
            }
            // The name the game calls it, not the key it is filed under:
            // "Combat Hall" rather than "MP3 PROVING GROUND", which is both
            // what a player recognises and short enough to stop the column
            // trimming every row. The key is still what RoomKey hands back,
            // because that is what the preview and the vote are looked up by.
            _roomKey = status.RoomKey;
            _map = Metadata.RoomMetadata.TryGetValue(status.RoomKey, out RoomMetadata? room)
                && !string.IsNullOrEmpty(room.InGameName)
                    ? room.InGameName!
                    : status.RoomKey;
            _mode = NetStatus.ModeName(status.Mode);
            _players = status.MaxPlayers > 0
                ? $"{status.Players}/{status.MaxPlayers}"
                : status.Players.ToString(CultureInfo.InvariantCulture);
            if (status.Latency >= 0)
            {
                _ping = status.Latency.ToString(CultureInfo.InvariantCulture);
                _pingBrush = new SolidColorBrush(PingColour(status.Latency));
            }
            else
            {
                _ping = "—";
                _pingBrush = GuiTheme.TextDimBrush;
            }
            InvalidateVisual();
        }

        /// <summary>
        /// The reference's three bands: under 60 ms, under 120, and over.
        ///
        /// The number matters far less than which of "fine", "playable" and
        /// "don't" it falls in, and a colour answers that without being read.
        /// </summary>
        public static Color PingColour(int ms) =>
            ms < 60 ? GuiTheme.Good : ms < 120 ? GuiTheme.Warn : GuiTheme.Bad;

        protected override void OnPointerEntered(PointerEventArgs e)
        {
            _hot = true;
            InvalidateVisual();
            base.OnPointerEntered(e);
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            _hot = false;
            _lean = 0;
            _tap.Cancel();
            InvalidateVisual();
            base.OnPointerExited(e);
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            // On the release, and only if the finger stayed: the browser's
            // list is scrolled as often as it is picked from. See Tap.
            _tap.Press(e, this);
            InvalidateVisual();
            base.OnPointerPressed(e);
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            if (_tap.Moved(e, this))
            {
                InvalidateVisual();
            }
            if (IsPointerOver && SlabHeight > 0)
            {
                // A row leans on one axis only. It is a bigger object than a
                // button and a yaw on it stops it lining up with the rows
                // above and below, which reads as the list coming apart --
                // so 2.2 degrees of pitch and no turn at all.
                double dy = (e.GetPosition(this).Y - SlabHeight / 2) / (SlabHeight / 2);
                double want = -Math.Clamp(dy, -1, 1) * 2.2;
                if (Math.Abs(want - _lean) > 0.05)
                {
                    _lean = want;
                    InvalidateVisual();
                }
            }
            base.OnPointerMoved(e);
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            bool tapped = _tap.Release(e, this);
            InvalidateVisual();
            if (tapped && IsLive)
            {
                Focus();
                Clicked?.Invoke(this, EventArgs.Empty);
            }
            base.OnPointerReleased(e);
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            _tap.Cancel();
            InvalidateVisual();
            base.OnPointerCaptureLost(e);
        }

        protected override void OnGotFocus(FocusChangedEventArgs e)
        {
            InvalidateVisual();
            base.OnGotFocus(e);
        }

        protected override void OnLostFocus(FocusChangedEventArgs e)
        {
            InvalidateVisual();
            base.OnLostFocus(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if ((e.Key == Key.Enter || e.Key == Key.Space) && IsLive)
            {
                Clicked?.Invoke(this, EventArgs.Empty);
                Activated?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                return;
            }
            base.OnKeyDown(e);
        }

        public override void Render(DrawingContext context)
        {
            if (Tactical) { RenderTactical(context); return; }
            double w = Bounds.Width;
            if (w <= 0)
            {
                return;
            }
            bool live = (_hot || IsFocused || _selected) && IsLive;
            double lip = live ? LipLive : Lip;
            double drop = _tap.Down && IsLive ? Lip : 0;
            var slab = new Rect(0, drop, w, SlabHeight);
            var round = new RoundedRect(slab, Radius);

            // Transparent fill first: an unfilled area is not hit-testable, so
            // the whole row has to be painted for the whole row to be
            // clickable. Same as UiWord.
            context.FillRectangle(Brushes.Transparent, new Rect(0, 0, w, Bounds.Height));

            // `--sc: 1.012` under the pointer, and a two-degree lean towards
            // it. A twelfth of what a button grows by: a row is five times
            // the width, and the same proportion on it reads as the list
            // breathing.
            double scale = _hot && !_tap.Down && IsLive ? 1.012 : 1;
            using DrawingContext.PushedState _pose = context.PushTransform(
                Avalonia.Matrix.CreateTranslation(-w / 2, -SlabHeight / 2)
                * Avalonia.Matrix.CreateScale(scale, scale)
                * Avalonia.Matrix.CreateRotation(_lean * Math.PI / 180 * 0.06)
                * Avalonia.Matrix.CreateTranslation(w / 2, SlabHeight / 2));

            // A row still waiting is drawn back rather than pulsed. The
            // reference animates it between .6 and .85 opacity; an animation
            // here is a redraw of the whole launcher surface every frame for
            // as long as any server has not answered, and the still it would
            // be photographed at is this.
            using DrawingContext.PushedState _wait = context.PushOpacity(_asking ? 0.72 : 1);

            // The reference's two shadows: a one-point ring in `--edge` and a
            // three-point solid edge under the slab. Live, the ring goes to
            // two points of blue-grey (or the accent, when this is the row the
            // foot is about) and the edge to five -- which is the whole of how
            // a row says it is under the pointer.
            //
            // A ring, not a stroke. `0 0 0 1px` spreads *outward* from the
            // box; a Pen straddles the edge, so the same number drawn as a
            // stroke lands half a point inside and turns every corner into a
            // slightly different curve from the one beside it.
            Color ring = _selected ? GuiTheme.Accent
                : live ? Deck.Rgb(0x4a6f8c) : GuiTheme.Edge;
            var shadows = new BoxShadows(
                Deck.Shadow(0, 0, 0, live || _selected ? 2 : 1, ring),
                new[] { Deck.Shadow(0, lip, 0, 0, Deck.Fade(0, live ? 0.5 : 0.45)) });
            context.DrawRectangle(new SolidColorBrush(GuiTheme.PanelDeep), null,
                round, shadows);

            DrawMap(context, round, slab, live);

            var columns = new Columns(w, Deck.Narrow(this));
            double top = drop;

            ServerBadge.Draw(context, _endpoint, _answered, columns.FlagX,
                top + Math.Round((SlabHeight - Columns.FlagHeight) / 2));

            // The name, with its tail in the accent, and a hard shadow under
            // it -- the reference's `text-shadow: 0 2px 0 rgba(0,0,0,.8)`,
            // which is what keeps a pale label legible over a map render.
            IBrush nameInk = _asking || !_answered
                ? GuiTheme.TextDimBrush : GuiTheme.TextBrush;
            DrawName(context, columns.NameX, columns.NameWidth, top, nameInk);

            // Brighter than the mode beside it: after the name, the map is
            // what anybody is actually scanning the list for.
            Cell(context, _map, columns.MapX, columns.MapWidth, top,
                1.02, display: true,
                _answered ? new SolidColorBrush(Deck.Rgb(0xcfd6e4)) : GuiTheme.TextDimBrush);

            if (!columns.Narrow)
            {
                Cell(context, _mode, columns.ModeX, columns.ModeWidth, top,
                    0.82, display: false, GuiTheme.TextDimBrush);
                Cell(context, _players, columns.PlayersX, columns.PlayersWidth, top,
                    0.86, display: false, GuiTheme.TextBrush, rightAlign: true);
            }
            Cell(context, _ping, columns.PingX, columns.PingWidth, top,
                0.86, display: false, _pingBrush, rightAlign: true);
        }

        private void RenderTactical(DrawingContext context)
        {
            var rect = new Rect(Bounds.Size);
            bool active = _selected || IsFocused || _hot;
            context.FillRectangle(active ? PrimeTheme.PanelHighlightBrush : PrimeTheme.PanelBrush, rect);
            context.DrawRectangle(new Pen(active ? PrimeTheme.HighlightBrush : PrimeTheme.BorderBrush, 1), rect.Deflate(.5));
            context.FillRectangle(active ? PrimeTheme.PrimaryBrush : PrimeTheme.BorderBrush, new Rect(1, 8, 5, Math.Max(0, Bounds.Height - 16)));
            void Text(string value, double x, double y, double width, double size, IBrush ink, bool mono = false)
            {
                if (width <= 0) return;
                var laid = DeckText.Run(value, mono ? Deck.Body(bold: true) : Deck.Label(strong: true), size, ink, width);
                using (context.PushClip(new Rect(x, y, width, 24))) context.DrawText(laid, new Point(x, y));
            }
            double main = Math.Max(0, Bounds.Width - 200);
            Text(_name.ToUpperInvariant(), 20, 11, main, 20, PrimeTheme.TextBrush);
            Text((_map + "  //  " + _mode).ToUpperInvariant(), 20, 43, main, 11, PrimeTheme.TextSecondaryBrush, true);
            Text(_players + " PLAYERS", Bounds.Width - 164, 11, 150, 12, PrimeTheme.TextBrush, true);
            Text(_ping + " MS // " + _availability, Bounds.Width - 190, 43, 178, 10,
                CanJoin ? PrimeTheme.GreenBrush : PrimeTheme.DangerBrush, true);
        }

        /// <summary>
        /// The map behind the row, dimmed hard.
        ///
        /// A row is read left to right and a picture at full strength under a
        /// line of type kills the type; the selected and hovered rows get more
        /// of it, which is what makes the picture an answer to "which map is
        /// that" rather than wallpaper. A row still waiting gets it blurred
        /// and grey, because the map it is showing is a guess until the server
        /// says so. No picture on a machine that has not run the thumbnail
        /// pass, and the row is then exactly what it was.
        /// </summary>
        private void DrawMap(DrawingContext context, RoundedRect round, Rect slab, bool live)
        {
            if (MapShot.For(_answered ? _roomKey : null) is not Bitmap shot)
            {
                return;
            }
            using (context.PushOpacity(live ? 0.72 : 0.5))
            using (context.PushClip(round))
            {
                // `object-fit: cover` by hand: the row is far wider than a 4:3
                // render, so the crop is vertical and centred on the middle of
                // the picture, which is where a map render's subject is.
                double scale = slab.Width / shot.Size.Width;
                double height = shot.Size.Height * scale;
                context.DrawImage(shot, new Rect(slab.X,
                    slab.Y + (slab.Height - height) / 2, slab.Width, height));
            }
            // `.srow .scrim`: darkest at both ends and lightest at 45%, which
            // is the band the name and the map sit in. A flat scrim was what
            // made the picture look like noise rather than a background.
            using (context.PushClip(round))
            {
                context.FillRectangle(new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                    GradientStops =
                    {
                        new GradientStop(Color.FromArgb(240, 10, 12, 16), 0),
                        new GradientStop(Color.FromArgb(184, 10, 12, 16), 0.45),
                        new GradientStop(Color.FromArgb(224, 10, 12, 16), 1)
                    }
                }, slab);
            }
        }

        /// <summary>
        /// One cell: a single line, trimmed to its column and clipped to it
        /// whatever the trimming decides.
        ///
        /// Both halves are needed. Without a height limit Avalonia wraps at
        /// the first space rather than ellipsizing, and a wrapped cell in a
        /// thirty-point row draws its second line over the row beneath -- which
        /// is what a two-word map name did. And trimming cannot help a single
        /// unbreakable word wider than its column: the clip is what makes that
        /// impossible rather than unlikely.
        /// </summary>
        private static void Cell(DrawingContext context, string text, double x, double width,
            double top, double sizeEms, bool display, IBrush ink, bool rightAlign = false)
        {
            if (text.Length == 0 || width <= 2)
            {
                return;
            }
            double size = Deck.RowEm * sizeEms;
            FormattedText laid = DeckText.Run(text,
                display ? Deck.Label(strong: false) : Deck.Body(bold: false), size, ink,
                Math.Round(width));
            double left = rightAlign ? x + width - Math.Min(laid.Width, width) : x;
            using (context.PushClip(new Rect(Math.Round(x), top,
                Math.Round(width), SlabHeight)))
            {
                context.DrawText(laid, new Point(Math.Round(left),
                    Math.Round(top + (SlabHeight - laid.Height) / 2)));
            }
        }

        /// <summary>
        /// The name, with its tail in the accent.
        ///
        /// Server names are nearly always a stem and a qualifier -- a TLD, a
        /// number, a mode -- and the qualifier is what tells two rows of the
        /// same operator apart. Colouring it is the cheapest way to make a list
        /// of "Project Prime - West US 2 / West Europe / Japan" scannable, and
        /// it costs nothing when a name has no tail. Split on the last
        /// separator only, and only when what follows is short: a long tail is
        /// part of the name, not a qualifier.
        /// </summary>
        private void DrawName(DrawingContext context, double x, double width, double top,
            IBrush ink)
        {
            if (width <= 2)
            {
                return;
            }
            double size = Deck.RowEm * 1.2;
            int cut = -1;
            for (int i = _name.Length - 1; i > 0 && _name.Length - i <= 7; i--)
            {
                if (_name[i] == '.' || _name[i] == '#')
                {
                    cut = i;
                    break;
                }
                if (_name[i] == '-')
                {
                    break;
                }
            }
            string stem = cut > 0 && cut < _name.Length - 1 ? _name[..cut] : _name;
            string tail = stem.Length == _name.Length ? "" : _name[cut..];

            using DrawingContext.PushedState _ = context.PushClip(
                new Rect(Math.Round(x), top, Math.Round(width), SlabHeight));
            double pen = x;
            pen += Shadowed(context, stem, size, ink, pen, top, width);
            if (tail.Length > 0)
            {
                double left = width - (pen - x);
                if (left > size * 0.5)
                {
                    Shadowed(context, tail, size, GuiTheme.AccentBrush, pen, top, left);
                }
            }
        }

        /// <summary>
        /// `text-shadow: 0 2px 0 rgba(0,0,0,.8)`: the same run again, two
        /// points down, in near-black. Not a blur -- a hard offset is what the
        /// reference sets and what keeps a pixel face crisp over a picture.
        /// </summary>
        private static double Shadowed(DrawingContext context, string text, double size,
            IBrush ink, double x, double top, double width)
        {
            var shadow = new SolidColorBrush(Color.FromArgb(204, 0, 0, 0));
            FormattedText under = DeckText.Run(text, Deck.Label(strong: false), size,
                shadow, Math.Round(width));
            FormattedText over = DeckText.Run(text, Deck.Label(strong: false), size, ink,
                Math.Round(width));
            double y = Math.Round(top + (SlabHeight - over.Height) / 2);
            context.DrawText(under, new Point(Math.Round(x), y + 2));
            context.DrawText(over, new Point(Math.Round(x), y));
            return Math.Min(over.Width, width);
        }

        public string Endpoint => _endpoint;

        /// <summary>
        /// The map this server said it was running, or nothing until it has
        /// answered. What the preview beside the list is drawn from.
        /// </summary>
        public string RoomKey => _answered ? _roomKey : "";

        /// <summary>
        /// What the drawer beside the list puts in its facts: the same five
        /// the reference's `.side` shows.
        ///
        /// Read off the row rather than asked of the directory a second time,
        /// because the row already holds the answer the server gave and a
        /// second query would be a different one -- the panel would show a
        /// ping and a count from a moment the list is not displaying.
        /// </summary>
        public string DisplayName => _name;

        public string MapName => _map;

        public string ModeName => _mode;

        public string PlayerCount => _players;

        public string PingText => _ping;

        public IBrush PingBrush => _pingBrush;
    }
}
#endif
