using System;
using System.IO;
using MphRead.Mods.Input;

namespace MphRead.Mods.Network
{
    internal static class ReplayControlCheck
    {
        public static int Run()
        {
            try
            {
                foreach (float rate in ReplayController.Rates)
                {
                    ReplayController.Begin();
                    ReplayController.SetPlaybackRate(rate);
                    int frames = 0;
                    for (int i = 0; i < 240; i++) frames += ReplayController.FramesDue();
                    Require(frames == 240 * rate, $"{rate}x: expected {240 * rate}, got {frames}");
                    ReplayController.Pause();
                    for (int i = 0; i < 60; i++) Require(ReplayController.FramesDue() == 0, "pause advanced");
                    ReplayController.StepForward();
                    Require(ReplayController.FramesDue() == 1, "step did not advance exactly once");
                    Require(ReplayController.FramesDue() == 0, "step repeated");
                    ReplayController.Play();
                    int resumed = 0;
                    for (int i = 0; i < 240; i++) resumed += ReplayController.FramesDue();
                    Require(resumed == 240 * rate, "resume changed rate");
                }
                // Different presentation rates all owe 60 fixed intervals per second.
                foreach (int fps in new[] { 30, 60, 120, 144, 240 })
                {
                    ReplayController.Begin();
                    ReplayController.SetPlaybackRate(4);
                    double accumulator = 0;
                    int frames = 0;
                    for (int draw = 0; draw < fps * 4; draw++)
                    {
                        accumulator += 60.0 / fps;
                        while (accumulator + 1e-9 >= 1)
                        { accumulator -= 1; frames += ReplayController.FramesDue(); }
                    }
                    Require(frames == 960, $"display rate {fps} changed replay timing");
                }
                Console.WriteLine("[replaycheck] timing: all rates, pause, step, resume and presentation independence passed");
                Replay.ReplayCameraTrackCheck.Run();

                var events = new[]
                {
                    new ReplayEvent(60, ReplayEventType.Kill, 0, 1),
                    new ReplayEvent(120, ReplayEventType.Kill, 0, 1),
                    new ReplayEvent(180, ReplayEventType.Objective, 1),
                    new ReplayEvent(200, ReplayEventType.Damage, 1, 0, 200),
                    new ReplayEvent(300, ReplayEventType.MatchEnded)
                };
                var analytics = Replay.ReplayStudio.Analytics(events, 300);
                Require(analytics.TotalKills == 2 && analytics.TotalDamage == 200
                    && analytics.ObjectiveEvents == 1, "studio aggregate analytics");
                var player0 = System.Linq.Enumerable.Single(analytics.Players, p => p.Slot == 0);
                var player1 = System.Linq.Enumerable.Single(analytics.Players, p => p.Slot == 1);
                Require(player0.Kills == 2 && player1.Deaths == 2
                    && player1.ObjectiveEvents == 1 && player1.Damage == 200,
                    "studio per-player analytics");
                var highlights = Replay.ReplayStudio.Highlights(events, 300);
                Require(System.Linq.Enumerable.Any(highlights,
                    h => h.Kind == Replay.ReplayHighlightKind.MultiKill && h.ActorSlot == 0),
                    "multi-kill highlight");
                Require(System.Linq.Enumerable.Any(highlights,
                    h => h.Kind == Replay.ReplayHighlightKind.Objective && h.ActorSlot == 1),
                    "objective highlight");
                Console.WriteLine("[replaycheck] studio: analytics and highlight derivation passed");

                Require(MphRead.Mods.KillCam.IsRecentFinalKill(120, 120),
                    "same-frame final kill was not eligible");
                Require(MphRead.Mods.KillCam.IsRecentFinalKill(120, 120 + 8 * 60),
                    "final kill window excluded its boundary");
                Require(!MphRead.Mods.KillCam.IsRecentFinalKill(120, 120 + 8 * 60 + 1),
                    "stale kill was accepted as final");
                Require(!MphRead.Mods.KillCam.IsRecentFinalKill(121, 120),
                    "future kill frame was accepted as final");
                Console.WriteLine("[replaycheck] kill cam: final-kill eligibility passed");

                var bindings = new PadBindingState();
                bindings.SetSlot(PadAction.ReplayPlayPause, 0, GamepadButtons.A,
                    GamepadButtons.LeftBumper);
                ulong liveButtons = bindings.Evaluate(
                    GamepadButtons.LeftBumper | GamepadButtons.A, replayContext: false);
                Require((liveButtons & (1UL << (int)PadAction.ReplayPlayPause)) == 0,
                    "replay controller chord leaked into live gameplay");
                Require((liveButtons & (1UL << (int)PadAction.Jump)) != 0,
                    "replay controller chord suppressed live jump");
                ulong replayButtons = bindings.Evaluate(
                    GamepadButtons.LeftBumper | GamepadButtons.A, replayContext: true);
                Require((replayButtons & (1UL << (int)PadAction.ReplayPlayPause)) != 0,
                    "replay controller chord did not activate in replay context");
                Console.WriteLine("[replaycheck] input: replay controller context isolation passed");

                string annotationReplay = Path.Combine(Path.GetTempPath(),
                    "replay-annotations-" + Guid.NewGuid().ToString("N") + DemoFile.Extension);
                try
                {
                    File.WriteAllBytes(annotationReplay, new byte[] { 1 });
                    var named = Replay.ReplayAnnotations.AddHighlight(
                        annotationReplay, 60, 180, "Final push");
                    var stored = Replay.ReplayAnnotations.Highlights(annotationReplay);
                    Require(stored.Count == 1 && stored[0].Id == named.Id
                        && stored[0].Name == "Final push"
                        && stored[0].StartFrame == 60 && stored[0].EndFrame == 180,
                        "named replay highlight did not round-trip");
                    Replay.ReplayAnnotations.RemoveHighlight(annotationReplay, named.Id);
                    Require(Replay.ReplayAnnotations.Highlights(annotationReplay).Count == 0,
                        "named replay highlight did not remove");
                }
                finally
                {
                    Replay.ReplayAnnotations.DeleteFor(annotationReplay);
                    File.Delete(annotationReplay);
                }
                Console.WriteLine("[replaycheck] studio: annotation sidecar round-trip passed");
                return 0;
            }
            catch (Exception ex) { Console.WriteLine($"[replaycheck] FAIL: {ex.Message}"); return 1; }
            finally { ReplayController.Stop(); }
        }
        private static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
    }
}
