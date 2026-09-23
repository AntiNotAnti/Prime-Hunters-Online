using System;
using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;

namespace MphRead.Mods.Network;

public static class SequenceMath
{
    public static bool Newer(uint value, uint previous) => unchecked((int)(value - previous)) > 0;
    public static uint Distance(uint newer, uint older) => unchecked(newer - older);
}

public enum SequenceResult { New, Reordered, Duplicate, TooOld }

public struct NetReceiveWindow
{
    public bool Initialized { get; private set; }
    public uint Latest { get; private set; }
    public uint Bits { get; private set; }
    public SequenceResult Observe(uint sequence)
    {
        if (!Initialized) { Initialized = true; Latest = sequence; return SequenceResult.New; }
        if (sequence == Latest) return SequenceResult.Duplicate;
        if (SequenceMath.Newer(sequence, Latest))
        {
            uint distance = SequenceMath.Distance(sequence, Latest);
            Bits = distance > 32 ? 0 : distance == 32 ? 1u << 31 : (Bits << (int)distance) | (1u << ((int)distance - 1));
            Latest = sequence;
            return SequenceResult.New;
        }
        uint age = SequenceMath.Distance(Latest, sequence);
        if (age > 32) return SequenceResult.TooOld;
        uint bit = 1u << ((int)age - 1);
        if ((Bits & bit) != 0) return SequenceResult.Duplicate;
        Bits |= bit;
        return SequenceResult.Reordered;
    }
    public static bool Acknowledges(uint sequence, uint ack, uint bits) => sequence == ack
        || (SequenceMath.Distance(ack, sequence) is >= 1 and <= 32
            && (bits & (1u << ((int)SequenceMath.Distance(ack, sequence) - 1))) != 0);
}

[Flags]
public enum NetHeaderFlags : byte { None = 0, AckValid = 1, Reliable = 2, AckOnly = 4 }
public readonly record struct NetHeader(PacketType Type, NetHeaderFlags Flags, ulong ConnectionId,
    uint Sequence, uint Ack, uint AckBits)
{
    public const byte Marker = 0xD7;
    public const int Size = 24;
    public void Write(Span<byte> destination)
    {
        destination[0] = Marker; destination[1] = (byte)Type; destination[2] = (byte)Flags;
        destination[3] = NetConfig.ProtocolVersion;
        BinaryPrimitives.WriteUInt64LittleEndian(destination[4..], ConnectionId);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[12..], Sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[16..], Ack);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[20..], AckBits);
    }
    public static bool TryRead(ReadOnlySpan<byte> source, out NetHeader header)
    {
        header = default;
        if (source.Length < Size || source[0] != Marker || source[3] != NetConfig.ProtocolVersion
            || (source[2] & ~7) != 0) return false;
        header = new((PacketType)source[1], (NetHeaderFlags)source[2], BinaryPrimitives.ReadUInt64LittleEndian(source[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(source[12..]), BinaryPrimitives.ReadUInt32LittleEndian(source[16..]),
            BinaryPrimitives.ReadUInt32LittleEndian(source[20..]));
        return header.ConnectionId != 0 && ((header.Flags & NetHeaderFlags.AckOnly) == 0 || source.Length == Size);
    }
}

public readonly record struct NetConnectionSnapshot(ulong ConnectionId, long Sent, long Acknowledged,
    long EstimatedLost, long Duplicates, long Reordered, long TooOld, double? RttMilliseconds,
    double? RttJitterMilliseconds, double? MinimumRttMilliseconds);

/// <summary>Bound to one endpoint/incarnation; caller serializes all access. No payload ownership.</summary>
public sealed class NetConnection
{
    private struct Attempt { public uint Sequence; public double SentAt; public bool Pending; public uint? EventId; }
    private readonly Attempt[] _attempts = new Attempt[512];
    private NetReceiveWindow _received;
    private uint _nextSequence;
    private long _sent, _acked, _lost, _duplicates, _reordered, _old;
    private double? _rtt, _variance, _minimum;
    private readonly double[] _recentRtt = new double[64];
    private int _recentCount, _recentCursor;
    private NetTokenBucket _intentBudget, _stateBudget, _controlBudget, _backgroundBudget;
    public bool Allow(PacketType type, double nowMs) => type == PacketType.Intent
        ? _intentBudget.Take(nowMs, 180, 64)
        : NetPacketQueue.Priority(type) == NetPacketPriority.Realtime ? _stateBudget.Take(nowMs, 2000, 512)
        : NetPacketQueue.Priority(type) == NetPacketPriority.Critical ? _controlBudget.Take(nowMs, 60, 128)
        : _backgroundBudget.Take(nowMs, 100, 32);
    public NetReliableChannel Reliable { get; } = new();
    public bool AckPending { get; set; }
    public bool FailureReported { get; set; }
    public double? RetiredAt { get; set; }
    public ulong Id { get; }
    public IPEndPoint Endpoint { get; }
    public uint ClientId { get; }
    public NetConnection(IPEndPoint endpoint, ulong id, uint clientId = 0, uint initialSequence = 0)
    {
        if (id == 0) throw new ArgumentOutOfRangeException(nameof(id));
        Endpoint = new IPEndPoint(endpoint.Address, endpoint.Port); Id = id; ClientId = clientId; _nextSequence = initialSequence;
    }
    public static ulong NewId()
    {
        Span<byte> bytes = stackalloc byte[8]; ulong id;
        do { RandomNumberGenerator.Fill(bytes); id = BinaryPrimitives.ReadUInt64LittleEndian(bytes); } while (id == 0);
        return id;
    }
    public NetHeader Send(PacketType type, double nowMs, NetHeaderFlags flags = NetHeaderFlags.None, uint? eventId = null)
    {
        uint sequence = _nextSequence++;
        ref Attempt previous = ref _attempts[sequence % (uint)_attempts.Length];
        if (previous.Pending) _lost++; // bounded-window estimate, not a claim of certain wire loss
        previous = new() { Sequence = sequence, SentAt = nowMs, Pending = true, EventId = eventId };
        _sent++; AckPending = false;
        return new(type, flags | (_received.Initialized ? NetHeaderFlags.AckValid : 0), Id, sequence, _received.Latest, _received.Bits);
    }
    public bool Accepts(IPEndPoint sender, in NetHeader header) => header.ConnectionId == Id && Endpoint.Equals(sender);
    public SequenceResult Receive(in NetHeader header, double nowMs)
    {
        if (header.ConnectionId != Id) throw new ArgumentException("Wrong connection");
        if ((header.Flags & NetHeaderFlags.AckValid) != 0)
        {
            Acknowledge(header.Ack, nowMs);
            for (int i = 0; i < 32; i++) if ((header.AckBits & (1u << i)) != 0) Acknowledge(unchecked(header.Ack - (uint)i - 1), nowMs);
        }
        var result = _received.Observe(header.Sequence);
        if (result == SequenceResult.Duplicate) _duplicates++;
        if (result == SequenceResult.Reordered) _reordered++;
        if (result == SequenceResult.TooOld) _old++;
        return result;
    }
    private void Acknowledge(uint sequence, double nowMs)
    {
        ref Attempt attempt = ref _attempts[sequence % (uint)_attempts.Length];
        if (!attempt.Pending || attempt.Sequence != sequence) return;
        attempt.Pending = false; _acked++;
        if (attempt.EventId.HasValue) Reliable.Acknowledge(attempt.EventId.Value);
        double sample = Math.Max(0, nowMs - attempt.SentAt);
        _recentRtt[_recentCursor++ % _recentRtt.Length] = sample;
        _recentCursor %= _recentRtt.Length;
        _recentCount = Math.Min(_recentCount + 1, _recentRtt.Length);
        double minimum = sample;
        for (int i = 0; i < _recentCount; i++) minimum = Math.Min(minimum, _recentRtt[i]);
        _minimum = minimum;
        _variance = _rtt.HasValue ? .75 * _variance!.Value + .25 * Math.Abs(sample - _rtt.Value) : sample / 2;
        _rtt = _rtt.HasValue ? .875 * _rtt.Value + .125 * sample : sample;
    }
    public NetConnectionSnapshot Capture() => new(Id, _sent, _acked, _lost, _duplicates, _reordered, _old, _rtt, _variance, _minimum);
}
