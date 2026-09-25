using System;
using System.Buffers.Binary;

namespace MphRead.Mods.Network;

[Flags]
public enum CombatAckFlags : byte
{
    None = 0, Headshot = 1, Lethal = 2, Frozen = 4, Burning = 8,
    Disrupted = 16, HalfturretAffected = 32, OutcomePresent = 64
}

// Numeric values retain the existing terminal claim reasons.
public enum CombatAckResult : byte
{
    Applied, AlreadyResolved, RejectedDeadShooter, RejectedDeadVictim,
    RejectedPlausibility, RejectedTooOld, RejectedLifecycle, RejectedGeometry,
    RejectedDamage, RejectedLaunch, RejectedNoDamage, RejectedImpulse,
    ClaimCapacity, Corrected
}

/// <summary>Immutable-at-resolution outcome. Health is historical evidence,
/// never a command to replay damage or resurrect a replica.</summary>
public struct CombatAckEntry
{
    public const int Size = 19;
    public ushort ClaimId;
    public byte Result;
    public byte VictimSlot;
    public ushort VictimGeneration, VictimLife;
    public ushort DamageApplied, HealthAfter, HalfturretHealthAfter;
    public uint DamageSequence;
    public CombatAckFlags Flags;
    public readonly bool Accepted => Result is 0 or 1 or 13;
    public readonly void Write(Span<byte> b)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(b, ClaimId); b[2] = Result; b[3] = VictimSlot;
        BinaryPrimitives.WriteUInt16LittleEndian(b[4..], VictimGeneration);
        BinaryPrimitives.WriteUInt16LittleEndian(b[6..], VictimLife);
        BinaryPrimitives.WriteUInt16LittleEndian(b[8..], DamageApplied);
        BinaryPrimitives.WriteUInt16LittleEndian(b[10..], HealthAfter);
        BinaryPrimitives.WriteUInt16LittleEndian(b[12..], HalfturretHealthAfter);
        BinaryPrimitives.WriteUInt32LittleEndian(b[14..], DamageSequence); b[18] = (byte)Flags;
    }
    public static CombatAckEntry Read(ReadOnlySpan<byte> b) => new()
    {
        ClaimId = BinaryPrimitives.ReadUInt16LittleEndian(b), Result = b[2], VictimSlot = b[3],
        VictimGeneration = BinaryPrimitives.ReadUInt16LittleEndian(b[4..]),
        VictimLife = BinaryPrimitives.ReadUInt16LittleEndian(b[6..]),
        DamageApplied = BinaryPrimitives.ReadUInt16LittleEndian(b[8..]),
        HealthAfter = BinaryPrimitives.ReadUInt16LittleEndian(b[10..]),
        HalfturretHealthAfter = BinaryPrimitives.ReadUInt16LittleEndian(b[12..]),
        DamageSequence = BinaryPrimitives.ReadUInt32LittleEndian(b[14..]), Flags = (CombatAckFlags)b[18]
    };
}
