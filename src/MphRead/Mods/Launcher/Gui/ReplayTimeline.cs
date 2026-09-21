#if MPHREAD_AVALONIA
using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using MphRead.Mods.Network;
using MphRead.Mods.Replay;

namespace MphRead.Mods.Launcher.Gui
{
    internal enum ReplayTimelineFilter
    {
        All,
        Combat,
        KillsDeaths,
        Damage,
        Objectives,
        Annotations
    }

    /// <summary>
    /// Zoomable multi-lane replay timeline. Combat, damage, objectives and
    /// annotations remain visually distinct while sharing one scrub axis.
    /// </summary>
    internal sealed class ReplayTimeline : Control
    {
        private static readonly IBrush TrackBrush = new SolidColorBrush(Deck.Rgb(0x171d27));
        private static readonly IBrush PlayedBrush = new SolidColorBrush(Deck.Rgb(0x2c5a4e));
        private static readonly IBrush EventBrush = new SolidColorBrush(Deck.Rgb(0x8b94a5));
        private static readonly IBrush KillBrush = new SolidColorBrush(Deck.Rgb(0xb66555));
        private static readonly IBrush DeathBrush = new SolidColorBrush(Deck.Rgb(0x9d6274));
        private static readonly IBrush DamageBrush = new SolidColorBrush(Deck.Rgb(0x7c91bd));
        private static readonly IBrush ObjectiveBrush = new SolidColorBrush(Deck.Rgb(0xb9974b));
        private static readonly IBrush HighlightBrush = new SolidColorBrush(Deck.Fade(0x7a6130, 0.25));
        private static readonly IBrush SelectionBrush = new SolidColorBrush(Deck.Fade(0xd8b45d, 0.14));
        private static readonly IBrush BookmarkBrush = new SolidColorBrush(Deck.Rgb(0xb78ce8));
        private static readonly IBrush NamedHighlightBrush = new SolidColorBrush(Deck.Fade(0xb78ce8, 0.16));
        private static readonly IBrush PlayheadBrush = new SolidColorBrush(Deck.Rgb(0xf0efe8));
        private static readonly IBrush MarkBrush = new SolidColorBrush(Deck.Rgb(0xd8b45d));
        private static readonly IBrush CameraBrush = new SolidColorBrush(Deck.Rgb(0x6fb7c8));
        private static readonly Pen EventPen = new(EventBrush, 1);
        private static readonly Pen KillPen = new(KillBrush, 2);
        private static readonly Pen DeathPen = new(DeathBrush, 2);
        private static readonly Pen DamagePen = new(DamageBrush, 1.5);
        private static readonly Pen ObjectivePen = new(ObjectiveBrush, 2);
        private static readonly Pen PlayheadPen = new(PlayheadBrush, 2);
        private static readonly Pen MarkPen = new(MarkBrush, 2);
        private static readonly Pen CameraPen = new(CameraBrush, 2);
        private static readonly Pen BookmarkPen = new(BookmarkBrush, 2);

        private enum DragTarget { None, Playhead, MarkIn, MarkOut }

        private DragTarget _dragTarget;
        private uint _duration;
        private uint _current;
        private uint? _markIn;
        private uint? _markOut;
        private IReadOnlyList<ReplayEvent> _events = Array.Empty<ReplayEvent>();
        private IReadOnlyList<ReplayHighlight> _highlights = Array.Empty<ReplayHighlight>();
        private IReadOnlyList<uint> _cameraKeys = Array.Empty<uint>();
        private IReadOnlyList<uint> _bookmarks = Array.Empty<uint>();
        private IReadOnlyList<ReplayNamedHighlight> _namedHighlights =
            Array.Empty<ReplayNamedHighlight>();
        private int _playerFilter = -1;
        private ReplayTimelineFilter _filter = ReplayTimelineFilter.All;

        public Action<uint>? FrameRequested { get; set; }
        public Action<uint>? MarkInRequested { get; set; }
        public Action<uint>? MarkOutRequested { get; set; }
        public double Zoom { get; private set; } = 1;

        public ReplayTimeline()
        {
            MinHeight = 118;
            Focusable = true;
            ClipToBounds = true;
        }

        public void Update(uint duration, uint current, uint? markIn, uint? markOut,
            IReadOnlyList<ReplayEvent> events, IReadOnlyList<ReplayHighlight> highlights,
            IReadOnlyList<uint>? cameraKeys = null, IReadOnlyList<uint>? bookmarks = null,
            IReadOnlyList<ReplayNamedHighlight>? namedHighlights = null,
            int playerFilter = -1, ReplayTimelineFilter filter = ReplayTimelineFilter.All)
        {
            _duration = duration;
            _current = Math.Min(current, duration);
            _markIn = markIn;
            _markOut = markOut;
            _events = events;
            _highlights = highlights;
            _cameraKeys = cameraKeys ?? Array.Empty<uint>();
            _bookmarks = bookmarks ?? Array.Empty<uint>();
            _namedHighlights = namedHighlights ?? Array.Empty<ReplayNamedHighlight>();
            _playerFilter = playerFilter;
            _filter = filter;
            InvalidateVisual();
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            double width = Math.Max(1, Bounds.Width);
            double height = Math.Max(1, Bounds.Height);
            (uint first, uint last) = Window();
            uint span = Math.Max(1, last - first);

            const double laneTop = 15;
            const double laneHeight = 14;
            const double laneGap = 3;
            double killY = laneTop + laneHeight / 2;
            double deathY = killY + laneHeight + laneGap;
            double damageY = deathY + laneHeight + laneGap;
            double objectiveY = damageY + laneHeight + laneGap;
            double annotationY = objectiveY + laneHeight + laneGap;
            double trackY = Math.Max(annotationY + 14, height - 13);

            DrawLane(context, width, killY, KillBrush);
            DrawLane(context, width, deathY, DeathBrush);
            DrawLane(context, width, damageY, DamageBrush);
            DrawLane(context, width, objectiveY, ObjectiveBrush);
            DrawLane(context, width, annotationY, BookmarkBrush);

            if (_markIn.HasValue && _markOut.HasValue)
            {
                uint start = Math.Min(_markIn.Value, _markOut.Value);
                uint end = Math.Max(_markIn.Value, _markOut.Value);
                if (end >= first && start <= last)
                {
                    double x1 = X(Math.Max(first, start), first, span, width);
                    double x2 = X(Math.Min(last, end), first, span, width);
                    context.DrawRectangle(SelectionBrush, null,
                        new Rect(x1, laneTop - 7, Math.Max(2, x2 - x1),
                            trackY - laneTop + 12));
                }
            }

            if (ShowAnnotations())
            {
                foreach (ReplayNamedHighlight highlight in _namedHighlights)
                {
                    if (highlight.EndFrame < first || highlight.StartFrame > last)
                        continue;
                    double x1 = X(Math.Max(first, highlight.StartFrame), first, span, width);
                    double x2 = X(Math.Min(last, highlight.EndFrame), first, span, width);
                    context.DrawRectangle(NamedHighlightBrush, null,
                        new Rect(x1, annotationY - 6, Math.Max(2, x2 - x1), 12));
                }
            }

            foreach (ReplayHighlight highlight in _highlights)
            {
                if (highlight.EndFrame < first || highlight.StartFrame > last)
                    continue;
                if (_playerFilter >= 0
                    && highlight.ActorSlot != _playerFilter
                    && highlight.TargetSlot != _playerFilter)
                    continue;
                double x1 = X(Math.Max(first, highlight.StartFrame), first, span, width);
                double x2 = X(Math.Min(last, highlight.EndFrame), first, span, width);
                context.DrawRectangle(HighlightBrush, null,
                    new Rect(x1, laneTop - 7, Math.Max(2, x2 - x1),
                        trackY - laneTop + 10));
            }

            foreach (ReplayEvent marker in _events)
            {
                if (marker.Frame < first || marker.Frame > last
                    || !EventVisible(marker))
                    continue;

                double x = X(marker.Frame, first, span, width);
                (Pen pen, double y) = marker.Type switch
                {
                    ReplayEventType.Kill => (KillPen, killY),
                    ReplayEventType.PlayerDeath => (DeathPen, deathY),
                    ReplayEventType.Damage => (DamagePen, damageY),
                    ReplayEventType.WeaponFired => (EventPen, damageY),
                    ReplayEventType.Objective or ReplayEventType.ScoreChanged
                        => (ObjectivePen, objectiveY),
                    _ => (EventPen, objectiveY)
                };
                context.DrawLine(pen, new Point(x, y - 6), new Point(x, y + 6));
            }

            if (ShowAnnotations())
            {
                foreach (uint bookmark in _bookmarks)
                {
                    if (bookmark < first || bookmark > last)
                        continue;
                    double x = X(bookmark, first, span, width);
                    context.DrawLine(BookmarkPen,
                        new Point(x, annotationY - 6), new Point(x, annotationY + 6));
                    context.DrawEllipse(BookmarkBrush, null,
                        new Point(x, annotationY), 2.5, 2.5);
                }
            }

            foreach (uint key in _cameraKeys)
            {
                if (key < first || key > last)
                    continue;
                double x = X(key, first, span, width);
                context.DrawLine(CameraPen, new Point(x, 2), new Point(x, 10));
            }

            var track = new Rect(0, trackY - 3, width, 6);
            context.DrawRectangle(TrackBrush, null, track);
            if (_current >= first)
            {
                double progress = X(Math.Min(_current, last), first, span, width);
                context.DrawRectangle(PlayedBrush, null,
                    new Rect(0, trackY - 3, progress, 6));
            }

            DrawMark(context, _markIn, first, last, span, width, height);
            DrawMark(context, _markOut, first, last, span, width, height);

            if (_current >= first && _current <= last)
            {
                double x = X(_current, first, span, width);
                context.DrawLine(PlayheadPen, new Point(x, 3),
                    new Point(x, height - 3));
                context.DrawEllipse(PlayheadBrush, null, new Point(x, 5), 3, 3);
            }
        }

        private static void DrawLane(DrawingContext context, double width,
            double y, IBrush brush)
        {
            context.DrawRectangle(new SolidColorBrush(Deck.Fade(0xffffff, 0.035)),
                null, new Rect(0, y - 6, width, 12));
            context.DrawRectangle(brush, null, new Rect(0, y - 0.5, 5, 1));
        }

        private bool EventVisible(ReplayEvent marker)
        {
            if (_playerFilter >= 0
                && marker.ActorSlot != _playerFilter
                && marker.TargetSlot != _playerFilter)
            {
                return false;
            }

            return _filter switch
            {
                ReplayTimelineFilter.Combat => marker.Type is ReplayEventType.Kill
                    or ReplayEventType.PlayerDeath or ReplayEventType.Damage
                    or ReplayEventType.WeaponFired,
                ReplayTimelineFilter.KillsDeaths => marker.Type is ReplayEventType.Kill
                    or ReplayEventType.PlayerDeath,
                ReplayTimelineFilter.Damage => marker.Type == ReplayEventType.Damage,
                ReplayTimelineFilter.Objectives => marker.Type is ReplayEventType.Objective
                    or ReplayEventType.ScoreChanged,
                ReplayTimelineFilter.Annotations => false,
                _ => true
            };
        }

        private bool ShowAnnotations()
            => _filter is ReplayTimelineFilter.All or ReplayTimelineFilter.Annotations;

        private static void DrawMark(DrawingContext context, uint? frame,
            uint first, uint last, uint span, double width, double height)
        {
            if (!frame.HasValue || frame < first || frame > last)
                return;
            double x = X(frame.Value, first, span, width);
            context.DrawLine(MarkPen, new Point(x, height - 22),
                new Point(x, height - 3));
            context.DrawRectangle(MarkBrush, null,
                new Rect(x - 4, height - 22, 8, 7));
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            Focus();
            double x = e.GetPosition(this).X;
            _dragTarget = PickDragTarget(x);
            e.Pointer.Capture(this);
            Request(_dragTarget, x);
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (_dragTarget == DragTarget.None)
                return;
            Request(_dragTarget, e.GetPosition(this).X);
            e.Handled = true;
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (_dragTarget == DragTarget.None)
                return;
            Request(_dragTarget, e.GetPosition(this).X);
            _dragTarget = DragTarget.None;
            e.Pointer.Capture(null);
            e.Handled = true;
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            _dragTarget = DragTarget.None;
            base.OnPointerCaptureLost(e);
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            Zoom = Math.Clamp(Zoom * (e.Delta.Y > 0 ? 1.35 : 1 / 1.35), 1, 16);
            InvalidateVisual();
            e.Handled = true;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key is Key.Left or Key.Right)
            {
                long delta = e.Key == Key.Left ? -60 : 60;
                uint target = (uint)Math.Clamp((long)_current + delta, 0, _duration);
                FrameRequested?.Invoke(target);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Home)
            {
                FrameRequested?.Invoke(0);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.End)
            {
                FrameRequested?.Invoke(_duration);
                e.Handled = true;
                return;
            }
            base.OnKeyDown(e);
        }

        private DragTarget PickDragTarget(double x)
        {
            const double grab = 10;
            double inDistance = DistanceTo(_markIn, x);
            double outDistance = DistanceTo(_markOut, x);
            if (inDistance <= grab || outDistance <= grab)
                return inDistance <= outDistance ? DragTarget.MarkIn : DragTarget.MarkOut;
            return DragTarget.Playhead;
        }

        private double DistanceTo(uint? frame, double x)
        {
            if (!frame.HasValue || Bounds.Width <= 0)
                return Double.PositiveInfinity;
            (uint first, uint last) = Window();
            if (frame.Value < first || frame.Value > last)
                return Double.PositiveInfinity;
            return Math.Abs(X(frame.Value, first,
                Math.Max(1, last - first), Bounds.Width) - x);
        }

        private void Request(DragTarget target, double x)
        {
            uint frame = FrameAt(x);
            switch (target)
            {
                case DragTarget.MarkIn:
                    MarkInRequested?.Invoke(frame);
                    break;
                case DragTarget.MarkOut:
                    MarkOutRequested?.Invoke(frame);
                    break;
                case DragTarget.Playhead:
                    FrameRequested?.Invoke(frame);
                    break;
            }
            InvalidateVisual();
        }

        private uint FrameAt(double x)
        {
            if (_duration == 0 || Bounds.Width <= 0)
                return 0;
            (uint first, uint last) = Window();
            double t = Math.Clamp(x / Bounds.Width, 0, 1);
            uint frame = first + (uint)Math.Round((last - first) * t);
            return Math.Min(frame, _duration);
        }

        private (uint First, uint Last) Window()
        {
            if (_duration == 0)
                return (0, 1);
            uint visible = (uint)Math.Max(60, Math.Ceiling(_duration / Zoom));
            if (visible >= _duration)
                return (0, _duration);
            long half = visible / 2;
            long first = (long)_current - half;
            first = Math.Clamp(first, 0, (long)_duration - visible);
            return ((uint)first, (uint)first + visible);
        }

        private static double X(uint frame, uint first, uint span, double width)
            => (frame - first) / (double)Math.Max(1u, span) * width;
    }
}
#endif
