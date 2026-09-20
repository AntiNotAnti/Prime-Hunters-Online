#if MPHREAD_AVALONIA
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MphRead.Mods.Network;

namespace MphRead.Mods.Launcher.Gui
{
    /// <summary>
    /// Multiplayer is one workspace, not three menu cards: Quick Play,
    /// the live server browser, manual join and lobby creation all meet here.
    /// </summary>
    internal sealed class HubMultiplayerView : UserControl
    {
        private static readonly string[] _hunters =
            Enumerable.Range(0, Hunters.Playable).Select(i => ((Hunter)i).ToString())
                .Append(Hunter.Random.ToString()).ToArray();

        private readonly UiList _servers = new()
        {
            AutoSelectFirst = false,
            SpacingEms = 0.32
        };
        private readonly DeckField _name;
        private readonly DeckField _address;
        private readonly ChoiceRow _hunter;
        private readonly ChoiceRow _suit;
        private readonly HunterStand _stand;
        private readonly Image _mapPreview;
        private readonly TextBlock _detailName;
        private readonly TextBlock _detailMeta;
        private readonly TextBlock _summary;
        private readonly IReadOnlyList<ServerBrowserEntry>? _sample;
        private readonly HubNavButton _quick;
        private readonly HubNavButton _refresh;
        private readonly HubNavButton _join;
        private CancellationTokenSource? _discover;
        private CancellationTokenSource? _quickSearch;
        private bool _joining;
        private int _replied, _live;

        public event EventHandler? Closed;
        public event EventHandler? CreateLobbyRequested;
        public event EventHandler<LaunchPlan>? Launched;

        public HubMultiplayerView(IReadOnlyList<ServerBrowserEntry>? sample = null)
        {
            _sample = sample;
            Focusable = true;
            Background = Brushes.Transparent;

            _name = new DeckField(PlayerName(), widthEms: 8);
            _address = new DeckField(
                $"{LauncherPrefs.ServerAddress}:{LauncherPrefs.ServerPort}",
                widthEms: 0, watermark: "host:port");

            _hunter = new ChoiceRow("Hunter", _hunters,
                Math.Max(0, Array.IndexOf(_hunters, LauncherPrefs.LastHunter.ToString())));
            _suit = new ChoiceRow("Suit", new[] { "1", "2", "3", "4" },
                Math.Clamp(LauncherPrefs.LastColor, 0, 3));

            _stand = new HunterStand
            {
                Height = 128,
                MinHeight = 110,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Name2 = _hunters[_hunter.Index],
                Suit = _suit.Index
            };
            _hunter.Changed += (_, _) => RefreshHunter();
            _suit.Changed += (_, _) => RefreshHunter();

            _mapPreview = new Image
            {
                Height = 126,
                Stretch = Stretch.UniformToFill
            };

            _detailName = new TextBlock
            {
                Text = "SELECT A SERVER",
                FontFamily = HubTheme.Ui,
                FontWeight = FontWeight.Bold,
                FontSize = 18,
                Foreground = HubTheme.TextBrush,
                TextWrapping = TextWrapping.Wrap
            };
            _detailMeta = new TextBlock
            {
                Text = "Pick a live server to inspect its map, mode, population and latency.",
                FontFamily = HubTheme.Ui,
                FontSize = 10.5,
                Foreground = HubTheme.TextDimBrush,
                TextWrapping = TextWrapping.Wrap
            };
            _summary = new TextBlock
            {
                Text = "CONTACTING DIRECTORY",
                FontFamily = HubTheme.Data,
                FontSize = 9,
                Foreground = HubTheme.TextDimBrush,
                VerticalAlignment = VerticalAlignment.Center
            };

            _servers.SelectionChanged += (_, row) => SelectionChanged(row);
            _servers.Activated += (_, row) =>
            {
                if (row is ServerRow server && server.IsLive)
                    _ = JoinAsync();
            };

            var root = new Grid
            {
                Margin = new Thickness(24, 20, 24, 34),
                RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"),
                RowSpacing = 12
            };

            root.Children.Add(BuildHeader());

            var identity = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("220,*"),
                ColumnSpacing = 9
            };
            identity.Children.Add(_name);
            Grid.SetColumn(_address, 1);
            identity.Children.Add(_address);
            Grid.SetRow(identity, 1);
            root.Children.Add(identity);

            var body = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,310"),
                ColumnSpacing = 14
            };

            var listStack = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,*"),
                RowSpacing = 7
            };
            listStack.Children.Add(HubChrome.Kicker("LIVE SERVERS"));
            var listFrame = new Border
            {
                Background = HubTheme.PanelBrush,
                BorderBrush = HubTheme.EdgeBrush,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8),
                Child = _servers
            };
            Grid.SetRow(listFrame, 1);
            listStack.Children.Add(listFrame);
            body.Children.Add(listStack);

            Border detail = BuildDetailPanel();
            Grid.SetColumn(detail, 1);
            body.Children.Add(detail);
            Grid.SetRow(body, 2);
            root.Children.Add(body);

            var footer = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*,Auto,Auto"),
                ColumnSpacing = 7
            };

            var back = new HubNavButton("BACK", compact: true);
            ControllerNav.Identify(back, "multiplayer.back");
            back.Click += (_, _) => Closed?.Invoke(this, EventArgs.Empty);
            footer.Children.Add(back);

            _quick = new HubNavButton("QUICK PLAY",
                "Find the best compatible open server", compact: true, primary: true);
            ControllerNav.Identify(_quick, "multiplayer.quick", initial: true);
            _quick.Click += (_, _) => _ = QuickPlayAsync();
            Grid.SetColumn(_quick, 1);
            footer.Children.Add(_quick);

            var create = new HubNavButton("CREATE LOBBY", compact: true,
                accent: HubTheme.Warm);
            ControllerNav.Identify(create, "multiplayer.create");
            create.Click += (_, _) => CreateLobbyRequested?.Invoke(this, EventArgs.Empty);
            Grid.SetColumn(create, 2);
            footer.Children.Add(create);

            _refresh = new HubNavButton("REFRESH", compact: true);
            ControllerNav.Identify(_refresh, "multiplayer.refresh");
            _refresh.Click += (_, _) => RefreshServers();
            Grid.SetColumn(_refresh, 4);
            footer.Children.Add(_refresh);

            _join = new HubNavButton("JOIN SERVER", compact: true,
                accent: HubTheme.Good)
            {
                IsEnabled = false
            };
            ControllerNav.Identify(_join, "multiplayer.join");
            _join.Click += (_, _) => _ = JoinAsync();
            Grid.SetColumn(_join, 5);
            footer.Children.Add(_join);

            back.SetValue(ControllerNav.NavRightProperty, "multiplayer.quick");
            _quick.SetValue(ControllerNav.NavLeftProperty, "multiplayer.back");
            _quick.SetValue(ControllerNav.NavRightProperty, "multiplayer.create");
            create.SetValue(ControllerNav.NavLeftProperty, "multiplayer.quick");
            create.SetValue(ControllerNav.NavRightProperty, "multiplayer.refresh");
            _refresh.SetValue(ControllerNav.NavLeftProperty, "multiplayer.create");
            _refresh.SetValue(ControllerNav.NavRightProperty, "multiplayer.join");
            _join.SetValue(ControllerNav.NavLeftProperty, "multiplayer.refresh");

            Grid.SetRow(footer, 3);
            root.Children.Add(footer);

            Content = root;
            AttachedToVisualTree += (_, _) => RefreshServers();
            DetachedFromVisualTree += (_, _) => CancelWork();

            SizeChanged += (_, e) =>
            {
                bool compact = e.NewSize.Width < 760;
                identity.ColumnDefinitions = compact
                    ? new ColumnDefinitions("*")
                    : new ColumnDefinitions("220,*");
                identity.RowDefinitions = compact
                    ? new RowDefinitions("Auto,Auto")
                    : new RowDefinitions("*");
                Grid.SetColumn(_address, compact ? 0 : 1);
                Grid.SetRow(_address, compact ? 1 : 0);

                body.ColumnDefinitions = compact
                    ? new ColumnDefinitions("*")
                    : new ColumnDefinitions("*,310");
                body.RowDefinitions = compact
                    ? new RowDefinitions("260,Auto")
                    : new RowDefinitions("*");
                Grid.SetColumn(detail, compact ? 0 : 1);
                Grid.SetRow(detail, compact ? 1 : 0);
                body.RowSpacing = compact ? 10 : 0;
            };
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Closed?.Invoke(this, EventArgs.Empty);
                return;
            }
            base.OnKeyDown(e);
        }

        private Grid BuildHeader()
        {
            var header = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto")
            };
            var copy = new StackPanel { Spacing = 2 };
            copy.Children.Add(new TextBlock
            {
                Text = "PLAY  /  MULTIPLAYER",
                FontFamily = HubTheme.DataBold,
                FontSize = 8.5,
                Foreground = HubTheme.AccentBrush
            });
            copy.Children.Add(new TextBlock
            {
                Text = "MULTIPLAYER",
                FontFamily = HubTheme.Ui,
                FontWeight = FontWeight.Bold,
                FontSize = 26,
                Foreground = HubTheme.TextBrush
            });
            copy.Children.Add(new TextBlock
            {
                Text = "QUICK PLAY  /  LIVE SERVERS  /  LOBBIES",
                FontFamily = HubTheme.DataBold,
                FontSize = 8.5,
                Foreground = HubTheme.GoodBrush
            });
            header.Children.Add(copy);

            var badge = new Border
            {
                Background = HubTheme.PanelBrush,
                BorderBrush = HubTheme.EdgeBrush,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(9, 5),
                Child = _summary,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(badge, 1);
            header.Children.Add(badge);
            return header;
        }

        private Border BuildDetailPanel()
        {
            var stack = new StackPanel
            {
                Margin = new Thickness(14),
                Spacing = 8
            };
            stack.Children.Add(HubChrome.Kicker("SELECTED SESSION"));
            stack.Children.Add(_detailName);
            stack.Children.Add(_detailMeta);

            stack.Children.Add(new Border
            {
                Height = 126,
                Background = HubTheme.InkBrush,
                BorderBrush = HubTheme.EdgeBrush,
                BorderThickness = new Thickness(1),
                ClipToBounds = true,
                Child = _mapPreview
            });
            stack.Children.Add(HubChrome.Kicker("DEPLOY AS", HubTheme.TextDimBrush));
            stack.Children.Add(_stand);
            stack.Children.Add(_hunter);
            stack.Children.Add(_suit);

            return new Border
            {
                Background = HubTheme.PanelStrongBrush,
                BorderBrush = HubTheme.EdgeBrush,
                BorderThickness = new Thickness(1),
                Child = stack
            };
        }

        private void RefreshHunter()
        {
            _stand.Name2 = _hunters[_hunter.Index];
            _stand.Suit = Math.Clamp(_suit.Index, 0, 3);
        }

        private void SelectionChanged(Control row)
        {
            if (row is not ServerRow server)
                return;

            _address.Value = server.Endpoint;
            _detailName.Text = server.DisplayName.ToUpperInvariant();
            _detailMeta.Text = server.IsLive
                ? $"{server.MapName}\n{server.ModeName}  /  {server.PlayerCount} PLAYERS  /  {server.PingText} MS\n{server.Endpoint}"
                : $"NO RESPONSE\n{server.Endpoint}";
            _mapPreview.Source = server.IsLive ? MapShot.For(server.RoomKey) : null;
            _join.IsEnabled = server.IsLive && !_joining;
        }

        private void CancelDiscovery()
        {
            _discover?.Cancel();
            _discover?.Dispose();
            _discover = null;
        }

        private void CancelQuickSearch()
        {
            _quickSearch?.Cancel();
            _quickSearch?.Dispose();
            _quickSearch = null;
        }

        private void CancelWork()
        {
            CancelDiscovery();
            CancelQuickSearch();
        }

        private async void RefreshServers()
        {
            CancelWork();
            _servers.Clear();
            _replied = 0;
            _live = 0;
            _join.IsEnabled = false;
            _mapPreview.Source = null;

            if (_sample != null)
            {
                foreach (ServerBrowserEntry entry in _sample)
                    AddEntry(entry);
                _summary.Text = $"{_live} LIVE  /  {_replied} CHECKED";
                _summary.Foreground = _live > 0
                    ? HubTheme.GoodBrush : HubTheme.WarmBrush;
                return;
            }

            _summary.Text = "CONTACTING DIRECTORY";
            _summary.Foreground = HubTheme.TextDimBrush;
            _detailName.Text = "SELECT A SERVER";
            _detailMeta.Text = "Waiting for live directory results.";
            var cancel = new CancellationTokenSource();
            _discover = cancel;

            ServerDiscoveryResult result = await ServerBrowserService.DiscoverAsync(entry =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (cancel.IsCancellationRequested || TopLevel.GetTopLevel(this) == null)
                        return;
                    AddEntry(entry);
                });
            }, cancel.Token);

            if (cancel.IsCancellationRequested || TopLevel.GetTopLevel(this) == null)
                return;
            _summary.Text = result.Message.ToUpperInvariant();
            _summary.Foreground = result.DirectoryAnswered
                ? (_live > 0 ? HubTheme.GoodBrush : HubTheme.WarmBrush)
                : HubTheme.DangerBrush;
        }

        private void AddEntry(ServerBrowserEntry entry)
        {
            var row = new ServerRow(entry.Name, entry.Endpoint);
            row.SetStatus(entry.Status);
            _servers.Add(row, _ => _address.Value = entry.Endpoint);
            _replied++;
            if (entry.Live)
                _live++;
            _summary.Text = $"{_live} LIVE  /  {_replied} CHECKED";
        }

        private async Task QuickPlayAsync()
        {
            if (_joining || _quickSearch != null)
                return;

            CancelDiscovery();
            _quick.IsEnabled = false;
            _refresh.IsEnabled = false;
            _join.IsEnabled = false;
            _summary.Text = "QUICK PLAY  /  SEARCHING";
            _summary.Foreground = HubTheme.AccentBrush;

            if (_sample != null)
            {
                ServerBrowserEntry[] candidates = _sample
                    .Where(entry => entry.Live && entry.Compatible
                        && (entry.Status.MaxPlayers <= 0
                            || entry.Status.Players < entry.Status.MaxPlayers))
                    .OrderBy(entry => entry.Status.Latency < 0
                        ? Int32.MaxValue : entry.Status.Latency)
                    .ToArray();
                _quick.IsEnabled = true;
                _refresh.IsEnabled = true;
                if (candidates.Length > 0)
                {
                    ServerBrowserEntry preview = candidates[0];
                    ShowEntry(preview);
                    _summary.Text = $"QUICK PLAY  /  {preview.Name}".ToUpperInvariant();
                    _summary.Foreground = HubTheme.GoodBrush;
                }
                else
                {
                    _summary.Text = "NO COMPATIBLE OPEN SERVER";
                    _summary.Foreground = HubTheme.WarmBrush;
                }
                return;
            }

            var cancel = new CancellationTokenSource();
            _quickSearch = cancel;
            QuickPlaySearchResult result = await ServerBrowserService.FindBestAsync(cancel.Token);
            if (cancel.IsCancellationRequested || TopLevel.GetTopLevel(this) == null)
                return;
            _quickSearch.Dispose();
            _quickSearch = null;
            _quick.IsEnabled = true;
            _refresh.IsEnabled = true;

            if (!result.Found)
            {
                _summary.Text = result.Message.ToUpperInvariant();
                _summary.Foreground = HubTheme.WarmBrush;
                return;
            }

            ShowEntry(result.Entry);
            _summary.Text = $"QUICK PLAY  /  {result.Message}".ToUpperInvariant();
            _summary.Foreground = HubTheme.GoodBrush;
            await JoinAsync();
        }

        private void ShowEntry(ServerBrowserEntry entry)
        {
            _address.Value = entry.Endpoint;
            string room = entry.Status.RoomKey;
            string mapName = Metadata.RoomMetadata.TryGetValue(room, out RoomMetadata? meta)
                && !String.IsNullOrWhiteSpace(meta.InGameName)
                    ? meta.InGameName!
                    : room;
            string players = entry.Status.MaxPlayers > 0
                ? $"{entry.Status.Players}/{entry.Status.MaxPlayers}"
                : entry.Status.Players.ToString();
            string ping = entry.Status.Latency >= 0
                ? $"{entry.Status.Latency} MS" : "PING --";

            _detailName.Text = entry.Name.ToUpperInvariant();
            _detailMeta.Text =
                $"{mapName}\n{NetStatus.ModeName(entry.Status.Mode)}  /  {players} PLAYERS  /  {ping}\n{entry.Endpoint}";
            _mapPreview.Source = MapShot.For(room);
            _join.IsEnabled = entry.Live && !_joining;
        }

        private async Task JoinAsync()
        {
            if (_joining)
                return;

            if (!ServerBrowserService.TryParseEndpoint(_address.Value,
                LauncherPrefs.ServerAddress, LauncherPrefs.ServerPort,
                out string host, out int port))
            {
                _summary.Text = "INVALID SERVER ADDRESS";
                _summary.Foreground = HubTheme.DangerBrush;
                return;
            }

            string player = _name.Value.Trim();
            Hunter hunter = Enum.TryParse(_hunter.Value, true, out Hunter parsed)
                ? parsed : LauncherPrefs.LastHunter;
            int suit = Math.Clamp(_suit.Index, 0, 3);

            _joining = true;
            _quick.IsEnabled = false;
            _refresh.IsEnabled = false;
            _join.IsEnabled = false;
            _summary.Text = $"CONNECTING TO {host}:{port}";
            _summary.Foreground = HubTheme.AccentBrush;
            CancelDiscovery();

            OnlineJoinResult result = await ServerBrowserService.JoinAsync(
                host, port, player, hunter, suit);

            _joining = false;
            _quick.IsEnabled = true;
            _refresh.IsEnabled = true;
            if (!result.Joined)
            {
                _summary.Text = result.Error.ToUpperInvariant();
                _summary.Foreground = HubTheme.DangerBrush;
                _join.IsEnabled = true;
                return;
            }
            Launched?.Invoke(this, result.Plan);
        }

        public void SessionEnded(string reason)
        {
            _joining = false;
            _summary.Text = reason.Length > 0 ? reason.ToUpperInvariant() : "SESSION ENDED";
            _summary.Foreground = HubTheme.DangerBrush;
            _quick.IsEnabled = true;
            _refresh.IsEnabled = true;
            RefreshServers();
        }

        private static string PlayerName()
        {
            string name = LauncherPrefs.PlayerName.Trim();
            return name.Length > 0 ? name : "Player";
        }
    }
}
#endif
