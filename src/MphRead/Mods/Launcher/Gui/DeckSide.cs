#if MPHREAD_AVALONIA
using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace MphRead.Mods.Launcher.Gui
{
    /// <summary>
    /// The room you picked, and what you are taking into it: the reference's
    /// <c>.side</c>.
    ///
    /// <para>
    /// Seventeen and a half ems down the right of the frame, sliding in from
    /// off the edge on the spring. Three rows, always the same three: what was
    /// picked at the top, the facts and the hunter in the middle, and the two
    /// words that leave or commit along the foot. A server and a map are the
    /// same object here -- something chosen on the left, the hunter you take
    /// into it, one button -- which is why there is one panel and not two.
    /// </para>
    ///
    /// <para>
    /// <b>It slides rather than appearing</b>, and that is the whole reason
    /// this is a control and not a <c>Border</c>: the panel arrives from where
    /// it is going to live, so the eye follows it in instead of finding it.
    /// `translate: calc(100% + 1.4em) 0` to nothing, `.34s var(--spring)`.
    /// </para>
    ///
    /// <para>
    /// Upright on a phone the same move is turned a quarter: the panel is the
    /// full width, comes up from the bottom, and is capped at two thirds of
    /// the frame. That is the reference's own portrait rule, and it is why the
    /// panel's box is worked out here rather than set by whoever builds it.
    /// </para>
    /// </summary>
    internal sealed class DeckSide : Decorator
    {
        /// <summary>`width: 17.5em` on a desktop, and the gap it parks beyond.</summary>
        private const double WidthEms = 17.5;
        private const double ParkEms = 1.4;

        /// <summary>`transition: translate .34s var(--spring)`.</summary>
        private const double SlideSeconds = 0.34;

        private readonly Stopwatch _clock = new();
        private bool _open;
        private bool _framePending;
        private double _at;

        static DeckSide()
        {
            AffectsMeasure<DeckSide>(Deck.EmProperty);
            AffectsMeasure<DeckSide>(Deck.FrameWidthProperty);
        }

        public DeckSide()
        {
            HorizontalAlignment = HorizontalAlignment.Right;
            VerticalAlignment = VerticalAlignment.Stretch;
            IsVisible = false;
        }

        private double Em => Deck.GetEm(this);

        /// <summary>Whether the frame is a phone held upright.</summary>
        private bool Upright => Deck.Phone && Deck.GetFrameWidth(this) <= Deck.NarrowFrame;

        /// <summary>Is the panel showing? Setting it starts the slide either way.</summary>
        public bool Open
        {
            get => _open;
            set
            {
                if (_open == value)
                {
                    return;
                }
                _open = value;
                if (value)
                {
                    IsVisible = true;
                }
                if (Deck.Still)
                {
                    _at = value ? 1 : 0;
                    IsVisible = value;
                    Place();
                    return;
                }
                _clock.Restart();
                Ask();
            }
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            double em = Em;
            if (Upright)
            {
                // A bottom sheet is wide and short where the side panel is
                // tall and narrow.
                HorizontalAlignment = HorizontalAlignment.Stretch;
                VerticalAlignment = VerticalAlignment.Bottom;
                double cap = Double.IsInfinity(availableSize.Height)
                    ? em * 31 : Math.Min(em * 31, availableSize.Height * 0.68);
                Child?.Measure(new Size(availableSize.Width, cap));
                return new Size(availableSize.Width, cap);
            }
            HorizontalAlignment = HorizontalAlignment.Right;
            VerticalAlignment = VerticalAlignment.Stretch;
            double width = Math.Round(em * WidthEms);
            Child?.Measure(new Size(width, availableSize.Height));
            return new Size(width, Double.IsInfinity(availableSize.Height)
                ? (Child?.DesiredSize.Height ?? 0) : availableSize.Height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            Child?.Arrange(new Rect(finalSize));
            Place();
            return finalSize;
        }

        private void Ask()
        {
            if (_framePending || Deck.Still)
            {
                return;
            }
            _framePending = true;
            Deck.NextFrame(this, Tick);
        }

        private void Tick()
        {
            _framePending = false;
            if (!_clock.IsRunning)
            {
                return;
            }
            double t = Math.Clamp(_clock.Elapsed.TotalSeconds / SlideSeconds, 0, 1);
            double eased = Deck.Spring(t);
            _at = _open ? eased : 1 - eased;
            Place();
            if (t >= 1)
            {
                _clock.Reset();
                // A panel that has finished leaving takes itself out of the
                // tree: it is over the list, and an invisible one that is
                // still there would swallow clicks meant for the cards under
                // it. (Opacity would not be enough for the same reason.)
                if (!_open)
                {
                    IsVisible = false;
                }
                return;
            }
            Ask();
        }

        /// <summary>Where the panel is, for the progress it has reached.</summary>
        private void Place()
        {
            double em = Em;
            double hidden = 1 - _at;
            if (Upright)
            {
                double park = Bounds.Height + em * ParkEms;
                RenderTransform = new TranslateTransform(0, park * hidden);
            }
            else
            {
                double park = Bounds.Width + em * ParkEms;
                RenderTransform = new TranslateTransform(park * hidden, 0);
            }
        }
    }
}
#endif
