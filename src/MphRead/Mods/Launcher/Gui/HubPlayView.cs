#if MPHREAD_AVALONIA
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace MphRead.Mods.Launcher.Gui
{
    /// <summary>
    /// Deployment chooser between the home hub and the existing game screens.
    ///
    /// The purpose is information architecture, not another implementation of
    /// launch logic. StartScreen turns the selected destination into the
    /// existing PlayScreen/CreateServerScreen path.
    /// </summary>
    internal sealed class HubPlayView : UserControl
    {
        public event Action<HubPlayDestination>? Selected;
        public event EventHandler? Closed;

        private readonly Grid _cards;
        private bool _compact;

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

            var heading = new StackPanel { Spacing = 4 };
            heading.Children.Add(new TextBlock
            {
                Text = "DEPLOYMENT",
                FontFamily = HubTheme.Ui,
                FontWeight = FontWeight.Bold,
                FontSize = 28,
                Foreground = HubTheme.TextBrush
            });
            heading.Children.Add(new TextBlock
            {
                Text = "Choose how you want to enter the hunt.",
                FontFamily = HubTheme.Ui,
                FontSize = 11,
                Foreground = HubTheme.TextDimBrush
            });
            root.Children.Add(heading);

            _cards = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,*"),
                RowDefinitions = new RowDefinitions("*,*"),
                ColumnSpacing = 12,
                RowSpacing = 12
            };
            AddCard(0, 0, "ONLINE",
                "Browse live public servers and join an active lobby.",
                "PUBLIC NETWORK", HubPlayDestination.Online, HubTheme.Accent, initial: true);
            AddCard(1, 0, "CUSTOM MATCH",
                "Create a lobby, choose rotation and rules, then invite players.",
                "HOST / LOBBY", HubPlayDestination.Custom, HubTheme.Warm);
            AddCard(0, 1, "OFFLINE",
                "Pick any map, mode and bot configuration for local combat.",
                "LOCAL COMBAT", HubPlayDestination.Offline, HubTheme.Good);
            AddCard(1, 1, "ADVENTURE",
                "Continue a save slot or begin a new single-player run.",
                "STORY", HubPlayDestination.Adventure, Color.FromRgb(0xa7, 0x9b, 0xf5));
            Grid.SetRow(_cards, 1);
            root.Children.Add(_cards);

            var footer = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*")
            };
            var back = new HubNavButton("BACK", "Return to command hub", compact: true);
            ControllerNav.Identify(back, "deploy.back");
            back.Click += (_, _) => Closed?.Invoke(this, EventArgs.Empty);
            footer.Children.Add(back);

            var note = new TextBlock
            {
                Text = "SERVER BROWSER AND MATCH LOGIC REMAIN SHARED WITH THE EXISTING NETWORK LAYER",
                FontFamily = HubTheme.Data,
                FontSize = 8,
                Foreground = HubTheme.TextDimBrush,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            Grid.SetColumn(note, 1);
            footer.Children.Add(note);
            Grid.SetRow(footer, 2);
            root.Children.Add(footer);

            Content = root;
            SizeChanged += (_, e) => ApplyResponsive(e.NewSize);
            ApplyResponsive(new Size(960, 600));
        }

        protected override void OnKeyDown(Avalonia.Input.KeyEventArgs e)
        {
            if (e.Key == Avalonia.Input.Key.Escape)
            {
                e.Handled = true;
                Closed?.Invoke(this, EventArgs.Empty);
                return;
            }
            base.OnKeyDown(e);
        }

        private void AddCard(int column, int row, string title, string detail,
            string tag, HubPlayDestination destination, Color accent, bool initial = false)
        {
            var stack = new StackPanel { Spacing = 8 };
            stack.Children.Add(new TextBlock
            {
                Text = tag,
                FontFamily = HubTheme.DataBold,
                FontSize = 8,
                Foreground = new SolidColorBrush(accent)
            });
            stack.Children.Add(new TextBlock
            {
                Text = title,
                FontFamily = HubTheme.Ui,
                FontWeight = FontWeight.Bold,
                FontSize = 19,
                Foreground = HubTheme.TextBrush
            });
            stack.Children.Add(new TextBlock
            {
                Text = detail,
                FontFamily = HubTheme.Ui,
                FontSize = 10.5,
                Foreground = HubTheme.TextDimBrush,
                TextWrapping = TextWrapping.Wrap
            });

            var button = new HubNavButton(title, detail, accent: accent)
            {
                MinHeight = 112
            };
            string id = $"deploy.{destination.ToString().ToLowerInvariant()}";
            ControllerNav.Identify(button, id, initial);
            button.Click += (_, _) => Selected?.Invoke(destination);

            Grid.SetColumn(button, column);
            Grid.SetRow(button, row);
            _cards.Children.Add(button);
        }

        private void ApplyResponsive(Size size)
        {
            bool compact = size.Width < 720 || size.Height < 500;
            if (compact == _compact)
            {
                return;
            }
            _compact = compact;
            if (compact)
            {
                _cards.ColumnDefinitions = new ColumnDefinitions("*");
                _cards.RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto");
                for (int i = 0; i < _cards.Children.Count; i++)
                {
                    Grid.SetColumn(_cards.Children[i], 0);
                    Grid.SetRow(_cards.Children[i], i);
                }
            }
            else
            {
                _cards.ColumnDefinitions = new ColumnDefinitions("*,*");
                _cards.RowDefinitions = new RowDefinitions("*,*");
                for (int i = 0; i < _cards.Children.Count; i++)
                {
                    Grid.SetColumn(_cards.Children[i], i % 2);
                    Grid.SetRow(_cards.Children[i], i / 2);
                }
            }
        }
    }
}
#endif
