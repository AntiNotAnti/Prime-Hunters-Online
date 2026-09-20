using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MphRead.Mods.Launcher.Gui
{
    /// <summary>
    /// A bar, what is happening, and a percentage.
    ///
    /// Custom-drawn rather than Fluent's ProgressBar for the reason everything
    /// else on this screen is: it has to sit on the card's own colours and
    /// carry a caption of its own.
    ///
    /// The number comes from <see cref="SetupProgress"/>, which is milestone
    /// driven because the extraction never says how much there is to do. It
    /// only ever moves forward, and it does not reach the end until the work
    /// has.
    /// </summary>
    internal sealed class ProgressRow : Control
    {
        private double _fraction;
        private string _stage = "";

        public ProgressRow()
        {
            Height = 44;
            IsVisible = false;
        }

        public void Set(double fraction, string stage)
        {
            _fraction = Math.Clamp(fraction, 0, 1);
            _stage = stage;
            IsVisible = true;
            InvalidateVisual();
        }

        public override void Render(DrawingContext context)
        {
            double width = Bounds.Width;
            const double barHeight = 8;
            double barTop = Bounds.Height - barHeight - 2;

            var stage = new FormattedText(_stage, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, GuiTheme.Face(false), 12, GuiTheme.TextDimBrush);
            context.DrawText(stage, new Point(0, 2));

            string percent = ((int)Math.Round(_fraction * 100))
                .ToString(CultureInfo.InvariantCulture) + "%";
            var number = new FormattedText(percent, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, GuiTheme.Face(true), 12, GuiTheme.TextBrush);
            context.DrawText(number, new Point(width - number.Width, 2));

            context.DrawRectangle(new SolidColorBrush(GuiTheme.Ink), null,
                new RoundedRect(new Rect(0, barTop, width, barHeight), barHeight / 2));
            double filled = width * _fraction;
            if (filled > 1)
            {
                // Rounded at both ends, so a bar that has barely started still
                // looks like a bar rather than a sliver of a rectangle.
                context.DrawRectangle(GuiTheme.AccentBrush, null,
                    new RoundedRect(new Rect(0, barTop, Math.Max(filled, barHeight), barHeight),
                        barHeight / 2));
            }
        }
    }
}
