#if MPHREAD_AVALONIA
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace MphRead.Mods.Launcher.Gui
{
    internal sealed class HubPlaceholderView : UserControl
    {
        public event EventHandler? Closed;

        public HubPlaceholderView(string title, string status, string detail)
        {
            Focusable = true;
            Background = Brushes.Transparent;

            var root = new Grid
            {
                Margin = new Thickness(28, 24, 28, 36),
                RowDefinitions = new RowDefinitions("Auto,*,Auto"),
                RowSpacing = 18
            };
            root.Children.Add(HubChrome.Header(
                "HOME  /  " + title,
                title,
                "Reserved for a future hub workspace.",
                "COMING LATER",
                HubTheme.WarmBrush));

            var center = new Border
            {
                MaxWidth = 640,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Background = HubTheme.PanelStrongBrush,
                BorderBrush = HubTheme.WarmBrush,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(28)
            };
            var copy = new StackPanel { Spacing = 10 };
            copy.Children.Add(new TextBlock
            {
                Text = status,
                FontFamily = HubTheme.DataBold,
                FontSize = 9,
                Foreground = HubTheme.WarmBrush,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            copy.Children.Add(new TextBlock
            {
                Text = detail,
                FontFamily = HubTheme.Ui,
                FontSize = 13,
                Foreground = HubTheme.TextDimBrush,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            });
            center.Child = copy;
            Grid.SetRow(center, 1);
            root.Children.Add(center);

            var back = new HubNavButton("BACK", compact: true);
            ControllerNav.Identify(back, "placeholder.back", initial: true);
            back.Click += (_, _) => Closed?.Invoke(this, EventArgs.Empty);
            Grid.SetRow(back, 2);
            root.Children.Add(back);
            Content = root;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Closed?.Invoke(this, EventArgs.Empty);
                return;
            }
            base.OnKeyDown(e);
        }
    }
}
#endif
