#if MPHREAD_AVALONIA
using System;
using Avalonia.Controls;
using Avalonia.Media;

namespace MphRead.Mods.Launcher.Gui
{
    internal static class HubMotion
    {
        public static void Enter(Control control, double lift = 10, int frames = 9)
        {
            if (Deck.Still || LauncherPrefs.ReduceMotion || frames <= 0)
            {
                control.Opacity = 1; control.RenderTransform = null; return;
            }
            var move = new TranslateTransform(0, lift);
            control.Opacity = 0; control.RenderTransform = move;
            int frame = 0;
            bool done = false;
            EventHandler<Avalonia.VisualTreeAttachmentEventArgs>? detached = null;
            void Finish()
            {
                done = true;
                if (ReferenceEquals(control.RenderTransform, move))
                { control.Opacity = 1; control.RenderTransform = null; }
                if (detached != null) control.DetachedFromVisualTree -= detached;
            }
            void Step()
            {
                if (done) return;
                if (!ReferenceEquals(control.RenderTransform, move)) { Finish(); return; }
                double t = Math.Clamp(++frame / (double)frames, 0, 1);
                double eased = 1 - Math.Pow(1 - t, 3);
                control.Opacity = eased; move.Y = lift * (1 - eased);
                if (t >= 1) Finish(); else Deck.NextFrame(control, Step);
            }
            detached = (_, _) => Finish();
            control.DetachedFromVisualTree += detached;
            // The embedded desktop surface has its own frame clock. A native
            // dispatcher timer is not an animation clock in that renderer.
            Deck.NextFrame(control, Step);
        }
    }
}
#endif
