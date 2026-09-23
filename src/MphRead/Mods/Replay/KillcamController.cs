using System;
using System.IO;
using System.Diagnostics;
using System.Linq;
using MphRead.Entities;
using MphRead.Formats;
using MphRead.Mods.Network;
using OpenTK.Mathematics;

namespace MphRead.Mods.Replay;

internal enum KillcamState { None, Preparing, Replay, AwaitCompletion }
internal enum KillcamEndReason { None, Completed, Skipped, Respawn, MatchChanged, Disconnected, Disabled, SceneClosed, InvalidIdentity, Unavailable, Failed }
internal readonly record struct KillcamContext(ushort MatchId, ulong Epoch, uint Frame, int LocalSlot,
    ushort LocalGeneration, ushort LocalLife, bool LocalAlive, bool Connected, bool PersonalEnabled, bool FinalEnabled);

/// <summary>Timeline and private-scene lifetime for personal/final presentation.
/// The caller keeps live simulation running and supplies authoritative lifecycle state.</summary>
internal sealed class KillcamController : IDisposable
{
    private readonly IReplayTimeline _timeline;
    private readonly Func<ReplayTimelineClip, Vector2i, PassiveReplayPlayer> _open;
    private PassiveReplayPlayer? _player;
    private ReplayMarker? _candidate, _pending, _playing;
    private uint _candidateFrame, _pendingFrame, _start, _end;
    private ReplayTimelineClip? _finalClip, _personalClip;
    private int _hold;
    private bool _skipArmed;
    private ulong _audio;
    private Scene? _live;
    private KillcamHud? _hud;
    private readonly Stopwatch _startup = new();
    internal double StartupMilliseconds { get; private set; }
    internal long ClipBytes { get; private set; }
    internal KillcamState State { get; private set; }
    internal KillcamEndReason EndReason { get; private set; }
    internal KillCamKind Kind { get; private set; }
    internal string? LastError { get; private set; }
    internal bool Active => _player != null;
    internal bool Visible => _player?.Ready == true && State is KillcamState.Replay or KillcamState.AwaitCompletion;
    internal Scene? Presentation => Visible ? _player!.Current.Scene : null;
    internal uint Frame => _player?.Current.Session.CurrentFrame ?? 0;
    internal float Progress => !Active || _end <= _start ? 1 : Math.Clamp((Frame - _start) / (float)(_end - _start), 0, 1);
    internal ReplayMarker? Playing => _playing;
    internal ReplayMarker? Candidate => _candidate;
    internal PassiveReplayScene? Replica => _player?.Current;

    internal KillcamController(IReplayTimeline timeline, Func<ReplayTimelineClip, Vector2i, PassiveReplayPlayer>? open = null)
    { _timeline = timeline; _open = open ?? ((clip, size) => new PassiveReplayPlayer(clip, size)); }

    internal void NoteKill(ReplayMarker marker, uint frame, KillcamContext context, bool enemy = true)
    {
        if (marker.Kill is not { } kill || !Matches(kill, context) || kill.KillerGeneration == 0
            || kill.VictimGeneration == 0 || kill.VictimLifeId == 0 || kill.KillerSlot >= 8
            || kill.VictimSlot >= 8 || kill.KillerSlot == kill.VictimSlot) return;
        if (enemy) { _candidate = marker; _candidateFrame = frame; _finalClip?.Dispose(); _finalClip = null; }
        if (context.PersonalEnabled && context.LocalSlot == kill.VictimSlot
            && context.LocalGeneration == kill.VictimGeneration && context.LocalLife == kill.VictimLifeId)
        { _pending = marker; _pendingFrame = frame; }
    }

    internal void Update(Scene live, KillcamContext context)
    {
        _live = live;
        try
        {
            if (!context.Connected) { Reset(KillcamEndReason.Disconnected); return; }
            if (_candidate?.Kill is { } candidate && !Matches(candidate, context))
            { Reset(KillcamEndReason.MatchChanged); return; }
            if (_candidate != null && _finalClip == null) _finalClip = Freeze(_candidateFrame, 300);
            if (_pending?.Kill is { } pending)
            {
                if (!Matches(pending, context)) { _pending = null; EndReason = KillcamEndReason.MatchChanged; }
                else if (!context.PersonalEnabled) { _pending = null; EndReason = KillcamEndReason.Disabled; }
                else if (context.LocalSlot != pending.VictimSlot || context.LocalGeneration != pending.VictimGeneration
                    || context.LocalLife != pending.VictimLifeId || context.LocalAlive)
                { _pending = null; EndReason = KillcamEndReason.Respawn; }
                else
                {
                    var clip = Freeze(_pendingFrame, 120);
                    if (clip != null) { Start(live, clip, _pending.Value, KillCamKind.Personal); _pending = null; }
                    else if (context.Frame > _pendingFrame + 30) { _pending = null; EndReason = KillcamEndReason.Unavailable; }
                }
            }
            if (_player == null || _playing?.Kill is not { } playing) return;
            if (!Matches(playing, context)) { Stop(KillcamEndReason.MatchChanged); return; }
            if (Kind == KillCamKind.Personal)
            {
                if (!context.PersonalEnabled) { Stop(KillcamEndReason.Disabled); return; }
                if (context.LocalSlot != playing.VictimSlot || context.LocalGeneration != playing.VictimGeneration
                    || context.LocalLife != playing.VictimLifeId || context.LocalAlive)
                { Stop(KillcamEndReason.Respawn); return; }
            }
            else if (!context.FinalEnabled) { Stop(KillcamEndReason.Disabled); return; }
            _player.Update();
            if (!_player.Ready) { State = KillcamState.Preparing; return; }
            if (_startup.IsRunning) { _startup.Stop(); StartupMilliseconds = _startup.Elapsed.TotalMilliseconds; }
            if (_audio == 0) _audio = ReplayAudioOwner.Acquire(_player.Current.Scene, live);
            State = _player.Current.Session.AtEnd ? KillcamState.AwaitCompletion : KillcamState.Replay;
            if (State == KillcamState.AwaitCompletion && Kind == KillCamKind.Personal && ++_hold >= 15)
            { Stop(KillcamEndReason.Completed); return; }
            Camera(live.Size);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        { LastError = ex.Message; _pending = null; Stop(KillcamEndReason.Failed); }
    }

    private ReplayTimelineClip? Freeze(uint death, uint preRoll)
    {
        if (_timeline.FirstRecordingFrame is not uint first || _timeline.LastRecordingFrame is not uint last || death > last) return null;
        uint start = Math.Max(first, death > preRoll ? death - preRoll : 0);
        return _timeline.TryFreeze(start, death, out var clip) && clip?.RestorePoint.Kind == ReplayRestoreKind.ReplicaCheckpoint ? clip : null;
    }

    internal bool BeginFinal(Scene live, KillcamContext context, uint endFrame, bool timedEnd, bool causalEnd)
    {
        Stop(KillcamEndReason.None); _pending = null;
        if (!context.FinalEnabled || _candidate is not { Kill: { } kill } marker || !Matches(kill, context)
            || !FinalEligible(_candidateFrame, endFrame, timedEnd, causalEnd)) return false;
        _finalClip ??= Freeze(_candidateFrame, 300);
        if (_finalClip == null) { EndReason = KillcamEndReason.Unavailable; return false; }
        try { Start(live, _finalClip, marker, KillCamKind.Final); return true; }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        { LastError = ex.Message; Stop(KillcamEndReason.Failed); return false; }
    }
    internal static bool FinalEligible(uint killFrame, uint endFrame, bool timedEnd, bool causalEnd)
        => endFrame >= killFrame && (causalEnd && endFrame - killFrame <= 120 || timedEnd && endFrame - killFrame <= 480);

    private void Start(Scene live, ReplayTimelineClip clip, ReplayMarker marker, KillCamKind kind)
    {
        Stop(KillcamEndReason.None);
        _startup.Restart(); StartupMilliseconds = 0;
        ClipBytes = clip.RestorePoint.PayloadBytes + clip.Records.Sum(r => r.PayloadBytes);
        if (kind == KillCamKind.Personal) _personalClip = clip;
        _player = _open(clip, live.Size); _playing = marker; Kind = kind; _live = live;
        _player.Current.Scene.ReplayPresentationHud = DrawHud;
        _start = clip.StartRecordingFrame; _end = clip.EndRecordingFrame; _hold = 0; _skipArmed = false;
        _player.Transport.SetPlaybackRate(kind == KillCamKind.Final ? 2 : 1);
        State = KillcamState.Preparing; EndReason = KillcamEndReason.None; LastError = null;
    }

    internal bool Input(bool down, bool pressed)
    {
        if (!Active && _pending == null) return false;
        if (!down) _skipArmed = true;
        if (_skipArmed && pressed) { _pending = null; Stop(KillcamEndReason.Skipped); }
        return true;
    }
    internal bool Skip()
    {
        if (!Active && _pending == null) return false;
        _pending = null; Stop(KillcamEndReason.Skipped); return true;
    }
    internal void Camera(Vector2i size)
    {
        if (!Visible || _playing?.Kill is not { } kill) return;
        var world = _player!.Current;
        int slot = world.State.Occupant(kill.KillerSlot).Generation == kill.KillerGeneration ? kill.KillerSlot
            : world.State.Occupant(kill.VictimSlot).Generation == kill.VictimGeneration ? kill.VictimSlot : -1;
        if (slot < 0) { Stop(KillcamEndReason.InvalidIdentity); return; }
        Scene scene = world.Scene;
        scene.ReplayRenderAlpha = Render.FrameTiming.Active
            ? _player.Transport.PresentationAlpha(Render.FrameTiming.PresentationAlpha) : 1;
        if (scene.Size != size) { scene.Size = size; scene.OnResize(); }
        PlayerEntity actor = scene.Players.Items[slot];
        Vector3 facing = actor.FacingVector;
        if (scene.ReplayPoses?.Sample(actor.SlotIndex, scene.ReplayRenderAlpha, out _, out var replicaFacing) == true)
            facing = replicaFacing;
        if (facing.LengthSquared < .0001f) facing = Vector3.UnitZ;
        facing = facing.Normalized();
        Vector3 focus = actor.ReplayDrawTransform.Row3.Xyz + Vector3.UnitY * (actor.IsAltForm ? .6f : 1.2f);
        Vector3 right = Vector3.Cross(facing, Vector3.UnitY);
        if (right.LengthSquared < .0001f) right = Vector3.UnitX;
        Vector3 camera = focus - facing * 3.2f + right.Normalized() * .9f + Vector3.UnitY * .8f;
        CollisionResult collision = default;
        if (CollisionDetection.CheckBetweenPoints(focus, camera, TestFlags.Players, scene, ref collision))
            camera = focus + (camera - focus) * Math.Max(.05f, collision.Distance - .05f);
        scene.SetReplicaCamera(camera, focus + facing * 5, 82);
    }
    internal void DrawHud(Scene scene)
    {
        if (!ReferenceEquals(scene, Presentation) || _playing is not { Kill: { } kill } marker) return;
        _hud ??= new KillcamHud(scene);
        string killer = scene.GameState.Nicknames[kill.KillerSlot].ToUpperInvariant();
        string victim = scene.GameState.Nicknames[kill.VictimSlot].ToUpperInvariant();
        string weapon = KillCam.WeaponName(marker.Weapon);
        if (((DamageFlags)marker.DamageFlags & DamageFlags.Headshot) != 0) weapon += "  HEADSHOT";
        _hud.Draw(Kind == KillCamKind.Final ? "FINAL KILL" : "KILL CAM",
            Kind == KillCamKind.Final ? killer + " > " + victim : "KILLED BY " + killer, weapon, Progress);
    }
    internal void FailPresentation(Exception error)
    { LastError = error.Message; Stop(KillcamEndReason.Failed); }
    private static bool Matches(ReplayKillIdentity kill, KillcamContext context) => kill.MatchId == context.MatchId && kill.AuthorityEpoch == context.Epoch;
    internal void Stop(KillcamEndReason reason)
    {
        ReplayAudioOwner.Release(_audio); _audio = 0;
        _startup.Stop();
        _hud = null; _player?.Dispose(); _player = null; _personalClip?.Dispose(); _personalClip = null; _playing = null; State = KillcamState.None; Kind = KillCamKind.None;
        EndReason = reason;
        if (_live != null && _live.Players.Items.Count > 0)
        { _live.Players.Main.Controls.ClearAll(); _live.Players.Main.ModForgetInputDeltas(); }
    }
    internal void Reset(KillcamEndReason reason)
    { Stop(reason); _pending = _candidate = null; _finalClip?.Dispose(); _finalClip = null; }
    public void Dispose() => Reset(KillcamEndReason.SceneClosed);
}
