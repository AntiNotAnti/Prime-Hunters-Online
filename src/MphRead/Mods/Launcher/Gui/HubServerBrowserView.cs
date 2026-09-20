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
    /// FPS-hub presentation of the shared server browser service.
    ///
    /// Discovery and joining are not implemented here. This view owns only
    /// selection, input fields and presentation; <see cref="ServerBrowserService"/>
    /// owns the application behaviour and is shared with the legacy Play face.
    /// </summary>
    internal sealed class HubServerBrowserView : UserControl
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
        private readonly TextBlock _detailName;
        private readonly TextBlock _detailMeta;
        private readonly TextBlock _summary;
        private HubNavButton _join = null!;
        private HubNavButton _refresh = null!;
        private CancellationTokenSource? _discover;
        private bool _joining;
        private int _replied, _live;

        public event EventHandler? Closed;
        public event EventHandler? CreateRequested;
        public event EventHandler<LaunchPlan>? Launched;

        public HubServerBrowserView()
        {
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
                Height = 175,
                MinHeight = 150,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Name2 = _hunters[_hunter.Index],
                Suit = _suit.Index
            };
            _hunter.Changed += (_, _) => RefreshHunter();
            _suit.Changed += (_, _) => RefreshHunter();

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
                Text = "Choose a live row to inspect its map, mode, population and latency.",
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
                ColumnDefinitions = new ColumnDefinitions("*,285"),
                ColumnSpacing = 14
            };
            var listFrame = new Border
            {
                Background = HubTheme.PanelBrush,
                BorderBrush = HubTheme.EdgeBrush,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8),
                Child = _servers
            };
            body.Children.Add(listFrame);

            Border detail = BuildDetailPanel();
            Grid.SetColumn(detail, 1);
            body.Children.Add(detail);
            Grid.SetRow(body, 2);
            root.Children.Add(body);

            var footer = BuildFooter();
            Grid.SetRow(footer, 3);
            root.Children.Add(footer);

            Content = root;
            AttachedToVisualTree += (_, _) => RefreshServers();
            DetachedFromVisualTree += (_, _) => CancelDiscovery();
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
                Text = "SERVER BROWSER",
                FontFamily = HubTheme.Ui,
                FontWeight = FontWeight.Bold,
                FontSize = 25,
                Foreground = HubTheme.TextBrush
            });
            copy.Children.Add(new TextBlock
            {
                Text = "LIVE DIRECTORY  /  UDP LATENCY FROM THIS MACHINE",
                FontFamily = HubTheme.DataBold,
                FontSize = 8.5,
                Foreground = HubTheme.AccentBrush
            });
            header.Children.Add(copy);

            var badge = new Border
            {
                Background = HubTheme.InkBrush,
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
                Margin = new Thickness(15),
                Spacing = 9
            };
            stack.Children.Add(new TextBlock
            {
                Text = "SESSION",
                FontFamily = HubTheme.DataBold,
                FontSize = 8,
                Foreground = HubTheme.AccentBrush
            });
            stack.Children.Add(_detailName);
            stack.Children.Add(_detailMeta);
            stack.Children.Add(new Border
            {
                Height = 1,
                Background = HubTheme.EdgeBrush,
                Margin = new Thickness(0, 2, 0, 3)
            });
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

        private Grid BuildFooter()
        {
            var footer = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto,Auto"),
                ColumnSpacing = 7
            };

            var back = new HubNavButton("BACK", compact: true);
            ControllerNav.Identify(back, "browser.back");
            back.Click += (_, _) => Closed?.Invoke(this, EventArgs.Empty);
            footer.Children.Add(back);

            var create = new HubNavButton("CREATE MATCH", compact: true,
                accent: HubTheme.Warm);
            ControllerNav.Identify(create, "browser.create");
            create.Click += (_, _) => CreateRequested?.Invoke(this, EventArgs.Empty);
            Grid.SetColumn(create, 1);
            footer.Children.Add(create);

            _refresh = new HubNavButton("REFRESH", compact: true);
            ControllerNav.Identify(_refresh, "browser.refresh", initial: true);
            _refresh.Click += (_, _) => RefreshServers();
            Grid.SetColumn(_refresh, 3);
            footer.Children.Add(_refresh);

            _join = new HubNavButton("JOIN", compact: true, primary: true);
            ControllerNav.Identify(_join, "browser.join");
            _join.Click += (_, _) => _ = JoinAsync();
            Grid.SetColumn(_join, 4);
            footer.Children.Add(_join);

            back.SetValue(ControllerNav.NavRightProperty, "browser.create");
            create.SetValue(ControllerNav.NavLeftProperty, "browser.back");
            create.SetValue(ControllerNav.NavRightProperty, "browser.refresh");
            _refresh.SetValue(ControllerNav.NavLeftProperty, "browser.create");
            _refresh.SetValue(ControllerNav.NavRightProperty, "browser.join");
            _join.SetValue(ControllerNav.NavLeftProperty, "browser.refresh");
            return footer;
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
        }

        private void CancelDiscovery()
        {
            _discover?.Cancel();
            _discover?.Dispose();
            _discover = null;
        }

        private async void RefreshServers()
        {
            CancelDiscovery();
            _servers.Clear();
            _replied = 0;
            _live = 0;
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
            _summary.Text = $"CONNECTING TO {host}:{port}";
            _summary.Foreground = HubTheme.AccentBrush;
            CancelDiscovery();

            OnlineJoinResult result = await ServerBrowserService.JoinAsync(
                host, port, player, hunter, suit);

            _joining = false;
            if (!result.Joined)
            {
                _summary.Text = result.Error.ToUpperInvariant();
                _summary.Foreground = HubTheme.DangerBrush;
                return;
            }
            Launched?.Invoke(this, result.Plan);
        }

        public void SessionEnded(string reason)
        {
            _joining = false;
            _summary.Text = reason.Length > 0 ? reason.ToUpperInvariant() : "SESSION ENDED";
            _summary.Foreground = HubTheme.DangerBrush;
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
