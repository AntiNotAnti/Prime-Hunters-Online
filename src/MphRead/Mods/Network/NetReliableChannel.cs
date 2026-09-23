using System;

namespace MphRead.Mods.Network;

public readonly record struct NetReliableSnapshot(int Pending, int HighWater, long Refused, long ReserveUses,
    long SpanRefused, long Retransmissions, long Duplicates, double? OldestAgeMilliseconds, bool Failed);
public readonly record struct ReliableTransmission(uint EventId, PacketType Type, ReadOnlyMemory<byte> Payload);

/// <summary>Bounded, unordered, exactly-once event delivery within one live connection.
/// Application state streams still validate revisions. The sender never advances
/// beyond the receiver's dedup window while an older event is pending.</summary>
public sealed class NetReliableChannel
{
    public const int OrdinaryCapacity = 32, Capacity = 40, History = 256;
    public const double LifetimeMilliseconds = 15000;
    private sealed class PendingEvent
    {
        public uint Id;
        public PacketType Type;
        public byte[] Payload = null!;
        public double Created, Due, Rto = 150;
        public int Attempts;
        public bool Critical;
    }
    private readonly PendingEvent?[] _pending = new PendingEvent?[Capacity];
    private readonly uint[] _received = new uint[History];
    private readonly bool[] _valid = new bool[History];
    private uint _next = 1, _latest;
    private bool _seen;
    private int _count, _ordinary, _high;
    private long _refused, _reserve, _spanRefused, _retransmissions, _duplicates;
    public bool Failed { get; private set; }
    public bool HasPending(PacketType type)
    { foreach (var pending in _pending) if (pending?.Type == type) return true; return false; }
    public static bool IsReliable(PacketType type) => type is PacketType.Welcome or PacketType.SessionState
        or PacketType.Roster or PacketType.MapChange or PacketType.Authority or PacketType.LobbyCommand
        or PacketType.LobbyCommandResult or PacketType.MatchLoaded or PacketType.MatchLoadFailed or PacketType.Refused
        or PacketType.Bye or PacketType.MatchEnd;
    public static bool IsCritical(PacketType type) => type is not (PacketType.Roster or PacketType.LobbyCommand or PacketType.LobbyCommandResult);

    public bool TryQueue(PacketType type, ReadOnlySpan<byte> payload, double nowMs, out uint eventId)
    {
        eventId = 0;
        if (!IsReliable(type) || payload.Length > NetConfig.MaxPayloadSize - 4)
            throw new ArgumentException("Not a bounded reliable control payload");
        bool critical = IsCritical(type);
        if (Failed) return false;
        // Periodic publication may repeat an identical outstanding state. Keep
        // its event identity, without replacing or coalescing distinct events.
        foreach (var pending in _pending)
            if (pending != null && pending.Type == type && payload.SequenceEqual(pending.Payload))
            { eventId = pending.Id; return true; }
        bool spanFull = false;
        foreach (var pending in _pending)
            if (pending != null && SequenceMath.Distance(_next, pending.Id) >= History) spanFull = true;
        if (_count >= Capacity || !critical && _ordinary >= OrdinaryCapacity || spanFull)
        {
            _refused++; if (spanFull) _spanRefused++;
            if (critical) Failed = true;
            return false;
        }
        for (int i = 0; i < _pending.Length; i++) if (_pending[i] == null)
        {
            eventId = _next++;
            _pending[i] = new PendingEvent { Id = eventId, Type = type, Payload = payload.ToArray(), Created = nowMs, Due = nowMs, Critical = critical };
            if (!critical) _ordinary++;
            _count++; _high = Math.Max(_high, _count); if (critical && _count > OrdinaryCapacity) _reserve++;
            return true;
        }
        throw new InvalidOperationException("Reliable capacity accounting");
    }
    public bool TrySend(double nowMs, out ReliableTransmission transmission)
    {
        transmission = default;
        if (Failed) return false;
        for (int i = 0; i < _pending.Length; i++)
        {
            var pending = _pending[i];
            if (pending == null) continue;
            if (nowMs - pending.Created >= LifetimeMilliseconds) { Failed = true; return false; }
            if (pending.Due > nowMs) continue;
            if (pending.Attempts++ > 0) _retransmissions++;
            pending.Due = nowMs + pending.Rto; pending.Rto = Math.Min(1200, pending.Rto * 2);
            transmission = new(pending.Id, pending.Type, pending.Payload);
            return true;
        }
        return false;
    }
    public void Acknowledge(uint eventId)
    {
        for (int i = 0; i < _pending.Length; i++) if (_pending[i]?.Id == eventId)
        {
            if (!_pending[i]!.Critical) _ordinary--;
            _pending[i] = null; _count--; return;
        }
    }
    public bool AlreadyReceived(uint id) => _valid[id % History] && _received[id % History] == id
        || _seen && !SequenceMath.Newer(id, _latest) && SequenceMath.Distance(_latest, id) >= History;
    public bool Receive(uint id)
    {
        if (AlreadyReceived(id)) { _duplicates++; return false; }
        if (!_seen || SequenceMath.Newer(id, _latest)) { _seen = true; _latest = id; }
        _valid[id % History] = true; _received[id % History] = id;
        return true;
    }
    public NetReliableSnapshot Capture(double nowMs)
    {
        double? oldest = null;
        foreach (var pending in _pending) if (pending != null) oldest = Math.Max(oldest ?? 0, nowMs - pending.Created);
        return new(_count, _high, _refused, _reserve, _spanRefused, _retransmissions, _duplicates, oldest, Failed);
    }
}
