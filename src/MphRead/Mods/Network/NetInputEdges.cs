using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace MphRead.Mods.Network;

public struct InputEdgeHistory
{
    private uint _0, _1, _2, _3, _4, _5, _6, _7;
    public const int Capacity = 16;
    public readonly int Length => Capacity;
    public ushort this[int index]
    {
        readonly get
        {
            uint word = (index / 2) switch { 0 => _0, 1 => _1, 2 => _2, 3 => _3, 4 => _4, 5 => _5, 6 => _6, 7 => _7, _ => throw new ArgumentOutOfRangeException(nameof(index)) };
            return (ushort)(word >> ((index & 1) * 16));
        }
        set
        {
            int shift = (index & 1) * 16;
            uint mask = ~(65535u << shift), bits = (uint)value << shift;
            switch (index / 2)
            {
                case 0: _0 = (_0 & mask) | bits; break; case 1: _1 = (_1 & mask) | bits; break;
                case 2: _2 = (_2 & mask) | bits; break; case 3: _3 = (_3 & mask) | bits; break;
                case 4: _4 = (_4 & mask) | bits; break; case 5: _5 = (_5 & mask) | bits; break;
                case 6: _6 = (_6 & mask) | bits; break; case 7: _7 = (_7 & mask) | bits; break;
                default: throw new ArgumentOutOfRangeException(nameof(index));
            }
        }
    }
    public static ushort Encode(byte sequence, IntentButtons action, int age)
    {
        uint bits = (uint)action;
        if (bits == 0 || (bits & (bits - 1)) != 0 || age is < 0 or > 7) throw new ArgumentOutOfRangeException(nameof(action));
        int id = BitOperations.TrailingZeroCount(bits) + 1;
        if (id > 31) throw new ArgumentOutOfRangeException(nameof(action));
        return (ushort)(sequence | (age << 8) | (id << 11));
    }
    public static int Action(ushort entry) => entry >> 11;
    public static int Age(ushort entry) => (entry >> 8) & 7;
}

public static class NetInputEdgeTelemetry
{
    public static long Overflow, Sent, Recovered, Duplicate, TooOld;
}

/// <summary>Eight-frame retention in the existing 32-byte budget, oldest first.</summary>
public sealed class NetInputEdgeSender
{
    private readonly ushort[] _events = new ushort[16];
    private readonly uint[] _frames = new uint[16];
    private int _count;
    private byte _sequence;
    // Movement/boost are held; zoom and weapon choice are absolute. Roll edges
    // are needed by the engine to establish its camera-relative roll basis.
    public const IntentButtons Actions = IntentButtons.Shoot | IntentButtons.Jump | IntentButtons.Morph
        | IntentButtons.AltAttack | IntentButtons.ScanVisor | IntentButtons.RollLeft | IntentButtons.RollRight
        | IntentButtons.RollUp | IntentButtons.RollDown;
    public void Reset() { _count = 0; _sequence = 0; }
    public InputEdgeHistory Record(uint frame, IntentButtons pressed)
    {
        int count = 0;
        for (int i = 0; i < _count; i++)
            if (frame - _frames[i] < 8) { _events[count] = _events[i]; _frames[count++] = _frames[i]; }
        _count = count;
        uint bits = (uint)(pressed & Actions);
        while (bits != 0)
        {
            uint bit = bits & (0u - bits); bits &= ~bit;
            if (_count == 16)
            {
                // Prefer the oldest repeated action, retaining newest events.
                int drop = 0;
                for (int i = 0; i < _count; i++)
                    if (InputEdgeHistory.Action(_events[i]) == BitOperations.TrailingZeroCount(bit) + 1) { drop = i; break; }
                for (int i = drop; i < 15; i++) { _events[i] = _events[i + 1]; _frames[i] = _frames[i + 1]; }
                _count--; NetInputEdgeTelemetry.Overflow++;
            }
            _events[_count] = InputEdgeHistory.Encode(unchecked(++_sequence), (IntentButtons)bit, 0);
            _frames[_count++] = frame; NetInputEdgeTelemetry.Sent++;
        }
        InputEdgeHistory history = default;
        for (int i = 0; i < _count; i++) history[i] = (ushort)(_events[i] | ((frame - _frames[i]) << 8));
        return history;
    }
}

/// <summary>Sequence dedup plus a bounded execution queue: two recovered edges of
/// one action are delivered on separate simulation frames, never OR-collapsed.</summary>
public sealed class NetInputEdgeReceiver
{
    private bool _seen;
    private byte _latest;
    private ulong _received;
    private readonly byte[] _actions = new byte[128];
    private readonly uint[] _sources = new uint[128];
    private int _count;
    public void Reset() { _seen = false; _received = 0; _count = 0; }
    private bool New(byte sequence)
    {
        if (!_seen) { _seen = true; _latest = sequence; _received = 1; return true; }
        int forward = unchecked((sbyte)(sequence - _latest));
        if (forward > 0)
        { _received = forward >= 64 ? 1 : (_received << forward) | 1; _latest = sequence; return true; }
        int behind = unchecked((byte)(_latest - sequence));
        if (behind >= 64) { NetInputEdgeTelemetry.TooOld++; return false; }
        ulong bit = 1UL << behind;
        if ((_received & bit) != 0) { NetInputEdgeTelemetry.Duplicate++; return false; }
        _received |= bit; return true;
    }
    public void Receive(in InputEdgeHistory history, uint frame)
    {
        for (int i = 0; i < history.Length; i++)
        {
            ushort entry = history[i]; int action = InputEdgeHistory.Action(entry);
            if (action == 0 || (NetInputEdgeSender.Actions & (IntentButtons)(1u << (action - 1))) == 0) continue;
            if (!New((byte)entry)) continue;
            if (_count == _actions.Length) { NetInputEdgeTelemetry.Overflow++; continue; }
            _actions[_count] = (byte)action; _sources[_count++] = unchecked(frame - (uint)InputEdgeHistory.Age(entry));
            if (InputEdgeHistory.Age(entry) > 0) NetInputEdgeTelemetry.Recovered++;
        }
    }
    public IntentButtons Consume(uint frame, out int shootAge)
    {
        IntentButtons result = 0; shootAge = 0; int retained = 0;
        for (int i = 0; i < _count; i++)
        {
            IntentButtons action = (IntentButtons)(1u << (_actions[i] - 1));
            if ((result & action) != 0) { _actions[retained] = _actions[i]; _sources[retained++] = _sources[i]; continue; }
            result |= action;
            if (action == IntentButtons.Shoot) shootAge = (int)Math.Min(unchecked(frame - _sources[i]), 127u);
        }
        _count = retained; return result;
    }
}
