using System;
using System.Collections.Generic;

namespace MphRead.Mods.Network;

/// <summary>Single simulation-thread owner for accepted authoritative facts.</summary>
internal sealed class ReplayRecorder
{
    public RollingReplayTimeline Timeline { get; } = new();
    private ReplayTimelineRecord? _match, _roster, _snapshot;
    private ushort _matchId;
    private ulong _epoch;
    private string? _room;
    private uint _lastRestore;
    public void Reset()
    {
        Timeline.Reset(); _match = _roster = _snapshot = null;
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
    public void AcceptSnapshot(ReadOnlySpan<byte> packet, uint frame, uint tick)
    {
        if (packet.Length < 1 + SnapshotHeader.Size || packet[0] != (byte)PacketType.Snapshot) return;
        var header = SnapshotHeader.Read(packet[1..]);
        if (_matchId == 0 || header.MatchId != _matchId || header.AuthorityEpoch != _epoch || header.Frame != tick) return;
        _snapshot = new(frame, tick, ReplayFactKind.Snapshot, packet);
        if (Timeline.NeedsRestorePoint || frame - _lastRestore >= 300)
        {
            if (_match != null && _roster != null)
            {
                var records = new List<ReplayTimelineRecord> { _match, _roster, _snapshot };
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
