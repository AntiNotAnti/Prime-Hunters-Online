using System;
using System.Collections.Generic;
using System.Linq;

namespace MphRead.Mods.Network;

public interface IReplayTimeline
{
    uint? FirstRecordingFrame { get; }
    uint? LastRecordingFrame { get; }
    bool TryGetRestorePoint(uint frame, out ReplayRestorePoint? restorePoint);
    bool TryFreeze(uint startFrame, uint endFrame, out ReplayTimelineClip? clip);
    bool TryMapServerTickToRecordingFrame(uint tick, out uint frame);
    bool TryMapKillToRecordingFrame(ReplayKillIdentity kill, out uint frame);
}

public enum ReplayFactKind { Match, Roster, Snapshot, Intent, World, Event, Presentation, AuthorityWorld }
// A network baseline is deliberately NOT advertised as a complete scene checkpoint.
// It cannot restore in-flight projectiles or animation, and must not enable passive killcams.
public enum ReplayRestoreKind { NetworkBaseline, ReplicaCheckpoint }
public enum ReplayMarkerKind
{
    Kill, Death, Spawn, Damage, Headshot, Score, Objective, FlagCapture,
    NodeCapture, PrimeChange, MatchPoint, Overtime, MatchEnd, Join, Leave, MatchStart, WeaponFired
}

public readonly record struct ReplayKillIdentity(ushort MatchId, ulong AuthorityEpoch,
    uint ServerTick, ushort EventId, byte KillerSlot, ushort KillerGeneration,
    byte VictimSlot, ushort VictimGeneration, ushort VictimLifeId);
public readonly record struct ReplayMarker(ReplayMarkerKind Kind, byte Actor, byte Target,
    int Value = 0, ReplayKillIdentity? Kill = null, byte Weapon = byte.MaxValue, byte DamageFlags = 0);

/// <summary>Detached, immutable accepted fact. Payload never exposes its backing array.</summary>
public readonly struct ReplayTimelineRecord
{
    private readonly ReplayPayload? _payload;
    public uint RecordingFrame { get; }
    public uint ServerTick { get; }
    public ReplayFactKind Kind { get; }
    public ReplayMarker? Marker { get; }
    public ReadOnlySpan<byte> Payload => _payload == null ? ReadOnlySpan<byte>.Empty : _payload.Span;
    // Include descriptor/list overhead so empty-payload floods are bounded too.
    public long PayloadBytes => (_payload?.Capacity ?? 0) + 128;
    public ReplayTimelineRecord(uint frame, uint serverTick, ReplayFactKind kind,
        ReadOnlySpan<byte> payload, ReplayMarker? marker = null)
    {
        RecordingFrame = frame; ServerTick = serverTick; Kind = kind;
        _payload = payload.IsEmpty ? null : ReplayPayload.Copy(payload); Marker = marker;
    }
    internal ReplayTimelineRecord(uint frame, uint tick, ReplayFactKind kind, ReplayPayload payload)
    { RecordingFrame = frame; ServerTick = tick; Kind = kind; Marker = null; _payload = payload; }
    internal void Retain() => _payload?.Retain();
    internal void Release() => _payload?.Release();
    internal bool SameFact(ReplayTimelineRecord other) => ReferenceEquals(_payload, other._payload)
        && RecordingFrame == other.RecordingFrame && ServerTick == other.ServerTick && Kind == other.Kind && Marker == other.Marker;
}

public sealed class ReplayRestorePoint : IDisposable
{
    public uint RecordingFrame { get; }
    public uint ServerTick { get; }
    public ReplayRestoreKind Kind { get; }
    public IReadOnlyList<ReplayTimelineRecord> Records { get; }
    public long PayloadBytes { get; }
    public ReplayRestorePoint(uint frame, uint tick, ReplayRestoreKind kind,
        IEnumerable<ReplayTimelineRecord> records)
    {
        var copy = records.ToArray();
        if (copy.Length == 0 || copy.Any(r => r.RecordingFrame > frame))
            throw new ArgumentException("A restore point requires non-future baseline records.", nameof(records));
        RecordingFrame = frame; ServerTick = tick; Kind = kind;
        foreach (var record in copy) record.Retain();
        Records = Array.AsReadOnly(copy);
        PayloadBytes = 128 + copy.Sum(r => r.PayloadBytes);
    }
    private bool _disposed;
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var record in Records) record.Release();
        GC.SuppressFinalize(this);
    }
    ~ReplayRestorePoint() => Dispose();
}

public sealed class ReplayTimelineClip : IDisposable
{
    public ReplayRestorePoint RestorePoint { get; }
    // Includes warmup facts between the baseline and the requested visible start.
    public IReadOnlyList<ReplayTimelineRecord> Records { get; }
    public uint StartRecordingFrame { get; }
    public uint EndRecordingFrame { get; }
    internal ReplayTimelineClip(ReplayRestorePoint restore, ReplayTimelineRecord[] records, uint start, uint end)
    {
        RestorePoint = new(restore.RecordingFrame, restore.ServerTick, restore.Kind, restore.Records);
        foreach (var record in records) record.Retain();
        Records = Array.AsReadOnly(records);
        StartRecordingFrame = start; EndRecordingFrame = end;
    }
    private bool _disposed;
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; RestorePoint.Dispose();
        foreach (var record in Records) record.Release();
        GC.SuppressFinalize(this);
    }
    ~ReplayTimelineClip() => Dispose();
}
