using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using MphRead.Mods.Input;

namespace MphRead.Mods.Launcher.Gui
{
    // Drawn geometry keeps controller symbols independent of installed font coverage.
    internal sealed class GamepadGlyph : Control
    {
        public GamepadButtons Button { get; set; }
        public override void Render(DrawingContext context) => Draw(context, new Rect(Bounds.Size), Button, GuiTheme.TextBrush);
        internal static void Draw(DrawingContext context, Rect bounds, GamepadButtons button, IBrush brush)
        {
            var family = GamepadOptions.GlyphStyle == GamepadFamily.Unknown
                ? GamepadManager.ActiveDevice?.Family ?? GamepadFamily.Generic : GamepadOptions.GlyphStyle;
            var pen = new Pen(brush, 1.5);
            double cx = bounds.Center.X, cy = bounds.Center.Y, radius = System.Math.Min(bounds.Width, bounds.Height) * .43;
            bool face = button is GamepadButtons.A or GamepadButtons.B or GamepadButtons.X or GamepadButtons.Y;
            if (face) context.DrawEllipse(null, pen, bounds.Center, radius, radius);
            else context.DrawRectangle(null, pen, bounds.Deflate(1), 3, 3);
            double r = radius * .55;
            if (family == GamepadFamily.PlayStation && face)
            {
                if (button == GamepadButtons.A)
                { context.DrawLine(pen, new(cx-r, cy-r), new(cx+r, cy+r)); context.DrawLine(pen, new(cx-r, cy+r), new(cx+r, cy-r)); }
                else if (button == GamepadButtons.B) context.DrawEllipse(null, pen, new(cx, cy), r, r);
                else if (button == GamepadButtons.X) context.DrawRectangle(null, pen, new Rect(cx-r, cy-r, r*2, r*2));
                else
                { context.DrawLine(pen, new(cx, cy-r), new(cx+r, cy+r)); context.DrawLine(pen, new(cx+r, cy+r), new(cx-r, cy+r)); context.DrawLine(pen, new(cx-r, cy+r), new(cx, cy-r)); }
                return;
            }
            var text = TrackedText.Make(GamepadGlyphs.Resolve(button, family), face ? 12 : 9, false, brush);
            context.DrawText(text, new Point(cx-text.Width/2, cy-text.Height/2));
        }
    }
}
