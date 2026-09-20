#if MPHREAD_AVALONIA
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace MphRead.Mods.Launcher.Gui
{
    internal sealed class HubPlayView : UserControl
    {
        public event Action<HubPlayDestination>? Selected;
        public event EventHandler? Closed;

        public HubPlayView()
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
                "HOME  /  PLAY",
                "PLAY",
                "Choose the kind of session you want to enter.",
                "SESSION SELECT"));

            var cards = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,*,*"),
                ColumnSpacing = 12
            };
            HubNavButton multiplayer = Card("MULTIPLAYER",
                "Quick Play, public servers and custom lobbies.",
                HubTheme.Accent, HubPlayDestination.Multiplayer, initial: true);
            HubNavButton offline = Card("OFFLINE",
                "Local combat with map, mode, bots and training options.",
                HubTheme.Good, HubPlayDestination.Offline);
            HubNavButton adventure = Card("ADVENTURE",
                "Continue a save slot or begin a new single-player run.",
                Color.FromRgb(0xa7, 0x9b, 0xf5), HubPlayDestination.Adventure);
            cards.Children.Add(multiplayer);
            Grid.SetColumn(offline, 1); cards.Children.Add(offline);
            Grid.SetColumn(adventure, 2); cards.Children.Add(adventure);

            multiplayer.SetValue(ControllerNav.NavLeftProperty, "play.adventure");
            multiplayer.SetValue(ControllerNav.NavRightProperty, "play.offline");
            offline.SetValue(ControllerNav.NavLeftProperty, "play.multiplayer");
            offline.SetValue(ControllerNav.NavRightProperty, "play.adventure");
            adventure.SetValue(ControllerNav.NavLeftProperty, "play.offline");
            adventure.SetValue(ControllerNav.NavRightProperty, "play.multiplayer");

            Grid.SetRow(cards, 1);
            root.Children.Add(cards);

            var back = new HubNavButton("BACK", compact: true);
            ControllerNav.Identify(back, "play.back");
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
                    multiplayer.SetValue(ControllerNav.NavUpProperty, "play.adventure");
                    multiplayer.SetValue(ControllerNav.NavDownProperty, "play.offline");
                    offline.SetValue(ControllerNav.NavUpProperty, "play.multiplayer");
                    offline.SetValue(ControllerNav.NavDownProperty, "play.adventure");
                    adventure.SetValue(ControllerNav.NavUpProperty, "play.offline");
                    adventure.SetValue(ControllerNav.NavDownProperty, "play.multiplayer");
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
            HubPlayDestination destination, bool initial = false)
        {
            var button = new HubNavButton(label, detail, primary: initial, accent: accent)
            {
                MinHeight = 150
            };
            ControllerNav.Identify(button,
                $"play.{destination.ToString().ToLowerInvariant()}", initial);
            button.Click += (_, _) => Selected?.Invoke(destination);
            return button;
        }
    }
}
#endif
