#if MPHREAD_AVALONIA
using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MphRead.Mods.Launcher.Gui
{
    /// <summary>
    /// The reference's <c>.sheet</c> arriving: the scrim and the panel on it
    /// fading up over <c>.22s</c>, with the panel springing out of
    /// <c>scale(.9) translateY(14px)</c> over <c>.4s</c> underneath.
    ///
    /// <para>
    /// A screen used to simply be there. <c>UiSurface.Show</c> swapped the
    /// view and the next frame had the new one in it, fully formed -- which is
    /// the one place the launcher still read as a program changing pages
    /// rather than as a deck dealing a card. The reference never cuts: every
    /// panel it opens is a fade and a spring, and the spring is what makes the
    /// panel an object that arrived rather than a rectangle that appeared.
    /// </para>
    ///
    /// <para>
    /// <b>Both halves move together, and that is why they are one control.</b>
    /// Darkening the frame instantly and then floating a panel onto it is two
    /// events where there is one; the scrim is a child of this, not a layer in
    /// the backdrop's bake, precisely so it can fade with what it belongs to.
    /// It costs one flat full-window rectangle a redraw, which against the
    /// gradients already in the bake is nothing.
    /// </para>
    ///
    /// <para>
    /// The clock starts when the sheet reaches the tree, because that is when
    /// the screen is shown. <see cref="Deck.Still"/> pins it at the end pose,
    /// so <c>-uishot</c> photographs the screen the player ends up looking at
    /// rather than one caught a fifth of a second in.
    /// </para>
    /// </summary>
    internal sealed class DeckSheet : Panel
    {
        /// <summary>`transition: opacity .22s var(--settle)`.</summary>
        private const double FadeSeconds = 0.22;

        /// <summary>`transition: transform .4s var(--spring)` on the panel.</summary>
        private const double RiseSeconds = 0.4;

        /// <summary>`transform: scale(.9) translateY(14px)` at rest.</summary>
        private const double FromScale = 0.9;
        private const double FromLift = 14;

        private readonly Stopwatch _clock = new();
        private bool _framePending;

        public DeckSheet()
        {
            // The panel is the one that scales, not the scrim: a scrim that
            // grew out of 90% would show the frame's own corners through the
            // gap for the length of the animation.
            ClipToBounds = false;
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            if (Deck.Still)
            {
                Apply(1);
                return;
            }
            Apply(0);
            _clock.Restart();
            Ask();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            _clock.Reset();
            base.OnDetachedFromVisualTree(e);
        }

        /// <summary>
        /// Step both curves for the time that has passed, and ask for another
        /// frame while either is still moving.
        /// </summary>
        private void Tick()
        {
            _framePending = false;
            if (!_clock.IsRunning)
            {
                return;
            }
            double t = _clock.Elapsed.TotalSeconds;
            Apply(t);
            if (t >= Math.Max(FadeSeconds, RiseSeconds))
            {
                _clock.Reset();
                return;
            }
            Ask();
        }

        private void Ask()
        {
            if (_framePending)
            {
                return;
            }
            _framePending = true;
            Deck.NextFrame(this, Tick);
        }

        /// <summary>
        /// Where both curves are at <paramref name="seconds"/>. The fade is on
        /// this, the spring is on whatever panel is inside it -- which is the
        /// second child, and is left alone if a caller ever builds a sheet
        /// without one.
        /// </summary>
        private void Apply(double seconds)
        {
            double fade = FadeSeconds <= 0 ? 1 : Math.Clamp(seconds / FadeSeconds, 0, 1);
            // `--settle`, the reference's other curve: no overshoot, which is
            // right for an opacity -- a fade that goes past 1 and comes back
            // is a flicker.
            Opacity = Deck.Bezier(fade, 0.3, 0.8, 0.4, 1);

            double rise = RiseSeconds <= 0 ? 1 : Math.Clamp(seconds / RiseSeconds, 0, 1);
            double amount = Deck.Spring(rise);
            double scale = FromScale + (1 - FromScale) * amount;
            double lift = FromLift * (1 - amount);
            for (int i = 1; i < Children.Count; i++)
            {
                Control child = Children[i];
                child.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
                child.RenderTransform = new TransformGroup
                {
                    Children =
                    {
                        new ScaleTransform(scale, scale),
                        new TranslateTransform(0, lift)
                    }
                };
            }
        }
    }
}
#endif
