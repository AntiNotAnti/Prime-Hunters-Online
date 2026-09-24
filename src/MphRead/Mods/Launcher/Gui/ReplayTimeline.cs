#if MPHREAD_AVALONIA
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using Avalonia.Media.Imaging;
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

        private enum DragTarget { None, Playhead, MarkIn, MarkOut, Range, Camera }
        private (uint First, uint Last)? _dragWindow;
        private uint _dragAnchor, _rangeIn, _rangeOut, _cameraFrame, _cameraDestination;
        private int[] _density = Array.Empty<int>();
        private uint _bucketFrames;
        private int _eventCount;
        private static readonly IBrush LaneBrush = new SolidColorBrush(Deck.Fade(0xffffff, 0.035));

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
        public Action<uint, uint>? RangeRequested { get; set; }
        public Action<uint, uint>? CameraMoved { get; set; }
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
            if (!ReferenceEquals(events, _events) || events.Count != _eventCount || duration != _duration
                || playerFilter != _playerFilter || filter != _filter)
            {
                _playerFilter = playerFilter; _filter = filter; _eventCount = events.Count;
                _bucketFrames = Math.Max(60u, (duration + 599) / 600);
                _density = new int[duration / _bucketFrames + 1];
                foreach (var marker in events)
                    if (marker.Frame <= duration && EventVisible(marker)) _density[marker.Frame / _bucketFrames]++;
            }
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

            int peak = _density.Length == 0 ? 1 : Math.Max(1, _density.Max());
            for (int i = 0; i < _density.Length; i++)
            {
                uint begin = (uint)i * _bucketFrames, end = Math.Min(_duration, begin + _bucketFrames);
                if (_density[i] == 0 || end < first || begin > last) continue;
                double x1 = X(Math.Max(begin, first), first, span, width), x2 = X(Math.Min(end, last), first, span, width);
                context.DrawRectangle(PlayedBrush, null, new Rect(x1, height - 7 - 6d * _density[i] / peak,
                    Math.Max(1, x2 - x1), 6d * _density[i] / peak));
            }
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

            foreach (uint original in _cameraKeys)
            {
                uint key = _dragTarget == DragTarget.Camera && original == _cameraFrame ? _cameraDestination : original;
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
            context.DrawRectangle(LaneBrush,
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
            _dragWindow = Window(); _dragAnchor = FrameAt(x);
            _rangeIn = _markIn ?? 0; _rangeOut = _markOut ?? 0;
            _dragTarget = PickDragTarget(x);
            if (e.GetPosition(this).Y < 14)
            {
                uint? key = _cameraKeys.Cast<uint?>().OrderBy(k => DistanceTo(k, x)).FirstOrDefault();
                if (DistanceTo(key, x) <= 10) { _dragTarget = DragTarget.Camera; _cameraFrame = _cameraDestination = key!.Value; }
            }
            else if (_dragTarget == DragTarget.Playhead && e.KeyModifiers.HasFlag(KeyModifiers.Shift)
                && _markIn.HasValue && _markOut.HasValue && _dragAnchor >= _rangeIn && _dragAnchor <= _rangeOut)
                _dragTarget = DragTarget.Range;
            e.Pointer.Capture(this);
            Request(_dragTarget, x);
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (_dragTarget == DragTarget.None)
            {
                uint hover = FrameAt(e.GetPosition(this).X);
                ShowHover(hover);
                return;
            }
            Request(_dragTarget, e.GetPosition(this).X);
            e.Handled = true;
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (_dragTarget == DragTarget.None)
                return;
            Request(_dragTarget, e.GetPosition(this).X);
            if (_dragTarget == DragTarget.Camera && _cameraFrame != _cameraDestination)
                CameraMoved?.Invoke(_cameraFrame, _cameraDestination);
            _dragTarget = DragTarget.None; _dragWindow = null;
            e.Pointer.Capture(null);
            e.Handled = true;
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            _dragTarget = DragTarget.None; _dragWindow = null;
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
            ShowHover(frame);
            // Snap to confirmed events within six pixels; otherwise retain exact frame precision.
            foreach (var marker in _events)
                if (EventVisible(marker) && DistanceTo(marker.Frame, x) <= 6) { frame = marker.Frame; break; }
            switch (target)
            {
                case DragTarget.Range:
                    long delta = Math.Clamp((long)frame - _dragAnchor, -(long)_rangeIn, (long)_duration - _rangeOut);
                    RangeRequested?.Invoke((uint)(_rangeIn + delta), (uint)(_rangeOut + delta));
                    break;
                case DragTarget.Camera:
                    if (frame == _cameraFrame || !_cameraKeys.Contains(frame)) _cameraDestination = frame;
                    break;
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

        private readonly List<(uint Frame, Bitmap Image)> _scrubThumbnails = new();
        private readonly Image _hoverImage = new() { Width = 192, Height = 108, Stretch = Stretch.Uniform };
        private readonly TextBlock _hoverText = new();
        private StackPanel? _hoverPanel;
        private long _thumbnailGeneration;
        protected override async void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            long generation = ++_thumbnailGeneration;
            string? replay = DemoPlayback.CurrentPath;
            uint duration = ReplayController.DurationFrames;
            if (replay == null) return;
            var images = await ReplayStorageJobs.Run(() =>
            {
                var loaded = new List<(uint Frame, Bitmap Image)>();
                for (int i = 0; i < 3; i++)
                {
                    try
                    {
                        string path = ReplayVideoExporter.ThumbnailPath(replay, i);
                        if (File.Exists(path)) loaded.Add((duration * (uint)(i + 1) / 4, new Bitmap(path)));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
                }
                return loaded;
            });
            if (generation != _thumbnailGeneration || replay != DemoPlayback.CurrentPath)
            { foreach (var thumbnail in images) thumbnail.Image.Dispose(); return; }
            _scrubThumbnails.AddRange(images);
        }
        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            _thumbnailGeneration++; _hoverImage.Source = null;
            foreach (var thumbnail in _scrubThumbnails) thumbnail.Image.Dispose();
            _scrubThumbnails.Clear(); ToolTip.SetIsOpen(this, false);
            base.OnDetachedFromVisualTree(e);
        }
        private void ShowHover(uint frame)
        {
            _hoverPanel ??= new StackPanel { Spacing = 5, Children = { _hoverImage, _hoverText } };
            var nearby = _events.Where(marker => EventVisible(marker) && Math.Abs((long)marker.Frame - frame) <= 60)
                .Take(3).Select(marker => marker.Type.ToString());
            _hoverText.Text = ReplayHud.Time(frame) + "  " + string.Join(" / ", nearby)
                + (_scrubThumbnails.Count > 0 ? "\nCached scene preview" : "") + "\nShift-drag to move a clip range";
            _hoverImage.IsVisible = _scrubThumbnails.Count > 0;
            if (_scrubThumbnails.Count > 0)
                _hoverImage.Source = _scrubThumbnails.MinBy(image => Math.Abs((long)image.Frame - frame)).Image;
            ToolTip.SetTip(this, _hoverPanel);
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
            if (_dragWindow is { } captured) return captured;
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
