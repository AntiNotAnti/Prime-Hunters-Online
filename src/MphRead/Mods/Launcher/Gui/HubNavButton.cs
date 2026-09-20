#if MPHREAD_AVALONIA
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace MphRead.Mods.Launcher.Gui
{
    /// <summary>
    /// Flat, controller-focusable navigation used by the FPS hub.
    ///
    /// It deliberately avoids perpetual animation. The launcher's current
    /// off-screen compositor rasterises a whole UI frame on the CPU whenever
    /// anything changes, so the shell gets its motion from discrete focus and
    /// hover changes while the OpenGL scene remains free to animate underneath.
    /// </summary>
    internal sealed class HubNavButton : ContentControl
    {
        private readonly Border _frame;
        private readonly Border _rail;
        private readonly TextBlock _label;
        private readonly IBrush _accent;
        private readonly bool _primary;
        private bool _pointer;
        private bool _pressed;

        public event EventHandler? Click;
        public string Label { get; }

        public HubNavButton(string label, string detail = "", bool primary = false,
            bool compact = false, Color? accent = null)
        {
            Label = label;
            _primary = primary;
            _accent = new SolidColorBrush(accent ?? HubTheme.Accent);

            Focusable = true;
            Cursor = new Cursor(StandardCursorType.Hand);
            MinHeight = compact ? 42 : 54;

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
                FontFamily = HubTheme.Ui,
                FontWeight = FontWeight.SemiBold,
                FontSize = compact ? 11 : 14,
                Foreground = primary ? _accent : HubTheme.TextBrush,
                VerticalAlignment = VerticalAlignment.Center
            };

            var detailText = new TextBlock
            {
                Text = detail,
                FontFamily = HubTheme.Ui,
                FontSize = 10,
                Foreground = HubTheme.TextDimBrush,
                TextWrapping = TextWrapping.Wrap,
                IsVisible = !compact && detail.Length > 0,
                Margin = new Thickness(0, 2, 0, 0)
            };

            var text = new StackPanel { Spacing = 0 };
            text.Children.Add(_label);
            text.Children.Add(detailText);

            var body = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*")
            };
            body.Children.Add(_rail);
            Grid.SetColumn(text, 1);
            body.Children.Add(text);

            _frame = new Border
            {
                Padding = compact ? new Thickness(8, 7) : new Thickness(12, 9),
                Background = primary ? HubTheme.PanelStrongBrush : HubTheme.PanelBrush,
                BorderBrush = primary ? _accent : HubTheme.EdgeBrush,
                BorderThickness = new Thickness(1),
                Child = body
            };
            Content = _frame;
        }

        protected override void OnPointerEntered(PointerEventArgs e)
        {
            _pointer = true;
            RefreshVisual();
            base.OnPointerEntered(e);
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            _pointer = false;
            RefreshVisual();
            base.OnPointerExited(e);
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            if (!IsEffectivelyEnabled)
            {
                base.OnPointerPressed(e);
                return;
            }
            _pressed = true;
            Focus();
            e.Pointer.Capture(this);
            e.Handled = true;
            RefreshVisual();
            base.OnPointerPressed(e);
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            bool click = _pressed && IsPointerOver && IsEffectivelyEnabled;
            _pressed = false;
            e.Pointer.Capture(null);
            RefreshVisual();
            if (click)
            {
                e.Handled = true;
                Click?.Invoke(this, EventArgs.Empty);
            }
            base.OnPointerReleased(e);
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            _pressed = false;
            RefreshVisual();
            base.OnPointerCaptureLost(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (IsEffectivelyEnabled && (e.Key == Key.Enter || e.Key == Key.Space))
            {
                e.Handled = true;
                Click?.Invoke(this, EventArgs.Empty);
                return;
            }
            base.OnKeyDown(e);
        }

        protected override void OnGotFocus(FocusChangedEventArgs e)
        {
            RefreshVisual();
            base.OnGotFocus(e);
        }

        protected override void OnLostFocus(FocusChangedEventArgs e)
        {
            _pressed = false;
            RefreshVisual();
            base.OnLostFocus(e);
        }

        private void RefreshVisual()
        {
            bool hot = _pointer || IsFocused;
            _frame.Background = hot
                ? HubTheme.PanelHotBrush
                : _primary ? HubTheme.PanelStrongBrush : HubTheme.PanelBrush;
            _frame.BorderBrush = hot || _primary ? _accent : HubTheme.EdgeBrush;
            _rail.Opacity = hot || _primary ? 1 : 0.35;
            _label.Foreground = hot || _primary ? _accent : HubTheme.TextBrush;
            _frame.Opacity = _pressed ? 0.78 : IsEffectivelyEnabled ? 1 : 0.48;
        }
    }
}
#endif
