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
        private static readonly IBrush PlayheadBrush = new SolidColorBrush(Deck.Rgb(0xf0efe8));
        private static readonly IBrush MarkBrush = new SolidColorBrush(Deck.Rgb(0xd8b45d));
        private static readonly IBrush CameraBrush = new SolidColorBrush(Deck.Rgb(0x6fb7c8));
        private static readonly Pen EventPen = new(EventBrush, 1);
        private static readonly Pen KillPen = new(KillBrush, 2);
        private static readonly Pen ObjectivePen = new(ObjectiveBrush, 2);
        private static readonly Pen PlayheadPen = new(PlayheadBrush, 2);
        private static readonly Pen MarkPen = new(MarkBrush, 2);
        private static readonly Pen CameraPen = new(CameraBrush, 2);

        private bool _dragging;
        private uint _duration;
        private uint _current;
        private uint? _markIn;
        private uint? _markOut;
        private IReadOnlyList<ReplayEvent> _events = Array.Empty<ReplayEvent>();
        private IReadOnlyList<ReplayHighlight> _highlights = Array.Empty<ReplayHighlight>();
        private IReadOnlyList<uint> _cameraKeys = Array.Empty<uint>();

        public Action<uint>? FrameRequested { get; set; }
        public double Zoom { get; private set; } = 1;

        public ReplayTimeline()
        {
            MinHeight = 66;
            Focusable = true;
            ClipToBounds = true;
        }

        public void Update(uint duration, uint current, uint? markIn, uint? markOut,
            IReadOnlyList<ReplayEvent> events, IReadOnlyList<ReplayHighlight> highlights,
            IReadOnlyList<uint>? cameraKeys = null)
        {
            _duration = duration;
            _current = Math.Min(current, duration);
            _markIn = markIn;
            _markOut = markOut;
            _events = events;
            _highlights = highlights;
            _cameraKeys = cameraKeys ?? Array.Empty<uint>();
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
            context.DrawLine(MarkPen, new Point(x, height - 20), new Point(x, height - 3));
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            Focus();
            _dragging = true;
            e.Pointer.Capture(this);
            Request(e.GetPosition(this).X);
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (!_dragging) return;
            Request(e.GetPosition(this).X);
            e.Handled = true;
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (!_dragging) return;
            _dragging = false;
            Request(e.GetPosition(this).X);
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

        private void Request(double x)
        {
            if (_duration == 0 || Bounds.Width <= 0) return;
            (uint first, uint last) = Window();
            double t = Math.Clamp(x / Bounds.Width, 0, 1);
            uint frame = first + (uint)Math.Round((last - first) * t);
            FrameRequested?.Invoke(Math.Min(frame, _duration));
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
