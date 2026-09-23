#if MPHREAD_AVALONIA
using System;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace MphRead.Mods.Launcher.Gui
{
    /// <summary>
    /// Short one-shot movement for launcher page changes.
    ///
    /// The desktop launcher is CPU-rasterised, so this intentionally lasts
    /// only a handful of frames. Deck.Still disables it for deterministic
    /// captures and checks.
    /// </summary>
    internal static class HubMotion
    {
        public static void Enter(Control control, double lift = 10, int frames = 9)
        {
            if (Deck.Still || LauncherPrefs.ReduceMotion || frames <= 0)
            {
                control.Opacity = 1;
                control.RenderTransform = null;
                return;
            }

            var move = new TranslateTransform(0, lift);
            control.Opacity = 0;
            control.RenderTransform = move;
            int frame = 0;
            DispatcherTimer? timer = null;
            EventHandler<Avalonia.VisualTreeAttachmentEventArgs>? detached = null;
            void Finish()
            {
                timer?.Stop(); control.Opacity = 1; control.RenderTransform = null;
                if (detached != null) control.DetachedFromVisualTree -= detached;
            }
            timer = new DispatcherTimer(
                TimeSpan.FromMilliseconds(16),
                DispatcherPriority.Render,
                (_, _) =>
                {
                    frame++;
                    double t = Math.Clamp(frame / (double)frames, 0, 1);
                    double eased = 1 - Math.Pow(1 - t, 3);
                    control.Opacity = eased;
                    move.Y = lift * (1 - eased);
                    if (t >= 1)
                    {
                        Finish();
                    }
                });
            detached = (_, _) => Finish();
            control.DetachedFromVisualTree += detached;
            timer.Start();
        }
    }
}
#endif
