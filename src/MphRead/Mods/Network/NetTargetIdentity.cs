namespace MphRead.Mods.Network;

/// <summary>Explicit player decision: 0x80 is none; 0x81..0x88 identify a fenced life.</summary>
public readonly record struct NetTargetIdentity(byte EncodedSlot, ushort Generation, ushort LifeId)
{
    public const byte Valid = 0x80;
    public bool IsSupplied => (EncodedSlot & Valid) != 0;
    public int Slot => (EncodedSlot & 0x7f) - 1;
    public bool HasPlayer => IsSupplied && Slot >= 0 && Slot < 8;
    public bool IsWellFormed => IsSupplied && (Slot == -1 ? Generation == 0 && LifeId == 0
        : HasPlayer && Generation != 0 && LifeId != 0);
    public static NetTargetIdentity None => new(Valid, 0, 0);
    public static NetTargetIdentity ForSlot(int slot) => (uint)slot < 8
        ? new((byte)(Valid | (slot + 1)), NetPlayerLifecycle.Generation(slot), NetPlayerLifecycle.Get(slot)) : None;
}

public enum ContinuousTargetStatus : byte { None, Acquired, Held, Lost, Changed }
public struct ContinuousTargetState
{
    public NetTargetIdentity Target;
    public uint AcquiredFrame, LastValidatedFrame, LastReportedFrame;
    public ContinuousTargetStatus Status;
    public void Observe(NetTargetIdentity target, uint frame, uint reportedFrame)
    {
        bool had = Target.HasPlayer;
        Status = !target.HasPlayer ? (had ? ContinuousTargetStatus.Lost : ContinuousTargetStatus.None)
            : Target == target ? ContinuousTargetStatus.Held : had ? ContinuousTargetStatus.Changed : ContinuousTargetStatus.Acquired;
        if (target.HasPlayer && target != Target) AcquiredFrame = frame;
        Target = target; LastValidatedFrame = frame; LastReportedFrame = reportedFrame;
    }
}
