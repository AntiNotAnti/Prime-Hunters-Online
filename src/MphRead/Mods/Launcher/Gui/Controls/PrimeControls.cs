#if MPHREAD_AVALONIA
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
namespace MphRead.Mods.Launcher.Gui
{
    internal sealed class PrimePanel : Border
    {
        public PrimePanel(Control content, bool raised = false)
        {
            Background = raised ? PrimeTheme.PanelRaisedBrush : PrimeTheme.PanelBrush;
            BorderBrush = PrimeTheme.BorderBrush;
            BorderThickness = new Thickness(0, 1, 0, 0);
            Padding = new Thickness(PrimeMetrics.PanelPadding);
            CornerRadius = new CornerRadius(3);
            Child = content;
        }
    }
    // Keep the tested pointer, touch and controller behavior of the shared buttons.
    internal class PrimeButton : HubNavButton
    {
        public PrimeButton(string text, Action? action = null, bool primary = false, bool danger = false)
            : base(text, primary: primary, compact: true, accent: danger ? PrimeTheme.Danger : null)
        {
            if (action != null) Click += (_, _) => action();
        }
    }
    internal sealed class PrimeTabButton : PrimeButton
    {
        public PrimeTabButton(string text, Action action) : base(text, action) { }
    }
    internal sealed class PrimeBadge : Border
    {
        public PrimeBadge(string text, IBrush? color = null)
        {
            Background = PrimeTheme.PanelHighlightBrush;
            Padding = new Thickness(8, 4);
            CornerRadius = new CornerRadius(2);
            Child = PrimeChrome.Text(text, PrimeTypography.DataSmall, color ?? PrimeTheme.CyanBrush, data: true);
        }
    }
    internal sealed class PrimeStatBar : StackPanel
    {
        public PrimeStatBar(string label, double value, double maximum, IBrush? color = null)
        {
            Spacing = 6;
            Children.Add(PrimeChrome.Text(label, PrimeTypography.DataSmall, data: true));
            Children.Add(new ProgressBar { Minimum = 0, Maximum = Math.Max(1, maximum),
                Value = Math.Clamp(value, 0, Math.Max(1, maximum)), Height = 6,
                Foreground = color ?? PrimeTheme.CyanStrongBrush, Background = PrimeTheme.PanelHighlightBrush });
        }
    }
    internal static class PrimeChrome
    {
        public static TextBlock Text(string text, double size = PrimeTypography.Body,
            IBrush? color = null, bool data = false) => new()
        {
            Text = text, FontSize = size, Foreground = color ?? PrimeTheme.TextBrush,
            FontFamily = data ? PrimeTypography.Data : PrimeTypography.Ui,
            TextWrapping = TextWrapping.Wrap
        };
        public static TextBlock Title(string text) => new()
        {
            Text = text, FontSize = PrimeTypography.HeadingLarge, FontWeight = FontWeight.Bold,
            Foreground = PrimeTheme.TextBrush, FontFamily = PrimeTypography.Ui,
            TextWrapping = TextWrapping.Wrap
        };
        public static StackPanel Stack(params Control[] children)
        {
            var stack = new StackPanel { Spacing = PrimeMetrics.PanelGap };
            foreach (var child in children) stack.Children.Add(child);
            return stack;
        }
        public static Grid Columns(string widths, params Control[] children)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(widths), ColumnSpacing = PrimeMetrics.PanelGap };
            for (int i = 0; i < children.Length; i++) { Grid.SetColumn(children[i], i); grid.Children.Add(children[i]); }
            return grid;
        }
    }
}
#endif
