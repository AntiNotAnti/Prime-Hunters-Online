using System;
using System.Collections.Generic;

namespace MphRead.Mods.Network;

/// <summary>Single simulation-thread owner for accepted authoritative facts.</summary>
internal sealed class ReplayRecorder
{
    public RollingReplayTimeline Timeline { get; } = new();
    private ReplayTimelineRecord? _match, _roster, _snapshot, _configuration;
    private readonly ReplayTimelineRecord?[] _intents = new ReplayTimelineRecord?[RosterPacket.MaxSlots];
    private ushort _matchId;
    private ulong _epoch;
    private string? _room;
    private uint _lastRestore;
    public void Reset()
    {
        Timeline.Reset(); _match = _roster = _snapshot = _configuration = null;
        Array.Clear(_intents);
        _matchId = 0; _epoch = 0; _room = null; _lastRestore = 0;
    }
    public void AcceptMatch(in MatchStatePacket match, uint frame)
    {
        if (_matchId != match.MatchId || _epoch != match.AuthorityEpoch || _room != match.RoomKey) Reset();
        _matchId = match.MatchId; _epoch = match.AuthorityEpoch; _room = match.RoomKey;
        byte[] bytes = new byte[1 + MatchStatePacket.Size];
        bytes[0] = (byte)PacketType.MatchState; match.Write(bytes.AsSpan(1));
        _match = new(frame, Timeline.LastServerTick ?? frame, ReplayFactKind.Match, bytes);
        if (!Timeline.NeedsRestorePoint) Timeline.Append(_match);
    }
    public void AcceptRoster(in RosterPacket roster, uint frame)
    {
        if (_matchId != roster.MatchId || _epoch != roster.AuthorityEpoch) return;
        byte[] bytes = new byte[1 + RosterPacket.Size];
        bytes[0] = (byte)PacketType.Roster; roster.Write(bytes.AsSpan(1));
        _roster = new(frame, Timeline.LastServerTick ?? frame, ReplayFactKind.Roster, bytes);
        if (!Timeline.NeedsRestorePoint) Timeline.Append(_roster);
    }
    public void AcceptConfiguration(in SessionStatePacket configuration, uint frame)
    {
        if (_matchId != configuration.MatchId || _epoch != configuration.AuthorityEpoch) return;
        byte[] bytes = new byte[1 + SessionStatePacket.Size];
        bytes[0] = (byte)PacketType.SessionState; configuration.Write(bytes.AsSpan(1));
        _configuration = new(frame, Timeline.LastServerTick ?? frame, ReplayFactKind.Match, bytes);
        if (!Timeline.NeedsRestorePoint) Timeline.Append(_configuration);
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
        byte[] bytes = new byte[2 + IntentPacket.FullSize];
        bytes[0] = (byte)PacketType.SlotIntent; bytes[1] = (byte)slot;
        intent.Write(bytes.AsSpan(2));
        var record = new ReplayTimelineRecord(frame, Timeline.LastServerTick ?? frame, ReplayFactKind.Intent, bytes);
        _intents[slot] = record;
        if (!Timeline.NeedsRestorePoint) Timeline.Append(record);
    }
    public void AcceptSnapshot(ReadOnlySpan<byte> packet, uint frame, uint tick)
    {
        if (packet.Length < 1 + SnapshotHeader.Size || packet[0] != (byte)PacketType.Snapshot) return;
        var header = SnapshotHeader.Read(packet[1..]);
        if (header.PlayerCount > RosterPacket.MaxSlots
            || packet.Length < 1 + SnapshotHeader.Size + header.PlayerCount * PlayerState.Size) return;
        if (_matchId == 0 || header.MatchId != _matchId || header.AuthorityEpoch != _epoch || header.Frame != tick) return;
        _snapshot = new(frame, tick, ReplayFactKind.Snapshot, packet);
        if (Timeline.NeedsRestorePoint || frame - _lastRestore >= 300)
        {
            if (_match != null && _roster != null)
            {
                var records = new List<ReplayTimelineRecord> { _match, _roster, _snapshot };
                if (_configuration != null) records.Insert(0, _configuration);
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
        if (!Timeline.NeedsRestorePoint) Timeline.Append(_snapshot);
    }
    public void Marker(uint frame, uint tick, ReplayMarker marker)
    {
        if (!Timeline.NeedsRestorePoint)
            Timeline.Append(new(frame, tick, ReplayFactKind.Event, ReadOnlySpan<byte>.Empty, marker));
    }
}
