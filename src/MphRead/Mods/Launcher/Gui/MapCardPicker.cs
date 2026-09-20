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
                Blurb = meta?.InGameName ?? ""
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

        private readonly DeckGrid _grid = new();
        private readonly Note _note = new("");
        private readonly UiMark _use;
        private string? _selected;

        public MapCardPicker(IReadOnlyList<string> rooms, string? selected)
        {
            Background = Brushes.Transparent;
            Focusable = true;
            _selected = selected;

            var back = new UiMark(UiMark.Shape.Cancel, "back");
            back.Click += (_, _) => Cancelled?.Invoke(this, EventArgs.Empty);

            _use = new UiMark(UiMark.Shape.Accept, "use map");
            _use.Click += (_, _) =>
            {
                if (!String.IsNullOrWhiteSpace(_selected))
                    Done?.Invoke(this, _selected);
            };

            var scroll = new ScrollViewer
            {
                Content = _grid,
                ClipToBounds = true,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
            };

            var body = new Grid
            {
                RowDefinitions = new RowDefinitions("*,Auto"),
                RowSpacing = 6
            };
            body.Children.Add(scroll);
            Grid.SetRow(_note, 1);
            body.Children.Add(_note);

            Content = UiLayout.Page(overGame: false, UiLayout.WellPlay,
                "choose map", strip: null, body: body, no: back, yes: _use);

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
        }
    }
}
#endif
