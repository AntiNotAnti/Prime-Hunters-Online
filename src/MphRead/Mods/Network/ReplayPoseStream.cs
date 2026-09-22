using System;
using System.Collections.Generic;
using System.IO;
using MphRead.Entities;
using MphRead.Mods.Replay;
using OpenTK.Mathematics;

namespace MphRead.Mods.Network;

/// <summary>A bounded presentation cursor over accepted snapshots. It can look
/// ahead six recorded frames without advancing the simulation, a socket or RNG.
/// No smoothing decision depends on arrival wall time or monitor refresh.</summary>
internal sealed class ReplayPoseStream : IDisposable
{
    private readonly PassiveReplayScene _world;
    private readonly string? _path;
    private readonly ReplayTimelineClip? _clip;
    private readonly ReplayReplicaState _decoder = new();
    private readonly List<(uint Frame, PlayerState State)>[] _poses = new List<(uint, PlayerState)>[8];
    private DemoReader? _reader;
    private DemoRecord? _pending;
    private int _index;
    private uint? _advanced;
    private bool _initialized, _failed;
    internal string? LastError { get; private set; }
    internal ReplayPoseStream(PassiveReplayScene world, string path) : this(world) { _path = path; }
    internal ReplayPoseStream(PassiveReplayScene world, ReplayTimelineClip clip) : this(world) { _clip = clip; }
    private ReplayPoseStream(PassiveReplayScene world)
    {
        _world = world;
        for (int i = 0; i < 8; i++) _poses[i] = new(24);
    }
    internal bool Sample(int slot, float alpha, out Vector3 position, out Vector3 facing)
    {
        position = facing = default;
        if (_failed || (uint)slot >= 8) return false;
        try { Advance(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
        { LastError = ex.Message; _failed = true; return false; }
        double frame = Math.Max(0, _world.Session.RecordingFrame - 1d + alpha);
        var samples = _poses[slot];
        if (samples.Count == 0) return false;
        int left = 0;
        while (left + 1 < samples.Count && samples[left + 1].Frame <= frame) left++;
        var a = samples[left];
        PlayerState value = a.State;
        position = value.Position; facing = value.Facing;
        if (left + 1 < samples.Count)
        {
            var b = samples[left + 1];
            if (b.Frame > a.Frame && b.Frame - a.Frame <= 12 && CanBlend(a.State, b.State))
            {
                float t = (float)Math.Clamp((frame - a.Frame) / (b.Frame - a.Frame), 0, 1);
                position = Vector3.Lerp(a.State.Position, b.State.Position, t);
                var rotation = Quaternion.Slerp(ReplayCameraTrack.FacingRotation(a.State.Facing),
                    ReplayCameraTrack.FacingRotation(b.State.Facing), t);
                facing = Vector3.Transform(-Vector3.UnitZ, rotation);
            }
        }
        // Draw history must never borrow a new occupant, life, death or form.
        if (!_world.State.TryGetPlayer(slot, out var current) || !SameLife(value, current)) return false;
        var actor = _world.Scene.Players.Items[slot];
        position = _world.Scene.PlayerReplication.InFormFor(actor, position, (value.Flags & PlayerState.FlagAltForm) != 0);
        return true;
    }
    private static bool SameLife(PlayerState a, PlayerState b) => a.SlotGeneration == b.SlotGeneration
        && a.LifeId == b.LifeId && (a.Health > 0) == (b.Health > 0)
        && (a.Flags & (PlayerState.FlagActive | PlayerState.FlagSpawned | PlayerState.FlagAltForm))
            == (b.Flags & (PlayerState.FlagActive | PlayerState.FlagSpawned | PlayerState.FlagAltForm));
    internal static bool CanBlend(PlayerState a, PlayerState b) => SameLife(a, b)
        && (a.Position - b.Position).LengthSquared <= 16;
    private void Advance()
    {
        uint frame = _world.Session.RecordingFrame;
        if (_advanced == frame) return;
        _advanced = frame;
        uint origin = _world.Session.Metadata?.OriginRecordingFrame ?? 0;
        if (!_initialized)
        {
            _initialized = true;
            _decoder.RestoreCheckpoint(_world.State.CaptureCheckpoint()); _decoder.Rewind();
            uint from = frame > 12 ? frame - 12 : 0;
            if (_path != null)
            {
                _reader = DemoReader.Open(_path, out var result) ?? throw new InvalidDataException(result.ToString());
                _pending = from > origin ? _reader.SeekAfter(from - origin - 1) : _reader.ReadNext();
            }
            else if (_clip != null)
            {
                foreach (var baseline in _clip.RestorePoint.Records)
                    if (baseline.Kind == ReplayFactKind.Snapshot) Accept(baseline.RecordingFrame, baseline.Payload);
                while (_index < _clip.Records.Count && _clip.Records[_index].RecordingFrame < from) _index++;
            }
        }
        int read = 0;
        while (true)
        {
            uint next;
            if (_reader != null && _pending is { } packet)
            {
                next = checked(origin + packet.Frame);
                if (next > (ulong)frame + 6) break;
                Accept(next, packet.Data); _pending = _reader.ReadNext();
            }
            else if (_clip != null && _index < _clip.Records.Count)
            {
                var record = _clip.Records[_index]; next = record.RecordingFrame;
                if (next > (ulong)frame + 6) break;
                if (record.Kind is ReplayFactKind.Match or ReplayFactKind.Roster or ReplayFactKind.Snapshot)
                    Accept(next, record.Payload);
                _index++;
            }
            else break;
            if (++read > 8192) throw new InvalidDataException("Replay presentation lookahead exceeds its record bound.");
        }
        for (int slot = 0; slot < 8; slot++)
        {
            var list = _poses[slot];
            if ((list.Count == 0 || list[0].Frame > frame) && _world.State.TryGetPlayer(slot, out var state))
                list.Insert(0, (frame, state));
            while (list.Count > 2 && ((ulong)list[1].Frame + 12 < frame || list.Count > 24)) list.RemoveAt(0);
        }
    }
    private void Accept(uint frame, ReadOnlySpan<byte> packet)
    {
        if (packet.Length == 0 || packet[0] is 253 or 254 or 255) return;
        long accepted = _decoder.AcceptedPackets;
        _decoder.Accept(packet, frame);
        if (packet[0] != (byte)PacketType.Snapshot || accepted == _decoder.AcceptedPackets) return;
        for (int i = 0; i < 8; i++)
            if (_decoder.TryGetPlayer(i, out var state))
            {
                var list = _poses[i];
                if (list.Count > 0 && list[^1].Frame == frame) list[^1] = (frame, state);
                else list.Add((frame, state));
                if (list.Count > 24) list.RemoveAt(0);
            }
    }
    public void Dispose() { _reader?.Dispose(); _reader = null; }
}
