#if MPHREAD_AVALONIA
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using MphRead.Entities;

namespace MphRead.Mods.Launcher.Gui
{
    internal sealed class HubOfflineView : UserControl
    {
        private static readonly string[] HunterNames =
            Enumerable.Range(0, Hunters.Playable).Select(i => ((Hunter)i).ToString())
                .Append(Hunter.Random.ToString()).ToArray();

        private readonly MenuSettings _settings;
        private readonly UiList _maps = new();
        private readonly Image _preview = new() { Stretch = Stretch.UniformToFill };
        private readonly TextBlock _mapName;
        private readonly TextBlock _mapCode;
        private readonly ChoiceRow _mode;
        private readonly ChoiceRow _hunter;
        private readonly ChoiceRow _suit;
        private readonly ChoiceRow _bots;
        private readonly ChoiceRow _skill;
        private readonly HunterStand _stand;
        private readonly HubNavButton _start;
        private readonly Grid _body;
        private Bitmap? _bitmap;
        private string _selectedRoom = "";

        public event EventHandler? Closed;
        public event EventHandler<LaunchPlan>? Launched;

        public HubOfflineView(MenuSettings settings, IReadOnlyList<string> rooms)
        {
            _settings = settings;
            Focusable = true;
            Background = Brushes.Transparent;

            var root = new Grid
            {
                Margin = new Thickness(24, 20, 24, 32),
                RowDefinitions = new RowDefinitions("Auto,*,Auto"),
                RowSpacing = 12
            };
            root.Children.Add(HubChrome.Header(
                "PLAY  /  OFFLINE",
                "OFFLINE",
                "Choose a map, configure the local match and deploy.",
                "LOCAL COMBAT",
                HubTheme.GoodBrush));

            _maps.SelectionChanged += (_, row) => SelectRow(row);
            _maps.Activated += (_, row) => SelectRow(row);
            foreach (string room in rooms)
            {
                string display = room;
                try
                {
                    (RoomMetadata? meta, _) = Metadata.GetRoomByName(room);
                    display = meta?.InGameName ?? room;
                }
                catch
                {
                    // The room code is enough for a first-run/headless layout.
                }
                _maps.Add(new UiListRow(display, room) { Choice = room });
            }
            if (rooms.Count == 0)
                _maps.AddNote("No multiplayer maps are available. Set up game files first.", HubTheme.Warm);

            var mapPanel = new Border
            {
                Background = HubTheme.PanelBrush,
                BorderBrush = HubTheme.EdgeBrush,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8),
                Child = _maps
            };

            var detail = new StackPanel { Spacing = 7, Margin = new Thickness(14) };
            detail.Children.Add(new TextBlock
            {
                Text = "MATCH SETUP",
                FontFamily = HubTheme.DataBold,
                FontSize = 8,
                Foreground = HubTheme.AccentBrush
            });
            _mapName = new TextBlock
            {
                Text = "SELECT A MAP",
                FontFamily = HubTheme.Ui,
                FontWeight = FontWeight.Bold,
                FontSize = 19,
                Foreground = HubTheme.TextBrush,
                TextWrapping = TextWrapping.Wrap
            };
            _mapCode = new TextBlock
            {
                FontFamily = HubTheme.Data,
                FontSize = 8.5,
                Foreground = HubTheme.TextDimBrush
            };
            detail.Children.Add(_mapName);
            detail.Children.Add(_mapCode);

            var visual = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,120"),
                Height = 150,
                ColumnSpacing = 8
            };
            visual.Children.Add(new Border
            {
                Background = HubTheme.InkBrush,
                BorderBrush = HubTheme.EdgeBrush,
                BorderThickness = new Thickness(1),
                ClipToBounds = true,
                Child = _preview
            });
            _stand = new HunterStand
            {
                Width = 112,
                Height = 150,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Name2 = HunterNames[Math.Clamp((int)LauncherPrefs.LastHunter, 0, HunterNames.Length - 1)],
                Suit = Math.Clamp(LauncherPrefs.LastColor, 0, 3)
            };
            Grid.SetColumn(_stand, 1);
            visual.Children.Add(_stand);
            detail.Children.Add(visual);

            _mode = new ChoiceRow("Mode",
                OfflineLaunch.Modes.Select(m => m.Label).ToArray());
            _hunter = new ChoiceRow("Hunter", HunterNames,
                Math.Max(0, Array.IndexOf(HunterNames, LauncherPrefs.LastHunter.ToString())));
            _suit = new ChoiceRow("Suit", new[] { "1", "2", "3", "4" },
                Math.Clamp(LauncherPrefs.LastColor, 0, 3));
            _bots = new ChoiceRow("Bots",
                Enumerable.Range(0, PlayerEntity.SlotCapacity)
                    .Select(i => i.ToString()).ToArray(),
                Math.Clamp(LauncherPrefs.Bots, 0, PlayerEntity.SlotCapacity - 1));
            _skill = new ChoiceRow("Bot skill",
                new[] { "Easy", "Normal", "Hard", "Insane" },
                Math.Clamp(LauncherPrefs.BotLevel, 0, 3));

            _hunter.Changed += (_, _) => _stand.Name2 = _hunter.Value;
            _suit.Changed += (_, _) => _stand.Suit = _suit.Index;
            detail.Children.Add(_mode);
            detail.Children.Add(_hunter);
            detail.Children.Add(_suit);
            detail.Children.Add(_bots);
            detail.Children.Add(_skill);

            var setupPanel = new Border
            {
                Background = HubTheme.PanelStrongBrush,
                BorderBrush = HubTheme.EdgeBrush,
                BorderThickness = new Thickness(1),
                Child = detail
            };

            _body = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("1.15*,0.85*"),
                ColumnSpacing = 12
            };
            _body.Children.Add(mapPanel);
            Grid.SetColumn(setupPanel, 1);
            _body.Children.Add(setupPanel);

            var scroll = new ScrollViewer
            {
                Content = _body,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
            };
            Grid.SetRow(scroll, 1);
            root.Children.Add(scroll);

            var footer = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
                ColumnSpacing = 8
            };
            var back = new HubNavButton("BACK", compact: true);
            ControllerNav.Identify(back, "offline.back");
            back.Click += (_, _) => Closed?.Invoke(this, EventArgs.Empty);
            footer.Children.Add(back);

            _start = new HubNavButton("START MATCH", compact: true, primary: true)
            {
                IsEnabled = rooms.Count > 0
            };
            ControllerNav.Identify(_start, "offline.start");
            _start.Click += (_, _) => Start();
            Grid.SetColumn(_start, 2);
            footer.Children.Add(_start);
            back.SetValue(ControllerNav.NavRightProperty, "offline.start");
            _start.SetValue(ControllerNav.NavLeftProperty, "offline.back");

            Grid.SetRow(footer, 2);
            root.Children.Add(footer);
            Content = root;

            SizeChanged += (_, e) => ApplyResponsive(e.NewSize, mapPanel, setupPanel);
            if (rooms.Count > 0)
            {
                _maps.FocusFirst();
                SelectRoom(rooms.Contains(settings.RoomKey) ? settings.RoomKey : rooms[0]);
            }
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            _bitmap?.Dispose();
            _bitmap = null;
            base.OnDetachedFromVisualTree(e);
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

        private void ApplyResponsive(Size size, Control mapPanel, Control setupPanel)
        {
            bool compact = size.Width < 760;
            _body.ColumnDefinitions = compact
                ? new ColumnDefinitions("*")
                : new ColumnDefinitions("1.15*,0.85*");
            _body.RowDefinitions = compact
                ? new RowDefinitions("260,Auto")
                : new RowDefinitions("*");
            Grid.SetColumn(mapPanel, 0);
            Grid.SetRow(mapPanel, 0);
            Grid.SetColumn(setupPanel, compact ? 0 : 1);
            Grid.SetRow(setupPanel, compact ? 1 : 0);
            _body.RowSpacing = compact ? 10 : 0;
        }

        private void SelectRow(Control row)
        {
            if (row is UiListRow line && line.Choice is string room)
                SelectRoom(room);
        }

        private void SelectRoom(string room)
        {
            _selectedRoom = room;
            _maps.SelectTag(room);
            try
            {
                (RoomMetadata? meta, _) = Metadata.GetRoomByName(room);
                _mapName.Text = (meta?.InGameName ?? room).ToUpperInvariant();
            }
            catch
            {
                _mapName.Text = room.ToUpperInvariant();
            }
            _mapCode.Text = room;
            _preview.Source = null;
            _bitmap?.Dispose();
            _bitmap = null;
            try
            {
                string path = ThumbnailGenerator.PathFor(room);
                if (File.Exists(path))
                    _bitmap = new Bitmap(path);
            }
            catch
            {
                // A missing thumbnail never makes a map unavailable.
            }
            _preview.Source = _bitmap;
        }

        private void Start()
        {
            if (_selectedRoom.Length == 0)
                return;
            Hunter hunter = Enum.TryParse(_hunter.Value, true, out Hunter parsed)
                ? parsed : LauncherPrefs.LastHunter;
            OfflineModeOption mode = OfflineLaunch.Modes[
                Math.Clamp(_mode.Index, 0, OfflineLaunch.Modes.Count - 1)];
            LaunchPlan plan = OfflineLaunch.Create(_settings, _selectedRoom, mode.Mode,
                hunter, _suit.Index, _bots.Index, _skill.Index);
            Launched?.Invoke(this, plan);
        }
    }
}
#endif
