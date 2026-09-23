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
    internal class HubNavButton : ContentControl
    {
        private readonly Border _frame;
        private readonly Border _rail;
        private readonly TextBlock _label;
        private readonly IBrush _accent;
        private readonly IBrush _restBackground;
        private readonly IBrush _hotBackground;
        private readonly TranslateTransform _shift = new();
        private readonly bool _primary;
        private bool _tactical, _tab;
        protected void UseTacticalStyle(bool tab = false)
        {
            _tactical = true; _tab = tab; _rail.IsVisible = false;
            _label.FontFamily = PrimeTypography.Data;
            _label.HorizontalAlignment = HorizontalAlignment.Center;
            _label.VerticalAlignment = VerticalAlignment.Center;
            _label.TextWrapping = TextWrapping.Wrap;
            _label.TextAlignment = TextAlignment.Center;
            _frame.Padding = new Thickness(10, 8);
            RefreshVisual();
        }
        private bool _pointer;
        private bool _pressed;
        private bool _selected;
        private readonly Tap _tap = new();

        public event EventHandler? Click;
        public string Label
        {
            get => _label.Text ?? "";
            set => _label.Text = value;
        }

        /// <summary>
        /// Persistently emphasize this destination while its page is active.
        /// Hover/focus remain separate so selection does not make the control
        /// look permanently pressed or shifted.
        /// </summary>
        public bool Selected
        {
            get => _selected;
            set
            {
                if (_selected == value)
                {
                    return;
                }
                _selected = value;
                RefreshVisual();
            }
        }

        public HubNavButton(string label, string detail = "", bool primary = false,
            bool compact = false, Color? accent = null)
        {
            _primary = primary;
            Color tint = accent ?? HubTheme.Accent;
            _accent = new SolidColorBrush(tint);
            _restBackground = primary
                ? HubTheme.AccentPanel(tint, 34)
                : HubTheme.PanelBrush;
            _hotBackground = HubTheme.AccentPanel(tint, 70);

            Focusable = true;
            Cursor = new Cursor(StandardCursorType.Hand);
            MinHeight = compact ? 42 : 54;
            HorizontalContentAlignment = HorizontalAlignment.Stretch;
            VerticalContentAlignment = VerticalAlignment.Stretch;

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
                FontSize = compact ? PrimeTypography.BodySmall : PrimeTypography.HeadingSmall,
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
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Padding = compact ? new Thickness(8, 7) : new Thickness(12, 9),
                Background = _restBackground,
                BorderBrush = primary ? _accent : HubTheme.EdgeBrush,
                BorderThickness = new Thickness(1),
                Child = body,
                RenderTransform = _shift
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

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            if (_tap.Down && _tap.Moved(e, this))
            {
                _pressed = false;
                RefreshVisual();
            }
            base.OnPointerMoved(e);
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            if (!IsEffectivelyEnabled)
            {
                base.OnPointerPressed(e);
                return;
            }
            _tap.Press(e, this);
            _pressed = true;
            Focus();
            e.Pointer.Capture(this);
            e.Handled = true;
            RefreshVisual();
            base.OnPointerPressed(e);
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            bool click = IsEffectivelyEnabled && _tap.Release(e, this);
            _pressed = false;
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
            _tap.Cancel();
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

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == IsEnabledProperty)
                RefreshVisual();
        }

        private void RefreshVisual()
        {
            if (_frame == null) return;
            if (_tactical)
            {
                bool hot = _pointer || IsFocused;
                _frame.Background = _primary ? (hot ? PrimeTheme.CyanBrush : PrimeTheme.CyanStrongBrush)
                    : hot || _selected ? PrimeTheme.PanelHighlightBrush : _tab ? Brushes.Transparent : PrimeTheme.PanelRaisedBrush;
                _frame.BorderBrush = hot || _selected ? PrimeTheme.CyanStrongBrush : _tab ? Brushes.Transparent : PrimeTheme.BorderBrush;
                _frame.BorderThickness = _tab && !IsFocused ? new Thickness(0, 0, 0, _selected ? 2 : 0) : new Thickness(1);
                _label.Foreground = _primary ? PrimeTheme.BackgroundDeepBrush : hot || _selected ? PrimeTheme.CyanBrush : PrimeTheme.TextBrush;
                _frame.Opacity = _pressed ? .76 : IsEffectivelyEnabled ? 1 : .48;
                _shift.X = 0;
                return;
            }
            bool interactive = _pointer || IsFocused;
            bool emphasized = interactive || _selected;
            _frame.Background = emphasized ? _hotBackground : _restBackground;
            _frame.BorderBrush = emphasized || _primary ? _accent : HubTheme.EdgeBrush;
            _rail.Opacity = emphasized || _primary ? 1 : 0.35;
            _label.Foreground = emphasized || _primary ? _accent : HubTheme.TextBrush;
            _shift.X = interactive && IsEffectivelyEnabled ? 2 : 0;
            _frame.Opacity = _pressed ? 0.76 : IsEffectivelyEnabled ? 1 : 0.48;
        }
    }
}
#endif
