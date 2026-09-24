using System;
using System.IO;
using MphRead.Entities;
using MphRead.Mods.Network;

namespace MphRead.Mods.Replay;

internal static class ReplayHardeningCheck
{
    internal static void Run(Action<bool, string> check)
    {
        var context = new KillcamContext(1, 2, 300, 0, 1, 1, false, true, true, true);
        var kill = new ReplayKillIdentity(1, 2, 300, 1, 1, 1, 0, 1, 1);
        var timeline = new RollingReplayTimeline();
        using var controller = new KillcamController(timeline);
        foreach (var invalid in new[] { kill with { KillerSlot = 255 }, kill with { VictimSlot = 255 },
            kill with { KillerSlot = PlayerEntity.SlotCapacity }, kill with { VictimSlot = PlayerEntity.SlotCapacity },
            kill with { KillerGeneration = 0 }, kill with { VictimGeneration = 0 }, kill with { VictimLifeId = 0 },
            kill with { MatchId = 3 }, kill with { AuthorityEpoch = 3 }, kill with { KillerSlot = 0 } })
        {
            check(!KillcamController.IsValidIdentity(invalid, context), "malformed kill identity admitted");
            controller.NoteKill(new(ReplayMarkerKind.Kill, invalid.KillerSlot, invalid.VictimSlot, Kill: invalid), 300, context);
            check(controller.Candidate == null && !controller.Skip(), "malformed kill created pending replay");
        }
        check(KillcamController.IsValidIdentity(kill, context), "valid kill identity rejected");
        check(KillcamController.PreRollFrames == 210 && KillcamController.PostRollFrames == 90, "killcam window changed");
        string directory = Path.Combine(Path.GetTempPath(), "prime-encoder-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            bool windows = OperatingSystem.IsWindows();
            string executable = windows ? "cmd.exe" : "/bin/sh";
            using (var success = new ReplayEncoderJob(executable,
                windows ? "/c \"echo frame=12&exit /b 0\"" : "-c \"echo frame=12; exit 0\"", directory))
            {
                check(success.Completion.Wait(10000), "encoder did not drain stdout");
                var result = success.Completion.Result;
                check(result.ExitCode == 0 && result.Error == null && success.Frames == 12, "encoder success/progress lost");
            }
            using (var failure = new ReplayEncoderJob(executable,
                windows ? "/c \"echo broken 1>&2&exit /b 7\"" : "-c \"echo broken >&2; exit 7\"", directory))
            {
                check(failure.Completion.Wait(10000), "encoder did not drain stderr");
                check(failure.Completion.Result.ExitCode == 7 && failure.Completion.Result.Stderr.Contains("broken"), "encoder failure lost");
            }
            using (var missing = new ReplayEncoderJob(Path.Combine(directory, "absent-ffmpeg"), "", directory))
            {
                check(missing.Completion.Wait(10000) && missing.Completion.Result.Error != null, "missing encoder reported success");
            }
            using (var cancel = new ReplayEncoderJob(executable,
                windows ? "/c ping -n 30 127.0.0.1" : "-c \"sleep 30\"", directory))
            {
                System.Threading.Thread.Sleep(100);
                cancel.Cancel();
                check(cancel.Completion.Wait(10000) && cancel.Completion.Result.Cancelled, "encoder cancellation leaked process");
            }
        }
        finally { Directory.Delete(directory, true); }
        Console.WriteLine("[replayhardening] identity fences, clip window, encoder progress/failure/cancellation passed");
    }
}
