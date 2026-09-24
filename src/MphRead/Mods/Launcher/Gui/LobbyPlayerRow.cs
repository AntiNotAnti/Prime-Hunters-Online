using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using MphRead.Mods.Network;

namespace MphRead.Mods.Launcher.Gui
{
    internal sealed class LobbyPlayerRow : Border
    {
        private readonly TextBlock _ready, _player, _hunter, _team, _ping;
        public LobbyPlayerRow(RosterPacket roster, int index, byte owner,
            bool showTeam = true, bool selected = false)
        {
            int slot = roster.Slots[index];
            string team = roster.Teams[index] < 0 ? "AUTO" : $"TEAM {(char)('A' + roster.Teams[index])}";
            string state = roster.LobbyReady[index] ? "READY" : "WAIT";
            string name = roster.Names[index] + (slot == owner ? "  [OWNER]" : "");
            string hunter = $"{(Hunter)roster.Hunters[index]} · S{roster.Colors[index] + 1}";

            var line = new Grid
            {
                ColumnDefinitions = showTeam
                    ? new ColumnDefinitions("Auto,*,Auto,Auto,Auto")
                    : new ColumnDefinitions("Auto,*,Auto,Auto"),
                ColumnSpacing = 8,
                MinHeight = 22
            };
            var ready = _ready = new TextBlock
            {
                Text = state,
                FontFamily = GuiTheme.Display,
                FontSize = 9.5,
                Foreground = roster.LobbyReady[index] ? GuiTheme.GoodBrush : GuiTheme.TextDimBrush,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            var player = _player = new TextBlock
            {
                Text = name,
                FontFamily = GuiTheme.Display,
                FontSize = 12,
                Foreground = GuiTheme.TextBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            var hunterText = _hunter = new TextBlock
            {
                Text = hunter,
                FontFamily = Deck.Mono,
                FontSize = 8.25,
                Foreground = GuiTheme.TextDimBrush,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            var teamText = _team = new TextBlock
            {
                Text = team,
                FontFamily = Deck.Mono,
                FontSize = 8.25,
                Foreground = GuiTheme.TextDimBrush,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            var ping = _ping = new TextBlock
            {
                Text = $"{roster.Pings[index]} ms",
                FontFamily = Deck.Mono,
                FontSize = 8.25,
                Foreground = GuiTheme.TextDimBrush,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };

            Grid.SetColumn(player, 1);
            Grid.SetColumn(hunterText, 2);
            if (showTeam)
            {
                Grid.SetColumn(teamText, 3);
                Grid.SetColumn(ping, 4);
            }
            else
            {
                Grid.SetColumn(ping, 3);
            }

            line.Children.Add(ready);
            line.Children.Add(player);
            line.Children.Add(hunterText);
            if (showTeam) line.Children.Add(teamText);
            line.Children.Add(ping);

            Padding = new Thickness(4, 2);
            Background = selected
                ? HubTheme.AccentPanel(HubTheme.Accent, 34)
                : Brushes.Transparent;
            BorderBrush = selected ? HubTheme.AccentBrush : Brushes.Transparent;
            BorderThickness = selected ? new Thickness(1) : new Thickness(0);
            Child = line;
        }
        internal void Update(RosterPacket roster, int index, byte owner, bool selected)
        {
            _ready.Text = roster.LobbyReady[index] ? "READY" : "WAIT";
            _ready.Foreground = roster.LobbyReady[index] ? GuiTheme.GoodBrush : GuiTheme.TextDimBrush;
            _player.Text = roster.Names[index] + (roster.Slots[index] == owner ? "  [OWNER]" : "");
            _hunter.Text = $"{(Hunter)roster.Hunters[index]} · S{roster.Colors[index] + 1}";
            _team.Text = roster.Teams[index] < 0 ? "AUTO" : $"TEAM {(char)('A' + roster.Teams[index])}";
            _ping.Text = $"{roster.Pings[index]} ms";
            Background = selected ? HubTheme.AccentPanel(HubTheme.Accent, 34) : Brushes.Transparent;
            BorderBrush = selected ? HubTheme.AccentBrush : Brushes.Transparent;
            BorderThickness = selected ? new Thickness(1) : new Thickness(0);
        }
    }
}
