#if MPHREAD_AVALONIA
using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using MphRead.Mods.Network;

namespace MphRead.Mods.Launcher.Gui
{
    internal sealed class HubQuickPlayView : UserControl
    {
        private readonly TextBlock _status;
        private readonly TextBlock _detail;
        private readonly bool _preview;
        private CancellationTokenSource? _cancel;
        private bool _started;

        public event EventHandler? Closed;
        public event EventHandler? BrowseRequested;
        public event EventHandler<LaunchPlan>? Launched;

        public HubQuickPlayView(bool preview = false)
        {
            _preview = preview;
            Focusable = true;
            Background = Brushes.Transparent;

            var root = new Grid
            {
                Margin = new Thickness(28, 24, 28, 36),
                RowDefinitions = new RowDefinitions("Auto,*,Auto"),
                RowSpacing = 18
            };
            var heading = new StackPanel { Spacing = 3 };
            heading.Children.Add(new TextBlock
            {
                Text = "QUICK PLAY",
                FontFamily = HubTheme.Ui,
                FontWeight = FontWeight.Bold,
                FontSize = 28,
                Foreground = HubTheme.TextBrush
            });
            heading.Children.Add(new TextBlock
            {
                Text = "Find the best compatible public session and deploy.",
                FontFamily = HubTheme.Ui,
                FontSize = 11,
                Foreground = HubTheme.TextDimBrush
            });
            root.Children.Add(heading);

            var center = new Border
            {
                Background = HubTheme.PanelStrongBrush,
                BorderBrush = HubTheme.AccentBrush,
                BorderThickness = new Thickness(1),
                MaxWidth = 620,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Padding = new Thickness(28)
            };
            var copy = new StackPanel { Spacing = 10 };
            _status = new TextBlock
            {
                Text = preview ? "BEST SERVER FOUND" : "SCANNING LIVE SERVERS",
                FontFamily = HubTheme.DataBold,
                FontSize = 10,
                Foreground = HubTheme.AccentBrush,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            _detail = new TextBlock
            {
                Text = preview
                    ? "TEST ARENA  /  31 MS  /  2 OF 8 PLAYERS"
                    : "Checking directory entries from this machine...",
                FontFamily = HubTheme.Ui,
                FontWeight = FontWeight.SemiBold,
                FontSize = 17,
                Foreground = HubTheme.TextBrush,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            };
            copy.Children.Add(_status);
            copy.Children.Add(_detail);
            center.Child = copy;
            Grid.SetRow(center, 1);
            root.Children.Add(center);

            var footer = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto")
            };
            var back = new HubNavButton("BACK", compact: true, accent: HubTheme.Danger);
            ControllerNav.Identify(back, "quick.back", initial: true);
            back.Click += (_, _) => Close();
            footer.Children.Add(back);
            var browse = new HubNavButton("BROWSE SERVERS", compact: true);
            ControllerNav.Identify(browse, "quick.browse");
            browse.Click += (_, _) =>
            {
                Cancel();
                BrowseRequested?.Invoke(this, EventArgs.Empty);
            };
            Grid.SetColumn(browse, 2);
            footer.Children.Add(browse);
            back.SetValue(ControllerNav.NavRightProperty, "quick.browse");
            browse.SetValue(ControllerNav.NavLeftProperty, "quick.back");
            Grid.SetRow(footer, 2);
            root.Children.Add(footer);

            Content = root;
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            if (_preview || _started)
                return;
            _started = true;
            _cancel = new CancellationTokenSource();
            _ = RunAsync(_cancel.Token);
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            Cancel();
            base.OnDetachedFromVisualTree(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();
                return;
            }
            base.OnKeyDown(e);
        }

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            QuickPlaySearchResult search =
                await ServerBrowserService.FindBestAsync(cancellationToken);
            if (cancellationToken.IsCancellationRequested)
                return;

            if (!search.Found)
            {
                _status.Text = "NO OPEN COMPATIBLE SERVER";
                _status.Foreground = HubTheme.WarmBrush;
                _detail.Text = search.Message;
                return;
            }

            ServerBrowserEntry entry = search.Entry;
            string ping = entry.Status.Latency >= 0
                ? $"{entry.Status.Latency} MS" : "PING --";
            _status.Text = "BEST SERVER FOUND";
            _status.Foreground = HubTheme.GoodBrush;
            _detail.Text = $"{entry.Name.ToUpperInvariant()}  /  {ping}\n"
                + $"{entry.Status.Players}/{entry.Status.MaxPlayers} PLAYERS  /  "
                + NetStatus.ModeName(entry.Status.Mode).ToUpperInvariant();

            HubSnapshot player = HubState.Capture();
            _status.Text = "JOINING";
            OnlineJoinResult joined = await ServerBrowserService.JoinAsync(
                entry.Listing.Address, entry.Listing.Port, player.PlayerName,
                player.PreferredHunter, player.Suit, cancellationToken);

            if (cancellationToken.IsCancellationRequested)
                return;
            if (!joined.Joined)
            {
                _status.Text = "COULD NOT JOIN";
                _status.Foreground = HubTheme.DangerBrush;
                _detail.Text = joined.Error;
                return;
            }
            Launched?.Invoke(this, joined.Plan);
        }

        private void Close()
        {
            Cancel();
            Closed?.Invoke(this, EventArgs.Empty);
        }

        private void Cancel()
        {
            _cancel?.Cancel();
            _cancel?.Dispose();
            _cancel = null;
        }
    }
}
#endif
