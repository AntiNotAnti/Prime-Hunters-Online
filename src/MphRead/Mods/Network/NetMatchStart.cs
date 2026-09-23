using System;

namespace MphRead.Mods.Network;

public enum StartStage : byte { None, Preparing, Loading, Countdown, InMatch }
public readonly record struct MatchStartIdentity(ushort MatchId, ulong AuthorityEpoch, uint StartGeneration);

/// <summary>Server-owned load barrier. Virtual time is in seconds; participants
/// freeze at Begin and can only be removed, never enlarged, during that attempt.</summary>
public sealed class NetMatchStart
{
    public MatchStartIdentity Identity { get; private set; }
    public StartStage Stage { get; private set; }
    public byte Expected { get; private set; }
    public byte Loaded { get; private set; }
    public double LoadDeadline { get; private set; }
    public double CountdownDeadline { get; private set; }
    public const double CountdownSeconds = 1.5, LoadTimeoutSeconds = 15;
    private uint _generation;
    public void Begin(ushort match, ulong epoch, byte participants)
    {
        _generation = unchecked(_generation + 1); if (_generation == 0) _generation = 1;
        Identity = new(match, epoch, _generation);
        Expected = participants; Loaded = 0; Stage = StartStage.Preparing;
        LoadDeadline = CountdownDeadline = 0;
    }
    public void AuthorityReady(double now)
    {
        if (Stage != StartStage.Preparing) return;
        Stage = StartStage.Loading; LoadDeadline = now + LoadTimeoutSeconds;
    }
    public bool MarkLoaded(int slot, MatchStartIdentity identity)
    {
        if ((uint)slot >= 8 || identity != Identity || Stage is not (StartStage.Preparing or StartStage.Loading)) return false;
        byte mask = (byte)(1 << slot);
        if ((Expected & mask) == 0 || (Loaded & mask) != 0) return false;
        Loaded |= mask; return true;
    }
    public void Remove(int slot)
    { if ((uint)slot < 8) { Expected &= (byte)~(1 << slot); Loaded &= Expected; } }
    public byte MissingAtDeadline(double now) => Stage == StartStage.Loading && now >= LoadDeadline ? (byte)(Expected & ~Loaded) : (byte)0;
    public bool Advance(double now)
    {
        if (Stage == StartStage.Loading && Loaded == Expected)
        { Stage = StartStage.Countdown; CountdownDeadline = now + CountdownSeconds; return true; }
        if (Stage == StartStage.Countdown && now >= CountdownDeadline)
        { Stage = StartStage.InMatch; return true; }
        return false;
    }
    public ushort RemainingMilliseconds(double now) => Stage == StartStage.Countdown
        ? (ushort)Math.Clamp(Math.Ceiling((CountdownDeadline - now) * 1000), 0, ushort.MaxValue) : (ushort)0;
    public void Reset()
    { Stage = StartStage.None; Expected = Loaded = 0; LoadDeadline = CountdownDeadline = 0; }
}
