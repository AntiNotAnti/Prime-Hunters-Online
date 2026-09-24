using System;

namespace MphRead.Mods.Network;

public enum StartStage : byte { None, Preparing, Loading, Synchronizing, Countdown, InMatch }
public readonly record struct MatchStartIdentity(ushort MatchId, ulong AuthorityEpoch, uint StartGeneration);

/// <summary>Server-owned load barrier. Virtual time is in seconds; participants
/// freeze at Begin and can only be removed, never enlarged, during that attempt.</summary>
public sealed class NetMatchStart
{
    public MatchStartIdentity Identity { get; private set; }
    public StartStage Stage { get; private set; }
    public byte Expected { get; private set; }
    public byte Loaded { get; private set; }
    public byte WorldReady { get; private set; }
    public double SlowLoadDeadline { get; private set; }
    public double LoadDeadline { get; private set; }
    public double CountdownDeadline { get; private set; }
    // Fifteen seconds is now diagnostic only. Cold Android/custom-map loads can
    // legitimately cross it; sixty seconds is the actual stuck-loader boundary.
    public const double CountdownSeconds = 1.5, SlowLoadSeconds = 15, LoadTimeoutSeconds = 60;
    private uint _generation;
    public void Begin(ushort match, ulong epoch, byte participants)
    {
        _generation = unchecked(_generation + 1); if (_generation == 0) _generation = 1;
        Identity = new(match, epoch, _generation);
        Expected = participants; Loaded = WorldReady = 0; Stage = StartStage.Preparing;
        SlowLoadDeadline = LoadDeadline = CountdownDeadline = 0;
    }
    public void AuthorityReady(double now)
    {
        if (Stage != StartStage.Preparing) return;
        Stage = StartStage.Loading;
        SlowLoadDeadline = now + SlowLoadSeconds;
        LoadDeadline = now + LoadTimeoutSeconds;
    }
    public bool MarkLoaded(int slot, MatchStartIdentity identity)
    {
        if ((uint)slot >= 8 || identity != Identity || Stage is not (StartStage.Preparing or StartStage.Loading or StartStage.Synchronizing)) return false;
        byte mask = (byte)(1 << slot);
        if ((Expected & mask) == 0 || (Loaded & mask) != 0) return false;
        Loaded |= mask; return true;
    }
    public bool MarkWorldReady(int slot, MatchStartIdentity identity)
    {
        if ((uint)slot >= 8 || identity != Identity || Stage is not (StartStage.Loading or StartStage.Synchronizing)
            || (Loaded & (1 << slot)) == 0 || (WorldReady & (1 << slot)) != 0) return false;
        WorldReady |= (byte)(1 << slot); return true;
    }
    public void Remove(int slot)
    { if ((uint)slot < 8) { Expected &= (byte)~(1 << slot); Loaded &= Expected; WorldReady &= Expected; } }
    public byte MissingAtSlowDeadline(double now) => Stage is (StartStage.Loading or StartStage.Synchronizing) && now >= SlowLoadDeadline
        ? (byte)(Expected & ~WorldReady) : (byte)0;
    public byte MissingAtDeadline(double now) => Stage is (StartStage.Loading or StartStage.Synchronizing) && now >= LoadDeadline
        ? (byte)(Expected & ~WorldReady) : (byte)0;
    public static bool ClientReleaseReady(StartStage stage, double deadline, double now) =>
        stage == StartStage.Countdown && deadline > 0 && now >= deadline;
    public bool Advance(double now)
    {
        if (Stage == StartStage.Loading && Loaded == Expected)
        { Stage = StartStage.Synchronizing; return true; }
        if (Stage == StartStage.Synchronizing && WorldReady == Expected)
        { Stage = StartStage.Countdown; CountdownDeadline = now + CountdownSeconds; return true; }
        if (Stage == StartStage.Countdown && now >= CountdownDeadline)
        { Stage = StartStage.InMatch; return true; }
        return false;
    }
    public ushort RemainingMilliseconds(double now) => Stage == StartStage.Countdown
        ? (ushort)Math.Clamp(Math.Ceiling((CountdownDeadline - now) * 1000), 0, ushort.MaxValue) : (ushort)0;
    public void Reset()
    { Stage = StartStage.None; Expected = Loaded = WorldReady = 0; SlowLoadDeadline = LoadDeadline = CountdownDeadline = 0; }
}
