using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MphRead.Entities;
using MphRead.Mods.Input;
using MphRead.Mods.Replay;
using OpenTK.Mathematics;
#if !ANDROID
using OpenTK.Graphics.OpenGL;
using OpenTK.Windowing.Desktop;
using GLFWBindingsContext = OpenTK.Windowing.GraphicsLibraryFramework.GLFWBindingsContext;
#endif

namespace MphRead.Mods.Network;

internal static class ReplayKillcamCheck
{
    internal static int Run(string path, string? shots)
    {
#if !ANDROID
        NativeWindow? window = null;
        if (shots != null)
        {
            Directory.CreateDirectory(shots);
            var settings = Render.DesktopGlContext.Settings(background: true); settings.ClientSize = new(640, 480);
            window = new NativeWindow(settings); window.Context.MakeCurrent(); GL.LoadBindings(new GLFWBindingsContext());
        }
        else
#endif
            Headless.Enter();
        var live = new Scene(new Vector2i(640, 480), SyntheticInput.CreateKeyboard(), SyntheticInput.CreateMouse(),
            _ => { }, () => { }, initializeRuntime: false);
        live.AddPlayer(Hunter.Samus); live.GameState.Points[0] = 572; live.Random.SetRng1(8943);
        string sentinel = ReplayStateHash.Compute(live, 0);
        var recorder = new ReplayRecorder();
        using var capture = new ReplayLiveWorld(recorder);
        using var controller = new KillcamController(recorder.Timeline);
        try
        {
            using var reader = DemoReader.Open(path, out var result) ?? throw new InvalidDataException(result.ToString());
            if (reader.Metadata is { } metadata)
                foreach (var packet in metadata.Bootstrap.Packets.OrderBy(p => p[0] == (byte)PacketType.MatchState ? 0 : 1))
                    ReplayLiveCaptureCheck.Accept(recorder, packet, 0);
            var hashes = new Dictionary<uint, string>();
            DemoRecord? pending = reader.ReadNext(); uint frame = 0;
            while (pending != null)
            {
                while (pending is DemoRecord record && record.Frame <= frame)
                { ReplayLiveCaptureCheck.Accept(recorder, record.Data, frame); pending = reader.ReadNext(); }
                capture.Advance(frame, live.Size);
                if (capture.LastError != null) throw new InvalidDataException(capture.LastError);
                if (capture.World is { } world) hashes[frame] = ReplayStateHash.Compute(world.Scene, frame);
                frame++;
            }
            var state = capture.World!.State;
            if (frame < 1800 || state.Match is not { } match || !state.TryGetPlayer(0, out var victim))
                throw new InvalidDataException("Use the eight-actor world-coverage fixture for killcam lifecycle checks.");
            const uint death = 1602;
            var identity = new ReplayKillIdentity(match.MatchId, match.AuthorityEpoch, death, 2,
                1, state.Occupant(1).Generation, 0, state.Occupant(0).Generation, 1);
            var marker = new ReplayMarker(ReplayMarkerKind.Kill, 1, 0, Kill: identity, Weapon: 1);
            var context = new KillcamContext(match.MatchId, match.AuthorityEpoch, death, 0,
                identity.VictimGeneration, 1, false, true, true, true);
            int checks = 0;
            void Require(bool value, string message) { if (!value) throw new InvalidDataException(message); checks++; }
            void Begin()
            {
                controller.NoteKill(marker, death, context);
                for (int warm = 0; warm < 8 && !controller.Visible; warm++) controller.Update(live, context);
                Require(controller.Visible && controller.Kind == KillCamKind.Personal, "Personal killcam did not become visible.");
            }
            Begin();
            Require(controller.Input(true, true) && controller.Active, "Held fire skipped before release.");
            controller.Input(false, false); controller.Input(true, true);
            Require(!controller.Active && controller.EndReason == KillcamEndReason.Skipped, "Rising fire did not skip.");
            for (int cycle = 0; cycle < 24; cycle++)
            {
                Begin();
                if (cycle % 3 == 0)
                    controller.Update(live, context with { LocalLife = 2, LocalAlive = true });
                else if (cycle % 3 == 1) controller.Skip();
                else controller.Update(live, context with { Connected = false });
                Require(!controller.Active, "A completed lifecycle retained replay presentation.");
                Require(ReplayStateHash.Compute(live, 0) == sentinel, "Killcam changed live simulation state.");
            }
            Begin();
            int compared = 0;
            while (controller.Active)
            {
                if (controller.Replica is { } replica)
                {
                    Require(ReplayStateHash.Compute(replica.Scene, replica.Session.CurrentFrame) == hashes[replica.Session.CurrentFrame],
                        "Killcam world differs from captured historical state.");
                    if (shots != null && compared is 0 or 40 or 90)
                    {
                        replica.Scene.OnDrawFrame(); replica.Scene.OnRenderFrame();
                        ScreenCapture.Save(replica.Scene, Path.Combine(shots, $"personal-{compared}.png"));
                    }
                }
                compared++;
                controller.Update(live, context);
                Require(compared < 400, "Personal killcam did not complete.");
            }
            Require(controller.EndReason == KillcamEndReason.Completed, "Personal end hold did not finish.");
            Begin(); live.Size = new(960, 600); controller.Camera(live.Size);
            Require(controller.Presentation?.Size == live.Size, "Resize did not reach the private scene.");
            controller.Update(live, context with { MatchId = (ushort)(match.MatchId + 1) });
            Require(!controller.Active && controller.EndReason == KillcamEndReason.MatchChanged, "Match transition retained replay.");
            Require(KillcamController.FinalEligible(100, 220, false, true), "Causal boundary rejected.");
            Require(!KillcamController.FinalEligible(100, 221, false, true), "Stale causal kill admitted.");
            Require(KillcamController.FinalEligible(100, 580, true, false), "Timed boundary rejected.");
            Require(!KillcamController.FinalEligible(100, 581, true, false), "Stale timed kill admitted.");
            Require(!KillcamController.FinalEligible(101, 100, true, true), "Future kill admitted.");
            controller.NoteKill(marker, death, context);
            Require(controller.BeginFinal(live, context, death, timedEnd: false, causalEnd: true), "Eligible final did not start.");
            for (int i = 0; i < 8 && !controller.Visible; i++) controller.Update(live, context);
            Require(controller.Visible && controller.Kind == KillCamKind.Final, "Final did not become visible.");
            recorder.Reset(); // the final clip was frozen before room/lobby handoff
            for (int i = 0; i < 180; i++) controller.Update(live, context);
            Require(controller.State == KillcamState.AwaitCompletion, "Final did not hold its immutable EOF.");
            if (shots != null && controller.Presentation is { } final)
            { final.OnDrawFrame(); final.OnRenderFrame(); ScreenCapture.Save(final, Path.Combine(shots, "final.png")); }
            var presented = controller.Presentation!;
            ulong oldLease = ReplayAudioOwner.Acquire(presented, live);
            ulong currentLease = ReplayAudioOwner.Acquire(live, live);
            ReplayAudioOwner.Release(oldLease);
            Require(ReplayAudioOwner.MayPlay(live) && !ReplayAudioOwner.MayPlay(presented), "Stale audio release changed current ownership.");
            ReplayAudioOwner.Release(currentLease);
            controller.Stop(KillcamEndReason.Completed);
            Require(ReplayStateHash.Compute(live, 0) == sentinel, "Final teardown changed live state.");
            Console.WriteLine($"[killcam] PASS: {checks} checks, 24 death/skip/respawn/disconnect cycles, {compared} historical frames, resize, match change, final eligibility/freeze and versioned audio handoff.");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("[killcam] FAIL: " + ex); return 1; }
        finally
        {
            controller.Dispose(); capture.Dispose(); live.DoCleanup(); live.UnloadGl();
#if !ANDROID
            window?.Dispose();
#endif
        }
    }
}
