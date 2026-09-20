#if MPHREAD_AVALONIA
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace MphRead.Mods.Launcher.Gui
{
    /// <summary>
    /// Flat, controller-focusable navigation used by the FPS hub.
    ///
    /// It deliberately avoids perpetual animation. The launcher's current
    /// off-screen compositor rasterises a whole UI frame on the CPU whenever
    /// anything changes, so the shell gets its motion from focus/hover state
    /// changes while the OpenGL scene remains free to animate underneath.
    /// </summary>
    internal sealed class HubNavButton : Button
    {
        private readonly Border _rail;
        private readonly TextBlock _label;
        private readonly TextBlock _detail;
        private readonly IBrush _accent;
        private readonly bool _primary;
        private bool _pointer;

        public string Label { get; }

        public HubNavButton(string label, string detail = "", bool primary = false,
            bool compact = false, Color? accent = null)
        {
            Label = label;
            _primary = primary;
            _accent = new SolidColorBrush(accent ?? HubTheme.Accent);

            Focusable = true;
            MinHeight = compact ? 42 : 54;
            Padding = compact
                ? new Thickness(8, 7)
                : new Thickness(12, 9);
            HorizontalContentAlignment = HorizontalAlignment.Stretch;
            VerticalContentAlignment = VerticalAlignment.Center;
            Background = primary ? HubTheme.PanelStrongBrush : HubTheme.PanelBrush;
            BorderBrush = primary ? _accent : HubTheme.EdgeBrush;
            BorderThickness = new Thickness(1);

            _rail = new Border
            {
                Width = compact ? 2 : 3,
                Background = _accent,
                Opacity = primary ? 1 : 0.35,
                Margin = new Thickness(0, 0, compact ? 7 : 11, 0)
            };

            _label = new TextBlock
            {
                Text = label,
                FontFamily = Deck.MonoBold,
                FontSize = compact ? 10 : 13,
                Foreground = primary ? _accent : HubTheme.TextBrush,
                VerticalAlignment = VerticalAlignment.Center
            };

            _detail = new TextBlock
            {
                Text = detail,
                FontFamily = Deck.Mono,
                FontSize = 9,
                Foreground = HubTheme.TextDimBrush,
                TextWrapping = TextWrapping.Wrap,
                IsVisible = !compact && detail.Length > 0,
                Margin = new Thickness(0, 2, 0, 0)
            };

            var text = new StackPanel { Spacing = 0 };
            text.Children.Add(_label);
            text.Children.Add(_detail);

            var body = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*")
            };
            body.Children.Add(_rail);
            Grid.SetColumn(text, 1);
            body.Children.Add(text);
            Content = body;

            PointerEntered += (_, _) =>
            {
                _pointer = true;
                RefreshVisual();
            };
            PointerExited += (_, _) =>
            {
                _pointer = false;
                RefreshVisual();
            };
            GotFocus += (_, _) => RefreshVisual();
            LostFocus += (_, _) => RefreshVisual();
        }

        private void RefreshVisual()
        {
            bool hot = _pointer || IsFocused;
            Background = hot
                ? HubTheme.PanelHotBrush
                : _primary ? HubTheme.PanelStrongBrush : HubTheme.PanelBrush;
            BorderBrush = hot || _primary ? _accent : HubTheme.EdgeBrush;
            _rail.Opacity = hot || _primary ? 1 : 0.35;
            _label.Foreground = hot || _primary ? _accent : HubTheme.TextBrush;
        }
    }
}
#endif
