#if MPHREAD_AVALONIA
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace MphRead.Mods.Launcher.Gui
{
    /// <summary>
    /// Hub-native entry point to the existing SettingsView sections.
    ///
    /// This owns navigation only. SettingsView remains the one place that
    /// stages, validates, applies and saves configuration.
    /// </summary>
    internal sealed class HubSettingsView : UserControl
    {
        private readonly Grid _cards;
        private bool _compact;

        public event Action<string>? SectionRequested;
        public event EventHandler? Closed;

        public HubSettingsView()
        {
            Focusable = true;
            Background = Brushes.Transparent;

            var root = new Grid
            {
                Margin = new Thickness(28, 24, 28, 36),
                RowDefinitions = new RowDefinitions("Auto,*,Auto"),
                RowSpacing = 18
            };

            root.Children.Add(HubChrome.Header(
                "HOME  /  SETTINGS",
                "SETTINGS",
                "Configure display, audio, controls, replays and your profile.",
                "CONFIGURATION"));

            _cards = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,*"),
                RowDefinitions = new RowDefinitions("*,*,*"),
                ColumnSpacing = 10,
                RowSpacing = 10
            };

            HubNavButton display = Add(0, 0, "DISPLAY",
                "Resolution, frame pacing, FOV, HUD and graphics", "Display",
                HubTheme.Accent, initial: true);
            HubNavButton audio = Add(1, 0, "AUDIO",
                "Sound effects and music volume", "Audio", HubTheme.Good);
            HubNavButton controls = Add(0, 1, "CONTROLS",
                "Keyboard, mouse, controller, touch and stylus", "Controls",
                Color.FromRgb(0x86, 0xb8, 0xff));
            HubNavButton replays = Add(1, 1, "REPLAYS",
                "Recording, instant clips and replay storage", "Replays",
                Color.FromRgb(0xa7, 0x9b, 0xf5));
            HubNavButton profile = Add(0, 2, "PROFILE",
                "Player identity, hunter, network and updates", "Profile",
                HubTheme.Warm);
            HubNavButton credits = Add(1, 2, "CREDITS",
                "Project, technology and attribution", "Credits",
                HubTheme.TextDim);

            Wire(display, "profile", "controls", "audio", "audio");
            Wire(audio, "credits", "replays", "display", "display");
            Wire(controls, "display", "profile", "replays", "replays");
            Wire(replays, "audio", "credits", "controls", "controls");
            Wire(profile, "controls", "display", "credits", "credits");
            Wire(credits, "replays", "audio", "profile", "profile");

            var cardScroll = new ScrollViewer
            {
                Content = _cards,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
            };
            Grid.SetRow(cardScroll, 1);
            root.Children.Add(cardScroll);

            var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            var back = new HubNavButton("BACK", compact: true);
            ControllerNav.Identify(back, "settings.back");
            back.Click += (_, _) => Closed?.Invoke(this, EventArgs.Empty);
            footer.Children.Add(back);
            var note = new TextBlock
            {
                Text = "CHANGES ARE STILL APPLIED BY THE EXISTING SETTINGS TRANSACTION",
                FontFamily = HubTheme.Data,
                FontSize = 8,
                Foreground = HubTheme.TextDimBrush,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            Grid.SetColumn(note, 1);
            footer.Children.Add(note);
            Grid.SetRow(footer, 2);
            root.Children.Add(footer);

            Content = root;
            SizeChanged += (_, e) => ApplyResponsive(e.NewSize);
            ApplyResponsive(new Size(960, 600));
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

        private HubNavButton Add(int column, int row, string label, string detail,
            string section, Color accent, bool initial = false)
        {
            var button = new HubNavButton(label, detail, accent: accent)
            {
                MinHeight = 88
            };
            string id = $"settings.{section.ToLowerInvariant()}";
            ControllerNav.Identify(button, id, initial);
            button.Click += (_, _) => SectionRequested?.Invoke(section);
            Grid.SetColumn(button, column);
            Grid.SetRow(button, row);
            _cards.Children.Add(button);
            return button;
        }

        private static void Wire(HubNavButton button, string up, string down,
            string left, string right)
        {
            button.SetValue(ControllerNav.NavUpProperty, $"settings.{up}");
            button.SetValue(ControllerNav.NavDownProperty, $"settings.{down}");
            button.SetValue(ControllerNav.NavLeftProperty, $"settings.{left}");
            button.SetValue(ControllerNav.NavRightProperty, $"settings.{right}");
        }

        private void ApplyResponsive(Size size)
        {
            bool oneColumn = size.Width < 560;
            bool twoColumnCompact = !oneColumn && size.Height < 500;
            bool compact = oneColumn || twoColumnCompact;
            if (compact == _compact && !twoColumnCompact)
                return;
            _compact = compact;

            HubNavButton display = (HubNavButton)ControllerNav.Find(_cards, "settings.display")!;
            HubNavButton audio = (HubNavButton)ControllerNav.Find(_cards, "settings.audio")!;
            HubNavButton controls = (HubNavButton)ControllerNav.Find(_cards, "settings.controls")!;
            HubNavButton replays = (HubNavButton)ControllerNav.Find(_cards, "settings.replays")!;
            HubNavButton profile = (HubNavButton)ControllerNav.Find(_cards, "settings.profile")!;
            HubNavButton credits = (HubNavButton)ControllerNav.Find(_cards, "settings.credits")!;

            if (oneColumn)
            {
                _cards.ColumnDefinitions = new ColumnDefinitions("*");
                _cards.RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto");
                for (int i = 0; i < _cards.Children.Count; i++)
                {
                    Grid.SetColumn(_cards.Children[i], 0);
                    Grid.SetRow(_cards.Children[i], i);
                }
                Wire(display, "credits", "audio", "credits", "audio");
                Wire(audio, "display", "controls", "display", "controls");
                Wire(controls, "audio", "replays", "audio", "replays");
                Wire(replays, "controls", "profile", "controls", "profile");
                Wire(profile, "replays", "credits", "replays", "credits");
                Wire(credits, "profile", "display", "profile", "display");
            }
            else if (twoColumnCompact)
            {
                _cards.ColumnDefinitions = new ColumnDefinitions("*,*");
                _cards.RowDefinitions = new RowDefinitions("Auto,Auto,Auto");
                for (int i = 0; i < _cards.Children.Count; i++)
                {
                    Grid.SetColumn(_cards.Children[i], i % 2);
                    Grid.SetRow(_cards.Children[i], i / 2);
                }
                Wire(display, "profile", "controls", "audio", "audio");
                Wire(audio, "credits", "replays", "display", "display");
                Wire(controls, "display", "profile", "replays", "replays");
                Wire(replays, "audio", "credits", "controls", "controls");
                Wire(profile, "controls", "display", "credits", "credits");
                Wire(credits, "replays", "audio", "profile", "profile");
            }
            else
            {
                _cards.ColumnDefinitions = new ColumnDefinitions("*,*");
                _cards.RowDefinitions = new RowDefinitions("*,*,*");
                for (int i = 0; i < _cards.Children.Count; i++)
                {
                    Grid.SetColumn(_cards.Children[i], i % 2);
                    Grid.SetRow(_cards.Children[i], i / 2);
                }
                Wire(display, "profile", "controls", "audio", "audio");
                Wire(audio, "credits", "replays", "display", "display");
                Wire(controls, "display", "profile", "replays", "replays");
                Wire(replays, "audio", "credits", "controls", "controls");
                Wire(profile, "controls", "display", "credits", "credits");
                Wire(credits, "replays", "audio", "profile", "profile");
            }
        }
    }
}
#endif
