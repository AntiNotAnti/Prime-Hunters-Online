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
    /// Tactical arena selection over the engine scoreboard. Network matches
    /// retain the existing ballot; local bot matches keep their configuration
    /// and repeat the current arena unless the player selects another one.
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
        private readonly TextBlock _next = PrimeChrome.Text("CURRENT ARENA // REMATCH", PrimeTypography.DataSmall,
            PrimeTheme.CyanBrush, data: true);
        private readonly TextBox _search = new() { PlaceholderText = "Search arenas" };

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
                ? (Mods.EndScreen.CharacterChangeEnabled
                    ? new[] { "Next match", "Change hunter" }
                    : new[] { "Next match" })
                : (Mods.EndScreen.CharacterChangeEnabled
                    ? new[] { "Change hunter" }
                    : new[] { "Results" }));
            _tabs.Changed += (_, _) => ShowFace();
            _tabs.IsVisible = Mods.EndScreen.CharacterChangeEnabled;

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

            var start = new PrimeButton("START NEXT MATCH", () => OfflineRematch.Continue(), primary: true)
            { IsVisible = !NetSession.Active };
            start.SetValue(ControllerNav.NavIdProperty, "results.next");
            var foot = PrimeChrome.Stack(_next, _count, start,
                PrimeChrome.Text("ESC / PAUSE  //  LEAVE MATCH", PrimeTypography.DataSmall, PrimeTheme.TextSecondaryBrush, data: true));
            _search.TextChanged += (_, _) => FilterMaps();
            var heading = PrimeChrome.Stack(new PrimeBadge("POST-MATCH DEPLOYMENT"),
                PrimeChrome.Title("NEXT ARENA"), _tabs, _search);

            var stack = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,*,Auto"),
                RowSpacing = 8
            };
            stack.Children.Add(heading);
            Grid.SetRow(body, 1);
            stack.Children.Add(body);
            Grid.SetRow(foot, 2);
            stack.Children.Add(foot);

            var root = new Panel();
            var host = new Border
            {
                Child = new PrimePanel(stack),
                // Preserve the original scoreboard's deaths-column clearance.
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
        internal void ShowHunter()
        {
            if (Mods.EndScreen.CharacterChangeEnabled)
            {
                _tabs.Index = _hasBallot ? 1 : 0;
            }
        }

        private void FilterMaps()
        {
            string query = _search.Text?.Trim() ?? "";
            foreach (Control child in _ballot.Children)
                if (child is DeckTile tile)
                    tile.IsVisible = tile.RoomKey.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || tile.Blurb.Contains(query, StringComparison.OrdinalIgnoreCase);
        }

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
            bool ballot = _hasBallot
                && (!Mods.EndScreen.CharacterChangeEnabled || _tabs.Index == 0);
            bool hunter = Mods.EndScreen.CharacterChangeEnabled && !ballot;
            _ballotScroll.IsVisible = ballot;
            _hunterPane.IsVisible = hunter;
            // The stand as well as the pane it is in. A hidden pane keeps the
            // bounds its children were last arranged at, and the head that
            // has the engine draw the real model into the stand's rectangle
            // reads those bounds -- so on the ballot face the model's own
            // dark ground was painted over the scoreboard's deaths column.
            _stand.IsVisible = hunter;
            _empty.Text = ballot
                ? "The rotation decides where next."
                : "Hunter changes are available from the lobby.";
            _empty.IsVisible = ballot ? MapPick.Order.Count == 0 : !hunter;
        }

        /// <summary>
        /// Send the two rows where the HUD's own arrows and swatches sent
        /// them. Nothing is held here.
        /// </summary>
        private void Commit()
        {
            if (!Mods.EndScreen.CharacterChangeEnabled)
            {
                return;
            }
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
                        Tactical = true,
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
                FilterMaps();
            }
            bool showingBallot = _hasBallot
                && (!Mods.EndScreen.CharacterChangeEnabled || _tabs.Index == 0);
            _empty.IsVisible = showingBallot ? order.Length == 0
                : !Mods.EndScreen.CharacterChangeEnabled;
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
                int tally = NetSession.Active ? votes : -1;
                bool leader = best > 0 && votes == best;
                // Only when one of them moved. This runs ten times a second
                // off TickEndPanel, and an invalidation here re-rasterises
                // the whole window and re-uploads it: unconditionally, that
                // was half the frame rate for as long as the panel was up.
                if (tile.Tally != tally || tile.Leader != leader)
                {
                    tile.Tally = tally;
                    tile.Leader = leader;
                    tile.InvalidateVisual();
                }
                tile.Chosen = tile.RoomKey == MapPick.Picked;
            }

            if (Mods.EndScreen.CharacterChangeEnabled)
            {
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
            }

            string next = MapPick.Picked;
            _next.Text = next.Length > 0 ? "SELECTED // " + MapPick.NameOf(next).ToUpperInvariant()
                : NetSession.Active ? "NEXT // " + Mods.EndScreen.NextRoomName.ToUpperInvariant() : "CURRENT ARENA // REMATCH";
            _count.Text = GameState.MatchState == MatchState.Ending
                ? $"DEPLOYING IN {Math.Max(0, Math.Ceiling(GameState.MatchTime)):0} SEC"
                : "RESULTS // CHOOSE YOUR NEXT ARENA";
        }
    }
}
#endif
