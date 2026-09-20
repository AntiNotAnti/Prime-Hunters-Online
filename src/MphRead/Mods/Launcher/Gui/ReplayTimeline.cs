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
    /// <summary>
    /// Zoomable replay ruler. Pointer drag scrubs, wheel zooms around the playhead,
    /// arrow keys nudge one second, and annotations/highlights/clip marks are drawn
    /// directly on the same time axis.
    /// </summary>
    internal sealed class ReplayTimeline : Control
    {
        private static readonly IBrush TrackBrush = new SolidColorBrush(Deck.Rgb(0x171d27));
        private static readonly IBrush PlayedBrush = new SolidColorBrush(Deck.Rgb(0x2c5a4e));
        private static readonly IBrush EventBrush = new SolidColorBrush(Deck.Rgb(0x8b94a5));
        private static readonly IBrush KillBrush = new SolidColorBrush(Deck.Rgb(0xb66555));
        private static readonly IBrush ObjectiveBrush = new SolidColorBrush(Deck.Rgb(0xb9974b));
        private static readonly IBrush HighlightBrush = new SolidColorBrush(Deck.Fade(0x7a6130, 0.34));
        private static readonly IBrush SelectionBrush = new SolidColorBrush(Deck.Fade(0xd8b45d, 0.18));
        private static readonly IBrush BookmarkBrush = new SolidColorBrush(Deck.Rgb(0xb78ce8));
        private static readonly IBrush NamedHighlightBrush = new SolidColorBrush(Deck.Fade(0xb78ce8, 0.18));
        private static readonly IBrush PlayheadBrush = new SolidColorBrush(Deck.Rgb(0xf0efe8));
        private static readonly IBrush MarkBrush = new SolidColorBrush(Deck.Rgb(0xd8b45d));
        private static readonly IBrush CameraBrush = new SolidColorBrush(Deck.Rgb(0x6fb7c8));
        private static readonly Pen EventPen = new(EventBrush, 1);
        private static readonly Pen KillPen = new(KillBrush, 2);
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
        private IReadOnlyList<ReplayNamedHighlight> _namedHighlights = Array.Empty<ReplayNamedHighlight>();

        public Action<uint>? FrameRequested { get; set; }
        public Action<uint>? MarkInRequested { get; set; }
        public Action<uint>? MarkOutRequested { get; set; }
        public double Zoom { get; private set; } = 1;

        public ReplayTimeline()
        {
            MinHeight = 66;
            Focusable = true;
            ClipToBounds = true;
        }

        public void Update(uint duration, uint current, uint? markIn, uint? markOut,
            IReadOnlyList<ReplayEvent> events, IReadOnlyList<ReplayHighlight> highlights,
            IReadOnlyList<uint>? cameraKeys = null, IReadOnlyList<uint>? bookmarks = null,
            IReadOnlyList<ReplayNamedHighlight>? namedHighlights = null)
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
            InvalidateVisual();
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            double width = Math.Max(1, Bounds.Width);
            double height = Math.Max(1, Bounds.Height);
            (uint first, uint last) = Window();
            uint span = Math.Max(1, last - first);

            const double top = 13;
            double trackY = Math.Round(height * 0.57);
            var track = new Rect(0, trackY - 4, width, 8);
            context.DrawRectangle(TrackBrush, null, track);

            if (_markIn.HasValue && _markOut.HasValue)
            {
                uint start = Math.Min(_markIn.Value, _markOut.Value);
                uint end = Math.Max(_markIn.Value, _markOut.Value);
                if (end >= first && start <= last)
                {
                    double x1 = X(Math.Max(first, start), first, span, width);
                    double x2 = X(Math.Min(last, end), first, span, width);
                    context.DrawRectangle(SelectionBrush, null,
                        new Rect(x1, top, Math.Max(2, x2 - x1), Math.Max(4, height - top - 12)));
                }
            }

            foreach (ReplayNamedHighlight highlight in _namedHighlights)
            {
                if (highlight.EndFrame < first || highlight.StartFrame > last) continue;
                double x1 = X(Math.Max(first, highlight.StartFrame), first, span, width);
                double x2 = X(Math.Min(last, highlight.EndFrame), first, span, width);
                context.DrawRectangle(NamedHighlightBrush, null,
                    new Rect(x1, top, Math.Max(2, x2 - x1), Math.Max(4, height - top - 12)));
            }

            foreach (ReplayHighlight highlight in _highlights)
            {
                if (highlight.EndFrame < first || highlight.StartFrame > last) continue;
                double x1 = X(Math.Max(first, highlight.StartFrame), first, span, width);
                double x2 = X(Math.Min(last, highlight.EndFrame), first, span, width);
                context.DrawRectangle(HighlightBrush, null,
                    new Rect(x1, top, Math.Max(2, x2 - x1), Math.Max(4, height - top - 12)));
            }

            if (_current >= first)
            {
                double progress = X(Math.Min(_current, last), first, span, width);
                context.DrawRectangle(PlayedBrush, null,
                    new Rect(0, trackY - 4, progress, 8));
            }

            foreach (ReplayEvent marker in _events)
            {
                if (marker.Frame < first || marker.Frame > last) continue;
                double x = X(marker.Frame, first, span, width);
                Pen pen = marker.Type switch
                {
                    ReplayEventType.Kill or ReplayEventType.PlayerDeath => KillPen,
                    ReplayEventType.Objective or ReplayEventType.ScoreChanged => ObjectivePen,
                    _ => EventPen
                };
                context.DrawLine(pen, new Point(x, top), new Point(x, trackY + 10));
            }

            DrawMark(context, _markIn, first, last, span, width, height);
            DrawMark(context, _markOut, first, last, span, width, height);
            foreach (uint key in _cameraKeys)
            {
                if (key < first || key > last) continue;
                double x = X(key, first, span, width);
                context.DrawLine(CameraPen, new Point(x, 3), new Point(x, 11));
            }
            foreach (uint bookmark in _bookmarks)
            {
                if (bookmark < first || bookmark > last) continue;
                double x = X(bookmark, first, span, width);
                context.DrawLine(BookmarkPen, new Point(x, 2), new Point(x, trackY + 10));
                context.DrawEllipse(BookmarkBrush, null, new Point(x, 4), 2.5, 2.5);
            }

            if (_current >= first && _current <= last)
            {
                double x = X(_current, first, span, width);
                context.DrawLine(PlayheadPen, new Point(x, 5), new Point(x, height - 5));
                context.DrawEllipse(PlayheadBrush, null, new Point(x, 6), 3, 3);
            }
        }

        private static void DrawMark(DrawingContext context, uint? frame,
            uint first, uint last, uint span, double width, double height)
        {
            if (!frame.HasValue || frame < first || frame > last) return;
            double x = X(frame.Value, first, span, width);
            context.DrawLine(MarkPen, new Point(x, height - 22), new Point(x, height - 3));
            context.DrawRectangle(MarkBrush, null, new Rect(x - 4, height - 22, 8, 7));
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
            if (_dragTarget == DragTarget.None) return;
            Request(_dragTarget, e.GetPosition(this).X);
            e.Handled = true;
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (_dragTarget == DragTarget.None) return;
            Request(_dragTarget, e.GetPosition(this).X);
            _dragTarget = DragTarget.None;
            e.Pointer.Capture(null);
            e.Handled = true;
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
            if (!frame.HasValue || Bounds.Width <= 0) return Double.PositiveInfinity;
            (uint first, uint last) = Window();
            if (frame.Value < first || frame.Value > last) return Double.PositiveInfinity;
            return Math.Abs(X(frame.Value, first, Math.Max(1, last - first), Bounds.Width) - x);
        }

        private void Request(DragTarget target, double x)
        {
            uint frame = FrameAt(x);
            switch (target)
            {
                case DragTarget.MarkIn: MarkInRequested?.Invoke(frame); break;
                case DragTarget.MarkOut: MarkOutRequested?.Invoke(frame); break;
                case DragTarget.Playhead: FrameRequested?.Invoke(frame); break;
            }
            InvalidateVisual();
        }

        private uint FrameAt(double x)
        {
            if (_duration == 0 || Bounds.Width <= 0) return 0;
            (uint first, uint last) = Window();
            double t = Math.Clamp(x / Bounds.Width, 0, 1);
            uint frame = first + (uint)Math.Round((last - first) * t);
            return Math.Min(frame, _duration);
        }

        private (uint First, uint Last) Window()
        {
            if (_duration == 0) return (0, 1);
            uint visible = (uint)Math.Max(60, Math.Ceiling(_duration / Zoom));
            if (visible >= _duration) return (0, _duration);
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
