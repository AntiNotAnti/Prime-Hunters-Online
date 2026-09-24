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
    private static WorldBootstrapIdentity? _appliedBootstrap, _receivingBootstrap;
    private static readonly NetReplicationReceiver _bootstrapReceiver = new();
    private static readonly byte[] _bootstrapFast = new byte[1200];
    private static int _bootstrapFastLength;
    private static byte _bootstrapMask;
    private static readonly byte[] _bootstrapSlow = new byte[256], _bootstrapWorld = new byte[512];
    private static int _bootstrapSlowLength, _bootstrapWorldLength;
    public static bool WorldIsReady => ServerSession is not { } session || IsAuthority || IsHost || _playback
        || _appliedBootstrap is { } baseline && baseline.Start == StartIdentity(session)
            && baseline.SlotGeneration == NetPlayerLifecycle.Generation(LocalSlot);
    private static void HandleWorldBootstrap(ReceivedPacket packet)
    {
        if (!_hasRoster || ServerSession is not { } session || _loadedStart != StartIdentity(session)
            || !WorldBootstrapIdentity.TryRead(packet.Payload, out var identity)
            || identity.Start != StartIdentity(session)
            || identity.SlotGeneration != NetPlayerLifecycle.Generation(LocalSlot)) return;
        if (_appliedBootstrap == identity) { SendWorldReady(identity); return; }
        if (_appliedBootstrap is { } previous && previous.Start == identity.Start
            && !NetLifecycleTracker.Newer(identity.Revision, previous.Revision)) return;
        if (packet.Payload.Length <= WorldBootstrapIdentity.Size) return;
        byte lane = packet.Payload[WorldBootstrapIdentity.Size];
        if (lane > 2) return;
        if (_receivingBootstrap != identity)
        {
            if (_receivingBootstrap is { } receiving && receiving.Start == identity.Start
                && !NetLifecycleTracker.Newer(identity.Revision, receiving.Revision)) return;
            _receivingBootstrap = identity; _bootstrapMask = 0; _bootstrapFastLength = 0;
            _bootstrapReceiver.Reset(identity.Start.MatchId, identity.Start.AuthorityEpoch);
        }
        var laneData = packet.Payload[(WorldBootstrapIdentity.Size + 1)..];
        if (lane == 0)
        {
            if (laneData.Length < SnapshotHeader.Size || laneData.Length > _bootstrapFast.Length
                || SnapshotHeader.Read(laneData).Frame != identity.AuthorityFrame) return;
            if ((_bootstrapMask & 1) != 0 && !laneData.SequenceEqual(_bootstrapFast.AsSpan(0, _bootstrapFastLength))) return;
            laneData.CopyTo(_bootstrapFast); _bootstrapFastLength = laneData.Length;
        }
        else
        {
            var stored = lane == 1 ? _bootstrapSlow.AsSpan(0, _bootstrapSlowLength)
                : _bootstrapWorld.AsSpan(0, _bootstrapWorldLength);
            if ((_bootstrapMask & (1 << lane)) != 0)
            {
                if (!laneData.SequenceEqual(stored)) return;
            }
            else
            {
                if (!_bootstrapReceiver.Receive(lane == 1 ? PacketType.PlayerSlowState : PacketType.WorldState,
                    laneData, identity.Start.MatchId, identity.Start.AuthorityEpoch)) return;
                if (lane == 1) { laneData.CopyTo(_bootstrapSlow); _bootstrapSlowLength = laneData.Length; }
                else { laneData.CopyTo(_bootstrapWorld); _bootstrapWorldLength = laneData.Length; }
            }
        }
        _bootstrapMask |= (byte)(1 << lane);
        if (_bootstrapMask != 7 || _bootstrapReceiver.SlowRevision != identity.SlowRevision
            || _bootstrapReceiver.WorldRevision != identity.WorldRevision) return;
        int length = _bootstrapReceiver.Assemble(_bootstrapFast.AsSpan(0, _bootstrapFastLength),
            _laneCanonical.AsSpan(1), identity.Start.MatchId, identity.Start.AuthorityEpoch);
        if (length == 0) return;
        ReadOnlySpan<byte> payload = _laneCanonical.AsSpan(1, length);
        // Use the full-state decoder explicitly while frozen; ordinary snapshots
        // remain gated. No gameplay or simulation step runs in this path.
        _laneCanonical[0] = (byte)PacketType.Snapshot;
        var baseline = new ReceivedPacket(packet.Sender, _laneCanonical, 1 + length, packet.ArrivedAt);
        Array.Clear(RemoteStateValid); // Readiness requires acceptance from this exact baseline.
        HandleSnapshot(baseline, bootstrap: true);
        if (_lastSnapshotFrame != identity.AuthorityFrame || !RemoteStateValid[LocalSlot]) return;
        for (int slot = 0; slot < RemoteStates.Length; slot++)
            if (session.Phase == SessionPhase.Starting && (session.ExpectedParticipants & (1 << slot)) != 0 && !RemoteStateValid[slot]) return;
        var header = SnapshotHeader.Read(payload);
        for (int i = 0; i < header.PlayerCount; i++)
        {
            var state = PlayerState.Read(payload[(SnapshotHeader.Size + i * PlayerState.Size)..]);
            // A departed/replaced occupant may still occur in a cached late-join
            // baseline. Every occupant that still matches the current roster
            // must have accepted its state before this client can play.
            if (NetPlayerLifecycle.Generation(state.SlotIndex) == state.SlotGeneration
                && !RemoteStateValid[state.SlotIndex]) return;
        }
        if (PlayerEntity.Players.Count <= LocalSlot) return;
        NetSlotManager.Sync();
        for (int slot = 0; slot < PlayerEntity.Players.Count; slot++)
        {
            if (!RemoteStateValid[slot]) continue;
            var state = RemoteStates[slot]; var player = PlayerEntity.Players[slot];
            NetPlayerBridge.ApplyState(player, state, isLocal: false);
            bool alt = (state.Flags & PlayerState.FlagAltForm) != 0;
            if (state.Health > 0)
            {
                // A baseline is already the settled authoritative form. Build
                // form-owned entities, then finish without advancing animation.
                if (player.IsAltForm != alt && !player.IsMorphing && !player.IsUnmorphing)
                    player.ModStartFormSwitch();
                player.ModForceForm(alt);
                // EnterAltForm splits local health while creating its entity.
                // Restore both canonical values before acknowledging WorldReady.
                player.Health = state.Health;
                if (player.Hunter == Hunter.Weavel && player.Halfturret != null
                    && player.Flags2.TestFlag(PlayerFlags2.Halfturret))
                {
                    player.Halfturret.Health = state.HalfturretActive ? state.HalfturretHealth : 0;
                    if (!state.HalfturretActive) player.OnHalfturretDied();
                }
            }
            player.ModPlaceAt(state.Position); player.Speed = state.Speed;
            player.ModSetSpawnFacing(state.Facing);
            GameState.Points[slot] = state.Points; GameState.Kills[slot] = state.Kills; GameState.Deaths[slot] = state.Deaths;
        }
        NetHealthSync.ApplyBootstrap();
        // Spawn/form presentation may consume random values while applying.
        Rng.SetRng1(header.Rng1); Rng.SetRng2(header.Rng2);
        NoteStatesApplied();
        _laneReceiver.Reset(identity.Start.MatchId, identity.Start.AuthorityEpoch);
        // The same lanes seed live recovery; later fast packets cannot erase
        // the scoreboard or world state applied at the barrier.
        _laneReceiver.Receive(PacketType.PlayerSlowState, _bootstrapSlow.AsSpan(0, _bootstrapSlowLength), identity.Start.MatchId, identity.Start.AuthorityEpoch);
        _laneReceiver.Receive(PacketType.WorldState, _bootstrapWorld.AsSpan(0, _bootstrapWorldLength), identity.Start.MatchId, identity.Start.AuthorityEpoch);
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
            _replication.Prepare(_lastSnapshot.AsSpan(0, _lastSnapshotLength));
            peer.BootstrapIdentity = new(CurrentStartIdentity, _bootstrapRevision,
                _slotGenerations[peer.SlotIndex], header.Frame, _replication.SlowRevision, _replication.WorldRevision);
            for (int lane = 0; lane < 3; lane++)
            {
                peer.BootstrapIdentity.Write(peer.Bootstrap[lane]);
                peer.Bootstrap[lane][WorldBootstrapIdentity.Size] = (byte)lane;
                var data = lane == 0 ? _replication.Fast.AsSpan(0, _replication.FastLength)
                    : lane == 1 ? _replication.Slow.AsSpan(0, _replication.SlowLength)
                    : _replication.World.AsSpan(0, _replication.WorldLength);
                data.CopyTo(peer.Bootstrap[lane].AsSpan(WorldBootstrapIdentity.Size + 1));
                peer.BootstrapLengths[lane] = WorldBootstrapIdentity.Size + 1 + data.Length;
            }
            peer.BootstrapLength = 1;
        }
        peer.BootstrapSentAt = now;
        for (int lane = 0; lane < 3; lane++)
            _transport.Send(peer.EndPoint, PacketType.WorldBootstrap, peer.Bootstrap[lane].AsSpan(0, peer.BootstrapLengths[lane]));
    }
    private void HandleWorldReady(ReceivedPacket packet, double now)
    {
        var peer = Find(packet.Sender);
        if (peer == null || !peer.SceneLoaded || peer.BootstrapLength == 0
            || packet.Payload.Length != WorldBootstrapIdentity.Size
            || !WorldBootstrapIdentity.TryRead(packet.Payload, out var ready)
            || ready != peer.BootstrapIdentity || ready.Start != CurrentStartIdentity
            || ready.SlotGeneration != _slotGenerations[peer.SlotIndex]) return;
        peer.LastSeen = now;
        if (!peer.MatchReady) Log($"[lobby] slot {peer.SlotIndex} applied world revision {ready.Revision}, frame {ready.AuthorityFrame}");
        if (!peer.MatchReady) Telemetry.ProductionTelemetry.Emit(new(Telemetry.TelemetryEventType.Lifecycle, NetSession.NetFrame,
            Player: (byte)peer.SlotIndex, Result: 200, A: ready.AuthorityFrame));
        peer.MatchReady = true;
        if (_phase == SessionPhase.Starting && _start.MarkWorldReady(peer.SlotIndex, ready.Start))
        { TouchLobbyRevision($"slot {peer.SlotIndex} world ready"); CheckLoadBarrier(now); }
    }
    private void PumpBootstraps(double now)
    {
        foreach (var peer in _peers)
            if (peer.SceneLoaded && !peer.MatchReady && now - peer.BootstrapSentAt >= .25) SendBootstrap(peer, now);
    }
}
