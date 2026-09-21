#if MPHREAD_AVALONIA
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace MphRead.Mods.Launcher.Gui
{
    /// <summary>Small shared pieces that make hub destinations read as one shell.</summary>
    internal static class HubChrome
    {
        public static Grid Header(string path, string title, string subtitle,
            string? status = null, IBrush? statusBrush = null)
        {
            var header = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto")
            };

            var copy = new StackPanel { Spacing = 2 };
            copy.Children.Add(new TextBlock
            {
                Text = path,
                FontFamily = HubTheme.DataBold,
                FontSize = 8.5,
                Foreground = HubTheme.AccentBrush
            });
            copy.Children.Add(new TextBlock
            {
                Text = title,
                FontFamily = HubTheme.Ui,
                FontWeight = FontWeight.Bold,
                FontSize = 27,
                Foreground = HubTheme.TextBrush
            });
            copy.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontFamily = HubTheme.Ui,
                FontSize = 10.5,
                Foreground = HubTheme.TextDimBrush,
                TextWrapping = TextWrapping.Wrap
            });
            header.Children.Add(copy);

            if (!string.IsNullOrWhiteSpace(status))
            {
                var badge = new Border
                {
                    Background = HubTheme.PanelBrush,
                    BorderBrush = HubTheme.EdgeBrush,
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(9, 5),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = status,
                        FontFamily = HubTheme.DataBold,
                        FontSize = 8,
                        Foreground = statusBrush ?? HubTheme.TextDimBrush
                    }
                };
                Grid.SetColumn(badge, 1);
                header.Children.Add(badge);
            }

            return header;
        }

        public static Border Divider() => new()
        {
            Height = 1,
            Background = HubTheme.EdgeBrush,
            Margin = new Thickness(0, 4)
        };

        public static TextBlock Kicker(string text, IBrush? brush = null) => new()
        {
            Text = text,
            FontFamily = HubTheme.DataBold,
            FontSize = 8,
            Foreground = brush ?? HubTheme.AccentBrush
        };
    }
}
#endif
