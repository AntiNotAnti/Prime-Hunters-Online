namespace MphRead.Mods.Network
{
    public enum ReplayState { Inactive, Playing, Paused, Seeking, Ended, Error }

    /// <summary>Studio compatibility facade; scheduling state belongs to its session.</summary>
    public static class ReplayController
    {
        private static ReplayTransport Current => DemoPlayback.Session.Transport;
        public static readonly float[] Rates = (float[])ReplayTransport.Rates.Clone();
        public static ReplayState State => Current.State;
        public static bool IsPaused => Current.IsPaused;
        public static bool AtEnd => Current.AtEnd;
        public static uint CurrentFrame => Current.CurrentFrame;
        public static uint DurationFrames => Current.DurationFrames;
        public static float PlaybackRate => Current.PlaybackRate;
        public static double CurrentSeconds => Current.CurrentSeconds;
        public static double DurationSeconds => Current.DurationSeconds;
        public static long LastInteraction => Current.LastInteraction;
        public static bool IsSeeking => Current.IsSeeking || DemoPlayback.Session.IsWarming;
        public static uint? ClipIn => Current.ClipIn;
        public static uint? ClipOut => Current.ClipOut;
        public static ReplayEventType? EventFilter { get => Current.EventFilter; set => Current.EventFilter = value; }
        public static void MarkIn() => Current.MarkIn();
        public static void MarkOut() => Current.MarkOut();
        public static void SetMarkIn(uint frame) => Current.SetMarkIn(frame);
        public static void SetMarkOut(uint frame) => Current.SetMarkOut(frame);
        public static ReplayOpenResult SaveSelection() => Current.SaveSelection();
        public static void NoteInput() => Current.NoteInput();
        public static void Play() => Current.Play();
        public static void Pause() => Current.Pause();
        public static void TogglePause() => Current.TogglePause();
        public static void StepForward() => Current.StepForward();
        public static void SetPlaybackRate(float rate) => Current.SetPlaybackRate(rate);
        public static void ChangeRate(int direction) => Current.ChangeRate(direction);
        public static void Restart() => Current.Restart();
        public static void JumpEvent(bool forward) => Current.JumpEvent(forward);
        public static void Seek(uint frame, bool? resume = null) => Current.Seek(frame, resume);
        internal static void ClearSelection() => Current.ClearSelection();
        internal static void Begin() => Current.Begin();
        internal static void Stop() => Current.Stop();
        internal static int FramesDue() => Current.FramesDue();
        internal static void AfterFrame() => Current.AfterFrame();
    }
}
