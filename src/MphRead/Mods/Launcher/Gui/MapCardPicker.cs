#if MPHREAD_AVALONIA
using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace MphRead.Mods.Launcher.Gui
{
    /// <summary>
    /// Builds the same map card used by the Offline screen and every popup
    /// that asks the player to choose one map.
    /// </summary>
    internal static class MapCardFactory
    {
        public static DeckTile Create(string room)
        {
            string code = room.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                is { Length: > 0 } parts ? parts[0] : room;
            (RoomMetadata? meta, _) = Metadata.GetRoomByName(room);
            return new DeckTile(room, code)
            {
                Blurb = meta?.InGameName ?? room, Tactical = true, Verb = "SELECT", ChosenVerb = "SELECTED"
            };
        }
    }

    /// <summary>
    /// One-map picker using the exact same DeckTile/DeckGrid presentation as
    /// Offline. The lobby uses this rather than a second list-style picker.
    /// </summary>
    internal sealed class MapCardPicker : UserControl
    {
        public event EventHandler<string>? Done;
        public event EventHandler? Cancelled;

        private readonly DeckGrid _grid = new() { FixedColumns = 2, Ratio = 16 / 9.0 };
        private readonly Image _preview = new() { Stretch = Stretch.UniformToFill, Height = 150 };
        private readonly TextBlock _name = PrimeChrome.Title("SELECT AN ARENA");
        private readonly TextBox _search = new() { PlaceholderText = "Search arena name or code" };
        private readonly Note _note = new("");
        private readonly PrimeButton _use;
        private string? _selected;

        public MapCardPicker(IReadOnlyList<string> rooms, string? selected)
        {
            Background = Brushes.Transparent;
            Focusable = true;
            _selected = selected != null && System.Linq.Enumerable.Contains(rooms, selected) ? selected : null;

            var back = new PrimeButton("CANCEL", () => Cancelled?.Invoke(this, EventArgs.Empty));
            _use = new PrimeButton("USE MAP", () =>
            {
                if (!String.IsNullOrWhiteSpace(_selected)) Done?.Invoke(this, _selected);
            }, primary: true);
            _use.SetValue(ControllerNav.NavIdProperty, "map-picker.use");
            _search.SetValue(ControllerNav.NavIdProperty, "map-picker.search");
            _search.TextChanged += (_, _) =>
            {
                string query = _search.Text?.Trim() ?? "";
                foreach (Control child in _grid.Children)
                    if (child is DeckTile tile)
                        tile.IsVisible = tile.RoomKey.Contains(query, StringComparison.OrdinalIgnoreCase)
                            || tile.Blurb.Contains(query, StringComparison.OrdinalIgnoreCase);
            };

            var scroll = new ScrollViewer
            {
                Content = _grid,
                ClipToBounds = true,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
            };

            var gallery = new Grid { RowDefinitions = new("Auto,*"), RowSpacing = PrimeMetrics.PanelGap };
            gallery.Children.Add(_search); Grid.SetRow(scroll, 1); gallery.Children.Add(scroll);
            var inspector = new PrimePanel(PrimeChrome.Stack(new PrimeBadge("DEPLOYMENT PREVIEW"),
                _preview, _name, _note, PrimeChrome.Text("Select an arena, then confirm to update the match.",
                    PrimeTypography.BodySmall, PrimeTheme.TextSecondaryBrush)));
            var body = PrimeChrome.Columns("2*,*", gallery, inspector);
            var frame = new Grid { RowDefinitions = new("Auto,*,Auto"), RowSpacing = PrimeMetrics.PanelGap,
                Margin = new Thickness(PrimeMetrics.PanelPadding) };
            frame.Children.Add(PrimeChrome.Stack(new PrimeBadge("ARENA DIRECTORY"), PrimeChrome.Title("CHOOSE DEPLOYMENT ZONE")));
            Grid.SetRow(body, 1); frame.Children.Add(body);
            var actions = PrimeChrome.Columns("*,*", back, _use);
            Grid.SetRow(actions, 2); frame.Children.Add(actions);
            Content = frame;

            if (rooms.Count == 0)
            {
                _note.Text = "No multiplayer rooms were found. Set the game files up from Settings.";
                _note.Foreground = GuiTheme.WarmBrush;
                _use.IsEnabled = false;
                return;
            }

            foreach (string room in rooms)
            {
                DeckTile tile = MapCardFactory.Create(room);
                tile.Chosen = String.Equals(room, _selected, StringComparison.OrdinalIgnoreCase);
                tile.Click += (_, _) => Select(tile);
                _grid.Children.Add(tile);
            }
            RefreshSelection();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            foreach (Control child in _grid.Children)
            {
                if (child is DeckTile tile && tile.Chosen)
                {
                    tile.Focus();
                    return;
                }
            }
            if (_grid.Children.Count > 0)
                _grid.Children[0].Focus();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Cancelled?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                return;
            }
            base.OnKeyDown(e);
        }

        private void Select(DeckTile selected)
        {
            _selected = selected.RoomKey;
            foreach (Control child in _grid.Children)
                if (child is DeckTile tile)
                    tile.Chosen = ReferenceEquals(tile, selected);
            RefreshSelection();
        }

        private void RefreshSelection()
        {
            _use.IsEnabled = !String.IsNullOrWhiteSpace(_selected);
            _note.Text = String.IsNullOrWhiteSpace(_selected)
                ? "Choose a map."
                : $"Selected: {Metadata.GetRoomByName(_selected).Item1?.InGameName ?? _selected}";
            _note.Foreground = GuiTheme.TextDimBrush;
            _name.Text = String.IsNullOrWhiteSpace(_selected) ? "SELECT AN ARENA" : MapPick.NameOf(_selected).ToUpperInvariant();
            _preview.Source = MapShot.For(_selected);
        }
    }
}
#endif
