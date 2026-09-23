using System;
using System.Diagnostics;
using System.Threading;
using MphRead.Entities;
using MphRead.Mods.Input;
using OpenTK.Mathematics;

namespace MphRead.Mods.Network;

/// <summary>Optional -netcheck -nographics network/replay soak. Runs the real
/// client simulation at 60 Hz; makes no claims about rendering or visible killcams.</summary>
internal static class NetCheckSimulation
{
    internal static int Run(string room, GameMode mode, Hunter hunter, double seconds)
    {
        var scene = new Scene(new Vector2i(256, 192), SyntheticInput.CreateKeyboard(),
            SyntheticInput.CreateMouse(), _ => { }, () => { });
        NetLaunch.BuildPlayers(scene, hunter, NetSession.LocalColor, teams: GameState.IsTeamMode(mode));
        scene.AddRoom(room, mode, playerCount: NetLaunch.RoomPlayerCount);
        scene.OnLoad();
        NetSession.MarkMatchLoaded();
        long start = Stopwatch.GetTimestamp();
        int steps = 0, remoteFrames = 0, clips = 0;
        double next = 0;
        bool testClips = Environment.GetEnvironmentVariable("MPHREAD_CLIP_TEST") != null;
        double clipAt = Math.Min(60, Math.Max(0, seconds - 12));
        var positions = new Vector3[PlayerEntity.SlotCapacity];
        var moved = new bool[PlayerEntity.SlotCapacity];
        while (Stopwatch.GetElapsedTime(start).TotalSeconds < seconds)
        {
            double now = Stopwatch.GetElapsedTime(start).TotalSeconds;
            if (now < next) { Thread.Sleep(1); continue; }
            if (now - next > 0.25) next = now;
            next += 1.0 / 60;
            scene.OnSimulationFrame();
            steps++;
            foreach (var player in scene.Players.Items)
            {
                int slot = player.SlotIndex;
                if (slot == NetSession.LocalSlot || !player.LoadFlags.TestFlag(LoadFlags.Spawned)) continue;
                remoteFrames++;
                if (positions[slot] != Vector3.Zero && (player.Position - positions[slot]).LengthSquared > 0.0025f)
                    moved[slot] = true;
                positions[slot] = player.Position;
            }
            if (testClips && clips < 2
                && now >= clipAt + clips * 6 && !DemoClip.IsSaving && DemoClip.Held > 0)
                Console.WriteLine($"[netchecksim] clip {++clips}: {DemoClip.Save()}");
        }
        if (testClips) DemoClip.CompletePending(scene.Size);
        bool clipsPassed = !testClips || clips == 2 && DemoClip.LastSavedPath != null && DemoClip.LastError == null;
        int moving = 0;
        foreach (bool value in moved) if (value) moving++;
        Console.WriteLine($"[netchecksim] steps={steps} remoteFrames={remoteFrames} movingPeers={moving} "
            + $"clips={clips} clipsPassed={clipsPassed} recordingError={DemoRecorder.LastError ?? "none"} captureError={ReplayCapture.WorldCapture.LastError ?? "none"}");
        Console.WriteLine(ReplayPerfTelemetry.Summary(ReplayCapture.Recorder.Timeline));
        return steps > 0 && moving > 0 && clipsPassed && DemoRecorder.LastError == null
            && ReplayCapture.WorldCapture.LastError == null ? 0 : 1;
    }
}
