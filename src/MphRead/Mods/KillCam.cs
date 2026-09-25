using System;
using System.Threading;
using MphRead.Entities;
using MphRead.Mods.Launcher;
using MphRead.Mods.Network;
using MphRead.Mods.Replay;

namespace MphRead.Mods;

internal enum KillCamKind { None, Personal, Final }

/// <summary>Foreground adapter. The instance controller owns historical scenes;
/// this adapter reads live lifecycle and routes presentation/input at the boundary.</summary>
internal static class KillCam
{
    private static readonly KillcamController Controller = new(ReplayCapture.Recorder.Timeline);
    private static Scene? _live;
    private static int _skipRequested;
    private static bool _releaseFire;
    private static bool _finalRequested;
    private static uint _finalRequestedFrame;
    public static bool Active => Controller.Active;
    public static bool IsPersonal => Controller.Kind == KillCamKind.Personal;
    public static bool IsFinal => Controller.Kind == KillCamKind.Final;
    public static uint PlaybackFrame => Controller.Frame;
    internal static float Progress => Controller.Progress;
    internal static KillcamState State => Controller.State;
    internal static KillcamEndReason EndReason => Controller.EndReason;
    internal static string? LastError => Controller.LastError;
    internal static string Diagnostics => $"{Controller.State}/{Controller.EndReason} · {Controller.StartupMilliseconds:0.0} ms · {Controller.ClipBytes / 1024d:0} KiB";

    private static KillcamContext Context(Scene? scene)
    {
        int local = NetHooks.LocalSlot;
        return new(NetSession.CurrentMatchId, NetSession.AuthorityEpoch, NetSession.NetFrame, local,
            NetPlayerLifecycle.Generation(local), NetPlayerLifecycle.Get(local),
            scene != null && (uint)local < (uint)scene.Players.Items.Count && scene.Players.Items[local] is { Health: > 0 },
            NetSession.Active && !DemoPlayback.IsActive,
            LauncherPrefs.KillCamEnabled && !Headless.Active && !SpectatorMode.IsSpectating,
            LauncherPrefs.FinalKillCamEnabled && !Headless.Active);
    }
    internal static void NoteKill(ReplayMarker marker, uint recordingFrame)
    {
        var context = Context(_live);
        if (marker.Kill is not { } identity || !KillcamController.IsValidIdentity(identity, context, _live)) return;
        bool enemy = true;
        if (_live?.GameState.Teams == true && marker.Kill is { } kill)
            enemy = _live.Players.Items[kill.KillerSlot].TeamIndex != _live.Players.Items[kill.VictimSlot].TeamIndex;
        Controller.NoteKill(marker, recordingFrame, context, enemy);
    }

    internal static void AfterSimulation(Scene scene)
    {
        _live = scene;
        if (Interlocked.Exchange(ref _skipRequested, 0) != 0) { Controller.Skip(); _releaseFire = true; }
        if (_finalRequested && scene.GameState.MatchTime <= 0)
        {
            var context = Context(scene);
            var world = ReplayCapture.LatestAuthorityWorld;
            bool matching = world != null && world.MatchId == context.MatchId && world.Epoch == context.Epoch;
            if (matching && world!.EndCause != ReplayEndCause.None)
            {
                _finalRequested = false;
                bool causal = world.EndCause == ReplayEndCause.Kill && world.EndingKill == Controller.Candidate?.Kill;
                Controller.BeginFinal(scene, context, _finalRequestedFrame, world.EndCause == ReplayEndCause.Time, causal);
            }
            else if (NetSession.NetFrame - _finalRequestedFrame >= 30)
            {
                _finalRequested = false;
                // Older protocol-16 servers have no world extension. Keep their
                // bounded terminal-clock fallback; new authorities must identify
                // the cause explicitly instead of selecting an unrelated kill.
                if (!matching)
                {
                    var game = scene.GameState;
                    bool causal = false;
                    if (Controller.Candidate?.Kill is { } kill && KillcamController.IsValidIdentity(kill, context, scene)
                        && (uint)game.ResultSlots[0] < (uint)scene.Players.Items.Count
                        && scene.Players.Items[game.ResultSlots[0]] != null)
                    {
                        int team = scene.Players.Items[kill.KillerSlot].TeamIndex;
                        bool winner = (uint)team < (uint)game.TeamPoints.Length
                            && scene.Players.Items[game.ResultSlots[0]].TeamIndex == team;
                        causal = winner && (game.Mode is GameMode.Survival or GameMode.SurvivalTeams
                            || game.Mode is GameMode.Battle or GameMode.BattleTeams or GameMode.InstaGib
                                && game.TeamPoints[team] >= game.PointGoal);
                    }
                    Controller.BeginFinal(scene, context, _finalRequestedFrame,
                        !causal && NetSession.ServerMatch is { TimeRemaining: <= 3.1f }, causal);
                }
            }
        }
        Controller.Update(scene, Context(scene) with { PersonalEnabled = Context(scene).PersonalEnabled && !_finalRequested
            && scene.GameState.MatchState != MatchState.GameOver });
    }
    internal static bool FinalPresentationPending => _finalRequested
        || IsFinal && Controller.State != KillcamState.AwaitCompletion;
    internal static bool BeginFinal(uint frame)
    {
        _finalRequested = NetSession.Active && LauncherPrefs.FinalKillCamEnabled;
        _finalRequestedFrame = NetSession.NetFrame; Controller.CancelPersonal(); return false;
    }
    internal static void EndFinal()
    {
        _finalRequested = false;
        if (IsFinal) Controller.Stop(KillcamEndReason.Completed);
    }
    internal static void FilterInput(Scene scene)
    {
        if (scene.Services.IsReplica) return;
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
        if (!Active) return false;
        Interlocked.Exchange(ref _skipRequested, 1); return true;
    }
    internal static Scene? Presentation(Scene live)
    {
        if (live.Services.IsReplica || !ReferenceEquals(live, _live)) return null;
        if (!NetSession.Active) { Controller.Reset(KillcamEndReason.Disconnected); return null; }
        if (Controller.Playing?.Kill is { } kill && (kill.MatchId != NetSession.CurrentMatchId || kill.AuthorityEpoch != NetSession.AuthorityEpoch))
        { Controller.Reset(KillcamEndReason.MatchChanged); return null; }
        try { Controller.Camera(live.Size); return Controller.Presentation; }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        { Controller.FailPresentation(ex); return null; }
    }
    internal static void FailPresentation(Exception error) => Controller.FailPresentation(error);
    internal static bool IsRecentFinalKill(uint kill, uint end) => KillcamController.FinalEligible(kill, end, timedEnd: true, causalEnd: false);
    internal static string WeaponName(int value) => value is >= sbyte.MinValue and <= sbyte.MaxValue
        && Enum.IsDefined(typeof(BeamType), (sbyte)value) ? ((BeamType)value).ToString().ToUpperInvariant() : "";
    internal static void Reset()
    {
        Controller.Reset(KillcamEndReason.SceneClosed); _live = null; _finalRequested = false;
        _releaseFire = true; Interlocked.Exchange(ref _skipRequested, 0);
    }
}
