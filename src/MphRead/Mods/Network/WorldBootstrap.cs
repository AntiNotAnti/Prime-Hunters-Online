using System;
using System.Buffers.Binary;
using MphRead.Entities;

namespace MphRead.Mods.Network;

// Echoed verbatim by WorldReady, including the recipient incarnation.
public readonly record struct WorldBootstrapIdentity(MatchStartIdentity Start, uint Revision,
    ushort SlotGeneration, uint AuthorityFrame, uint SlowRevision = 1, uint WorldRevision = 1)
{
    public const int Size = 32;
    public void Write(Span<byte> dest)
    {
        new MatchLoadedPacket(Start.MatchId, Start.AuthorityEpoch, Start.StartGeneration).Write(dest);
        BinaryPrimitives.WriteUInt32LittleEndian(dest[14..], Revision);
        BinaryPrimitives.WriteUInt16LittleEndian(dest[18..], SlotGeneration);
        BinaryPrimitives.WriteUInt32LittleEndian(dest[20..], AuthorityFrame);
        BinaryPrimitives.WriteUInt32LittleEndian(dest[24..], SlowRevision);
        BinaryPrimitives.WriteUInt32LittleEndian(dest[28..], WorldRevision);
    }
    public static bool TryRead(ReadOnlySpan<byte> src, out WorldBootstrapIdentity value)
    {
        value = default;
        if (src.Length < Size || !MatchLoadedPacket.TryRead(src[..14], out var start)) return false;
        value = new(start.Identity, BinaryPrimitives.ReadUInt32LittleEndian(src[14..]),
            BinaryPrimitives.ReadUInt16LittleEndian(src[18..]), BinaryPrimitives.ReadUInt32LittleEndian(src[20..]),
            BinaryPrimitives.ReadUInt32LittleEndian(src[24..]), BinaryPrimitives.ReadUInt32LittleEndian(src[28..]));
        return value.Revision != 0 && value.SlotGeneration != 0 && value.AuthorityFrame != 0
            && value.SlowRevision != 0 && value.WorldRevision != 0;
    }
}

public static partial class NetSession
{
    private static WorldBootstrapIdentity? _appliedBootstrap;
    public static bool WorldIsReady => ServerSession is not { } session || IsAuthority || IsHost || _playback
        || _appliedBootstrap is { } baseline && baseline.Start == StartIdentity(session)
            && baseline.SlotGeneration == NetPlayerLifecycle.Generation(LocalSlot);
    private static void HandleWorldBootstrap(ReceivedPacket packet)
    {
        if (ServerSession is not { } session || _loadedStart != StartIdentity(session)
            || !WorldBootstrapIdentity.TryRead(packet.Payload, out var identity)
            || identity.Start != StartIdentity(session)
            || identity.SlotGeneration != NetPlayerLifecycle.Generation(LocalSlot)) return;
        if (_appliedBootstrap == identity) { SendWorldReady(identity); return; }
        if (_appliedBootstrap is { } previous && previous.Start == identity.Start
            && !NetLifecycleTracker.Newer(identity.Revision, previous.Revision)) return;
        var payload = packet.Payload[WorldBootstrapIdentity.Size..];
        if (payload.Length < SnapshotHeader.Size || SnapshotHeader.Read(payload).Frame != identity.AuthorityFrame) return;
        // Use the full-state decoder explicitly while frozen; ordinary snapshots
        // remain gated. No gameplay or simulation step runs in this path.
        byte[] data = new byte[1 + payload.Length]; data[0] = (byte)PacketType.Snapshot;
        payload.CopyTo(data.AsSpan(1));
        var baseline = new ReceivedPacket(packet.Sender, data, data.Length, packet.ArrivedAt);
        HandleSnapshot(baseline, bootstrap: true);
        if (_lastSnapshotFrame != identity.AuthorityFrame || !RemoteStateValid[LocalSlot]) return;
        for (int slot = 0; slot < RemoteStates.Length; slot++)
            if (SlotOccupied[slot] && !RemoteStateValid[slot]) return;
        if (PlayerEntity.Players.Count <= LocalSlot) return;
        NetSlotManager.Sync();
        NetHooks.ApplyRemoteStates();
        for (int slot = 0; slot < PlayerEntity.Players.Count; slot++)
        {
            if (!RemoteStateValid[slot]) continue;
            var state = RemoteStates[slot]; var player = PlayerEntity.Players[slot];
            player.ModPlaceAt(state.Position); player.Speed = state.Speed;
            player.ModSetSpawnFacing(state.Facing);
            GameState.Points[slot] = state.Points; GameState.Kills[slot] = state.Kills; GameState.Deaths[slot] = state.Deaths;
        }
        _appliedBootstrap = identity;
        SendWorldReady(identity);
    }
    private static void SendWorldReady(WorldBootstrapIdentity identity)
    {
        if (_hostEndPoint == null) return;
        identity.Write(_scratch);
        _transport?.Send(_hostEndPoint, PacketType.WorldReady, _scratch.AsSpan(0, WorldBootstrapIdentity.Size));
    }
}

public sealed partial class DedicatedServer
{
    private uint _bootstrapRevision;
    private void SendBootstrap(Peer peer, double now)
    {
        if (_transport == null || !peer.SceneLoaded || peer.MatchReady) return;
        if (peer.BootstrapLength == 0)
        {
            if (_sim != null)
            {
                NetSlotManager.Sync();
                foreach (var player in PlayerEntity.Players) player.ModBootstrapSpawn();
                NetSession.BroadcastSnapshot();
            }
            if (_lastSnapshotLength == 0) return;
            var header = SnapshotHeader.Read(_lastSnapshot);
            if (header.Frame == 0 || header.MatchId != _matchId || header.AuthorityEpoch != _authorityEpoch) return;
            if (++_bootstrapRevision == 0) ++_bootstrapRevision;
            peer.BootstrapIdentity = new(CurrentStartIdentity, _bootstrapRevision,
                _slotGenerations[peer.SlotIndex], header.Frame);
            peer.BootstrapIdentity.Write(peer.Bootstrap);
            _lastSnapshot.AsSpan(0, _lastSnapshotLength).CopyTo(peer.Bootstrap.AsSpan(WorldBootstrapIdentity.Size));
            peer.BootstrapLength = WorldBootstrapIdentity.Size + _lastSnapshotLength;
        }
        peer.BootstrapSentAt = now;
        _transport.Send(peer.EndPoint, PacketType.WorldBootstrap, peer.Bootstrap.AsSpan(0, peer.BootstrapLength));
    }
    private void HandleWorldReady(ReceivedPacket packet, double now)
    {
        var peer = Find(packet.Sender);
        if (peer == null || !peer.SceneLoaded || peer.BootstrapLength == 0
            || packet.Payload.Length != WorldBootstrapIdentity.Size
            || !WorldBootstrapIdentity.TryRead(packet.Payload, out var ready)
            || ready != peer.BootstrapIdentity || ready.Start != CurrentStartIdentity
            || ready.SlotGeneration != _slotGenerations[peer.SlotIndex]) return;
        peer.LastSeen = now; peer.MatchReady = true;
        if (_phase == SessionPhase.Starting && _start.MarkWorldReady(peer.SlotIndex, ready.Start))
        { TouchLobbyRevision($"slot {peer.SlotIndex} world ready"); CheckLoadBarrier(now); }
    }
    private void PumpBootstraps(double now)
    {
        foreach (var peer in _peers)
            if (peer.SceneLoaded && !peer.MatchReady && now - peer.BootstrapSentAt >= .25) SendBootstrap(peer, now);
    }
}
