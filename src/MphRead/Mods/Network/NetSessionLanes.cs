using System;
namespace MphRead.Mods.Network;
public static partial class NetSession
{
    private static readonly NetReplicationReceiver _laneReceiver = new();
    private static readonly NetReplicationLanes _hostLanes = new();
    private static readonly byte[] _laneCanonical = new byte[NetConfig.MaxPacketSize + 1];
    private static void HandleLane(ReceivedPacket packet)
    {
        if (packet.Type != PacketType.SnapshotFast)
        {
            _laneReceiver.Receive(packet.Type, packet.Payload, CurrentMatchId, AuthorityEpoch);
            return;
        }
        if (FreezeGameplay) return;
        int length = _laneReceiver.Assemble(packet.Payload, _laneCanonical.AsSpan(1), CurrentMatchId, AuthorityEpoch);
        if (length == 0) return;
        _laneCanonical[0] = (byte)PacketType.Snapshot;
        HandleSnapshot(new ReceivedPacket(packet.Sender, _laneCanonical, length + 1, packet.ArrivedAt));
    }
    private static void SendHostLanes(System.Net.IPEndPoint endpoint)
    {
        void Send(PacketType type, byte[] bytes, int length)
        { _transport!.Send(endpoint, type, bytes.AsSpan(0, length)); NetReplicationLanes.Count(type, length); }
        Send(PacketType.SnapshotFast, _hostLanes.Fast, _hostLanes.FastLength);
        if (_hostLanes.SendSlow) Send(PacketType.PlayerSlowState, _hostLanes.Slow, _hostLanes.SlowLength);
        if (_hostLanes.SendWorld) Send(PacketType.WorldState, _hostLanes.World, _hostLanes.WorldLength);
    }
}
