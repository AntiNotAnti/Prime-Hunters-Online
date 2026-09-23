#if MPHREAD_AVALONIA
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
namespace MphRead.Mods.Launcher.Gui
{
    internal sealed class PrimeFovPreview : Control
    {
        private readonly Func<int> _fov;
        public PrimeFovPreview(Func<int> fov) { _fov = fov; Height = 180; }
        public override void Render(DrawingContext context)
        {
            base.Render(context);
            context.FillRectangle(PrimeTheme.BackgroundDeepBrush, new Rect(Bounds.Size));
            var grid = new Pen(PrimeTheme.BorderBrush, .5);
            for (int x = 0; x < Bounds.Width; x += 24) context.DrawLine(grid, new Point(x,0), new Point(x,Bounds.Height));
            for (int y = 0; y < Bounds.Height; y += 24) context.DrawLine(grid, new Point(0,y), new Point(Bounds.Width,y));
            var origin = new Point(Bounds.Width / 2, Bounds.Height - 24);
            double radius = Math.Min(Bounds.Width / 2 - 10, Bounds.Height - 50);
            double angle = Math.Clamp(_fov(), 20, 150) * Math.PI / 360;
            var pen = new Pen(PrimeTheme.PrimaryBrush, 2);
            context.DrawLine(pen, origin, new Point(origin.X - Math.Sin(angle) * radius, origin.Y - Math.Cos(angle) * radius));
            context.DrawLine(pen, origin, new Point(origin.X + Math.Sin(angle) * radius, origin.Y - Math.Cos(angle) * radius));
            context.DrawEllipse(PrimeTheme.PrimaryBrush, null, origin, 4, 4);
            context.DrawText(GuiTheme.Lay($"FOV // {_fov()}°", 12, PrimeTheme.HighlightBrush, true), new Point(12,12));
        }
    }
}
#endif
