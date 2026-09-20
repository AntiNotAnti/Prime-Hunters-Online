#if MPHREAD_AVALONIA
using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace MphRead.Mods.Launcher.Gui
{
    internal sealed class HubAdventureView : UserControl
    {
        private static readonly string[] HunterNames =
            Enumerable.Range(0, Hunters.Playable)
                .Select(i => ((Hunter)i).ToString())
                .Append(Hunter.Random.ToString()).ToArray();

        private readonly HubNavButton[] _slots = new HubNavButton[AdventureSave.SlotCount];
        private readonly TextBlock _slotTitle;
        private readonly TextBlock _slotDetail;
        private readonly ChoiceRow _hunter;
        private readonly HunterStand _stand;
        private readonly HubNavButton _continue;
        private readonly HubNavButton _newGame;
        private byte _selected = 1;

        public event EventHandler? Closed;
        public event EventHandler<LaunchPlan>? Launched;

        public HubAdventureView()
        {
            Focusable = true;
            Background = Brushes.Transparent;

            var root = new Grid
            {
                Margin = new Thickness(28, 24, 28, 34),
                RowDefinitions = new RowDefinitions("Auto,*,Auto"),
                RowSpacing = 16
            };
            root.Children.Add(HubChrome.Header(
                "PLAY  /  ADVENTURE",
                "ADVENTURE",
                "Choose a save slot and hunter, then continue or begin again.",
                "SAVE DATA"));

            var body = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("0.9*,1.1*"),
                ColumnSpacing = 14
            };

            var slotStack = new StackPanel { Spacing = 7 };
            for (byte slot = 1; slot <= AdventureSave.SlotCount; slot++)
            {
                AdventureSave.SlotInfo info = AdventureSave.Read(slot);
                var button = new HubNavButton($"SLOT {slot}", SlotLine(info),
                    primary: slot == 1,
                    accent: info.Used ? HubTheme.Accent : HubTheme.TextDim)
                {
                    MinHeight = 76
                };
                byte chosen = slot;
                ControllerNav.Identify(button, $"adventure.slot{slot}", slot == 1);
                button.Click += (_, _) => Select(chosen);
                _slots[slot - 1] = button;
                slotStack.Children.Add(button);
            }
            for (int i = 0; i < _slots.Length; i++)
            {
                int previous = (i + _slots.Length - 1) % _slots.Length;
                int next = (i + 1) % _slots.Length;
                _slots[i].SetValue(ControllerNav.NavUpProperty, $"adventure.slot{previous + 1}");
                _slots[i].SetValue(ControllerNav.NavDownProperty, $"adventure.slot{next + 1}");
            }

            var slotPanel = new Border
            {
                Background = HubTheme.PanelBrush,
                BorderBrush = HubTheme.EdgeBrush,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12),
                Child = slotStack
            };
            body.Children.Add(slotPanel);

            var detailStack = new StackPanel { Spacing = 9, Margin = new Thickness(16) };
            detailStack.Children.Add(new TextBlock
            {
                Text = "SELECTED SAVE",
                FontFamily = HubTheme.DataBold,
                FontSize = 8,
                Foreground = HubTheme.AccentBrush
            });
            _slotTitle = new TextBlock
            {
                FontFamily = HubTheme.Ui,
                FontWeight = FontWeight.Bold,
                FontSize = 21,
                Foreground = HubTheme.TextBrush
            };
            _slotDetail = new TextBlock
            {
                FontFamily = HubTheme.Ui,
                FontSize = 10.5,
                Foreground = HubTheme.TextDimBrush,
                TextWrapping = TextWrapping.Wrap
            };
            detailStack.Children.Add(_slotTitle);
            detailStack.Children.Add(_slotDetail);

            _stand = new HunterStand
            {
                Height = 175,
                MinHeight = 150,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Name2 = HunterNames[Math.Clamp((int)LauncherPrefs.LastHunter, 0, HunterNames.Length - 1)]
            };
            detailStack.Children.Add(_stand);

            int hunterIndex = Array.IndexOf(HunterNames, LauncherPrefs.LastHunter.ToString());
            _hunter = new ChoiceRow("Hunter", HunterNames, Math.Max(0, hunterIndex));
            _hunter.Changed += (_, _) => _stand.Name2 = _hunter.Value;
            detailStack.Children.Add(_hunter);

            var actions = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,*"),
                ColumnSpacing = 7
            };
            _continue = new HubNavButton("CONTINUE", compact: true, primary: true);
            ControllerNav.Identify(_continue, "adventure.continue");
            _continue.Click += (_, _) => Launch(newGame: false);
            actions.Children.Add(_continue);

            _newGame = new HubNavButton("NEW GAME",
                "Start this slot from the beginning", compact: true, accent: HubTheme.Warm);
            ControllerNav.Identify(_newGame, "adventure.newgame");
            _newGame.Click += (_, _) => Launch(newGame: true);
            Grid.SetColumn(_newGame, 1);
            actions.Children.Add(_newGame);
            _continue.SetValue(ControllerNav.NavRightProperty, "adventure.newgame");
            _newGame.SetValue(ControllerNav.NavLeftProperty, "adventure.continue");
            detailStack.Children.Add(actions);

            var detailPanel = new Border
            {
                Background = HubTheme.PanelStrongBrush,
                BorderBrush = HubTheme.EdgeBrush,
                BorderThickness = new Thickness(1),
                Child = detailStack
            };
            Grid.SetColumn(detailPanel, 1);
            body.Children.Add(detailPanel);

            Grid.SetRow(body, 1);
            root.Children.Add(body);

            var back = new HubNavButton("BACK", compact: true);
            ControllerNav.Identify(back, "adventure.back");
            back.Click += (_, _) => Closed?.Invoke(this, EventArgs.Empty);
            Grid.SetRow(back, 2);
            root.Children.Add(back);

            Content = root;
            Select(1);

            SizeChanged += (_, e) =>
            {
                bool compact = e.NewSize.Width < 760 || e.NewSize.Height < 520;
                body.ColumnDefinitions = compact
                    ? new ColumnDefinitions("*")
                    : new ColumnDefinitions("0.9*,1.1*");
                body.RowDefinitions = compact
                    ? new RowDefinitions("Auto,Auto")
                    : new RowDefinitions("*");
                Grid.SetColumn(detailPanel, compact ? 0 : 1);
                Grid.SetRow(detailPanel, compact ? 1 : 0);
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

        private void Select(byte slot)
        {
            _selected = slot;
            AdventureSave.SlotInfo info = AdventureSave.Read(slot);
            _slotTitle.Text = $"SLOT {slot}";
            _slotDetail.Text = info.Used
                ? $"{info.Area}\n{info.Octoliths}/8 OCTOLITHS  /  {info.Health}/{info.HealthMax} ENERGY"
                : "EMPTY SLOT\nStart a new hunt from Celestial Archives.";
            _continue.IsEnabled = info.Used;
            _newGame.Label = info.Used ? "NEW GAME" : "START NEW GAME";
        }

        private void Launch(bool newGame)
        {
            AdventureSave.SlotInfo info = AdventureSave.Read(_selected);
            if (!newGame && !info.Used)
                return;
            Hunter hunter = Enum.TryParse(_hunter.Value, true, out Hunter parsed)
                ? parsed : LauncherPrefs.LastHunter;
            Launched?.Invoke(this, AdventureLaunch.Create(_selected, newGame, hunter));
        }

        private static string SlotLine(AdventureSave.SlotInfo info) =>
            info.Used ? $"{info.Area}  /  {info.Octoliths}/8 OCTOLITHS" : "EMPTY";
    }
}
#endif
