#if MPHREAD_AVALONIA
using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace MphRead.Mods.Launcher.Gui
{
    /// <summary>
    /// The persistent front-door concept for Project Prime.
    ///
    /// This first slice owns only the home surface. Existing Play, lobby,
    /// settings and replay views still open through StartScreen's stack so no
    /// networking or launch contract changes with the visual overhaul.
    /// Subsequent phases move those views into the same shell one at a time.
    /// </summary>
    internal sealed class HubHomeView : UserControl
    {
        private readonly Grid _main;
        private readonly StackPanel _desktopNav;
        private readonly Grid _compactNav;
        private readonly Border _hero;
        private readonly Grid _heroBody;
        private readonly Border _profile;
        private readonly StackPanel _headerState;
        private readonly HunterStand _stand;
        private readonly Border _heroArt;
        private readonly TextBlock _player;
        private readonly TextBlock _hunter;
        private readonly TextBlock _data;
        private readonly TextBlock _onlinePlayers;
        private readonly HubNavButton _returnLobby;
        private bool _compact;
        private bool _lobbyActive;
        private bool _populationRefreshing;
        private DateTime _nextPopulationRefresh = DateTime.MinValue;

        public event Action<HubDestination>? NavigateRequested;
        public event Action? ReturnToLobbyRequested;

        public HubHomeView()
        {
            Background = Brushes.Transparent;

            var root = new Grid
            {
                Margin = new Thickness(24, 20, 24, 44),
                RowDefinitions = new RowDefinitions("Auto,*,Auto"),
                RowSpacing = 14
            };

            var header = BuildHeader(out StackPanel headerState);
            _headerState = headerState;
            _onlinePlayers = new TextBlock
            {
                Text = "ONLINE PLAYERS  //  --",
                FontFamily = HubTheme.DataBold,
                FontSize = 8.5,
                Foreground = HubTheme.AccentBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
            _headerState.Children.Add(_onlinePlayers);
            _returnLobby = new HubNavButton("RETURN TO LOBBY",
                compact: true, accent: HubTheme.Good)
            {
                IsVisible = false
            };
            _returnLobby.Click += (_, _) => ReturnToLobbyRequested?.Invoke();
            ControllerNav.Identify(_returnLobby, "hub.return-lobby");
            _headerState.Children.Add(_returnLobby);
            root.Children.Add(header);

            _main = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("210,*,260"),
                ColumnSpacing = 14
            };
            Grid.SetRow(_main, 1);
            root.Children.Add(_main);

            _desktopNav = BuildDesktopNav();
            Grid.SetColumn(_desktopNav, 0);
            _main.Children.Add(_desktopNav);

            _compactNav = BuildCompactNav();
            _compactNav.IsVisible = false;
            _main.Children.Add(_compactNav);

            _hero = BuildHero(out HunterStand stand, out Grid heroBody,
                out Border heroArt);
            _stand = stand;
            _heroBody = heroBody;
            _heroArt = heroArt;
            Grid.SetColumn(_hero, 1);
            _main.Children.Add(_hero);

            _profile = BuildProfile(out TextBlock player, out TextBlock hunter,
                out TextBlock data);
            _player = player;
            _hunter = hunter;
            _data = data;
            Grid.SetColumn(_profile, 2);
            _main.Children.Add(_profile);

            var footer = BuildFooter();
            Grid.SetRow(footer, 2);
            root.Children.Add(footer);

            Content = root;
            SizeChanged += (_, e) => ApplyResponsive(e.NewSize);
            RefreshProfile();
            ApplyResponsive(new Size(960, 600));
        }

        public void RefreshProfile()
        {
            HubSnapshot snapshot = HubState.Capture();
            _player.Text = snapshot.PlayerName.ToUpperInvariant();
            _stand.Name2 = snapshot.DisplayHunter.ToString();
            _stand.Suit = snapshot.Suit;
            _hunter.Text = snapshot.PreferredHunter == Hunter.Random
                ? $"RANDOM // {snapshot.DisplayHunter}".ToUpperInvariant()
                : snapshot.DisplayHunter.ToString().ToUpperInvariant();

            _data.Text = snapshot.GameFilesReady ? "READY" : "SETUP REQUIRED";
            _data.Foreground = snapshot.GameFilesReady
                ? HubTheme.GoodBrush
                : HubTheme.WarmBrush;
            _ = RefreshOnlinePopulation();
        }

        public void SetLobbyActive(bool active, int players)
        {
            _lobbyActive = active;
            _returnLobby.IsVisible = active;
            _returnLobby.Label = players > 0
                ? $"RETURN TO LOBBY  //  {players} PLAYER{(players == 1 ? "" : "S")}"
                : "RETURN TO LOBBY";
            // The compact header cannot fit the status rail without crowding the
            // title. PLAY still returns to a parked lobby on compact layouts.
            _headerState.IsVisible = !_compact;
        }

        private async Task RefreshOnlinePopulation()
        {
            if (_populationRefreshing || DateTime.UtcNow < _nextPopulationRefresh)
                return;

            _populationRefreshing = true;
            try
            {
                OnlinePopulationResult population =
                    await ServerBrowserService.CountOnlinePlayersAsync().ConfigureAwait(false);
                Dispatcher.UIThread.Post(() =>
                {
                    _onlinePlayers.Text = population.DirectoryAnswered
                        ? $"ONLINE PLAYERS  //  {population.Players}"
                        : "ONLINE PLAYERS  //  --";
                    _onlinePlayers.Foreground = population.DirectoryAnswered
                        ? HubTheme.GoodBrush
                        : HubTheme.TextDimBrush;
                });
            }
            catch
            {
                Dispatcher.UIThread.Post(() =>
                {
                    _onlinePlayers.Text = "ONLINE PLAYERS  //  --";
                    _onlinePlayers.Foreground = HubTheme.TextDimBrush;
                });
            }
            finally
            {
                _nextPopulationRefresh = DateTime.UtcNow.AddSeconds(30);
                _populationRefreshing = false;
            }
        }

        private Grid BuildHeader(out StackPanel headerState)
        {
            var header = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto")
            };
            var title = new StackPanel { Spacing = 1 };
            title.Children.Add(new TextBlock
            {
                Text = "PROJECT PRIME",
                FontFamily = HubTheme.Ui,
                FontWeight = FontWeight.Bold,
                FontSize = 18,
                Foreground = HubTheme.TextBrush
            });
            title.Children.Add(new TextBlock
            {
                Text = "HUNTER NETWORK  /  COMBAT INTERFACE",
                FontFamily = HubTheme.Ui,
                FontWeight = FontWeight.Medium,
                FontSize = 10,
                Foreground = HubTheme.AccentBrush
            });
            header.Children.Add(title);

            headerState = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 125, 0)
            };
            headerState.Children.Add(StatusDot(HubTheme.GoodBrush));
            headerState.Children.Add(new TextBlock
            {
                Text = "LOCAL SYSTEM ONLINE",
                FontFamily = HubTheme.Data,
                FontSize = 9,
                Foreground = HubTheme.TextDimBrush,
                VerticalAlignment = VerticalAlignment.Center
            });
            Grid.SetColumn(headerState, 1);
            header.Children.Add(headerState);
            return header;
        }

        private StackPanel BuildDesktopNav()
        {
            var nav = new StackPanel { Spacing = 6 };
            HubNavButton[] buttons =
            {
                Action("PLAY", "Multiplayer, offline combat or adventure",
                    () => Navigate(HubDestination.Play), primary: true),
                Action("MAP EDITOR", "Custom-map workshop (coming soon)",
                    () => Navigate(HubDestination.MapEditor), accent: HubTheme.Warm),
                Action("REPLAY STUDIO", "Recordings, clips and cinematic replay tools",
                    () => Navigate(HubDestination.ReplayStudio)),
                Action("HUNTER LICENSE", "Profile, career stats, match history and progression",
                    () => Navigate(HubDestination.HunterLicense), accent: HubTheme.Good),
                Action("SETTINGS", "Video, audio, input and player",
                    () => Navigate(HubDestination.Settings)),
                Action("QUIT", "Close Project Prime",
                    () => Navigate(HubDestination.Quit),
                    accent: HubTheme.Danger)
            };
            for (int i = 0; i < buttons.Length; i++)
            {
                string id = $"hub.desktop.{NavKey(buttons[i].Label)}";
                ControllerNav.Identify(buttons[i], id, initial: i == 0);
                buttons[i].SetValue(ControllerNav.NavUpProperty,
                    $"hub.desktop.{NavKey(buttons[(i + buttons.Length - 1) % buttons.Length].Label)}");
                buttons[i].SetValue(ControllerNav.NavDownProperty,
                    $"hub.desktop.{NavKey(buttons[(i + 1) % buttons.Length].Label)}");
                nav.Children.Add(buttons[i]);
            }
            return nav;
        }

        private Grid BuildCompactNav()
        {
            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,*,*"),
                RowDefinitions = new RowDefinitions("Auto,Auto"),
                ColumnSpacing = 5,
                RowSpacing = 5
            };
            HubNavButton play = AddCompact(grid, 0, 0, "PLAY",
                () => Navigate(HubDestination.Play), true);
            HubNavButton editor = AddCompact(grid, 1, 0, "MAP EDITOR",
                () => Navigate(HubDestination.MapEditor), accent: HubTheme.Warm);
            HubNavButton replays = AddCompact(grid, 2, 0, "REPLAY STUDIO",
                () => Navigate(HubDestination.ReplayStudio));
            HubNavButton license = AddCompact(grid, 0, 1, "HUNTER LICENSE",
                () => Navigate(HubDestination.HunterLicense), accent: HubTheme.Good);
            HubNavButton settings = AddCompact(grid, 1, 1, "SETTINGS",
                () => Navigate(HubDestination.Settings));
            HubNavButton quit = AddCompact(grid, 2, 1, "QUIT",
                () => Navigate(HubDestination.Quit), accent: HubTheme.Danger);

            WireCompact(play, "play", up: "hunter-license", down: "hunter-license",
                left: "replay-studio", right: "map-editor", initial: true);
            WireCompact(editor, "map-editor", up: "settings", down: "settings",
                left: "play", right: "replay-studio");
            WireCompact(replays, "replay-studio", up: "quit", down: "quit",
                left: "map-editor", right: "play");
            WireCompact(license, "hunter-license", up: "play", down: "play",
                left: "quit", right: "settings");
            WireCompact(settings, "settings", up: "map-editor", down: "map-editor",
                left: "hunter-license", right: "quit");
            WireCompact(quit, "quit", up: "replay-studio", down: "replay-studio",
                left: "settings", right: "hunter-license");
            return grid;
        }

        private Border BuildHero(out HunterStand stand, out Grid heroBody,
            out Border heroArt)
        {
            heroBody = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,220")
            };
            Grid body = heroBody;

            var copy = new StackPanel
            {
                Margin = new Thickness(28, 26, 12, 24),
                Spacing = 9,
                VerticalAlignment = VerticalAlignment.Center
            };
            copy.Children.Add(new TextBlock
            {
                Text = "DEPLOYMENT",
                FontFamily = HubTheme.Ui,
                FontWeight = FontWeight.SemiBold,
                FontSize = 11,
                Foreground = HubTheme.AccentBrush
            });
            copy.Children.Add(new TextBlock
            {
                Text = "ENTER THE HUNT",
                FontFamily = HubTheme.Ui,
                FontWeight = FontWeight.Bold,
                FontSize = 32,
                Foreground = HubTheme.TextBrush
            });
            copy.Children.Add(new TextBlock
            {
                Text = "Project Prime is a Metroid Prime Hunters fork for the community, by the community. Play online, build custom maps, create replays and help shape what comes next.",
                FontFamily = HubTheme.Ui,
                FontSize = 11,
                Foreground = HubTheme.TextDimBrush,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 470
            });

            // PLAY already owns the primary position in the navigation rail.
            // Repeating the same destination in the hero made the welcome
            // surface feel like two menus competing for the same click.
            var tags = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 7
            };
            tags.Children.Add(ChipTag("MULTIPLAYER"));
            tags.Children.Add(ChipTag("CUSTOM MAPS"));
            tags.Children.Add(ChipTag("REPLAYS"));
            copy.Children.Add(tags);
            body.Children.Add(copy);

            stand = new HunterStand
            {
                Width = 190,
                Height = 282,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                IsHitTestVisible = false,
                Name2 = Hunter.Samus.ToString()
            };
            var art = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,*"),
                RowSpacing = 5,
                Margin = new Thickness(8)
            };
            art.Children.Add(HubChrome.Kicker("HUNTER PREVIEW"));
            Grid.SetRow(stand, 1);
            art.Children.Add(stand);
            heroArt = new Border
            {
                Margin = new Thickness(0, 18, 10, 18),
                Background = HubTheme.AccentPanel(HubTheme.Accent, 24),
                BorderBrush = HubTheme.EdgeBrush,
                BorderThickness = new Thickness(1),
                Child = art
            };
            Grid.SetColumn(heroArt, 1);
            body.Children.Add(heroArt);

            return new Border
            {
                Background = HubTheme.PanelBrush,
                BorderBrush = HubTheme.AccentBrush,
                BorderThickness = new Thickness(1, 1, 1, 2),
                Child = body,
                ClipToBounds = true
            };
        }

        private Border BuildProfile(out TextBlock player, out TextBlock hunter,
            out TextBlock data)
        {
            var stack = new StackPanel
            {
                Margin = new Thickness(17),
                Spacing = 9
            };
            stack.Children.Add(Section("OPERATIVE"));

            player = Value("PLAYER");
            player.FontSize = 18;
            player.Foreground = HubTheme.TextBrush;
            stack.Children.Add(player);

            stack.Children.Add(Key("HUNTER"));
            hunter = Value("SAMUS");
            stack.Children.Add(hunter);

            stack.Children.Add(Key("GAME DATA"));
            data = Value("READY");
            stack.Children.Add(data);

            stack.Children.Add(Key("INPUT"));
            stack.Children.Add(Value("AUTO DETECT"));

            stack.Children.Add(Key("PLATFORM"));
            stack.Children.Add(Value(HubState.Capture().Platform.ToUpperInvariant()));

            var divider = new Border
            {
                Height = 1,
                Background = HubTheme.EdgeBrush,
                Margin = new Thickness(0, 5)
            };
            stack.Children.Add(divider);
            stack.Children.Add(new TextBlock
            {
                Text = "Your preferred hunter and suit become the defaults for new sessions.",
                FontFamily = HubTheme.Ui,
                FontSize = 9.5,
                Foreground = HubTheme.TextDimBrush,
                TextWrapping = TextWrapping.Wrap
            });

            return new Border
            {
                Background = HubTheme.PanelStrongBrush,
                BorderBrush = HubTheme.EdgeBrush,
                BorderThickness = new Thickness(1),
                Child = stack
            };
        }

        private Border BuildFooter()
        {
            var line = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto")
            };
            line.Children.Add(new TextBlock
            {
                Text = "CONTROLLER  //  MOUSE + KEYBOARD  //  TOUCH",
                FontFamily = HubTheme.Data,
                FontSize = 8.5,
                Foreground = HubTheme.TextDimBrush,
                VerticalAlignment = VerticalAlignment.Center
            });
            var right = new TextBlock
            {
                Text = "COMMAND HUB",
                FontFamily = HubTheme.DataBold,
                FontSize = 8.5,
                Foreground = HubTheme.AccentBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(right, 1);
            line.Children.Add(right);
            return new Border
            {
                BorderBrush = HubTheme.EdgeBrush,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(0, 9, 0, 0),
                Child = line
            };
        }

        private void ApplyResponsive(Size size)
        {
            bool compact = size.Width < 820 || size.Height < 520;
            if (compact == _compact)
            {
                return;
            }
            _compact = compact;
            _desktopNav.IsVisible = !compact;
            _compactNav.IsVisible = compact;
            _profile.IsVisible = !compact;
            _headerState.IsVisible = !compact;

            if (compact)
            {
                _main.ColumnDefinitions = new ColumnDefinitions("*");
                _main.RowDefinitions = new RowDefinitions("Auto,*");
                _main.ColumnSpacing = 0;
                _main.RowSpacing = 8;

                Grid.SetColumn(_compactNav, 0);
                Grid.SetRow(_compactNav, 0);
                Grid.SetColumn(_hero, 0);
                Grid.SetRow(_hero, 1);
                Grid.SetColumn(_profile, 0);
                Grid.SetRow(_profile, 0);
                _hero.Margin = new Thickness(0);
                _heroBody.ColumnDefinitions = new ColumnDefinitions("*");
                _heroBody.RowDefinitions = new RowDefinitions("*");
                Grid.SetColumn(_heroArt, 0);
                Grid.SetRow(_heroArt, 0);
                _heroArt.IsVisible = false;
                _stand.IsVisible = false;
            }
            else
            {
                _main.ColumnDefinitions = new ColumnDefinitions("210,*,260");
                _main.RowDefinitions = new RowDefinitions("*");
                _main.ColumnSpacing = 14;
                _main.RowSpacing = 0;

                Grid.SetColumn(_desktopNav, 0);
                Grid.SetRow(_desktopNav, 0);
                Grid.SetColumn(_hero, 1);
                Grid.SetRow(_hero, 0);
                Grid.SetColumn(_profile, 2);
                Grid.SetRow(_profile, 0);
                _heroBody.ColumnDefinitions = new ColumnDefinitions("*,220");
                _heroBody.RowDefinitions = new RowDefinitions("*");
                Grid.SetColumn(_heroArt, 1);
                Grid.SetRow(_heroArt, 0);
                _heroArt.IsVisible = true;
                _stand.IsVisible = true;
                _stand.Width = 190;
                _stand.Height = 282;
            }
        }

        private void Navigate(HubDestination destination) =>
            NavigateRequested?.Invoke(destination);

        private HubNavButton Action(string label, string detail, Action action,
            bool primary = false, Color? accent = null)
        {
            var button = new HubNavButton(label, detail, primary, compact: false, accent: accent);
            button.Click += (_, _) => action();
            return button;
        }

        private HubNavButton AddCompact(Grid grid, int column, int row, string label,
            Action action, bool primary = false, Color? accent = null)
        {
            var button = new HubNavButton(label, primary: primary, compact: true, accent: accent);
            button.Click += (_, _) => action();
            Grid.SetColumn(button, column);
            Grid.SetRow(button, row);
            grid.Children.Add(button);
            return button;
        }

        private static string NavKey(string label) =>
            label.ToLowerInvariant().Replace(" ", "-");

        private static void WireCompact(HubNavButton button, string id,
            string up, string down, string left, string right, bool initial = false)
        {
            ControllerNav.Identify(button, $"hub.compact.{id}", initial);
            button.SetValue(ControllerNav.NavUpProperty, $"hub.compact.{up}");
            button.SetValue(ControllerNav.NavDownProperty, $"hub.compact.{down}");
            button.SetValue(ControllerNav.NavLeftProperty, $"hub.compact.{left}");
            button.SetValue(ControllerNav.NavRightProperty, $"hub.compact.{right}");
        }

        private static Border ChipTag(string text) => new()
        {
            Background = HubTheme.InkBrush,
            BorderBrush = HubTheme.EdgeBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(7, 4),
            Child = new TextBlock
            {
                Text = text,
                FontFamily = HubTheme.Ui,
                FontWeight = FontWeight.SemiBold,
                FontSize = 9,
                Foreground = HubTheme.TextDimBrush
            }
        };

        private static Border StatusDot(IBrush brush) => new()
        {
            Width = 7,
            Height = 7,
            CornerRadius = new CornerRadius(4),
            Background = brush,
            VerticalAlignment = VerticalAlignment.Center
        };

        private static TextBlock Section(string text) => new()
        {
            Text = text,
            FontFamily = HubTheme.Ui,
            FontWeight = FontWeight.SemiBold,
            FontSize = 11,
            Foreground = HubTheme.AccentBrush
        };

        private static TextBlock Key(string text) => new()
        {
            Text = text,
            FontFamily = HubTheme.Data,
            FontSize = 8,
            Foreground = HubTheme.TextDimBrush,
            Margin = new Thickness(0, 6, 0, -5)
        };

        private static TextBlock Value(string text) => new()
        {
            Text = text,
            FontFamily = HubTheme.Ui,
            FontWeight = FontWeight.SemiBold,
            FontSize = 12,
            Foreground = HubTheme.TextBrush
        };
    }
}
#endif
