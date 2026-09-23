#if MPHREAD_AVALONIA
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
namespace MphRead.Mods.Launcher.Gui
{
    internal sealed class PrimeShell : UserControl
    {
        public PrimeRouter Router { get; } = new();
        public PrimeWorkspaceHost Workspaces { get; }
        public PrimeOverlayHost Overlays { get; } = new();
        public PrimeHeader Header { get; }
        public PrimeFooter Footer { get; }
        public Func<bool>? BackRequested { get; set; }
        private readonly LayoutTransformControl _canvas;
        private double _scale = 1;
        public PrimeShell(Func<PrimeRoute, Control> create, Action update)
        {
            Background = PrimeTheme.BackgroundBrush;
            Workspaces = new PrimeWorkspaceHost(create) { HasOverlay = () => Overlays.IsOpen };
            Header = new PrimeHeader(r => Router.Navigate(r));
            Footer = new PrimeFooter(update);
            var main = new Grid { RowDefinitions = new("Auto,*,Auto") };
            main.Children.Add(Header);
            Grid.SetRow(Workspaces, 1); main.Children.Add(Workspaces);
            Grid.SetRow(Footer, 2); main.Children.Add(Footer);
            var layers = new Panel(); layers.Children.Add(main); layers.Children.Add(Overlays);
            _canvas = new LayoutTransformControl { Child = layers };
            Content = _canvas;
            Router.Changed += route => { Workspaces.Show(route); Header.SetRoute(route); Refresh(); };
            AddHandler(KeyDownEvent, HandleKey, RoutingStrategies.Tunnel);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (_, _) => Refresh();
            AttachedToVisualTree += (_, _) => { timer.Start(); Refresh(); };
            DetachedFromVisualTree += (_, _) => timer.Stop();
            Header.SetRoute(PrimeRoute.News);
        }
        public void Start() => Workspaces.Show(Router.Current);
        public void Refresh() { var state = PrimeGlobalState.Read(); Header.Refresh(state); Footer.Refresh(state); }
        public bool Back()
        {
            if (Overlays.IsOpen) { Overlays.Back(); return true; }
            if (BackRequested?.Invoke() == true) return true;
            return Router.Back();
        }
        private void HandleKey(object? sender, KeyEventArgs e)
        {
            if (Mods.Input.GamepadContexts.Capturing || e.Source is KeyRow { Listening: true }) return;
            if (e.Key == Key.Escape) { Back(); e.Handled = true; return; }
            // Text entry and binding capture own letter keys.
            if (e.Source is TextBox || Mods.Input.GamepadContexts.Capturing || e.Source is KeyRow { Listening: true }) return;
            if (Overlays.IsOpen || e.KeyModifiers != KeyModifiers.None) return;
            if (e.Key == Key.Q) { Router.PreviousRoute(); e.Handled = true; }
            if (e.Key == Key.E) { Router.NextRoute(); e.Handled = true; }
        }
        public void SwitchTab(bool next)
        {
            if (Overlays.IsOpen) return;
            if (next) Router.NextRoute(); else Router.PreviousRoute();
        }
        protected override Size MeasureOverride(Size availableSize)
        {
            if (double.IsFinite(availableSize.Width) && double.IsFinite(availableSize.Height)
                && availableSize.Width > 0 && availableSize.Height > 0)
            {
                // Below the safe tactical layout, scale the entire canvas including
                // focus rings and modals; never clip a primary action off-screen.
                double factor = Math.Min(availableSize.Width / 1440, availableSize.Height / 810);
                factor = Math.Min(1.5, factor);
                if (Math.Abs(factor - _scale) > .001)
                { _scale = factor; _canvas.LayoutTransform = new ScaleTransform(factor, factor); }
            }
            return base.MeasureOverride(availableSize);
        }
    }
}
#endif
