#if MPHREAD_AVALONIA
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace MphRead.Mods.Launcher.Gui
{
    internal sealed class HubMultiplayerView : UserControl
    {
        public event Action<HubMultiplayerDestination>? Selected;
        public event EventHandler? Closed;

        public HubMultiplayerView()
        {
            Focusable = true;
            Background = Brushes.Transparent;

            var root = new Grid
            {
                Margin = new Thickness(28, 24, 28, 34),
                RowDefinitions = new RowDefinitions("Auto,*,Auto"),
                RowSpacing = 18
            };
            root.Children.Add(HubChrome.Header(
                "PLAY  /  MULTIPLAYER",
                "MULTIPLAYER",
                "Find a session, inspect the server list or create your own lobby.",
                "ONLINE",
                HubTheme.GoodBrush));

            var cards = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,*,*"),
                ColumnSpacing = 12
            };
            HubNavButton quick = Card("QUICK PLAY",
                "Automatically choose the lowest-latency compatible open server.",
                HubTheme.Accent, HubMultiplayerDestination.QuickPlay, initial: true);
            HubNavButton browser = Card("SERVER BROWSER",
                "Browse public sessions by latency, map, mode and population.",
                HubTheme.Good, HubMultiplayerDestination.ServerBrowser);
            HubNavButton custom = Card("CUSTOM MATCH",
                "Create a lobby, map rotation and rules for your own session.",
                HubTheme.Warm, HubMultiplayerDestination.CustomMatch);
            cards.Children.Add(quick);
            Grid.SetColumn(browser, 1); cards.Children.Add(browser);
            Grid.SetColumn(custom, 2); cards.Children.Add(custom);

            quick.SetValue(ControllerNav.NavLeftProperty, "multiplayer.custommatch");
            quick.SetValue(ControllerNav.NavRightProperty, "multiplayer.serverbrowser");
            browser.SetValue(ControllerNav.NavLeftProperty, "multiplayer.quickplay");
            browser.SetValue(ControllerNav.NavRightProperty, "multiplayer.custommatch");
            custom.SetValue(ControllerNav.NavLeftProperty, "multiplayer.serverbrowser");
            custom.SetValue(ControllerNav.NavRightProperty, "multiplayer.quickplay");

            Grid.SetRow(cards, 1);
            root.Children.Add(cards);

            var back = new HubNavButton("BACK", compact: true);
            ControllerNav.Identify(back, "multiplayer.back");
            back.Click += (_, _) => Closed?.Invoke(this, EventArgs.Empty);
            Grid.SetRow(back, 2);
            root.Children.Add(back);

            Content = root;
            SizeChanged += (_, e) =>
            {
                bool compact = e.NewSize.Width < 720 || e.NewSize.Height < 500;
                cards.ColumnDefinitions = compact ? new ColumnDefinitions("*")
                    : new ColumnDefinitions("*,*,*");
                cards.RowDefinitions = compact ? new RowDefinitions("Auto,Auto,Auto")
                    : new RowDefinitions("*");
                for (int i = 0; i < cards.Children.Count; i++)
                {
                    Grid.SetColumn(cards.Children[i], compact ? 0 : i);
                    Grid.SetRow(cards.Children[i], compact ? i : 0);
                }
                if (compact)
                {
                    quick.SetValue(ControllerNav.NavUpProperty, "multiplayer.custommatch");
                    quick.SetValue(ControllerNav.NavDownProperty, "multiplayer.serverbrowser");
                    browser.SetValue(ControllerNav.NavUpProperty, "multiplayer.quickplay");
                    browser.SetValue(ControllerNav.NavDownProperty, "multiplayer.custommatch");
                    custom.SetValue(ControllerNav.NavUpProperty, "multiplayer.serverbrowser");
                    custom.SetValue(ControllerNav.NavDownProperty, "multiplayer.quickplay");
                }
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

        private HubNavButton Card(string label, string detail, Color accent,
            HubMultiplayerDestination destination, bool initial = false)
        {
            var button = new HubNavButton(label, detail, primary: initial, accent: accent)
            {
                MinHeight = 150
            };
            string id = destination switch
            {
                HubMultiplayerDestination.QuickPlay => "quickplay",
                HubMultiplayerDestination.ServerBrowser => "serverbrowser",
                _ => "custommatch"
            };
            ControllerNav.Identify(button, $"multiplayer.{id}", initial);
            button.Click += (_, _) => Selected?.Invoke(destination);
            return button;
        }
    }
}
#endif
