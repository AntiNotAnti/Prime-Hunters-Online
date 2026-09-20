using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using MphRead.Mods.Network;

namespace MphRead.Mods.Launcher.Gui
{
    internal sealed class LobbyPlayerRow : Border
    {
        public LobbyPlayerRow(RosterPacket roster, int index, byte owner, bool showTeam = true)
        {
            int slot = roster.Slots[index];
            string team = roster.Teams[index] < 0 ? "FFA" : $"Team {(char)('A' + roster.Teams[index])}";
            string state = roster.LobbyReady[index] ? "READY" : "WAIT";
            string name = roster.Names[index] + (slot == owner ? "  [OWNER]" : "");
            string teamPart = showTeam ? $" · {team}" : "";
            string detail = $"{(Hunter)roster.Hunters[index]} · S{roster.Colors[index] + 1}{teamPart} · {roster.Pings[index]} ms";

            var line = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
                ColumnSpacing = 10
            };
            var ready = new TextBlock
            {
                Text = state,
                FontFamily = GuiTheme.Display,
                FontSize = 11,
                Foreground = roster.LobbyReady[index] ? GuiTheme.GoodBrush : GuiTheme.TextDimBrush,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            var player = new TextBlock
            {
                Text = name,
                FontFamily = GuiTheme.Display,
                FontSize = 13,
                Foreground = GuiTheme.TextBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            var facts = new TextBlock
            {
                Text = detail,
                FontFamily = Deck.Mono,
                FontSize = 10,
                Foreground = GuiTheme.TextDimBrush,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            Grid.SetColumn(player, 1);
            Grid.SetColumn(facts, 2);
            line.Children.Add(ready);
            line.Children.Add(player);
            line.Children.Add(facts);
            Padding = new Thickness(4, 3);
            Child = line;
        }
    }
}
