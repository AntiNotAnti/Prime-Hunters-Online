using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MphRead.Entities;
using MphRead.Mods;
using MphRead.Mods.Network;
using MphRead.Mods.Replay;

namespace MphRead.Mods.Launcher.Gui
{
    /// <summary>
    /// Everything that answers "what are we playing", on one screen.
    ///
    /// It replaces seven: a host card, a join card, a server browser, an
    /// adventure card, a demo list, a map grid and a vote picker. All seven
    /// were a list of things to pick from and a handful of settings about the
    /// pick, and each had invented its own layout for that -- which is why
    /// joining a server took five presses and starting an offline match took
    /// six.
    ///
    /// So there is one list, one column of settings beside it, and a strip
    /// over the top saying which of the four sources the list is showing.
    /// Changing the strip rebuilds both, because the settings that mean
    /// anything are different for each: bots and a match type belong to a
    /// local game and nothing else, and a greyed-out row that can never be
    /// used is a row that should not be drawn.
    ///
    /// The fifth face, <see cref="Face.Vote"/>, is the same screen opened from
    /// the pause menu with the strip taken away: calling a vote is picking a
    /// map, and picking a map is what this screen does.
    /// </summary>
    internal sealed class PlayScreen : UserControl
    {
        public enum Face
        {
            Online,
            Offline,
            Story,
            Clips,
            Vote
        }

        /// <summary>Raised when the player backed out without choosing anything.</summary>
        public event EventHandler? Closed;

        /// <summary>Raised with a match to start. The screen is finished with by then.</summary>
        public event EventHandler<LaunchPlan>? Launched;

        /// <summary>Vote mode only: the map to put to the room.</summary>
        public event EventHandler<string>? Voted;

        /// <summary>
        /// Online only: run a server rather than join one.
        ///
        /// Raised rather than handled here because the screen that answers it
        /// is a screen, not a panel: it is pushed onto the same stack this one
        /// is on, so it gets the window, the keyboard and the back mark that
        /// every other screen has, and this one is not holding a second layout
        /// it shows a tenth of the time.
        /// </summary>
        public event EventHandler? CreateRequested;

        private static readonly (string Label, GameMode Mode)[] _modes =
        {
            ("Battle", GameMode.Battle),
            ("Battle teams", GameMode.BattleTeams),
            ("Survival", GameMode.Survival),
            ("Survival teams", GameMode.SurvivalTeams),
            ("Capture", GameMode.Capture),
            ("Bounty", GameMode.Bounty),
            ("Bounty teams", GameMode.BountyTeams),
            ("Defender", GameMode.Defender),
            ("Defender teams", GameMode.DefenderTeams),
            ("Nodes", GameMode.Nodes),
            ("Nodes teams", GameMode.NodesTeams),
            ("Prime hunter", GameMode.PrimeHunter)
        };

        private static readonly string[] _hunters =
            Enumerable.Range(0, Hunters.Playable).Select(i => ((Hunter)i).ToString())
                .Append(Hunter.Random.ToString()).ToArray();

        private readonly MenuSettings _settings;
        private readonly List<string> _rooms;
        private readonly bool _overGame;
        private readonly Face _only;

        private readonly UiTabs? _tabs;
        private readonly UiList _list = new();
        private EventHandler<Control>? _replaySelection;
        private readonly StackPanel _options = new() { Spacing = 2, Width = 300 };
        private readonly Image _preview = new() { Stretch = Stretch.UniformToFill };
        private readonly Border _previewBox;

        /// <summary>
        /// The hunter, turning, at the map picture's right-hand end -- see
        /// <see cref="AddHunter"/> for why it is here and not under the row
        /// that names it. Only the faces that ask which hunter you are taking
        /// in show it.
        /// </summary>
        private readonly HunterStand _stand;

        /// <summary>The three things the well holds, kept so the shape can change.</summary>
        private readonly Grid _body;
        private readonly ScrollViewer _side;
        private bool _compact;

        /// <summary>
        /// Whether this face has a picture to show, as against whether it is
        /// being drawn. Two questions since the compact arrangement exists:
        /// every face still says what it wants, and the box says whether
        /// there is room for it.
        /// </summary>
        private bool _previewWanted;
        private readonly Note _note = new("");
        private readonly UiMark _go;
        private readonly UiMark _back;
        private readonly UiMark _createLobby;

        private DispatcherTimer? _statusTimer;
        private CancellationTokenSource? _statusCancel;
        private DispatcherTimer? _replayPreviewTimer;
        private int _replayPreviewIndex;
        private string? _replaySelectedPath;
        private string _replaySearchText = "";
        private int _replayFilterIndex;
        private int _replaySortIndex;
        private int _replayViewIndex; // 0 grid, 1 list
        private bool _finished;

        private ChoiceRow? _hunter;
        private ChoiceRow? _mode;
        private ChoiceRow? _bots;
        private ChoiceRow? _skill;
        private ChoiceRow? _resume;
        private DeckField? _name;
        private DeckField? _address;

        /// <summary>
        /// Picture above the list, or options beside it.
        ///
        /// The stacked arrangement -- what you are choosing at the top, with
        /// its picture, and everything else underneath -- wants about 190
        /// points before the list gets any, and it is the right shape
        /// wherever there is height for it. A phone in landscape has 390
        /// points in total (see <c>UiScaleHost</c>), so the top block and the
        /// well's own margins took all of it and the list was arranged one
        /// row tall behind the footer: a server browser with no servers in
        /// it, again, for a different reason than the last one.
        ///
        /// So on a short box the two swap axes. The options go down the right
        /// at the width they were drawn for, the list takes the whole height
        /// on the left, and the picture goes under the options in the space
        /// they do not use -- which is the arrangement this screen had before
        /// the picture was made big, with the picture kept. It is a band
        /// rather than a landscape box there, and that is enough: the
        /// question it answers is "which map is that". Keyed on the height
        /// actually handed over rather than on the platform, so a tablet in
        /// landscape keeps the desktop shape.
        /// </summary>
        private void SetCompact(bool compact)
        {
            if (compact == _compact)
            {
                return;
            }
            _compact = compact;
            _previewBox.IsVisible = _previewWanted;
            _stand.IsVisible = _standWanted && _previewWanted;
            // A band rather than a landscape box leaves less to stand in.
            _stand.Height = compact ? 110 : 150;
            if (_bar != null)
            {
                // The online face lays row 0 out itself; letting the compact
                // rule move the list back over it is how the column headings
                // ended up on top of the address box.
                return;
            }
            if (compact)
            {
                // The list down the left, the options and the picture down the
                // right. The column is already as wide as the options asked
                // for and they do not fill the height, so the picture takes
                // what is under them: a band rather than the landscape box it
                // is on a desktop, which is what UniformToFill and a clipping
                // border were already there for.
                _side.MaxHeight = Double.PositiveInfinity;
                Grid.SetRow(_side, 0);
                Grid.SetRowSpan(_side, 1);
                _side.Margin = new Thickness(18, 0, 0, 0);
                Grid.SetColumn(_previewBox, 1);
                Grid.SetRow(_previewBox, 1);
                Grid.SetColumn(_stand, 1);
                Grid.SetRow(_stand, 1);
                // Stretched into what is left rather than given a height:
                // whatever that is, it is the room there is, and a fixed
                // number here would either overlap the line under the list on
                // the shortest screens or leave a gap on the tallest. Capped
                // so that a tall-enough box does not hand a 300-point-wide
                // thumbnail half the screen.
                _previewBox.Height = Double.NaN;
                _previewBox.MaxHeight = 150;
                _previewBox.VerticalAlignment = VerticalAlignment.Stretch;
                _previewBox.Margin = new Thickness(18, 12, 0, 0);
                _stand.Margin = _previewBox.Margin;
                Grid.SetRow(_list, 0);
                Grid.SetRowSpan(_list, 2);
                Grid.SetColumnSpan(_list, 1);
                if (_replayGridScroll != null)
                {
                    Grid.SetRow(_replayGridScroll, 0);
                    Grid.SetRowSpan(_replayGridScroll, 2);
                    Grid.SetColumnSpan(_replayGridScroll, 1);
                }
            }
            else
            {
                _side.MaxHeight = 190;
                Grid.SetRow(_side, 0);
                Grid.SetRowSpan(_side, 1);
                _side.Margin = new Thickness(0);
                Grid.SetColumn(_stand, 0);
                Grid.SetRow(_stand, 0);
                Grid.SetColumn(_previewBox, 0);
                Grid.SetRow(_previewBox, 0);
                _previewBox.Height = 172;
                _previewBox.MaxHeight = Double.PositiveInfinity;
                _previewBox.VerticalAlignment = VerticalAlignment.Stretch;
                _previewBox.Margin = new Thickness(0, 0, 24, 14);
                _stand.Margin = _previewBox.Margin;
                Grid.SetRow(_list, 1);
                Grid.SetRowSpan(_list, 1);
                Grid.SetColumnSpan(_list, 2);
                if (_replayGridScroll != null)
                {
                    Grid.SetRow(_replayGridScroll, 1);
                    Grid.SetRowSpan(_replayGridScroll, 1);
                    Grid.SetColumnSpan(_replayGridScroll, 2);
                }
            }
        }

        /// <summary>
        /// Undo what a face added outside the options column. Only the online
        /// face has anything here, and it has to go before the next face lays
        /// its own row 0 out on top of it.
        /// </summary>
        private void ClearFaceExtras()
        {
            if (_bar != null)
            {
                _body.Children.Remove(_bar);
                _bar = null;
            }
            _side.IsVisible = true;
        }

        /// <summary>This face wants the picture; whether it gets it is the box's.</summary>
        private void WantPreview(bool wanted)
        {
            _previewWanted = wanted;
            _previewBox.IsVisible = wanted;
            _stand.IsVisible = _standWanted && wanted;
        }

        /// <summary>Whether this face asks which hunter you are taking in.</summary>
        private bool _standWanted;

        protected override Size MeasureOverride(Size availableSize)
        {
            if (!Double.IsInfinity(availableSize.Height) && availableSize.Height > 0)
            {
                SetCompact(Current == Face.Clips || availableSize.Height < UiLayout.ShortBox);
                _side.MaxHeight = Current == Face.Clips ? Math.Max(100, availableSize.Height - 190) : _compact ? Double.PositiveInfinity : 190;
            }
            return base.MeasureOverride(availableSize);
        }

        public PlayScreen(MenuSettings settings, IReadOnlyList<string> rooms,
            Face face = Face.Online, bool overGame = false, bool singleFace = false)
        {
            _settings = settings;
            _rooms = new List<string>(rooms);
            _overGame = overGame;
            _only = face;
            Background = Brushes.Transparent;
            Focusable = true;

            _previewBox = new Border
            {
                Height = 172,
                CornerRadius = new CornerRadius(3),
                ClipToBounds = true,
                IsVisible = false,
                Child = _preview
            };

            // In the picture's cell rather than a cell of its own: a column
            // for it would take width off the picture on every face,
            // including the ones with no hunter to show.
            _stand = new HunterStand
            {
                Width = 96,
                Height = 150,
                IsVisible = false,
                IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Name2 = _hunters[0]
            };

            // The picture above the list rather than beside it, and the
            // settings about it to its right.
            //
            // A centred well has no side to hang a column off without
            // stopping being centred, so the three things this screen holds
            // are stacked instead: what you are choosing at the top -- its
            // picture, and the settings that belong to it -- and the list of
            // everything you could choose instead underneath, running the full
            // width of the well. The old arrangement put the picture in the
            // corner of the options column, a third of the size, and the
            // picture is the answer to "which map is that".
            //
            // The preview stretches and the options do not: a room's picture
            // is better bigger, and a row of settings at 320 is the width it
            // was drawn for.
            // `.body`: a scrolling column with a `.6em` gap and `.3em` of
            // padding on the right, which is the gutter the scrollbar lives
            // in. Without it the list runs to the panel's inside edge and the
            // thumb is drawn over the last column.
            var body = new BodyGrid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                RowDefinitions = new RowDefinitions("Auto,*")
            };
            _body = body;
            _previewBox.Margin = new Thickness(0, 0, 24, 14);
            Grid.SetColumn(_previewBox, 0);
            Grid.SetRow(_previewBox, 0);
            body.Children.Add(_previewBox);
            Grid.SetColumn(_stand, 0);
            Grid.SetRow(_stand, 0);
            body.Children.Add(_stand);
            // Scrolled, because a short window gives this row less height than
            // the options asked for -- and a StackPanel given less height than
            // it wants does not shrink and is not clipped, it simply draws
            // past the bottom of its row. That was the tick being covered by
            // the rows above it, which is a layout fault rather than a
            // z-order one.
            var side = new ScrollViewer
            {
                Content = _options,
                MaxHeight = 190,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            _side = side;
            Grid.SetColumn(side, 1);
            Grid.SetRow(side, 0);
            body.Children.Add(side);
            Grid.SetColumn(_list, 0);
            Grid.SetColumnSpan(_list, 2);
            Grid.SetRow(_list, 1);
            body.Children.Add(_list);

            // Offline's own face: every map at once, as pictures. It shares
            // the body's second row with the list and only one of the two is
            // ever up -- a grid of cards and a column of names are two answers
            // to the same question, and the reference only asks it the one
            // way.
            _grid = new DeckGrid();
            _gridScroll = new ScrollViewer
            {
                Content = _grid,
                IsVisible = false,
                // So the cards scrolled out of sight are clipped away rather
                // than composited and then covered: twenty-one cards are in
                // this grid and about nine are on screen.
                ClipToBounds = true,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            Grid.SetColumn(_gridScroll, 0);
            Grid.SetColumnSpan(_gridScroll, 2);
            Grid.SetRow(_gridScroll, 1);
            body.Children.Add(_gridScroll);

            _replayGrid = new DeckGrid { Ratio = 16d / 9d };
            _replayGridScroll = new ScrollViewer
            {
                Content = _replayGrid,
                IsVisible = false,
                ClipToBounds = true,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            Grid.SetColumn(_replayGridScroll, 0);
            Grid.SetColumnSpan(_replayGridScroll, 2);
            Grid.SetRow(_replayGridScroll, 1);
            body.Children.Add(_replayGridScroll);

            _back = new UiMark(UiMark.Shape.Cancel, "back");
            _back.Click += (_, _) => Leave();
            _go = new UiMark(UiMark.Shape.Accept, "play");
            _go.Click += (_, _) => Go();
            _createLobby = new UiMark(UiMark.Shape.Add, "create lobby") { IsVisible = face == Face.Online };
            _createLobby.Click += (_, _) => CreateRequested?.Invoke(this, EventArgs.Empty);
            // One commit on the right, not two. The browser used to carry
            // both CREATE SERVER and JOIN down there, which is a foot offering
            // two acts of equal weight when only one of them is ever the one
            // meant -- and the reference has a single blue button whose word
            // is whatever the list is currently about. Nothing picked, and it
            // creates; a row picked, and it joins that row.

            if (face != Face.Vote && !singleFace)
            {
                _tabs = new UiTabs(new[] { "Online", "Offline", "Story", "Replays" },
                    (int)face);
                _tabs.Changed += (_, _) => Rebuild();
            }
            string heading = face == Face.Vote ? "vote"
                : singleFace && face == Face.Clips ? "replay studio"
                : singleFace && face == Face.Offline ? "offline"
                : singleFace && face == Face.Story ? "adventure"
                : "play";
            Panel page = UiLayout.Page(overGame, UiLayout.WellPlay,
                heading, _tabs, body, _back, _go,
                extra: _createLobby, note: _note);
            // Over the sheet, not inside the panel: the reference's `.side` is
            // a sibling of the sheet and slides in past its right edge, which
            // is what makes it read as a drawer the panel opened rather than
            // as another column of it.
            _sidePanel = BuildSidePanel();
            page.Children.Add(_sidePanel);
            Content = page;

            _list.Activated += (_, _) => Go();
            // Subscribed once, not per face: the list outlives every rebuild,
            // and a handler added each time round is a handler added twenty
            // times by the time somebody has looked at each face five times.
            _list.SelectionChanged += (_, _) =>
            {
                RefreshPreview();
                RefreshStory();
                RefreshGoLabel();
                // A server that answered opens the drawer beside the list, the
                // way a map card does: the reference has one `.side` for both
                // because they are the same question asked of two things.
                if (Current == Face.Online && _list.Selected is ServerRow row)
                {
                    OpenServerSide(row);
                }
            };
            Rebuild();
        }

        private DeckGrid? _grid;
        private ScrollViewer? _gridScroll;
        private DeckGrid? _replayGrid;
        private ScrollViewer? _replayGridScroll;
        private DeckSide? _sidePanel;
        private StackPanel? _sideFacts;
        private TextBlock? _sideNameText;
        private TextBlock? _sideAddr;
        private DeckChip? _sideCode;
        private HunterStand? _sideStand;
        private DeckButton? _sideGo;
        private string? _picked;

        /// <summary>
        /// The drawer the reference opens when a map is picked: what it is at
        /// the top, the hunter and the rules in the middle, Back and START
        /// along the foot.
        ///
        /// Built once and refilled, not rebuilt per map: it carries a hunter
        /// turntable and three stepper rows, and standing those up again on
        /// every card press would throw away whatever the turntable was in the
        /// middle of.
        /// </summary>
        private DeckSide BuildSidePanel()
        {
            // `.code`: a bare badge, no key -- see DeckChip.Label.
            _sideCode = new DeckChip("", "");
            _sideNameText = new TextBlock
            {
                FontFamily = GuiTheme.Display,
                FontSize = 17,
                Foreground = GuiTheme.TextBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            _sideAddr = new TextBlock
            {
                FontFamily = GuiTheme.Display,
                FontSize = 11,
                Foreground = GuiTheme.TextDimBrush,
                TextWrapping = TextWrapping.Wrap
            };
            var close = new DeckButton("x", Deck.Face.Slate,
                sizeEms: 1, padXEms: 0.6, padYEms: 0.3, lip: 2);
            close.Click += (_, _) => CloseSide();

            var headLine = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            Grid.SetColumn(_sideCode, 0);
            headLine.Children.Add(_sideCode);
            _sideNameText.Margin = new Thickness(8, 0, 8, 0);
            Grid.SetColumn(_sideNameText, 1);
            headLine.Children.Add(_sideNameText);
            Grid.SetColumn(close, 2);
            headLine.Children.Add(close);

            _sideStand = new HunterStand
            {
                Height = 150,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Name2 = _hunters[0]
            };
            _sideFacts = new StackPanel { Spacing = 4 };

            var bodyStack = new StackPanel { Spacing = 8 };
            bodyStack.Children.Add(_sideStand);
            bodyStack.Children.Add(_sideFacts);
            var scroll = new ScrollViewer
            {
                Content = bodyStack,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };

            var back = new DeckButton("Back", Deck.Face.Brass,
                sizeEms: 1.1, padXEms: 0.9, padYEms: 0.5, lip: 5);
            back.Click += (_, _) => CloseSide();
            _sideGo = new DeckButton("START", Deck.Face.Moss,
                sizeEms: 1.4, padXEms: 1, padYEms: 0.45, lip: 6);
            // The same act the foot's own tick performs. Two buttons, one
            // decision -- the drawer carries it because that is where the
            // player is looking once it is open, and the foot keeps it for the
            // keyboard.
            _sideGo.Click += (_, _) => Go();
            var foot = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            Grid.SetColumn(back, 0);
            foot.Children.Add(back);
            _sideGo.Margin = new Thickness(6, 0, 0, 0);
            _sideGo.HorizontalAlignment = HorizontalAlignment.Stretch;
            Grid.SetColumn(_sideGo, 1);
            foot.Children.Add(_sideGo);

            var stack = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,*,Auto"),
                RowSpacing = 8
            };
            var head = new StackPanel { Spacing = 3 };
            head.Children.Add(headLine);
            head.Children.Add(_sideAddr);
            Grid.SetRow(head, 0);
            stack.Children.Add(head);
            Grid.SetRow(scroll, 1);
            stack.Children.Add(scroll);
            Grid.SetRow(foot, 2);
            stack.Children.Add(foot);

            var card = new DeckCard { Child = stack, MaxWidthEms = 17.5 };
            return new DeckSide { Child = card, Margin = new Thickness(0, 14, 12, 14) };
        }

        /// <summary>
        /// The drawer for a server: what it is running, who is on it, the
        /// hunter you are taking in, and the one word that joins.
        ///
        /// The same object as the map drawer, because they are the same
        /// question -- something chosen on the left, the hunter you take into
        /// it, one button -- which is why the reference has one `.side` and
        /// not two. Only the middle differs: a server's facts and roster,
        /// against a map's rules.
        /// </summary>
        private void OpenServerSide(ServerRow row)
        {
            if (!row.IsLive)
            {
                return;
            }
            if (_sideCode != null)
            {
                // The reference puts the server's flag here. The mode is the
                // shortest true thing this row can hand over without asking
                // the directory again.
                _sideCode.Text = row.ModeName.Length > 0 ? row.ModeName : "server";
            }
            if (_sideNameText != null)
            {
                _sideNameText.Text = row.DisplayName;
            }
            if (_sideAddr != null)
            {
                _sideAddr.Text = row.Endpoint;
            }
            if (_sideFacts != null)
            {
                _sideFacts.Children.Clear();
                Fact(_sideFacts, "Map", row.MapName, null);
                Fact(_sideFacts, "Mode", row.ModeName, null);
                Fact(_sideFacts, "Players", row.PlayerCount, null);
                Fact(_sideFacts, "Ping", row.PingText + " ms", row.PingBrush);
                // The hunter rows come after the facts here, where on the
                // offline face they *are* the facts: joining somebody else's
                // server settles the map and the mode, so the only thing left
                // to choose is who you arrive as.
                if (_hunter != null)
                {
                    _sideFacts.Children.Add(_hunter);
                }
                if (_suit != null)
                {
                    _sideFacts.Children.Add(_suit);
                }
            }
            if (_sideGo != null)
            {
                _sideGo.Text = "JOIN";
            }
            RefreshSideHunter();
            if (_sidePanel != null)
            {
                _sidePanel.Open = true;
            }
        }

        /// <summary>One `.fact`: a dim key on the left, the value on the right.</summary>
        private static void Fact(Panel into, string key, string value, IBrush? tint)
        {
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Height = 26
            };
            var k = new TextBlock
            {
                Text = key.ToUpperInvariant(),
                FontFamily = GuiTheme.Display,
                FontSize = 10,
                Foreground = GuiTheme.TextDimBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
            var v = new TextBlock
            {
                Text = value,
                FontFamily = GuiTheme.Display,
                FontSize = 12,
                Foreground = tint ?? GuiTheme.TextBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(k, 0);
            row.Children.Add(k);
            Grid.SetColumn(v, 1);
            row.Children.Add(v);
            into.Children.Add(row);
        }

        /// <summary>
        /// Show the drawer for a map, and put the rules that belong to it in.
        /// </summary>
        private void OpenSide(DeckTile tile)
        {
            _picked = tile.RoomKey;
            if (_grid != null)
            {
                foreach (Control child in _grid.Children)
                {
                    if (child is DeckTile other)
                    {
                        other.Chosen = other.RoomKey == tile.RoomKey;
                    }
                }
            }
            if (_sideCode != null)
            {
                _sideCode.Text = tile.Code;
            }
            if (_sideNameText != null)
            {
                (RoomMetadata? meta, _) = Metadata.GetRoomByName(tile.RoomKey);
                _sideNameText.Text = meta?.InGameName ?? tile.RoomKey;
            }
            if (_sideAddr != null)
            {
                _sideAddr.Text = "offline \u2014 bots on this machine";
            }
            // The steppers move into the drawer: they are about the match this
            // map is going to be, and the reference asks them where the map
            // was chosen rather than on the far side of the screen.
            if (_sideFacts != null)
            {
                _sideFacts.Children.Clear();
                foreach (Control row in _offlineRows)
                {
                    _sideFacts.Children.Add(row);
                }
            }
            if (_sideGo != null)
            {
                _sideGo.Text = "START";
            }
            if (_sidePanel != null)
            {
                _sidePanel.Open = true;
            }
            _note.Text = "";
        }

        /// <summary>
        /// Picking a map is proposing it, picking the one already picked takes
        /// it back. One vote each, so the previous one is given up first.
        /// </summary>
        private void CastVote(DeckTile tile)
        {
            if (_grid == null)
            {
                return;
            }
            bool taking = !tile.Chosen;
            foreach (Control child in _grid.Children)
            {
                if (child is DeckTile other && other.Chosen)
                {
                    other.Chosen = false;
                    other.Tally = Math.Max(0, other.Tally - 1);
                }
            }
            if (taking)
            {
                tile.Chosen = true;
                tile.Tally = Math.Max(0, tile.Tally) + 1;
                _picked = tile.RoomKey;
            }
            else
            {
                _picked = null;
            }
            RefreshBallot();
        }

        private void CloseSide()
        {
            if (_sidePanel != null)
            {
                _sidePanel.Open = false;
            }
        }

        /// <summary>The rows the drawer borrows, kept alive across openings.</summary>
        private readonly List<Control> _offlineRows = new();

        private Face Current => _tabs == null ? _only : (Face)_tabs.Index;

        /// <summary>
        /// The one blue button says what it is about to do, and on the browser
        /// that depends on whether a row is picked.
        ///
        /// Nothing picked and there is nothing to join, so the offer is the
        /// other one the screen has: run a server of your own. It is the same
        /// rule the reference uses and it is what lets the foot carry one act
        /// instead of two competing ones.
        /// </summary>
        private void RefreshGoLabel()
        {
            if (Current != Face.Online)
            {
                return;
            }
            _go.Label = "join";
            _go.IsEnabled = _list.Selected is ServerRow;
            _createLobby.IsVisible = true;
        }

        /// <summary>
        /// `.body { gap: .6em; padding-right: .3em }`, in ems.
        /// </summary>
        private sealed class BodyGrid : Grid
        {
            protected override Size MeasureOverride(Size availableSize)
            {
                double em = Deck.GetEm(this);
                double gap = Math.Round(em * 0.6);
                if (Math.Abs(gap - RowSpacing) > 0.01)
                {
                    RowSpacing = gap;
                }
                var want = new Thickness(0, 0, Math.Round(em * 0.3), 0);
                if (Margin != want)
                {
                    Margin = want;
                }
                return base.MeasureOverride(availableSize);
            }
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            // The way out takes the keyboard, not the list. A screen that
            // opens with a row already under the caret has picked for you, and
            // Escape is the one key somebody arriving on a screen they did not
            // mean to open is reaching for.
            if (Current == Face.Online)
            {
                Dispatcher.UIThread.Post(() => _back.Focus());
            }
            else
            {
                _list.FocusFirst();
            }
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            StopPolling();
            base.OnDetachedFromVisualTree(e);
        }

        /// <summary>
        /// The keys the screen owns rather than any one control on it.
        ///
        /// Reached only when nothing under them wanted the key: a row does not
        /// handle up and down, so those arrive here and walk the list; a
        /// setting beside the list *does* handle left and right, so those
        /// never reach here while the keyboard is on one. Which is the
        /// behaviour wanted in both cases and not a special case anywhere.
        /// </summary>
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Leave();
                e.Handled = true;
                return;
            }
            if (Current == Face.Clips && _replayViewIndex == 0
                && _replayGrid != null && _replayGrid.HandleKey(e.Key))
            {
                e.Handled = true;
                return;
            }
            if (_list.IsVisible && _list.HandleKey(e.Key))
            {
                e.Handled = true;
                return;
            }
            if (_tabs != null && _tabs.HandleKey(e.Key))
            {
                e.Handled = true;
                return;
            }
            base.OnKeyDown(e);
        }

        private void Leave()
        {
            if (_finished)
            {
                return;
            }
            _finished = true;
            StopPolling();
            Closed?.Invoke(this, EventArgs.Empty);
        }

        private void Finish(LaunchPlan plan)
        {
            if (_finished)
            {
                return;
            }
            _finished = true;
            StopPolling();
            Launched?.Invoke(this, plan);
        }

        // ------------------------------------------------------------- shaping

        /// <summary>
        /// Throw the list and the settings away and build the face that is up.
        ///
        /// Rebuilt rather than hidden and shown: the four faces share no row
        /// at all -- even "Hunter" means a different preference on each -- and
        /// a screen holding four sets of controls, three of them invisible, is
        /// how the card it replaces grew to two thousand lines.
        /// </summary>
        private void Rebuild()
        {
            StopPolling();
            StopReplayPreview();
            if (_replaySelection != null) { _list.SelectionChanged -= _replaySelection; _replaySelection = null; }
            _list.Clear();
            _list.SetHeader(null);
            // What the tick does, in the word for this face. It is the only
            // thing that starts anything now -- a click on a row selects it
            // and nothing more -- so it has to say which of the four it is
            // about to do.
            // Joining somebody else's server is a different act from starting
            // a match, and is the only one that keeps its own word. Offline
            // and Story both start something of your own, so they say the same
            // thing -- "play" on one and "start" on the other was two words
            // for one action, which is the habit this screen exists to break.
            _go.Label = Current switch
            {
                // Nothing is picked yet on a browser that has just been
                // rebuilt, so the word is the one for the act that needs no
                // selection. Picking a row changes it -- see SelectServer.
                Face.Online => "join",
                Face.Clips => "watch",
                Face.Vote => "vote",
                _ => "start"
            };
            _go.IsEnabled = true;
            _createLobby.IsVisible = Current == Face.Online;
            _options.Children.Clear();
            ClearFaceExtras();
            _note.Text = "";
            _note.Foreground = GuiTheme.TextDimBrush;
            _hunter = _mode = _bots = _skill = _resume = null;
            _name = _address = null;
            _standWanted = false;
            WantPreview(false);
            bool offline = Current == Face.Offline || Current == Face.Vote;
            if (_gridScroll != null)
            {
                _gridScroll.IsVisible = offline;
            }
            if (_replayGridScroll != null)
            {
                _replayGridScroll.IsVisible = false;
                _replayGrid?.Children.Clear();
            }
            _list.IsVisible = !offline;
            if (_sidePanel != null && !offline)
            {
                _sidePanel.Open = false;
            }
            _offlineRows.Clear();
            if (_sideFacts != null)
            {
                _sideFacts.Children.Clear();
            }

            switch (Current)
            {
            case Face.Online:
                BuildOnline();
                break;
            case Face.Offline:
                BuildOffline();
                break;
            case Face.Story:
                BuildStory();
                break;
            case Face.Clips:
                BuildDemo();
                break;
            case Face.Vote:
                BuildVote();
                break;
            }
            RefreshPreview();
            // Every face but the browser opens with its list ready under the
            // arrow keys. The browser does not: selecting a row is what turns
            // the commit into JOIN, and a screen that picks a server for you
            // the moment it opens has answered the question it is asking.
            if (Current == Face.Clips && _replayViewIndex == 0
                && _replayGrid != null && _replayGrid.Children.Count > 0)
            {
                Dispatcher.UIThread.Post(() => _replayGrid.Children[0].Focus(),
                    DispatcherPriority.Background);
            }
            else if (Current != Face.Online)
            {
                _list.FocusFirst();
            }
            RefreshGoLabel();
        }

        /// <summary>
        /// The hunter and the suit, built once for whichever face is asking.
        ///
        /// Both the browser and the offline grid put them in the drawer now,
        /// and they are the same two questions on both -- so they are made
        /// here rather than twice, and <see cref="RefreshSideHunter"/> is what
        /// keeps the turntable in step with them.
        /// </summary>
        private void MakeHunterRows()
        {
            _hunter = new ChoiceRow("Hunter", _hunters,
                Math.Max(0, Array.IndexOf(_hunters, LauncherPrefs.LastHunter.ToString())));
            _suit = new ChoiceRow("Suit", new[] { "1", "2", "3", "4" },
                Math.Clamp(LauncherPrefs.LastColor, 0, 3));
            _suit.Preview = (context, area) =>
            {
                context.DrawRectangle(new SolidColorBrush(SuitColour()), null,
                    new RoundedRect(area, 3));
            };
            _hunter.Changed += (_, _) => RefreshSideHunter();
            _suit.Changed += (_, _) => RefreshSideHunter();
            RefreshSideHunter();
        }

        private ChoiceRow AddHunter()
        {
            var row = new ChoiceRow("Hunter", _hunters,
                Math.Max(0, Array.IndexOf(_hunters, LauncherPrefs.LastHunter.ToString())));
            _options.Children.Add(row);

            // The hunter, turning, beside the map rather than under the row
            // that names it: the options column is a fixed height and anything
            // added to it pushes the rows at the bottom -- Address, Refresh --
            // off the screen. A name in a list is not what anybody recognises
            // a hunter by, the silhouette is, so it goes where there is room
            // for it to be one.
            _standWanted = true;
            _stand.IsVisible = _previewBox.IsVisible;
            _stand.Margin = _previewBox.Margin;
            _stand.Name2 = _hunters[row.Index];
            row.Changed += (_, _) => _stand.Name2 = _hunters[row.Index];
            return row;
        }

        private static string PlayerName()
        {
            string name = LauncherPrefs.PlayerName.Trim();
            return name.Length > 0 ? name : "Player";
        }

        // -------------------------------------------------------------- online

        private void BuildOnline()
        {
            // No column headings, and no labels on the two boxes. The
            // reference drops both and it is right to: a name, a map, a mode,
            // a count and a number need no labelling, and "Name" printed
            // beside a box that already says livetek is a word taking the
            // width the address needs.
            //
            // The `.blist` bar: a short box for who you are, a box that takes
            // the rest for where you are going, and the button that asks
            // again. `.short` is 7 ems, `.grow` is `flex: 1`, and the gap
            // between them is `.45em` of the frame.
            _name = new DeckField(PlayerName(), widthEms: 7);
            _address = new DeckField(
                $"{LauncherPrefs.ServerAddress}:{LauncherPrefs.ServerPort}",
                widthEms: 0, watermark: "host:port \u2014 or pick a row");
            _address.Box.LostFocus += (_, _) => QueryStatusSoon();

            // No separate preview on this face any more: every row carries its
            // own map behind it (ServerRow), which answers "which map is that"
            // for the whole list at once instead of for the one row selected.
            // What the column it used to share was costing was the list: three
            // rows of a browser, with a third of the card empty beside them.
            WantPreview(false);

            // `.slist { gap: .32em }`, less the three points each row already
            // spends on its own edge.
            _list.SpacingEms = 0.32;
            // Nothing picked on arrival. Every other face's list is a set of
            // things you are choosing between and the first is as good a place
            // to start as any; a browser's first row is whichever server the
            // directory happened to name first, and highlighting it is the
            // screen answering its own question.
            _list.AutoSelectFirst = false;
            // Who you arrive as, for the drawer a row opens.
            MakeHunterRows();

            var refresh = new DeckButton("Refresh", Deck.Face.Slate,
                sizeEms: 0.95, padXEms: 0.8, padYEms: 0.4, lip: 3);
            refresh.Click += (_, _) => ReloadServers();
            var bar = new BarRow();
            DockPanel.SetDock(_name, Dock.Left);
            DockPanel.SetDock(refresh, Dock.Right);
            bar.Children.Add(_name);
            bar.Children.Add(refresh);
            // Last and undocked, so it fills what the other two leave: that is
            // `flex: 1` in a DockPanel's own terms.
            bar.Children.Add(_address);
            _bar = bar;
            Grid.SetColumn(_bar, 0);
            Grid.SetColumnSpan(_bar, 2);
            Grid.SetRow(_bar, 0);
            _body.Children.Add(_bar);
            _side.IsVisible = false;
            // SetCompact spans the list across rows 0 and 1 when the options
            // are beside it. They are not on this face -- the bar is above it
            // -- so the span has to come off, or the list starts in row 0 and
            // draws over the two boxes.
            Grid.SetRow(_list, 1);
            Grid.SetRowSpan(_list, 1);
            Grid.SetColumnSpan(_list, 2);

            ReloadServers();
            StartPolling();
        }

        /// <summary>The online face's row of fields, cleared with the face.</summary>
        private Control? _bar;

        /// <summary>
        /// `.blist`: a flex row with a `.45em` gap and one item that grows.
        ///
        /// A DockPanel rather than a StackPanel because one of the three has
        /// to take what the other two leave, and a StackPanel has no way to
        /// say that. The gap is an em, so it is applied here -- the last
        /// child gets no trailing margin, or the address box would stop short
        /// of the Refresh button by one gap too many.
        /// </summary>
        private sealed class BarRow : DockPanel
        {
            protected override Size MeasureOverride(Size availableSize)
            {
                double gap = Math.Round(Deck.GetEm(this) * 0.45);
                foreach (Control child in Children)
                {
                    Thickness want = GetDock(child) switch
                    {
                        Dock.Left => new Thickness(0, 0, gap, 0),
                        Dock.Right => new Thickness(gap, 0, 0, 0),
                        _ => new Thickness(0)
                    };
                    if (child.Margin != want)
                    {
                        child.Margin = want;
                    }
                }
                return base.MeasureOverride(availableSize);
            }
        }

        /// <summary>
        /// Ask the directory who is up, then ask each of them directly.
        ///
        /// Directly, not through the directory: the round trip that matters is
        /// this machine's, and an answer also proves the server is reachable
        /// from here rather than only from there.
        /// </summary>
        private void ReloadServers()
        {
            _list.Clear();
            _replied = 0;
            _live = 0;
            if (Sample != null)
            {
                // A browser with rows in it, with no directory and no network.
                // -uishot exists to photograph a layout, and a layout with
                // "asking..." on every row is a photograph of the one state
                // that says nothing about the other two.
                foreach ((string name, string endpoint, ServerStatus status) in Sample)
                {
                    var row = new ServerRow(name, endpoint);
                    _list.Add(row);
                    row.SetStatus(status);
                    _replied++;
                    if (status.Online)
                    {
                        _live++;
                    }
                }
                Answered(Sample.Count);
                return;
            }
            _note.Text = $"Asking {LauncherPrefs.MasterHost}...";
            _note.Foreground = GuiTheme.TextDimBrush;
            Task.Run(() =>
            {
                MasterListResult result = NetMasterClient.Query(LauncherPrefs.MasterHost,
                    LauncherPrefs.MasterPort);
                Dispatcher.UIThread.Post(() =>
                {
                    if (_finished || Current != Face.Online)
                    {
                        return;
                    }
                    if (!result.Answered)
                    {
                        _note.Text = "The directory did not answer. It may be down, or UDP "
                            + "may not reach it. The address beside the list still works.";
                        _note.Foreground = GuiTheme.WarmBrush;
                        return;
                    }
                    if (result.Servers.Count == 0)
                    {
                        _note.Text = "The directory is up and has nobody listed.";
                        _note.Foreground = GuiTheme.WarmBrush;
                        return;
                    }
                    _asked = result.Servers.Count;
                    _note.Text = $"asking {_asked} servers\u2026";
                    foreach (MasterListing listing in result.Servers)
                    {
                        AddServerRow(listing);
                    }
                });
            });
        }

        /// <summary>
        /// Rows to draw instead of asking anybody: <c>-uishot</c>'s, and
        /// nothing else's. Null in every shipped path.
        /// </summary>
        internal static IReadOnlyList<(string Name, string Endpoint, ServerStatus Status)>? Sample
        {
            get;
            set;
        }

        private int _asked;
        private int _replied;
        private int _live;

        /// <summary>
        /// The reference's line under the foot: how many of the servers asked
        /// have said something, and what to do about it. Counting rather than
        /// listing, because "13 listed" is what the *directory* said and what
        /// a player wants to know is how many of those are actually there.
        /// </summary>
        private void Answered(int asked)
        {
            _asked = asked;
            _note.Foreground = GuiTheme.TextDimBrush;
            // A server that says "I am not here" has still replied, so the
            // progress count and the final count are different numbers: the
            // first is how much of the sweep is done and the second is how
            // many servers there are to join. Reporting the same number twice
            // left a browser stuck on "12 answered" with thirteen rows drawn.
            _note.Text = _replied < asked
                ? $"asking {asked} servers\u2026 {_replied} answered"
                : $"{_live} of {asked} answered. Click one to join.";
        }

        private void AddServerRow(MasterListing listing)
        {
            string name = listing.ServerName.Length > 0 ? listing.ServerName : listing.Endpoint;
            var row = new ServerRow(name, listing.Endpoint);
            ToolTip.SetTip(row, listing.Endpoint);
            // Pressing a server fills the address box in and selects it;
            // JOIN in the corner (or a second click, or Enter) is what
            // actually connects. One press used to do both, which is a player
            // dropped into a match they had only meant to read the ping of.
            _list.Add(row, _ =>
            {
                if (_address != null)
                {
                    _address.Value = $"{listing.Address}:{listing.Port}";
                }
            });
            Task.Run(() =>
            {
                ServerStatus status = NetStatus.Query(listing.Address, listing.Port,
                    allowJoinProbe: false);
                Dispatcher.UIThread.Post(() =>
                {
                    row.SetStatus(status);
                    _replied++;
                    if (status.Online)
                    {
                        _live++;
                    }
                    Answered(_asked);
                    // The answers come back in whatever order the servers
                    // reply in, and the selected row is usually the first one
                    // -- which means its map arrives after the preview was
                    // last refreshed. Refresh again for the row that is
                    // selected, and for no other.
                    if (ReferenceEquals(_list.Selected, row))
                    {
                        RefreshPreview();
                    }
                });
            });
        }

        private (string Host, int Port) Endpoint()
        {
            string host = LauncherPrefs.ServerAddress;
            int port = LauncherPrefs.ServerPort;
            if (_address != null)
            {
                ParseEndpoint(_address.Value, ref host, ref port);
            }
            return (host, port);
        }

        /// <summary>
        /// Poll what the typed-in server is running while somebody is reading
        /// the screen. StatusQuery answers without claiming a slot, which is
        /// what makes polling it reasonable.
        /// </summary>
        private void StartPolling()
        {
            QueryStatusSoon();
            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _statusTimer.Tick += (_, _) => QueryStatusSoon();
            _statusTimer.Start();
        }

        private void StopPolling()
        {
            _statusTimer?.Stop();
            _statusTimer = null;
            _statusCancel?.Cancel();
            _statusCancel = null;
        }

        private void QueryStatusSoon()
        {
            if (Current != Face.Online)
            {
                return;
            }
            _statusCancel?.Cancel();
            var cancel = new CancellationTokenSource();
            _statusCancel = cancel;
            (string host, int port) = Endpoint();
            Task.Run(() =>
            {
                ServerStatus status = NetStatus.Query(host, port, allowJoinProbe: true);
                if (cancel.IsCancellationRequested)
                {
                    return;
                }
                Dispatcher.UIThread.Post(() =>
                {
                    if (cancel.IsCancellationRequested || _finished
                        || Current != Face.Online)
                    {
                        return;
                    }
                    // Only while the list has nothing to say. The browser's
                    // own line -- "12 of 13 answered" -- is the one the note
                    // is for once there are rows, and a probe firing every
                    // four seconds was overwriting it with a sentence about a
                    // server nobody had picked.
                    if (_asked > 0)
                    {
                        return;
                    }
                    _note.Text = status.Online
                        ? $"{host}:{port} -- {Describe(status)}"
                        : $"{host}:{port} -- no answer. It may be off, or UDP may be blocked.";
                    _note.Foreground = status.Online ? GuiTheme.GoodBrush : GuiTheme.WarmBrush;
                });
            });
        }

        private static string Describe(ServerStatus status)
        {
            string players = status.MaxPlayers > 0
                ? $"{status.Players}/{status.MaxPlayers}"
                : status.Players.ToString(CultureInfo.InvariantCulture);
            string ping = status.Latency >= 0
                ? $"{status.Latency.ToString(CultureInfo.InvariantCulture)} ms"
                : "-- ms";
            return $"{status.RoomKey} ({NetStatus.ModeName(status.Mode)}) {players} players, {ping}";
        }

        private async Task Join()
        {
            (string host, int port) = Endpoint();
            string name = _name != null && _name.Value.Trim().Length > 0
                ? _name.Value.Trim() : PlayerName();
            var hunter = (Hunter)Enum.Parse(typeof(Hunter), _hunter!.Value);
            int suit = _suit?.Index ?? LauncherPrefs.LastColor;
            StopPolling();
            _go.IsEnabled = false;
            _go.Label = "joining";
            _note.Text = $"Connecting to {host}:{port}...";
            _note.Foreground = GuiTheme.TextDimBrush;

            OnlineJoinResult result = await ServerBrowserService.JoinAsync(
                host, port, name, hunter, suit);
            _go.IsEnabled = true;
            _go.Label = "join";
            if (!result.Joined)
            {
                _note.Text = result.Error;
                _note.Foreground = GuiTheme.BadBrush;
                StartPolling();
                return;
            }
            Finish(result.Plan);
        }

        // ------------------------------------------------------------- offline

        private void BuildOffline()
        {
            _go.Label = "start";
            // Every map at once, as pictures, and the rules in the drawer the
            // picture opens. See DeckTile: a column of names beside one
            // thumbnail asks the player to remember what the other twenty look
            // like, which is the thing this face is for.
            FillMapGrid();
            // No "Where" row. Offline is a match on this machine and nothing
            // else: the row's other answer -- have the directory run it --
            // was a *server*, sitting on the one face of this screen that is
            // about not being online, offering a choice whose two halves have
            // different settings under them. Running a server is its own act
            // and it moved to Online, beside the servers it joins.
            _mode = new ChoiceRow("Match type", _modes.Select(m => m.Label).ToArray());
            MakeHunterRows();
            _bots = new ChoiceRow("Bots",
                Enumerable.Range(0, PlayerEntity.SlotCapacity)
                    .Select(i => i.ToString(CultureInfo.InvariantCulture)).ToArray(),
                LauncherPrefs.Bots);
            _skill = new ChoiceRow("Bot skill", new[] { "Easy", "Normal", "Hard", "Insane" },
                LauncherPrefs.BotLevel);
            // Held rather than parented: the drawer takes them when it opens,
            // and they keep whatever the player set the last time it was up.
            _offlineRows.Clear();
            _offlineRows.Add(_mode);
            _offlineRows.Add(_hunter!);
            _offlineRows.Add(_suit!);
            _offlineRows.Add(_bots);
            _offlineRows.Add(_skill);
            RefreshSideHunter();
            WantPreview(false);
        }

        private ChoiceRow? _suit;

        /// <summary>The sampled colour of the suit the rows currently name.</summary>
        private Color SuitColour()
        {
            try
            {
                if (_hunter == null || !Enum.TryParse(_hunter.Value, ignoreCase: true,
                    out Hunter which))
                {
                    return GuiTheme.Accent;
                }
                ColorRgba sampled = Mods.HunterSuits.Color(which, _suit?.Index ?? 0);
                return Color.FromRgb(sampled.Red, sampled.Green, sampled.Blue);
            }
            catch (Exception)
            {
                return GuiTheme.Accent;
            }
        }

        /// <summary>The turntable in the drawer follows the two rows under it.</summary>
        private void RefreshSideHunter()
        {
            if (_sideStand != null && _hunter != null)
            {
                _sideStand.Name2 = _hunter.Value;
                _sideStand.Suit = _suit?.Index ?? 0;
            }
            _suit?.InvalidateVisual();
        }

        /// <summary>
        /// One card per room, in the order the room list is in.
        ///
        /// A map with no render still gets a card: the picture comes out of
        /// the player's own thumbnail pass, and a machine that has not run it
        /// would otherwise be shown an empty face rather than a grid with the
        /// codes on it.
        /// </summary>
        private void FillMapGrid()
        {
            if (_grid == null)
            {
                return;
            }
            _grid.Children.Clear();
            if (_rooms.Count == 0)
            {
                _note.Text = "No multiplayer rooms were found. Set the game files up "
                    + "from Settings.";
                _note.Foreground = GuiTheme.WarmBrush;
                return;
            }
            foreach (string room in _rooms)
            {
                DeckTile tile = MapCardFactory.Create(room);
                tile.Chosen = room == _settings.RoomKey;
                tile.Click += (_, _) =>
                {
                    if (Current == Face.Vote)
                    {
                        CastVote(tile);
                        return;
                    }
                    OpenSide(tile);
                };
                _grid.Children.Add(tile);
            }
            _picked = _settings.RoomKey;
        }

        private void FillRooms(string? current)
        {
            if (_rooms.Count == 0)
            {
                _list.AddNote("No multiplayer rooms were found. Set the game files up "
                    + "from Settings.", GuiTheme.Warm);
                return;
            }
            foreach (string room in _rooms)
            {
                (RoomMetadata? meta, _) = Metadata.GetRoomByName(room);
                _list.Add(new UiListRow(meta?.InGameName ?? room, room) { Choice = room });
            }
            _list.SelectTag(current);
        }

        private void StartMatch()
        {
            if (SelectedRoom() is not string roomKey)
            {
                _note.Text = "Pick a map first.";
                _note.Foreground = GuiTheme.WarmBrush;
                return;
            }
            GameMode mode = _modes[_mode!.Index].Mode;
            var custom = MapGen.CustomRooms.Definitions.FirstOrDefault(d => d.Name == roomKey);
            string? unsupported = custom == null ? null
                : MapGen.MapModeValidator.WhyUnsupported(custom, mode, 1 + _bots!.Index);
            if (unsupported != null)
            {
                _note.Text = unsupported;
                _note.Foreground = GuiTheme.WarmBrush;
                return;
            }
            var hunter = (Hunter)Enum.Parse(typeof(Hunter), _hunter!.Value);
            Finish(OfflineLaunch.Create(_settings, roomKey, mode, hunter,
                _suit?.Index ?? LauncherPrefs.LastColor, _bots!.Index, _skill!.Index));
        }

        private string? SelectedRoom()
        {
            if (Current == Face.Offline || Current == Face.Vote)
            {
                // Both faces pick from the card grid now, and the list they
                // used to read is not even built. This is what made "call the
                // vote" do nothing at all: the word was pressed, the grid knew
                // perfectly well which map was picked, and the commit asked a
                // list that was empty.
                return _picked;
            }
            return (_list.Selected as UiListRow)?.Choice as string;
        }

        /// <summary>
        /// Which map the picture beside the list should be of.
        ///
        /// A map row stands for its own room; a server row stands for whatever
        /// that server said it was running, which is a thing it only knows
        /// once the server has answered. Nothing is drawn until it has.
        /// </summary>
        private string? PreviewRoom()
        {
            if (_list.Selected is ServerRow server)
            {
                return server.RoomKey.Length > 0 ? server.RoomKey : null;
            }
            if (Current == Face.Clips && SelectedReplayPath() is string replay)
            {
                if (replay.EndsWith(ReplayVirtualClips.Extension, StringComparison.OrdinalIgnoreCase)
                    && ReplayVirtualClips.TryLoad(replay, out ReplayVirtualClipDocument? virtualClip)
                    && virtualClip != null)
                {
                    replay = virtualClip.SourceReplay;
                }
                DemoRecording demo = DemoLibrary.List().FirstOrDefault(d =>
                    String.Equals(d.Path, replay, StringComparison.OrdinalIgnoreCase));
                return demo.Path != null && demo.Room.Length > 0 ? demo.Room : null;
            }
            return SelectedRoom();
        }

        private string? SelectedReplayPath()
        {
            if (Current != Face.Clips) return null;
            if (_replayViewIndex == 1
                && (_list.Selected as UiListRow)?.Choice is string listPath)
            {
                _replaySelectedPath = listPath;
            }
            return _replaySelectedPath;
        }

        // --------------------------------------------------------------- story

        private void BuildStory()
        {
            _go.Label = "start";
            for (byte slot = 1; slot <= AdventureSave.SlotCount; slot++)
            {
                AdventureSave.SlotInfo info = AdventureSave.Read(slot);
                _list.Add(new UiListRow($"Slot {slot}", info.Describe()) { Choice = slot });
            }
            _hunter = AddHunter();
            // Continue or start over, rather than two buttons: an empty slot
            // has nothing to continue, and the row says so by being fixed on
            // the only answer it has.
            _resume = new ChoiceRow("Start", new[] { "Continue", "New game" }, 0);
            _options.Children.Add(_resume);
            RefreshStory();
        }

        private void RefreshStory()
        {
            if (_resume == null || Current != Face.Story)
            {
                return;
            }
            bool used = AdventureSave.Read(SelectedSlot()).Used;
            _resume.SetItems(used ? new[] { "Continue", "New game" } : new[] { "New game" });
        }

        private byte SelectedSlot()
        {
            if ((_list.Selected as UiListRow)?.Choice is byte slot)
            {
                return slot;
            }
            return 1;
        }

        private void StartAdventure()
        {
            byte slot = SelectedSlot();
            bool used = AdventureSave.Read(slot).Used;
            bool newGame = !used || _resume!.Value == "New game";
            var hunter = (Hunter)Enum.Parse(typeof(Hunter), _hunter!.Value);
            Finish(AdventureLaunch.Create(slot, newGame, hunter));
        }

        // ---------------------------------------------------------------- replay library

        private readonly record struct ReplayLibraryEntry(
            string Path,
            string ThumbnailSource,
            string Title,
            string Detail,
            DateTime Recorded,
            uint DurationFrames,
            bool IsClip,
            bool Favorite,
            bool Recoverable,
            string Room,
            string SearchText);

        private void BuildDemo()
        {
            _go.Label = "watch";
            if (LauncherPrefs.ReplayAutoPrune && LauncherPrefs.ReplayStorageLimitGb > 0)
            {
                try
                {
                    ReplayStorageManager.Apply(new ReplayStoragePolicy(
                        MaxBytes: LauncherPrefs.ReplayStorageLimitGb * 1024L * 1024L * 1024L,
                        DeleteFullMatches: true,
                        DeleteMaterializedClips: LauncherPrefs.ReplayDeleteClips,
                        DeleteVirtualClips: false));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Console.WriteLine($"[replay] storage auto-management skipped: {ex.Message}");
                }
            }

            IReadOnlyList<DemoRecording> demos = DemoLibrary.List();
            var demoByPath = new Dictionary<string, DemoRecording>(StringComparer.OrdinalIgnoreCase);
            foreach (DemoRecording demo in demos) demoByPath[demo.Path] = demo;

            var virtualClips = new Dictionary<string, ReplayVirtualClipDocument>(
                StringComparer.OrdinalIgnoreCase);
            foreach (string path in ReplayVirtualClips.List())
            {
                if (ReplayVirtualClips.TryLoad(path, out ReplayVirtualClipDocument? clip)
                    && clip != null)
                {
                    virtualClips[path] = clip;
                }
            }

            var entries = new List<ReplayLibraryEntry>();
            foreach (DemoRecording demo in demos)
            {
                bool clip = demo.Metadata?.Type == ReplayType.Clip
                    || demo.FileName.Contains("_clip_", StringComparison.OrdinalIgnoreCase);
                bool recoverable = demo.Path.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                    || demo.Compatibility == ReplayOpenResult.Truncated;
                string people = demo.Metadata == null ? ""
                    : String.Join(" ", demo.Metadata.Players.Select(player => player.Name));
                string mode = demo.Metadata?.Mode.ToString() ?? "";
                string annotations = String.Join(" ",
                    ReplayAnnotations.Bookmarks(demo.Path).Select(bookmark => bookmark.Name)
                    .Concat(ReplayAnnotations.Highlights(demo.Path).Select(highlight => highlight.Name)));
                entries.Add(new ReplayLibraryEntry(
                    demo.Path,
                    demo.Path,
                    demo.DisplayName,
                    DemoLibrary.Describe(demo),
                    demo.Recorded,
                    demo.DurationFrames,
                    clip,
                    demo.Favorite,
                    recoverable,
                    demo.Room,
                    $"{demo.DisplayName} {demo.Room} {mode} {people} {annotations} {demo.FileName}"));
            }
            foreach ((string path, ReplayVirtualClipDocument clip) in virtualClips)
            {
                demoByPath.TryGetValue(clip.SourceReplay, out DemoRecording source);
                string room = source.Path != null ? source.Room : "";
                uint duration = clip.EndFrame - clip.StartFrame;
                string detail = $"virtual clip · {ReplayHud.Time(duration)} · "
                    + $"source {Path.GetFileName(clip.SourceReplay)}";
                entries.Add(new ReplayLibraryEntry(
                    path,
                    clip.SourceReplay,
                    clip.Name,
                    detail,
                    clip.CreatedUtc.ToLocalTime(),
                    duration,
                    IsClip: true,
                    ReplayVirtualClips.IsFavorite(path),
                    Recoverable: false,
                    room,
                    $"{clip.Name} {room} "
                        + String.Join(" ", ReplayAnnotations.Bookmarks(path).Select(bookmark => bookmark.Name)
                            .Concat(ReplayAnnotations.Highlights(path).Select(highlight => highlight.Name)))
                        + $" {Path.GetFileName(clip.SourceReplay)}"));
            }

            var search = new FieldRow("Search", _replaySearchText, boxWidth: 210);
            search.Box.Watermark = "name, map, player...";
            _options.Children.Add(search);
            var filter = new ChoiceRow("Show",
                new[] { "All", "Full replays", "Clips", "Favorites", "Needs recovery" },
                _replayFilterIndex);
            var sort = new ChoiceRow("Sort",
                new[] { "Newest", "Oldest", "Name", "Longest" }, _replaySortIndex);
            var view = new ChoiceRow("View", new[] { "Grid", "List" }, _replayViewIndex);
            _options.Children.Add(filter);
            _options.Children.Add(sort);
            _options.Children.Add(view);

            var openFile = new UiWord("Open replay file...");
            openFile.Click += async (_, _) => await ImportDemo();
            _options.Children.Add(openFile);

            UiWord? recover = null;
            var replayDetails = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Foreground = GuiTheme.TextDimBrush,
                FontSize = 12,
                Margin = new Thickness(0, 8, 0, 10)
            };
            _options.Children.Add(replayDetails);
            var replayName = new FieldRow("Display name", "", boxWidth: 210);
            replayName.Box.MaxLength = 100;
            _options.Children.Add(replayName);

            void ApplyView()
            {
                bool grid = _replayViewIndex == 0;
                _list.IsVisible = !grid;
                if (_replayGridScroll != null) _replayGridScroll.IsVisible = grid;
                WantPreview(!grid);
                if (grid) StopReplayPreview();
                else if (_replayPreviewTimer == null) StartReplayPreview();
            }

            void MarkGridSelection()
            {
                if (_replayGrid == null) return;
                foreach (Control child in _replayGrid.Children)
                {
                    if (child is DeckTile tile)
                        tile.Chosen = tile.Choice is string path
                            && String.Equals(path, _replaySelectedPath,
                                StringComparison.OrdinalIgnoreCase);
                }
            }

            void RefreshReplayDetails()
            {
                string? path = SelectedReplayPath();
                if (path == null)
                {
                    replayDetails.Text = "Select a replay or clip to see its details.";
                    replayName.Value = "";
                    if (recover != null) recover.IsVisible = false;
                    return;
                }
                if (virtualClips.TryGetValue(path, out ReplayVirtualClipDocument? virtualClip))
                {
                    replayDetails.Text = $"{virtualClip.Name}\n"
                        + $"{ReplayHud.Time(virtualClip.StartFrame)}–{ReplayHud.Time(virtualClip.EndFrame)} "
                        + $"from {Path.GetFileName(virtualClip.SourceReplay)}\n"
                        + "Non-destructive virtual clip; source replay data is shared until export/watch.";
                    replayName.Value = virtualClip.Name;
                }
                else
                {
                    DemoRecording selected = demos.FirstOrDefault(d =>
                        String.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase));
                    replayDetails.Text = selected.Path == null
                        ? Path.GetFileName(path) : DemoLibrary.Details(selected);
                    replayName.Value = selected.Path == null
                        ? Path.GetFileNameWithoutExtension(path) : selected.DisplayName;
                }
                if (recover != null)
                    recover.IsVisible = path.EndsWith(".part", StringComparison.OrdinalIgnoreCase);
            }

            void Populate()
            {
                string query = _replaySearchText.Trim();
                IEnumerable<ReplayLibraryEntry> filtered = entries;
                if (query.Length > 0)
                    filtered = filtered.Where(entry =>
                        entry.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase));

                filtered = _replayFilterIndex switch
                {
                    1 => filtered.Where(entry => !entry.IsClip),
                    2 => filtered.Where(entry => entry.IsClip),
                    3 => filtered.Where(entry => entry.Favorite),
                    4 => filtered.Where(entry => entry.Recoverable),
                    _ => filtered
                };
                filtered = _replaySortIndex switch
                {
                    1 => filtered.OrderBy(entry => entry.Recorded),
                    2 => filtered.OrderBy(entry => entry.Title,
                        StringComparer.OrdinalIgnoreCase),
                    3 => filtered.OrderByDescending(entry => entry.DurationFrames),
                    _ => filtered.OrderByDescending(entry => entry.Recorded)
                };
                ReplayLibraryEntry[] shown = filtered.ToArray();

                string? previous = _replaySelectedPath;
                _list.Clear();
                _replayGrid?.Children.Clear();

                foreach (ReplayLibraryEntry entry in shown)
                {
                    _list.Add(new UiListRow(
                        (entry.Favorite ? "★ " : "") + entry.Title, entry.Detail)
                    { Choice = entry.Path });

                    if (_replayGrid != null)
                    {
                        string kind = entry.IsClip ? "CLIP" : "REPLAY";
                        string duration = ReplayHud.Time(entry.DurationFrames);
                        var card = new DeckTile(entry.Room, $"{kind} · {duration}")
                        {
                            Choice = entry.Path,
                            ImagePath = ReplayVideoExporter.BestThumbnail(entry.ThumbnailSource),
                            Blurb = (entry.Favorite ? "★ " : "") + entry.Title,
                            Verb = "WATCH",
                            ChosenVerb = "SELECTED"
                        };
                        card.Click += (_, _) =>
                        {
                            _replaySelectedPath = entry.Path;
                            _list.SelectTag(entry.Path);
                            MarkGridSelection();
                            _replayPreviewIndex = 0;
                            RefreshReplayDetails();
                            RefreshPreview();
                        };
                        _replayGrid.Children.Add(card);
                    }
                }

                if (shown.Length == 0)
                {
                    _replaySelectedPath = null;
                    _list.AddNote(entries.Count == 0
                        ? "Nothing recorded yet."
                        : "No replays match the current search and filters.");
                    _go.IsEnabled = false;
                    _note.Text = entries.Count == 0
                        ? "Nothing recorded yet. Record a replay from the pause menu, or save an instant clip."
                        : "No replays match the current search and filters.";
                }
                else
                {
                    ReplayLibraryEntry selected = shown.FirstOrDefault(entry =>
                        String.Equals(entry.Path, previous, StringComparison.OrdinalIgnoreCase));
                    _replaySelectedPath = selected.Path ?? shown[0].Path;
                    _list.SelectTag(_replaySelectedPath);
                    _go.IsEnabled = true;
                    _note.Text = $"{shown.Length} of {entries.Count} replay"
                        + $"{(entries.Count == 1 ? "" : "s")} shown";
                }

                ApplyView();
                MarkGridSelection();
                RefreshReplayDetails();
                RefreshPreview();
            }

            _replaySelection = (_, _) =>
            {
                if (Current != Face.Clips) return;
                if ((_list.Selected as UiListRow)?.Choice is string selected)
                    _replaySelectedPath = selected;
                _replayPreviewIndex = 0;
                MarkGridSelection();
                RefreshReplayDetails();
                RefreshPreview();
            };
            _list.SelectionChanged += _replaySelection;

            search.Box.TextChanged += (_, _) =>
            {
                _replaySearchText = search.Value;
                Populate();
            };
            filter.Changed += (_, _) =>
            {
                _replayFilterIndex = filter.Index;
                Populate();
            };
            sort.Changed += (_, _) =>
            {
                _replaySortIndex = sort.Index;
                Populate();
            };
            view.Changed += (_, _) =>
            {
                _replayViewIndex = view.Index;
                Populate();
            };

            UiWord Action(string label, System.Action<string> action)
            {
                var button = new UiWord(label);
                button.Click += (_, _) =>
                {
                    string? path = SelectedReplayPath();
                    if (path == null) return;
                    try { action(path); }
                    catch (Exception ex) when (ex is IOException
                        or UnauthorizedAccessException or ArgumentException)
                    {
                        _note.Text = ex.Message;
                    }
                };
                _options.Children.Add(button);
                return button;
            }

            Action("Rename", path =>
            {
                if (path.EndsWith(ReplayVirtualClips.Extension, StringComparison.OrdinalIgnoreCase))
                    ReplayVirtualClips.Rename(path, replayName.Value);
                else
                    DemoLibrary.Rename(path, replayName.Value);
                Rebuild();
            });

            var validate = new UiWord("Check replay integrity");
            validate.Click += async (_, _) =>
            {
                string? path = SelectedReplayPath();
                if (path == null) return;
                _note.Text = "Checking replay integrity...";
                string validationPath = path;
                bool virtualClip = path.EndsWith(ReplayVirtualClips.Extension,
                    StringComparison.OrdinalIgnoreCase);
                if (virtualClip)
                {
                    (string? resolved, ReplayOpenResult openResult) = await Task.Run(() =>
                    {
                        string? output = ReplayVirtualClips.ResolveForPlayback(
                            path, out ReplayOpenResult result);
                        return (output, result);
                    });
                    if (resolved == null)
                    {
                        _note.Text = $"Virtual clip could not be materialized: {openResult}";
                        return;
                    }
                    validationPath = resolved;
                }
                ReplayOpenResult result = await Task.Run(() => ReplayArchive.Validate(validationPath));
                if (!virtualClip) DemoLibrary.NoteValidation(path, result);
                Rebuild();
                _note.Text = $"Replay integrity: {result}";
            };
            _options.Children.Add(validate);

            recover = Action("Recover interrupted recording", path =>
            {
                if (!path.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                {
                    _note.Text = "Select an interrupted .part recording first.";
                    return;
                }
                ReplayArchive.Recover(path, out string? output, out ReplayOpenResult result);
                Rebuild();
                _note.Text = output == null ? $"Recovery failed: {result}"
                    : "Recovered " + Path.GetFileName(output);
            });

            Action("Favorite / unfavorite", path =>
            {
                if (path.EndsWith(ReplayVirtualClips.Extension, StringComparison.OrdinalIgnoreCase))
                    ReplayVirtualClips.ToggleFavorite(path);
                else
                    DemoLibrary.ToggleFavorite(path);
                Rebuild();
            });

            string? confirmDelete = null;
            Action("Delete (press twice to confirm)", path =>
            {
                if (confirmDelete != path)
                {
                    confirmDelete = path;
                    _note.Text = "Press Delete again to delete " + Path.GetFileName(path);
                    return;
                }
                if (path.EndsWith(ReplayVirtualClips.Extension, StringComparison.OrdinalIgnoreCase))
                    ReplayVirtualClips.Delete(path);
                else
                    DemoLibrary.Delete(path);
                _replaySelectedPath = null;
                Rebuild();
            });

            var export = new UiWord("Export replay");
            export.Click += async (_, _) =>
            {
                string? path = SelectedReplayPath();
                if (path == null) return;
                string exportSource = path;
                if (path.EndsWith(ReplayVirtualClips.Extension, StringComparison.OrdinalIgnoreCase))
                {
                    (string? resolved, ReplayOpenResult openResult) = await Task.Run(() =>
                    {
                        string? output = ReplayVirtualClips.ResolveForPlayback(
                            path, out ReplayOpenResult result);
                        return (output, result);
                    });
                    if (resolved == null)
                    {
                        _note.Text = $"Export failed: {openResult}";
                        return;
                    }
                    exportSource = resolved;
                }
#if !ANDROID
                try
                {
                    string directory = Path.Combine(DemoLibrary.Directory, "exports");
                    Directory.CreateDirectory(directory);
                    string destination = Path.Combine(directory,
                        Path.GetFileNameWithoutExtension(path) + $"_{Guid.NewGuid():N}.ppdemo");
                    File.Copy(exportSource, destination, overwrite: false);
                    _note.Text = "Exported to " + destination;
                }
                catch (Exception ex) { _note.Text = "Export failed: " + ex.Message; }
                await Task.CompletedTask;
#else
                if (TopLevel.GetTopLevel(this) is not TopLevel top) return;
                try
                {
                    var target = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                    {
                        Title = "Export replay",
                        SuggestedFileName = Path.GetFileNameWithoutExtension(path) + ".ppdemo",
                        DefaultExtension = "ppdemo"
                    });
                    if (target == null) return;
                    if (target.TryGetLocalPath() is string local
                        && Path.GetFullPath(local) == Path.GetFullPath(exportSource)) return;
                    using var input = File.OpenRead(exportSource);
                    await using var output = await target.OpenWriteAsync();
                    await input.CopyToAsync(output);
                    _note.Text = "Replay exported";
                }
                catch (Exception ex) { _note.Text = "Export failed: " + ex.Message; }
#endif
            };
            _options.Children.Add(export);
#if !ANDROID
            Action("Reveal in folder", path =>
            {
                string folder = Path.GetDirectoryName(path)!;
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });
            });
#endif

            Populate();
        }

        private async Task PlayDemo()
        {
            if (SelectedReplayPath() is string path)
                await Watch(path);
        }

        /// <summary>Load a demo file and, if it reads, start playing it.</summary>
        public void SessionEnded(string reason)
        {
            _finished = false;
            _note.Text = reason;
            _note.Foreground = GuiTheme.BadBrush;
            StartPolling();
        }

        private async Task Watch(string path)
        {
            if (path.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
            { _note.Text = "Recover this interrupted recording before watching it."; return; }
            if (path.EndsWith(ReplayVirtualClips.Extension, StringComparison.OrdinalIgnoreCase))
            {
                string virtualPath = path;
                _go.IsEnabled = false;
                _go.Label = "preparing";
                (string? resolved, ReplayOpenResult result) = await Task.Run(() =>
                {
                    string? output = ReplayVirtualClips.ResolveForPlayback(
                        virtualPath, out ReplayOpenResult openResult);
                    return (output, openResult);
                });
                _go.IsEnabled = true;
                _go.Label = "watch";
                if (resolved == null)
                {
                    _note.Text = $"Could not prepare virtual clip: {result}.";
                    _note.Foreground = GuiTheme.BadBrush;
                    return;
                }
                path = resolved;
            }
            // Joined here, not inside MatchStart: a failure has to land back on
            // a screen that is still open to show it on. The Windows build has
            // no console for anything the launcher starts, so the alternative
            // is a menu that silently does nothing.
            _go.IsEnabled = false;
            _go.Label = "loading";
            bool joined = await Task.Run(() => DemoPlayback.Join(path));
            _go.IsEnabled = true;
            _go.Label = "watch";
            if (!joined)
            {
                _note.Text = DemoPlayback.LastError
                    ?? "That file could not be read as a replay.";
                _note.Foreground = GuiTheme.BadBrush;
                return;
            }
            Finish(new LaunchPlan
            {
                Kind = LaunchKind.Demo,
                DemoPath = path,
                Hunter = Hunter.Samus,
                PlayerName = "",
                RoomKey = ""
            });
        }

        private async Task ImportDemo()
        {
            TopLevel? top = TopLevel.GetTopLevel(this);
            if (top == null)
            {
                return;
            }
            // The desktop draws these screens with the headless backend and so
            // has no StorageProvider at all: ask the operating system instead.
            // See NativeFilePicker.
            if (!top.StorageProvider.CanOpen)
            {
                if (!NativeFilePicker.Available)
                {
                    _note.Text = "This desktop has no file dialog to open "
                        + $"(install zenity or kdialog). Clips in {DemoLibrary.Directory} "
                        + "are listed here without one.";
                    _note.Foreground = GuiTheme.BadBrush;
                    return;
                }
                string? chosen = await NativeFilePicker.OpenFile("Replays",
                    $"{Branding.Name} replay", DemoFile.Extension.TrimStart('.'));
                if (chosen != null)
                {
                    await Watch(chosen);
                }
                return;
            }
            var options = new FilePickerOpenOptions { Title = "Replays", AllowMultiple = false };
            if (!OperatingSystem.IsAndroid())
            {
                // Android filters by MIME type and a demo file has none; a
                // pattern there produces a picker in which every file is
                // refused.
                options.FileTypeFilter = new[]
                {
                    new FilePickerFileType($"{Branding.Name} replay")
                    {
                        Patterns = new[] { $"*{DemoFile.Extension}" }
                    },
                    new FilePickerFileType("Every file") { Patterns = new[] { "*" } }
                };
            }
            try
            {
                string demoDir = DemoLibrary.Directory;
                if (Directory.Exists(demoDir))
                {
                    options.SuggestedStartLocation =
                        await top.StorageProvider.TryGetFolderFromPathAsync(demoDir);
                }
            }
            catch (IOException)
            {
                // No default folder is a worse first run than one with a clean
                // slate, not a reason to refuse the picker outright.
            }
            IReadOnlyList<IStorageFile> picked =
                await top.StorageProvider.OpenFilePickerAsync(options);
            if (picked.Count == 0)
            {
                return;
            }
            string? path = picked[0].TryGetLocalPath();
            if (path == null)
            {
                // Android hands back a content:// document with no file behind
                // it, and the demo reader takes a path. Kept afterwards rather
                // than deleted: the reader holds the file open for the whole
                // session.
                try
                {
                    string scratch = Path.Combine(GameFiles.Root, $"picked{DemoFile.Extension}");
                    await using (Stream source = await picked[0].OpenReadAsync())
                    await using (var target = File.Create(scratch))
                    {
                        await source.CopyToAsync(target);
                    }
                    path = scratch;
                }
                catch (Exception ex)
                {
                    _note.Text = $"That file could not be read: {ex.Message}";
                    _note.Foreground = GuiTheme.BadBrush;
                    return;
                }
            }
            await Watch(path);
        }

        // ---------------------------------------------------------------- vote

        private void BuildVote()
        {
            _go.Label = "call the vote";
            // The whole map list as pictures, not a column of names beside one
            // preview. MapPick's own note says why the ballot is every map
            // rather than four the server drew up -- "where next" is not
            // multiple choice -- and the reference draws that ballot as the
            // same cards the offline face uses, with a count in the corner of
            // each and a ring on whatever is ahead.
            FillMapGrid();
            _picked = NetSession.ServerMatch?.RoomKey;
            foreach (Control child in _grid?.Children ?? (IList<Control>)Array.Empty<Control>())
            {
                if (child is DeckTile tile)
                {
                    tile.Verb = "Pick";
                    tile.ChosenVerb = "Picked";
                    tile.Tally = 0;
                    tile.Chosen = false;
                }
            }
            RefreshBallot();
            WantPreview(false);
        }

        /// <summary>
        /// `.ballotbar` and the badges under it: what is leading, and by how
        /// much, said once above the grid rather than on every card.
        /// </summary>
        private void RefreshBallot()
        {
            if (_grid == null)
            {
                return;
            }
            int best = 0;
            foreach (Control child in _grid.Children)
            {
                if (child is DeckTile tile && tile.Tally > best)
                {
                    best = tile.Tally;
                }
            }
            string? leader = null;
            foreach (Control child in _grid.Children)
            {
                if (child is not DeckTile tile)
                {
                    continue;
                }
                tile.Leader = best > 0 && tile.Tally == best;
                if (tile.Leader && leader == null)
                {
                    (RoomMetadata? meta, _) = Metadata.GetRoomByName(tile.RoomKey);
                    leader = meta?.InGameName ?? tile.RoomKey;
                }
                tile.InvalidateVisual();
            }
            _note.Foreground = GuiTheme.TextDimBrush;
            _note.Text = leader == null
                ? "Nobody has picked. The rotation decides."
                : $"{leader} is leading with {best}. Most votes wins.";
        }

        // -------------------------------------------------------------- shared

        private void Go()
        {
            if (_finished)
            {
                return;
            }
            switch (Current)
            {
            case Face.Online:
                // Whatever the word on it says: a row picked means join it,
                // nothing picked means there is nothing to join and the offer
                // is to run one. See RefreshGoLabel.
                if (_list.Selected is ServerRow)
                {
                    _ = Join();
                }
                else
                {
                    CreateRequested?.Invoke(this, EventArgs.Empty);
                }
                break;
            case Face.Offline:
                StartMatch();
                break;
            case Face.Story:
                StartAdventure();
                break;
            case Face.Clips:
                _ = PlayDemo();
                break;
            case Face.Vote:
                if (SelectedRoom() is string room)
                {
                    _finished = true;
                    Voted?.Invoke(this, room);
                }
                break;
            }
        }

        /// <summary>
        /// The picture of the chosen map, beside the list.
        ///
        /// The grid of previews is gone -- twenty-seven pictures is a page to
        /// scroll where a column of names is a glance -- but the picture
        /// itself is the answer to "which map is that", so it follows the
        /// selection instead of being a screen of its own.
        /// </summary>
        private void RefreshPreview()
        {
            if (!_previewBox.IsVisible)
            {
                return;
            }
            _preview.Source = null;
            try
            {
                string? path = null;
                if (Current == Face.Clips && SelectedReplayPath() is string replay)
                {
                    if (replay.EndsWith(ReplayVirtualClips.Extension, StringComparison.OrdinalIgnoreCase)
                        && ReplayVirtualClips.TryLoad(replay, out ReplayVirtualClipDocument? virtualClip)
                        && virtualClip != null)
                    {
                        replay = virtualClip.SourceReplay;
                    }
                    string[] replayThumbs = ReplayVideoExporter.Thumbnails(replay);
                    if (replayThumbs.Length > 0)
                    {
                        path = replayThumbs[_replayPreviewIndex % replayThumbs.Length];
                    }
                }

                if (path == null && PreviewRoom() is string room)
                {
                    path = ThumbnailGenerator.PathFor(room);
                }
                if (path == null || !File.Exists(path))
                {
                    return;
                }

                // Through a MemoryStream so the file is not held open: replay
                // stills are written opportunistically while playback runs.
                using var stream = new MemoryStream(File.ReadAllBytes(path));
                _preview.Source = new Bitmap(stream);
            }
            catch (Exception)
            {
                // A truncated PNG from an interrupted capture should show an
                // empty frame, not take the screen down.
            }
        }

        private void StartReplayPreview()
        {
            StopReplayPreview();
            _replayPreviewIndex = 0;
            _replayPreviewTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1100)
            };
            _replayPreviewTimer.Tick += (_, _) =>
            {
                if (Current != Face.Clips) return;
                _replayPreviewIndex++;
                RefreshPreview();
            };
            _replayPreviewTimer.Start();
        }

        private void StopReplayPreview()
        {
            _replayPreviewTimer?.Stop();
            _replayPreviewTimer = null;
            _replayPreviewIndex = 0;
        }

        /// <summary>host, or host:port. Leaves both alone on anything else, so a
        /// typo does not silently change the address.</summary>
        private static bool ParseEndpoint(string text, ref string host, ref int port)
        {
            text = text.Trim();
            if (text.Length == 0)
            {
                return false;
            }
            int colon = text.LastIndexOf(':');
            if (colon <= 0)
            {
                host = text;
                return true;
            }
            if (!Int32.TryParse(text[(colon + 1)..], NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int parsed)
                || parsed < 1 || parsed > 65535)
            {
                return false;
            }
            host = text[..colon];
            port = parsed;
            return true;
        }
    }
}
