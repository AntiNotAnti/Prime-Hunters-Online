using System.Collections.Generic;

namespace MphRead.Mods.Network
{
    /// <summary>Compatibility entry point for the foreground Replay Studio session.</summary>
    public static class DemoPlayback
    {
        internal static ReplayPlaybackSession Session { get; } = new(new TheatreReplaySessionHost());
        public static bool IsActive => Session.IsActive;
        public static string? CurrentPath => Session.CurrentPath;
        public static IReadOnlyList<ReplayEvent> Events => Session.Events;
        internal static ReplayMetadata? Metadata => Session.Metadata;
        public static uint CurrentFrame => Session.CurrentFrame;
        public static uint LastFrame => Session.LastFrame;
        public static ReplayOpenResult LastResult => Session.LastResult;
        public static double CurrentSeconds => Session.CurrentSeconds;
        public static double DurationSeconds => Session.DurationSeconds;
        public static bool AtEnd => Session.AtEnd;
        public static string? LastError => Session.LastError;
        public static bool Join(string path, int timeoutMs = 8000) => Session.Join(path, timeoutMs);
        public static void PumpFrame() => Session.PumpFrame();
        public static void Stop() => Session.Stop();
        public static bool TakeControl(int slot, out string? branchPath) => Session.TakeControl(slot, out branchPath);
        internal static bool Reposition(uint frame, uint netFrame) => Session.Reposition(frame, netFrame);
        internal static void FailVerification(string error) => Session.FailVerification(error);
        internal static long PlaybackArrivalTicks(uint frame) => ReplayPlaybackSession.PlaybackArrivalTicks(frame);
    }
}
