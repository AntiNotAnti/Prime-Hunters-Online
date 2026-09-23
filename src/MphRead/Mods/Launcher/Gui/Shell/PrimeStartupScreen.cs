#if MPHREAD_AVALONIA
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace MphRead.Mods.Launcher.Gui
{
    /// <summary>One opening gate per launcher instance, over the already mounted shell.</summary>
    internal sealed class PrimeStartupScreen : ContentControl, IDisposable
    {
        private readonly Bitmap _art;
        private readonly TextBlock _prompt;
        private readonly Border _shade;
        private readonly PrimeUiPulse _flash;
        private readonly Tap _tap = new();
        private bool _requested, _leaving, _disposed;
        public event Action? Continued;
        internal bool Leaving => _leaving;

        public PrimeStartupScreen()
        {
            Focusable = true;
            Cursor = new Cursor(StandardCursorType.Hand);
            Background = PrimeTheme.BackgroundDeepBrush;
            HorizontalContentAlignment = HorizontalAlignment.Stretch;
            VerticalContentAlignment = VerticalAlignment.Stretch;
            ControllerNav.Identify(this, "startup.continue", initial: true);
            SetValue(ControllerNav.ModalProperty, true);
            using var stream = AssetLoader.Open(new Uri("avares://ProjectPrime/Assets/Backgrounds/launcher-bg.png"));
            _art = new Bitmap(stream);
            var layers = new Panel();
            // Uniform preserves the wordmark even on ultrawide and short landscape screens.
            layers.Children.Add(new Image { Source = _art, Stretch = Stretch.Uniform });
            _shade = new Border { Height = 115, VerticalAlignment = VerticalAlignment.Bottom,
                Background = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                    GradientStops = { new GradientStop(Color.FromArgb(0, 4, 11, 18), 0),
                        new GradientStop(Color.FromArgb(235, 4, 11, 18), 1) }
                } };
            layers.Children.Add(_shade);
            _prompt = new TextBlock { Text = "PRESS START TO CONTINUE", FontFamily = PrimeTypography.Label,
                FontWeight = FontWeight.SemiBold, FontSize = 26, Foreground = PrimeTheme.HighlightBrush,
                TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(20, 0, 20, 28) };
            layers.Children.Add(_prompt);
            Content = layers;
            _flash = new PrimeUiPulse(TimeSpan.FromMilliseconds(700), () =>
            {
                if (LauncherPrefs.ReduceMotion || Deck.Still) { _prompt.Opacity = 1; return; }
                _prompt.Opacity = _prompt.Opacity < 1 ? 1 : .35;
            });
            AttachedToVisualTree += (_, _) =>
            {
                if (!_requested && !LauncherPrefs.ReduceMotion && !Deck.Still) _flash.Start();
                Deck.NextFrame(this, () => { if (!_disposed && !_leaving && IsEffectivelyVisible) Focus(); });
            };
            DetachedFromVisualTree += (_, _) => _flash.Stop();
            AddHandler(KeyDownEvent, (_, e) =>
            {
                if (e.Key is Key.Enter or Key.Space) Continue();
                // The opening gate owns navigation, including Escape and tab switching.
                e.Handled = true;
            }, RoutingStrategies.Tunnel);
        }

        internal void Continue()
        {
            if (_requested || _disposed) return;
            _requested = true; _flash.Stop(); _prompt.Opacity = 1;
            Continued?.Invoke();
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            if (double.IsFinite(availableSize.Height) && availableSize.Height > 0)
            {
                double scale = availableSize.Height / 810;
                _prompt.FontSize = Math.Clamp(26 * scale, 16, 32);
                _prompt.Margin = new Thickness(20, 0, 20, Math.Clamp(28 * scale, 16, 36));
                _shade.Height = Math.Clamp(115 * scale, 64, 150);
            }
            return base.MeasureOverride(availableSize);
        }

        internal void Reveal(Action completed)
        {
            if (_leaving || _disposed) return;
            _leaving = true;
            int frame = 0;
            void Step()
            {
                if (_disposed) return;
                double t = Deck.Still || LauncherPrefs.ReduceMotion ? 1 : Math.Min(1, ++frame / 24.0);
                Opacity = 1 - t * t * (3 - 2 * t);
                if (t < 1) Deck.NextFrame(this, Step);
                else { IsVisible = false; completed(); }
            }
            Deck.NextFrame(this, Step);
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.Pointer.Type != PointerType.Touch) return;
            _tap.Press(e, this); e.Pointer.Capture(this); e.Handled = true;
        }
        protected override void OnPointerMoved(PointerEventArgs e) { if (_tap.Down) _tap.Moved(e, this); }
        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        { if (_tap.Release(e, this)) Continue(); e.Handled = true; }
        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) => _tap.Cancel();
        public void Dispose()
        { if (_disposed) return; _disposed = true; _flash.Dispose(); Content = null; _art.Dispose(); }
    }
}
#endif
