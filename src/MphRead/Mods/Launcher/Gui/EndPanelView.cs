#if MPHREAD_AVALONIA
using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using MphRead.Mods.Network;

namespace MphRead.Mods.Launcher.Gui
{
    /// <summary>
    /// What the results screen asks: where next, and who you are coming back
    /// as. The reference's <c>.endside</c>.
    ///
    /// <para>
    /// <b>The scoreboard is not in here and must not be.</b> The engine draws
    /// the results itself and that stays the game's screen -- plain rows in
    /// the HUD's own idiom, no cards, no lips, no radius. A scoreboard is not
    /// a place to put a theme, and the reference says so in as many words.
    /// What this panel is for is the part the theme *should* touch: the
    /// ballot and the hunter picker, which used to be drawn in the HUD as a
    /// column of arrows and swatches beside a 32x32 sprite.
    /// </para>
    ///
    /// <para>
    /// A panel down the right, over the match, the way the pause menu already
    /// is: `top: .9em; right: .9em; bottom: .9em; width: 22em`. Two faces on a
    /// strip -- the map ballot and the hunter -- because they are two
    /// questions asked at the same moment and neither is worth half a panel.
    /// </para>
    ///
    /// <para>
    /// It decides nothing itself. Every press goes to the same places the
    /// HUD's own picker went: <see cref="MapPick.Choose"/> and
    /// <see cref="Mods.EndScreen.Pick"/>. The server owns the rotation
    /// and the respawn, and a second opinion held in a menu is how two screens
    /// come to disagree about what you picked.
    /// </para>
    /// </summary>
    internal sealed class EndPanelView : UserControl
    {
        private readonly UiTabs _tabs;
        private readonly bool _hasBallot;
        private readonly DeckGrid _ballot = new() { FixedColumns = 2, Ratio = 16 / 9.0 };
        private readonly ScrollViewer _ballotScroll;
        private readonly StackPanel _hunterPane = new() { Spacing = 8 };
        private readonly HunterStand _stand;
        private readonly ChoiceRow _hunter;
        private readonly ChoiceRow _suit;
        private readonly Note _count = new("");

        /// <summary>What the ballot face says before the server has sent one.</summary>
        private readonly Note _empty = new("The rotation decides where next.")
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false
        };
        private readonly string[] _hunters;

        /// <summary>What the ballot was built from, so it is only rebuilt when it moves.</summary>
        private string _order = "";

        public EndPanelView()
        {
            Background = Brushes.Transparent;
            Focusable = true;
            IsHitTestVisible = true;

            _hunters = HunterStand.Names;
            // Persistent online sessions return to their lobby after the report,
            // so the lobby is where the next map is chosen. Continuous/offline
            // flows keep the existing results ballot.
            _hasBallot = !NetSession.PersistentLobby;
            _tabs = new UiTabs(_hasBallot
                ? new[] { "Next match", "Change hunter" }
                : new[] { "Change hunter" });
            _tabs.Changed += (_, _) => ShowFace();

            _ballotScroll = new ScrollViewer
            {
                Content = _ballot,
                ClipToBounds = true,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };

            _stand = new HunterStand
            {
                Height = 150,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            _hunter = new ChoiceRow("Hunter", _hunters, HunterIndex());
            _suit = new ChoiceRow("Suit", new[] { "1", "2", "3", "4" },
                Math.Clamp(Mods.EndScreen.Suit, 0, 3));
            _suit.Preview = (context, area) =>
            {
                context.DrawRectangle(new SolidColorBrush(SuitColour()), null,
                    new RoundedRect(area, 3));
            };
            _hunter.Changed += (_, _) => Commit();
            _suit.Changed += (_, _) => Commit();
            _hunterPane.Children.Add(_stand);
            _hunterPane.Children.Add(_hunter);
            _hunterPane.Children.Add(_suit);
            _hunterPane.IsVisible = false;

            var body = new Panel();
            body.Children.Add(_ballotScroll);
            body.Children.Add(_empty);
            body.Children.Add(_hunterPane);

            var foot = new Grid();
            _count.VerticalAlignment = VerticalAlignment.Center;
            foot.Children.Add(_count);

            var stack = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,*,Auto"),
                RowSpacing = 8
            };
            Grid.SetRow(_tabs, 0);
            stack.Children.Add(_tabs);
            Grid.SetRow(body, 1);
            stack.Children.Add(body);
            Grid.SetRow(foot, 2);
            stack.Children.Add(foot);

            // `.endside`: down the right, inset by .9em, 22 ems wide. Not the
            // page layout every other screen uses -- this one shares the frame
            // with a scoreboard it must not cover.
            var card = new DeckCard
            {
                Child = stack,
                MaxWidthEms = 22,
                Fill = true,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            var root = new Panel();
            var host = new Border
            {
                Child = card,
                // Narrower on a phone. It is the same 340 points either way
                // and the frame is not: a phone lays these screens out in a
                // box about 830 points across against a desktop's eleven
                // hundred, so the same panel takes 41% of the width there and
                // 31% here -- and the difference is exactly the scoreboard's
                // deaths column, which it must not cover.
                Width = Deck.Phone ? 285 : 340,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Stretch,
                Margin = new Thickness(0, 14, 14, 14)
            };
            root.Children.Add(host);
            GuiTheme.PixelPerfect(root);
            Content = root;
            ShowFace();
            Refresh();
        }

        /// <summary>Open on the hunter face, for -uishot.</summary>
        internal void ShowHunter() => _tabs.Index = _hasBallot ? 1 : 0;

        private int HunterIndex() =>
            Math.Max(0, Array.IndexOf(_hunters, Mods.EndScreen.Hunter.ToString()));

        private Color SuitColour()
        {
            try
            {
                ColorRgba sampled = Mods.HunterSuits.Color(
                    Mods.EndScreen.Hunter, Math.Clamp(_suit.Index, 0, 3));
                return Color.FromRgb(sampled.Red, sampled.Green, sampled.Blue);
            }
            catch (Exception)
            {
                return GuiTheme.Accent;
            }
        }

        private void ShowFace()
        {
            bool ballot = _hasBallot && _tabs.Index == 0;
            _ballotScroll.IsVisible = ballot;
            _hunterPane.IsVisible = !ballot;
            // The stand as well as the pane it is in. A hidden pane keeps the
            // bounds its children were last arranged at, and the head that
            // has the engine draw the real model into the stand's rectangle
            // reads those bounds -- so on the ballot face the model's own
            // dark ground was painted over the scoreboard's deaths column.
            _stand.IsVisible = !ballot;
            _empty.IsVisible = ballot && MapPick.Order.Count == 0;
        }

        /// <summary>
        /// Send the two rows where the HUD's own arrows and swatches sent
        /// them. Nothing is held here.
        /// </summary>
        private void Commit()
        {
            if (!Enum.TryParse(_hunter.Value, ignoreCase: true, out Hunter which))
            {
                return;
            }
            Mods.EndScreen.Pick(which, Math.Clamp(_suit.Index, 0, 3));
            _stand.Name2 = _hunter.Value;
            _stand.Suit = Math.Clamp(_suit.Index, 0, 3);
            _suit.InvalidateVisual();
        }

        /// <summary>
        /// Read the match's own state back into the panel, once a frame.
        ///
        /// Everything here is somebody else's: the ballot is the server's and the
        /// hunter is <c>RespawnChoice</c>'s. So this pulls rather than pushing;
        /// the only writes are the choices made through this panel.
        /// </summary>
        public void Refresh()
        {
            // A copy, because the list is the network thread's: it is rebuilt
            // whenever a vote arrives, and enumerating it from here while that
            // happens took the process down with "collection was modified".
            string[] order = _hasBallot
                ? System.Linq.Enumerable.ToArray(MapPick.Order)
                : Array.Empty<string>();
            string key = String.Join('|', order);
            if (key != _order)
            {
                _order = key;
                _ballot.Children.Clear();
                foreach (string room in order)
                {
                    string code = MapPick.IsReturnToLobby(room)
                        ? "LOBBY"
                        : room.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                            is { Length: > 0 } parts ? parts[0] : room;
                    var tile = new DeckTile(room, code)
                    {
                        Blurb = MapPick.NameOf(room),
                        // No slab: the whole card is the button, and on a
                        // ballot of 27 it was a third of every one of them.
                        Verb = "",
                        ChosenVerb = "",
                        Ratio = 16 / 9.0
                    };
                    tile.Click += (_, _) =>
                    {
                        MapPick.Choose(MapPick.IndexOf(tile.RoomKey));
                        Refresh();
                    };
                    _ballot.Children.Add(tile);
                }
            }
            _empty.IsVisible = _hasBallot && order.Length == 0 && _tabs.Index == 0;
            int best = 0;
            foreach (string room in order)
            {
                best = Math.Max(best, MapPick.VotesFor(room));
            }
            foreach (Control child in _ballot.Children)
            {
                if (child is not DeckTile tile)
                {
                    continue;
                }
                int votes = MapPick.VotesFor(tile.RoomKey);
                bool leader = best > 0 && votes == best;
                // Only when one of them moved. This runs ten times a second
                // off TickEndPanel, and an invalidation here re-rasterises
                // the whole window and re-uploads it: unconditionally, that
                // was half the frame rate for as long as the panel was up.
                if (tile.Tally != votes || tile.Leader != leader)
                {
                    tile.Tally = votes;
                    tile.Leader = leader;
                    tile.InvalidateVisual();
                }
                tile.Chosen = tile.RoomKey == MapPick.Picked;
            }

            int wantHunter = HunterIndex();
            if (_hunter.Index != wantHunter)
            {
                _hunter.Index = wantHunter;
            }
            int wantSuit = Math.Clamp(Mods.EndScreen.Suit, 0, 3);
            if (_suit.Index != wantSuit)
            {
                _suit.Index = wantSuit;
            }
            _stand.Name2 = _hunter.Value;
            _stand.Suit = wantSuit;

            _count.Text = _hasBallot && MapPick.Eligible > 1
                ? $"{MapPick.Eligible} in the room"
                : "";
        }
    }
}
#endif
