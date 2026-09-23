using System;
using System.Collections.Generic;

namespace MphRead.Mods.Network;

/// <summary>Single simulation-thread owner for accepted authoritative facts.</summary>
internal sealed class ReplayRecorder
{
    public RollingReplayTimeline Timeline { get; } = new();
    internal bool ProducesWorldCheckpoints { get; set; }
    internal event Action<ReplayTimelineRecord>? Accepted;
    internal event Action? Resetting;
    internal event Action<ReplayTimelineRecord>? CheckpointCaptured;
    private readonly ReplayAuthorityWire _worldWire = new();
    private ReplayTimelineRecord? _match, _roster, _snapshot, _configuration;
    private readonly ReplayTimelineRecord?[] _intents = new ReplayTimelineRecord?[RosterPacket.MaxSlots];
    private ushort _matchId;
    private ulong _epoch;
    private string? _room;
    private uint _lastRestore;
    public void Reset()
    {
        Resetting?.Invoke();
        _worldWire.Reset();
        Timeline.Reset();
        _match?.Release(); _roster?.Release(); _snapshot?.Release(); _configuration?.Release();
        foreach (var intent in _intents) intent?.Release();
        _match = _roster = _snapshot = _configuration = null;
        Array.Clear(_intents);
        _matchId = 0; _epoch = 0; _room = null; _lastRestore = 0;
    }
    public void AcceptMatch(in MatchStatePacket match, uint frame)
    {
        if (_matchId != match.MatchId || _epoch != match.AuthorityEpoch || _room != match.RoomKey) Reset();
        _matchId = match.MatchId; _epoch = match.AuthorityEpoch; _room = match.RoomKey;
        Span<byte> bytes = stackalloc byte[1 + MatchStatePacket.Size];
        bytes[0] = (byte)PacketType.MatchState; match.Write(bytes[1..]);
        _match?.Release();
        _match = new(frame, Timeline.LastServerTick ?? frame, ReplayFactKind.Match, bytes);
        Publish(_match.Value);
    }
    public void AcceptRoster(in RosterPacket roster, uint frame)
    {
        if (_matchId != roster.MatchId || _epoch != roster.AuthorityEpoch) return;
        Span<byte> bytes = stackalloc byte[1 + RosterPacket.Size];
        bytes[0] = (byte)PacketType.Roster; roster.Write(bytes[1..]);
        _roster?.Release();
        _roster = new(frame, Timeline.LastServerTick ?? frame, ReplayFactKind.Roster, bytes);
        Publish(_roster.Value);
    }
    public void AcceptConfiguration(in SessionStatePacket configuration, uint frame)
    {
        if (_matchId != configuration.MatchId || _epoch != configuration.AuthorityEpoch) return;
        Span<byte> bytes = stackalloc byte[1 + SessionStatePacket.Size];
        bytes[0] = (byte)PacketType.SessionState; configuration.Write(bytes[1..]);
        _configuration?.Release();
        _configuration = new(frame, Timeline.LastServerTick ?? frame, ReplayFactKind.Match, bytes);
        Publish(_configuration.Value);
    }
    // Remote callers enter after lifecycle/order acceptance. A local caller records
    // the submitted input for presentation, never a hit or a damage decision.
    public void AcceptIntent(int slot, in IntentPacket intent, uint frame)
    {
        if ((uint)slot >= (uint)_intents.Length || intent.MatchId != _matchId
            || intent.AuthorityEpoch != _epoch || intent.SlotGeneration == 0 || intent.LifeId == 0) return;
        if (_intents[slot] is { } prior)
        {
            var old = IntentPacket.Read(prior.Payload[2..]);
            if (old.SlotGeneration == intent.SlotGeneration && old.LifeId == intent.LifeId
                && !NetLifecycleTracker.Newer(intent.Frame, old.Frame)) return;
        }
        Span<byte> bytes = stackalloc byte[2 + IntentPacket.FullSize];
        bytes[0] = (byte)PacketType.SlotIntent; bytes[1] = (byte)slot;
        intent.Write(bytes[2..]);
        var record = new ReplayTimelineRecord(frame, Timeline.LastServerTick ?? frame, ReplayFactKind.Intent, bytes);
        _intents[slot]?.Release();
        _intents[slot] = record;
        Publish(record);
    }
    public void AcceptSnapshot(ReadOnlySpan<byte> packet, uint frame, uint tick)
    {
        if (packet.Length < 1 + SnapshotHeader.Size || packet[0] != (byte)PacketType.Snapshot) return;
        var header = SnapshotHeader.Read(packet[1..]);
        if (header.PlayerCount > RosterPacket.MaxSlots
            || packet.Length < 1 + SnapshotHeader.Size + header.PlayerCount * PlayerState.Size) return;
        if (_matchId == 0 || header.MatchId != _matchId || header.AuthorityEpoch != _epoch || header.Frame != tick) return;
        _snapshot?.Release();
        _snapshot = new(frame, tick, ReplayFactKind.Snapshot, packet);
        if (!ProducesWorldCheckpoints && (Timeline.NeedsRestorePoint || frame - _lastRestore >= 300))
        {
            if (_match != null && _roster != null)
            {
                var records = new List<ReplayTimelineRecord> { _match.Value, _roster.Value, _snapshot.Value };
                if (_configuration != null) records.Insert(0, _configuration.Value);
                // Preserve held input only for an occupant/life actually present
                // in this baseline. Old firing state must not cross a respawn.
                for (int i = 0; i < header.PlayerCount; i++)
                {
                    var player = PlayerState.Read(packet[(1 + SnapshotHeader.Size + i * PlayerState.Size)..]);
                    if (player.SlotIndex >= _intents.Length || _intents[player.SlotIndex] is not { } record) continue;
                    var intent = IntentPacket.Read(record.Payload[2..]);
                    if (intent.SlotGeneration == player.SlotGeneration && intent.LifeId == player.LifeId)
                        records.Add(record);
                }
                if (Timeline.AppendRestorePoint(new(frame, tick, ReplayRestoreKind.NetworkBaseline, records)))
                    _lastRestore = frame;
            }
        }
        // Keep the snapshot in the sequential stream too: a clip starting from an
        // earlier baseline must not omit the snapshot at a later index boundary.
        Publish(_snapshot.Value);
    }
    internal void AcceptWorldPacket(ReadOnlySpan<byte> payload, uint frame)
    {
        if (_worldWire.Accept(payload, _matchId, _epoch) is { } world) AcceptWorld(world, frame);
    }
    internal void AcceptWorld(ReplayAuthorityWorld world, uint frame)
        => AcceptWorld(world, frame, world.Encode());
    internal void AcceptWorld(ReplayAuthorityWorld world, uint frame, ReadOnlySpan<byte> encoded, bool send = false)
    {
        if (world.MatchId != _matchId || world.Epoch != _epoch) return;
        Span<byte> packet = stackalloc byte[NetConfig.MaxPacketSize];
        int parts = (encoded.Length + ReplayAuthorityWire.PartBytes - 1) / ReplayAuthorityWire.PartBytes;
        for (int part = 0; part < parts; part++)
        {
            int length = ReplayAuthorityWire.WritePacket(world, encoded, part, packet);
            PublishTransient(new(frame, world.Tick, ReplayFactKind.AuthorityWorld, packet[..length]));
            if (send) NetSession.SendReplayWorldPacket(packet.Slice(1, length - 1));
        }
    }
    public void Marker(uint frame, uint tick, ReplayMarker marker)
    {
        PublishTransient(new(frame, tick, ReplayFactKind.Event, ReadOnlySpan<byte>.Empty, marker));
    }

    internal void SeedWorld(Action<ReplayTimelineRecord> accept)
    {
        if (_match is { } match) accept(match);
        if (_configuration is { } configuration) accept(configuration);
        if (_roster is { } roster) accept(roster);
        if (_snapshot is { } snapshot) accept(snapshot);
        foreach (var intent in _intents) if (intent is { } record) accept(record);
    }

    private void PublishTransient(ReplayTimelineRecord record)
    { try { Publish(record); } finally { record.Release(); } }

    private void Publish(ReplayTimelineRecord record)
    {
        using (ReplayPerfTelemetry.Measure(ReplayPerfOperation.Timeline))
            if (!Timeline.NeedsRestorePoint) Timeline.Append(record);
        Accepted?.Invoke(record);
    }

    internal bool AppendWorldCheckpoint(uint frame, uint tick, ReplayPayload payload)
    {
        payload.Retain();
        return AppendWorldCheckpoint(new ReplayTimelineRecord(frame, tick, ReplayFactKind.World, payload));
    }
    internal bool AppendWorldCheckpoint(uint frame, uint tick, ReadOnlySpan<byte> bytes)
        => AppendWorldCheckpoint(new ReplayTimelineRecord(frame, tick, ReplayFactKind.World, bytes));
    private bool AppendWorldCheckpoint(ReplayTimelineRecord world)
    {
        if (_match == null || _roster == null || _snapshot == null) { world.Release(); return false; }
        uint frame = world.RecordingFrame, tick = world.ServerTick;
        // Packet baselines remain available to metadata/compatibility tools. Only
        // the World record is allowed to restore a replica scene.
        var records = new List<ReplayTimelineRecord> { _match.Value, _roster.Value, _snapshot.Value };
        if (_configuration != null) records.Insert(0, _configuration.Value);
        records.Add(world);
        try
        {
            if (!Timeline.AppendRestorePoint(new(frame, tick, ReplayRestoreKind.ReplicaCheckpoint, records))) return false;
            CheckpointCaptured?.Invoke(world); return true;
        }
        finally { world.Release(); }
    }
}
