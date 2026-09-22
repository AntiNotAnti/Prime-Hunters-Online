using System;
using System.Threading;
using MphRead.Entities;
using MphRead.Mods.Launcher;
using MphRead.Mods.Network;
using MphRead.Mods.Replay;

namespace MphRead.Mods;

/// <summary>Foreground adapter. The instance controller owns historical scenes;
/// this adapter reads live lifecycle and routes presentation/input at the boundary.</summary>
internal static class KillCam
{
    // Temporary developer migration fallback; removal is gated on runtime acceptance.
    internal static bool UseReplayKillcam = true;
    private static readonly KillcamController Controller = new(ReplayCapture.Recorder.Timeline);
    private static Scene? _live;
    private static int _skipRequested;
    private static bool _releaseFire;
    private static bool _finalRequested;
    public static bool Active => UseReplayKillcam ? Controller.Active : LegacyKillCam.Active;
    public static bool IsPersonal => UseReplayKillcam ? Controller.Kind == KillCamKind.Personal : LegacyKillCam.IsPersonal;
    public static bool IsFinal => UseReplayKillcam ? Controller.Kind == KillCamKind.Final : LegacyKillCam.IsFinal;
    public static uint PlaybackFrame => UseReplayKillcam ? Controller.Frame : LegacyKillCam.PlaybackFrame;
    internal static float Progress => UseReplayKillcam ? Controller.Progress : LegacyKillCam.Progress;
    internal static KillcamState State => Controller.State;
    internal static KillcamEndReason EndReason => Controller.EndReason;
    internal static string? LastError => Controller.LastError;

    private static KillcamContext Context(Scene? scene)
    {
        int local = NetHooks.LocalSlot;
        return new(NetSession.CurrentMatchId, NetSession.AuthorityEpoch, NetSession.NetFrame, local,
            NetPlayerLifecycle.Generation(local), NetPlayerLifecycle.Get(local),
            scene != null && (uint)local < (uint)scene.Players.Items.Count && scene.Players.Items[local].Health > 0,
            NetSession.Active && !DemoPlayback.IsActive,
            LauncherPrefs.KillCamEnabled && !Headless.Active && !SpectatorMode.IsSpectating,
            LauncherPrefs.FinalKillCamEnabled && !Headless.Active);
    }
    internal static void NoteKill(ReplayMarker marker, uint recordingFrame)
    {
        if (!UseReplayKillcam) return;
        bool enemy = true;
        if (_live?.GameState.Teams == true && marker.Kill is { } kill)
            enemy = _live.Players.Items[kill.KillerSlot].TeamIndex != _live.Players.Items[kill.VictimSlot].TeamIndex;
        Controller.NoteKill(marker, recordingFrame, Context(_live), enemy);
    }
    internal static void NoteDeath(int victimSlot, int attackerSlot, uint frame)
    { if (!UseReplayKillcam) LegacyKillCam.NoteDeath(victimSlot, attackerSlot, frame); }

    internal static void AfterSimulation(Scene scene)
    {
        _live = scene;
        if (!UseReplayKillcam) { LegacyKillCam.AfterSimulation(scene); return; }
        if (Interlocked.Exchange(ref _skipRequested, 0) != 0) { Controller.Skip(); _releaseFire = true; }
        if (_finalRequested)
        {
            _finalRequested = false;
            var game = scene.GameState;
            bool causal = false;
            if (Controller.Candidate?.Kill is { } kill)
            {
                int team = scene.Players.Items[kill.KillerSlot].TeamIndex;
                bool winner = scene.Players.Items[game.ResultSlots[0]].TeamIndex == team;
                causal = winner && (game.Mode is GameMode.Survival or GameMode.SurvivalTeams
                    || game.Mode is GameMode.Battle or GameMode.BattleTeams && game.TeamPoints[team] >= game.PointGoal);
            }
            // Server ending announcements retain a small terminal clock. A
            // non-causal recent kill is allowed only when the clock expired.
            bool timed = !causal && NetSession.ServerMatch is { TimeRemaining: <= 3.1f };
            Controller.BeginFinal(scene, Context(scene), NetSession.NetFrame, timed, causal);
        }
        Controller.Update(scene, Context(scene));
    }
    internal static bool BeginFinal(uint frame)
    {
        if (!UseReplayKillcam) return LegacyKillCam.BeginFinal(frame);
        _finalRequested = true; return false;
    }
    internal static void EndFinal()
    {
        _finalRequested = false;
        if (!UseReplayKillcam) LegacyKillCam.EndFinal();
        else if (IsFinal) Controller.Stop(KillcamEndReason.Completed);
    }
    internal static void FilterInput(Scene scene)
    {
        if (!UseReplayKillcam || scene.Services.IsReplica) return;
        var player = scene.Players.Main;
        bool down = player.Controls.Shoot.IsDown;
        bool wasActive = Controller.Active;
        if (Controller.Input(down, player.Controls.Shoot.IsPressed))
        {
            player.Controls.ClearAll(); player.ModForgetInputDeltas();
            if (wasActive && !Controller.Active) _releaseFire = true;
        }
        else if (_releaseFire)
        {
            player.Controls.Shoot.IsDown = player.Controls.Shoot.IsPressed = false;
            if (!down) _releaseFire = false;
        }
    }
    // UI/Android callbacks only enqueue. Scene/audio/GL disposal stays on the owner.
    internal static bool RequestSkip()
    {
        if (!UseReplayKillcam || !Active) return false;
        Interlocked.Exchange(ref _skipRequested, 1); return true;
    }
    internal static Scene? Presentation(Scene live)
    {
        if (!UseReplayKillcam || live.Services.IsReplica || !ReferenceEquals(live, _live)) return null;
        if (!NetSession.Active) { Controller.Reset(KillcamEndReason.Disconnected); return null; }
        if (Controller.Playing?.Kill is { } kill && (kill.MatchId != NetSession.CurrentMatchId || kill.AuthorityEpoch != NetSession.AuthorityEpoch))
        { Controller.Reset(KillcamEndReason.MatchChanged); return null; }
        try { Controller.Camera(live.Size); return Controller.Presentation; }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        { Controller.FailPresentation(ex); return null; }
    }
    internal static void DrawHud(Scene scene) => Controller.DrawHud(scene);
    internal static void FailPresentation(Exception error) => Controller.FailPresentation(error);
    internal static bool OwnsPresentation(Scene scene) => UseReplayKillcam && ReferenceEquals(Controller.Presentation, scene);
    internal static bool TryGetHistoricalPose(int slot, out KillCamPlayerPose pose)
    { pose = default; return !UseReplayKillcam && LegacyKillCam.TryGetHistoricalPose(slot, out pose); }
    internal static bool TryGetHistoricalCamera(out KillCamCameraPose camera)
    { camera = default; return !UseReplayKillcam && LegacyKillCam.TryGetHistoricalCamera(out camera); }
    internal static bool IsHistoricalCameraOwner(int slot) => false;
    internal static bool IsRecentFinalKill(uint kill, uint end) => KillcamController.FinalEligible(kill, end, timedEnd: true, causalEnd: false);
    internal static string WeaponName(int value) => value is >= sbyte.MinValue and <= sbyte.MaxValue
        && Enum.IsDefined(typeof(BeamType), (sbyte)value) ? ((BeamType)value).ToString().ToUpperInvariant() : "";
    internal static bool TryGetBanner(out bool final, out string killer, out string victim, out string weapon)
    {
        if (!UseReplayKillcam) return LegacyKillCam.TryGetBanner(out final, out killer, out victim, out weapon);
        final = IsFinal; killer = victim = weapon = "";
        if (Controller.Presentation is not { } scene || Controller.Playing is not { Kill: { } kill } marker) return false;
        killer = scene.GameState.Nicknames[kill.KillerSlot].ToUpperInvariant();
        victim = scene.GameState.Nicknames[kill.VictimSlot].ToUpperInvariant(); weapon = WeaponName(marker.Weapon);
        if (((DamageFlags)marker.DamageFlags & DamageFlags.Headshot) != 0) weapon += " · HEADSHOT";
        return true;
    }
    internal static void Reset()
    {
        if (!UseReplayKillcam) LegacyKillCam.Reset();
        Controller.Reset(KillcamEndReason.SceneClosed); _live = null; _finalRequested = false;
        _releaseFire = true; Interlocked.Exchange(ref _skipRequested, 0);
    }
}
