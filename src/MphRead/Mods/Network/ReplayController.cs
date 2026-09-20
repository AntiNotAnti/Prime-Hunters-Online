using System;

namespace MphRead.Mods.Network
{
    public enum ReplayState { Inactive, Playing, Paused, Seeking, Ended, Error }

    // Rates schedule whole engine steps; the physics timestep is never scaled.
    public static class ReplayController
    {
        public static readonly float[] Rates = { 0.25f, 0.5f, 1, 2, 4 };
        public static ReplayState State { get; private set; }
        public static bool IsPaused => State == ReplayState.Paused;
        public static bool AtEnd => State == ReplayState.Ended;
        public static uint CurrentFrame => DemoPlayback.CurrentFrame;
        public static uint DurationFrames => DemoPlayback.LastFrame;
        public static float PlaybackRate { get; private set; } = 1;
        public static double CurrentSeconds => CurrentFrame / 60.0;
        public static double DurationSeconds => DurationFrames / 60.0;
        public static long LastInteraction { get; private set; }
        private static float _fraction;
        private static int _steps;
        private static uint? _rebuild;
        private static uint? _target;
        private static bool _resumeAfterSeek;
        public static bool IsSeeking => State == ReplayState.Seeking;
        public static uint? ClipIn { get; private set; }
        public static uint? ClipOut { get; private set; }
        public static void MarkIn() { ClipIn = CurrentFrame; NoteInput(); }
        public static void MarkOut() { ClipOut = CurrentFrame; NoteInput(); }
        public static ReplayOpenResult SaveSelection()
        {
            if (!ClipIn.HasValue || !ClipOut.HasValue || DemoPlayback.CurrentPath == null) return ReplayOpenResult.Empty;
            System.IO.Directory.CreateDirectory(DemoLibrary.Directory);
            string output = System.IO.Path.Combine(DemoLibrary.Directory, $"clip_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{Guid.NewGuid():N}.fpdemo");
            return ReplayArchive.Extract(DemoPlayback.CurrentPath, ClipIn.Value, ClipOut.Value, output);
        }
        public static void NoteInput() => LastInteraction = Environment.TickCount64;
        internal static void ClearSelection() { ClipIn = null; ClipOut = null; }
        internal static void Begin()
        {
            State = ReplayState.Playing;
            PlaybackRate = 1;
            _fraction = 0;
            _steps = 0;
            _target = null;
            _rebuild = null;
            NoteInput();
        }
        internal static void Stop() { State = ReplayState.Inactive; _steps = 0; _target = null; }
        public static void Play() { if (IsPaused) State = ReplayState.Playing; NoteInput(); }
        public static void Pause() { if (State == ReplayState.Playing) State = ReplayState.Paused; NoteInput(); }
        public static void TogglePause() { if (IsPaused) Play(); else Pause(); }
        public static void StepForward() { Pause(); if (IsPaused) _steps++; NoteInput(); }
        public static void SetPlaybackRate(float rate)
        {
            if (Array.IndexOf(Rates, rate) < 0) throw new ArgumentOutOfRangeException(nameof(rate));
            PlaybackRate = rate;
            NoteInput();
        }
        public static void ChangeRate(int direction) => SetPlaybackRate(Rates[Math.Clamp(Array.IndexOf(Rates, PlaybackRate) + direction, 0, Rates.Length - 1)]);
        public static void Restart() => Seek(0, resume: true);
        public static ReplayEventType? EventFilter { get; set; }
        public static void JumpEvent(bool forward)
        {
            uint? target = null;
            foreach (ReplayEvent marker in DemoPlayback.Events)
            {
                if (EventFilter.HasValue && marker.Type != EventFilter.Value) continue;
                if (forward && marker.Frame > CurrentFrame && (!target.HasValue || marker.Frame < target)) target = marker.Frame;
                if (!forward && marker.Frame < CurrentFrame && (!target.HasValue || marker.Frame > target)) target = marker.Frame;
            }
            if (target.HasValue) Seek(target.Value);
        }
        public static void Seek(uint frame, bool? resume = null)
        {
            if (!DemoPlayback.IsActive) return;
            uint target = Math.Min(frame, DurationFrames);
            _resumeAfterSeek = resume ?? State == ReplayState.Playing;
            if (target == CurrentFrame)
            {
                _target = null;
                _rebuild = null;
                State = _resumeAfterSeek ? ReplayState.Playing : ReplayState.Paused;
                NoteInput();
                return;
            }

            if (target > CurrentFrame && State != ReplayState.Ended)
            {
                _target = target;
                _rebuild = null;
            }
            else
            {
                _target = null;
                _rebuild = target;
            }
            State = ReplayState.Seeking;
            NoteInput();
        }

        internal static void RequestFullRebuild(uint frame, bool resume)
        {
            _target = null;
            _rebuild = Math.Min(frame, DurationFrames);
            _resumeAfterSeek = resume;
            State = ReplayState.Seeking;
        }
        // Hosts must destroy and recreate the scene before completing this request.
        public static bool TakeRebuild(out uint frame, out bool resume)
        {
            frame = _rebuild ?? 0;
            resume = _resumeAfterSeek;
            bool pending = _rebuild.HasValue;
            _rebuild = null;
            return pending;
        }
        public static void ContinueSeek(uint frame, bool resume)
        {
            _resumeAfterSeek = resume;
            if (CurrentFrame >= frame)
            {
                _target = null;
                State = resume ? ReplayState.Playing : ReplayState.Paused;
            }
            else
            {
                _target = frame;
                State = ReplayState.Seeking;
            }
        }
        internal static int FramesDue()
        {
            if (State == ReplayState.Seeking) return _target.HasValue ? 32 : 0;
            if (State == ReplayState.Paused)
            {
                if (_steps == 0) return 0;
                _steps--;
                return 1;
            }
            if (State != ReplayState.Playing) return 0;
            _fraction += PlaybackRate;
            int frames = (int)_fraction;
            _fraction -= frames;
            return frames;
        }
        internal static void AfterFrame()
        {
            if (DemoPlayback.AtEnd)
            {
                State = DemoPlayback.LastResult == ReplayOpenResult.Success ? ReplayState.Ended : ReplayState.Error;
                _target = null;
            }
            else if (_target.HasValue && CurrentFrame >= _target.Value)
            {
                _target = null;
                State = _resumeAfterSeek ? ReplayState.Playing : ReplayState.Paused;
            }
        }
    }
}
