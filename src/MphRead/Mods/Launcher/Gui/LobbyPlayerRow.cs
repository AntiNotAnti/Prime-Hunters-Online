using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using MphRead.Mods.Network;
using MphRead.Mods.Multiplayer;

namespace MphRead.Mods.Launcher.Gui
{
    internal sealed class LobbyPlayerRow : Border
    {
        private readonly TextBlock _ready, _player, _hunter, _team, _ping;
        private readonly PrimeButton _previousTeam, _nextTeam;
        public LobbyPlayerRow(RosterPacket roster, int index, byte owner,
            bool showTeam = true, bool selected = false, Action<int>? changeTeam = null)
        {
            int slot = roster.Slots[index];
            string team = roster.Teams[index] < 0 ? "AUTO" : $"{(char)('A' + roster.Teams[index])}";
            string state = roster.LobbyReady[index] ? "READY" : "WAIT";
            string name = roster.Names[index] + (slot == owner ? "  [OWNER]" : "");
            string hunter = $"{(Hunter)roster.Hunters[index]} · S{roster.Colors[index] + 1}";

            var line = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions(showTeam ? "Auto,*,Auto,Auto,Auto,Auto,Auto" : "Auto,*,Auto,Auto"),
                ColumnSpacing = 4,
                MinHeight = 26
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

            _previousTeam = new PrimeButton("‹", () => changeTeam?.Invoke(-1), compact: true) { Width = 28 };
            _nextTeam = new PrimeButton("›", () => changeTeam?.Invoke(1), compact: true) { Width = 28 };
            ControllerNav.Identify(_previousTeam, $"lobby.player.{slot}.team.previous");
            ControllerNav.Identify(_nextTeam, $"lobby.player.{slot}.team.next");
            Avalonia.Automation.AutomationProperties.SetName(_previousTeam, $"Previous team for {roster.Names[index]}");
            Avalonia.Automation.AutomationProperties.SetName(_nextTeam, $"Next team for {roster.Names[index]}");
            _previousTeam.IsEnabled = _nextTeam.IsEnabled = changeTeam != null;

            Grid.SetColumn(player, 1);
            Grid.SetColumn(hunterText, 2);
            Grid.SetColumn(ping, showTeam ? 6 : 3);
            line.Children.Add(ready);
            line.Children.Add(player);
            line.Children.Add(hunterText);
            line.Children.Add(ping);
            if (showTeam)
            {
                Grid.SetColumn(_previousTeam, 3); line.Children.Add(_previousTeam);
                Grid.SetColumn(teamText, 4); line.Children.Add(teamText);
                Grid.SetColumn(_nextTeam, 5); line.Children.Add(_nextTeam);
            }
            ToolTip.SetTip(player, name);

            Padding = new Thickness(4, 2);
            Background = selected
                ? HubTheme.AccentPanel(HubTheme.Accent, 34)
                : Brushes.Transparent;
            BorderBrush = selected ? HubTheme.AccentBrush : Brushes.Transparent;
            BorderThickness = selected ? new Thickness(1) : new Thickness(0);
            Child = line;
        }
        internal void SetTeamAvailability(bool previous, bool next)
        {
            _previousTeam.IsEnabled = previous;
            _nextTeam.IsEnabled = next;
        }

        // Skip full destinations, wrapping through the configured teams. The
        // server remains authoritative; Auto balance stays in team management.
        internal static sbyte? NextTeam(RosterPacket roster, byte slot, TeamLayout layout, int direction)
        {
            int index = Array.IndexOf(roster.Slots, slot, 0, roster.Count);
            if (index < 0 || layout.TeamCount < 2) return null;
            int current = roster.Teams[index];
            int start = current >= 0 ? current : direction > 0 ? -1 : 0;
            for (int step = 1; step <= layout.TeamCount; step++)
            {
                int team = (start + direction * step + layout.TeamCount * 2) % layout.TeamCount;
                if (team == current) continue;
                int count = 0;
                for (int i = 0; i < roster.Count; i++)
                    if (roster.Slots[i] != slot && roster.Teams[i] == team) count++;
                if (count < layout.Capacity(team)) return (sbyte)team;
            }
            return null;
        }

        internal void Update(RosterPacket roster, int index, byte owner, bool selected)
        {
            _ready.Text = roster.LobbyReady[index] ? "READY" : "WAIT";
            _ready.Foreground = roster.LobbyReady[index] ? GuiTheme.GoodBrush : GuiTheme.TextDimBrush;
            _player.Text = roster.Names[index] + (roster.Slots[index] == owner ? "  [OWNER]" : "");
            _hunter.Text = $"{(Hunter)roster.Hunters[index]} · S{roster.Colors[index] + 1}";
            _team.Text = roster.Teams[index] < 0 ? "AUTO" : $"{(char)('A' + roster.Teams[index])}";
            ToolTip.SetTip(_player, _player.Text);
            _ping.Text = $"{roster.Pings[index]} ms";
            Background = selected ? HubTheme.AccentPanel(HubTheme.Accent, 34) : Brushes.Transparent;
            BorderBrush = selected ? HubTheme.AccentBrush : Brushes.Transparent;
            BorderThickness = selected ? new Thickness(1) : new Thickness(0);
        }
    }
}
