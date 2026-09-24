using System;

namespace MphRead.Mods.Network
{

    // Rates schedule whole engine steps; the physics timestep is never scaled.
    internal sealed class ReplayTransport
    {
        private readonly ReplayPlaybackSession _session;
        internal bool SeekingEnabled { get; set; } = true;
        public ReplayTransport(ReplayPlaybackSession session) => _session = session;
        public static readonly float[] Rates = { 0.25f, 0.5f, 1, 2, 4 };
        public ReplayState State { get; private set; }
        public bool IsPaused => State == ReplayState.Paused;
        public bool AtEnd => State == ReplayState.Ended;
        public uint CurrentFrame => _session.CurrentFrame;
        public uint DurationFrames => _session.LastFrame;
        public float PlaybackRate { get; private set; } = 1;
        public double CurrentSeconds => CurrentFrame / 60.0;
        public double DurationSeconds => DurationFrames / 60.0;
        public long LastInteraction { get; private set; }
        private float _fraction;
        internal float PresentationAlpha(double hostAlpha) => IsPaused || AtEnd || IsSeeking ? 1
            : Math.Clamp(_fraction + (float)hostAlpha * PlaybackRate, 0, 1);
        private int _steps;
        private uint? _rebuild;
        private uint? _target;
        private bool _resumeAfterSeek;
        public bool IsSeeking => State == ReplayState.Seeking;
        internal long SeekGeneration { get; private set; }
        internal uint? RequestedSeekTarget => SeekTarget;
        internal uint? SeekTarget => _target ?? _rebuild;
        internal bool ResumeAfterSeek => _resumeAfterSeek;
        internal void CopyPreferences(ReplayTransport source)
        {
            SeekingEnabled = source.SeekingEnabled;
            PlaybackRate = source.PlaybackRate; EventFilter = source.EventFilter;
            ClipIn = source.ClipIn; ClipOut = source.ClipOut; LastInteraction = source.LastInteraction;
        }
        public uint? ClipIn { get; private set; }
        public uint? ClipOut { get; private set; }
        public void MarkIn() => SetMarkIn(CurrentFrame);
        public void MarkOut() => SetMarkOut(CurrentFrame);
        public void SetMarkIn(uint frame)
        {
            uint value = Math.Min(frame, DurationFrames);
            if (ClipOut.HasValue) value = Math.Min(value, ClipOut.Value);
            ClipIn = value;
            NoteInput();
        }
        public void SetMarkOut(uint frame)
        {
            uint value = Math.Min(frame, DurationFrames);
            if (ClipIn.HasValue) value = Math.Max(value, ClipIn.Value);
            ClipOut = value;
            NoteInput();
        }
        public System.Threading.Tasks.Task<ReplayOpenResult> SaveSelectionAsync(System.Threading.CancellationToken cancellation = default)
        {
            if (!ClipIn.HasValue || !ClipOut.HasValue || _session.CurrentPath == null) return System.Threading.Tasks.Task.FromResult(ReplayOpenResult.Empty);
            string source = _session.CurrentPath; uint start = ClipIn.Value, end = ClipOut.Value;
            string output = System.IO.Path.Combine(DemoLibrary.Directory, $"clip_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{Guid.NewGuid():N}.ppdemo");
            return ReplayStorageJobs.Run(() => { System.IO.Directory.CreateDirectory(DemoLibrary.Directory); return ReplayArchive.Extract(source, start, end, output, cancellation); }, cancellation);
        }
        public void NoteInput() => LastInteraction = Environment.TickCount64;
        internal void ClearSelection() { ClipIn = null; ClipOut = null; }
        internal void Begin()
        {
            State = ReplayState.Playing;
            PlaybackRate = 1;
            _fraction = 0;
            _steps = 0;
            _target = null;
            _rebuild = null;
            NoteInput();
        }
        internal void Stop() { State = ReplayState.Inactive; _steps = 0; _target = null; _rebuild = null; _fraction = 0; }
        public void Play() { if (IsPaused) State = ReplayState.Playing; NoteInput(); }
        public void Pause() { if (State == ReplayState.Playing) State = ReplayState.Paused; NoteInput(); }
        public void TogglePause()
        {
            // Media-style behavior: play from a finished replay starts it again
            // instead of silently doing nothing at the end frame.
            if (AtEnd) Restart();
            else if (IsPaused) Play();
            else Pause();
        }
        public void StepForward() { Pause(); if (IsPaused) _steps++; NoteInput(); }
        public void SetPlaybackRate(float rate)
        {
            if (Array.IndexOf(Rates, rate) < 0) throw new ArgumentOutOfRangeException(nameof(rate));
            PlaybackRate = rate;
            NoteInput();
        }
        public void ChangeRate(int direction) => SetPlaybackRate(Rates[Math.Clamp(Array.IndexOf(Rates, PlaybackRate) + direction, 0, Rates.Length - 1)]);
        public void Restart() => Seek(0, resume: true);
        public ReplayEventType? EventFilter { get; set; }
        public void JumpEvent(bool forward)
        {
            uint? target = null;
            foreach (ReplayEvent marker in _session.Events)
            {
                if (EventFilter.HasValue && marker.Type != EventFilter.Value) continue;
                if (forward && marker.Frame > CurrentFrame && (!target.HasValue || marker.Frame < target)) target = marker.Frame;
                if (!forward && marker.Frame < CurrentFrame && (!target.HasValue || marker.Frame > target)) target = marker.Frame;
            }
            if (target.HasValue) Seek(target.Value);
        }
        public void Seek(uint frame, bool? resume = null)
        {
            if (!SeekingEnabled) throw new InvalidOperationException("Linear replay playback does not support seeking.");
            if (!_session.IsActive) return;
            uint target = Math.Min(frame, DurationFrames);
            _resumeAfterSeek = resume ?? (IsSeeking ? _resumeAfterSeek : State == ReplayState.Playing);
            SeekGeneration++;
            if (target == CurrentFrame && _session.HasSimulatedFrame)
            {
                _target = null;
                _rebuild = null;
                State = _resumeAfterSeek ? ReplayState.Playing : ReplayState.Paused;
                NoteInput();
                return;
            }

            if ((target > CurrentFrame || !_session.HasSimulatedFrame) && State != ReplayState.Ended)
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

        internal void RequestFullRebuild(uint frame, bool resume)
        {
            _target = null;
            _rebuild = Math.Min(frame, DurationFrames);
            _resumeAfterSeek = resume;
            State = ReplayState.Seeking;
        }
        // Hosts must destroy and recreate the scene before completing this request.
        public bool TakeRebuild(out uint frame, out bool resume)
        {
            frame = _rebuild ?? 0;
            resume = _resumeAfterSeek;
            bool pending = _rebuild.HasValue;
            _rebuild = null;
            return pending;
        }
        public void ContinueSeek(uint frame, bool resume)
        {
            _resumeAfterSeek = resume;
            if (CurrentFrame >= frame && _session.HasSimulatedFrame)
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
        internal int FramesDue()
        {
            if (State == ReplayState.Seeking)
                return _target.HasValue ? (int)Math.Min(120u,
                    _target.Value - CurrentFrame + (_session.HasSimulatedFrame ? 0u : 1u)) : 0;
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
        internal void AfterFrame()
        {
            if (_session.AtEnd)
            {
                State = _session.LastResult == ReplayOpenResult.Success ? ReplayState.Ended : ReplayState.Error;
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
