#if MPHREAD_AVALONIA
using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
namespace MphRead.Mods.Launcher.Gui
{
    internal enum PrimeModalSize { Small, Medium, Large, Wide, FullscreenWorkspace }
    internal sealed class PrimeOverlayHost : Panel
    {
        private readonly List<(Control View, Action? Cancel, Control? Focus)> _stack = new();
        public bool IsOpen => _stack.Count > 0;
        public PrimeOverlayHost()
        {
            IsVisible = false;
            Background = GuiTheme.ScrimBrush;
            SetValue(ControllerNav.ModalProperty, true);
            KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);
        }
        public void Show(Control view, PrimeModalSize size = PrimeModalSize.Large, Action? cancel = null)
        {
            var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Control;
            double width = size switch { PrimeModalSize.Small => 480, PrimeModalSize.Medium => 680,
                PrimeModalSize.Large => 960, PrimeModalSize.Wide => 1180, _ => double.PositiveInfinity };
            var frame = new Border
            {
                Child = view, Background = PrimeTheme.BackgroundBrush,
                BorderBrush = PrimeTheme.BorderBrightBrush, BorderThickness = new Thickness(1),
                Margin = new Thickness(24), MaxWidth = width,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = size == PrimeModalSize.Small || size == PrimeModalSize.Medium
                    ? VerticalAlignment.Center : VerticalAlignment.Stretch,
                MaxHeight = size == PrimeModalSize.Small ? 420 : double.PositiveInfinity
            };
            _stack.Add((frame, cancel, focused));
            Present();
        }
        public void Close(Control view)
        {
            if (IsOpen && _stack[^1].View is Border frame && ReferenceEquals(frame.Child, view)) Close();
        }
        public void Close()
        {
            if (!IsOpen) return;
            var entry = _stack[^1];
            _stack.RemoveAt(_stack.Count - 1);
            Present();
            if (entry.Focus is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true })
                Dispatcher.UIThread.Post(() => entry.Focus.Focus(), DispatcherPriority.Background);
        }
        public void Back()
        {
            if (!IsOpen) return;
            var cancel = _stack[^1].Cancel;
            if (cancel != null) cancel(); else Close();
        }
        public void Clear() { _stack.Clear(); Present(); }
        private void Present()
        {
            Children.Clear(); IsVisible = IsOpen;
            if (!IsOpen) return;
            var view = _stack[^1].View; Children.Add(view);
            Dispatcher.UIThread.Post(() => { if (IsOpen) FocusNavigator.Ensure(this); }, DispatcherPriority.Background);
        }
    }
}
#endif
