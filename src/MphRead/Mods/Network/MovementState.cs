using System;
using System.Buffers.Binary;
using MphRead.Entities;
using MphRead.Formats;
using MphRead.Mods.Network;
using OpenTK.Mathematics;

namespace MphRead.Mods.Network
{
    // Explicit wire layout: no object references, runtime reflection or native padding.
    public struct MovementState
    {
        public const int Size = 348;
        public Vector3 TurretPosition;
        public float TurretYSpeed;
        public uint TurretGrounded;
        public int LastTeleport;
        public uint TeleportSerial;
        public int PadId;
        public ushort PadCooldown;
        public Vector3 RollRight, RollUp, RollFacing;
        public uint RollContacts;
        public Vector3 Position;
        public Vector3 Speed;
        public Vector3 Acceleration;
        public Vector3 PrevSpeed;
        public Vector3 PrevPosition;
        public Vector3 FacingVector;
        public Vector3 GunVec1;
        public Vector3 GunVec2;
        public Vector3 UpVector;
        public Vector3 JumpPadAccel;
        public Vector3 FieldC0;
        public float HSpeedCap;
        public float HSpeedMag;
        public float Gravity;
        public float Field70;
        public float Field74;
        public float Field78;
        public float Field7C;
        public float Field80;
        public float Field84;
        public float AimY;
        public float AltRollFbX;
        public float AltRollFbZ;
        public float AltRollLrX;
        public float AltRollLrZ;
        public float AltSpinSpeed;
        public float AltTiltX;
        public float AltTiltZ;
        public float AltWobble;
        public float Field44C;
        public ushort AccelerationTimer;
        public ushort BoostCharge;
        public ushort BoostDamage;
        public ushort BoostAimLock;
        public ushort AltAttackCooldown;
        public ushort AltAttackTime;
        public ushort JumpPadControlLock;
        public ushort JumpPadControlLockMin;
        public ushort TimeSinceJumpPad;
        public ushort TimeSinceMorphCamera;
        public ushort FrozenTimer;
        public ushort DeathaltTimer;
        public ushort TimeStanding;
        public ushort TimeSinceStanding;
        public ushort TimeSinceGrounded;
        public ushort TimeBeforeLanding;
        public ushort Field449;
        public ushort HorizColTimer;
        public ushort MovementMorphTicks;
        public uint Flags1;
        public uint Flags2;
        public uint MovementBoostFrame;
        public int Slipperiness;
        public int StandingEntity;
        public int StandingPart;
        public int LastJumpPad;
        public readonly void Write(Span<byte> bytes)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes[0..], Position.X);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[4..], Position.Y);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[8..], Position.Z);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[12..], Speed.X);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[16..], Speed.Y);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[20..], Speed.Z);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[24..], Acceleration.X);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[28..], Acceleration.Y);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[32..], Acceleration.Z);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[36..], PrevSpeed.X);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[40..], PrevSpeed.Y);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[44..], PrevSpeed.Z);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[48..], PrevPosition.X);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[52..], PrevPosition.Y);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[56..], PrevPosition.Z);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[60..], FacingVector.X);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[64..], FacingVector.Y);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[68..], FacingVector.Z);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[72..], GunVec1.X);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[76..], GunVec1.Y);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[80..], GunVec1.Z);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[84..], GunVec2.X);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[88..], GunVec2.Y);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[92..], GunVec2.Z);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[96..], UpVector.X);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[100..], UpVector.Y);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[104..], UpVector.Z);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[108..], JumpPadAccel.X);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[112..], JumpPadAccel.Y);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[116..], JumpPadAccel.Z);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[120..], FieldC0.X);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[124..], FieldC0.Y);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[128..], FieldC0.Z);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[132..], HSpeedCap);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[136..], HSpeedMag);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[140..], Gravity);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[144..], Field70);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[148..], Field74);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[152..], Field78);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[156..], Field7C);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[160..], Field80);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[164..], Field84);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[168..], AimY);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[172..], AltRollFbX);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[176..], AltRollFbZ);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[180..], AltRollLrX);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[184..], AltRollLrZ);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[188..], AltSpinSpeed);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[192..], AltTiltX);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[196..], AltTiltZ);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[200..], AltWobble);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[204..], Field44C);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes[208..], AccelerationTimer);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes[210..], BoostCharge);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes[212..], BoostDamage);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes[214..], BoostAimLock);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes[216..], AltAttackCooldown);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes[218..], AltAttackTime);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes[220..], JumpPadControlLock);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes[222..], JumpPadControlLockMin);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes[224..], TimeSinceJumpPad);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes[226..], TimeSinceMorphCamera);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes[228..], FrozenTimer);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes[230..], DeathaltTimer);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes[232..], TimeStanding);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes[234..], TimeSinceStanding);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes[236..], TimeSinceGrounded);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes[238..], TimeBeforeLanding);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes[240..], Field449);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes[242..], HorizColTimer);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes[244..], MovementMorphTicks);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[246..], Flags1);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[250..], Flags2);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[254..], MovementBoostFrame);
            BinaryPrimitives.WriteInt32LittleEndian(bytes[258..], Slipperiness);
            BinaryPrimitives.WriteInt32LittleEndian(bytes[262..], StandingEntity);
            BinaryPrimitives.WriteInt32LittleEndian(bytes[266..], StandingPart);
            BinaryPrimitives.WriteInt32LittleEndian(bytes[270..], LastJumpPad);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[274..], RollRight.X);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[278..], RollRight.Y);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[282..], RollRight.Z);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[286..], RollUp.X);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[290..], RollUp.Y);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[294..], RollUp.Z);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[298..], RollFacing.X);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[302..], RollFacing.Y);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[306..], RollFacing.Z);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[310..], RollContacts);
            BinaryPrimitives.WriteInt32LittleEndian(bytes[314..], PadId);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes[318..], PadCooldown);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[320..], TurretPosition.X);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[324..], TurretPosition.Y);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[328..], TurretPosition.Z);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[332..], TurretYSpeed);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[336..], TurretGrounded);
            BinaryPrimitives.WriteInt32LittleEndian(bytes[340..], LastTeleport);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[344..], TeleportSerial);
        }
        public static bool TryRead(ReadOnlySpan<byte> bytes, out MovementState state)
        {
            state = default;
            if (bytes.Length != Size) return false;
            state.Position.X = BinaryPrimitives.ReadSingleLittleEndian(bytes[0..]);
            state.Position.Y = BinaryPrimitives.ReadSingleLittleEndian(bytes[4..]);
            state.Position.Z = BinaryPrimitives.ReadSingleLittleEndian(bytes[8..]);
            state.Speed.X = BinaryPrimitives.ReadSingleLittleEndian(bytes[12..]);
            state.Speed.Y = BinaryPrimitives.ReadSingleLittleEndian(bytes[16..]);
            state.Speed.Z = BinaryPrimitives.ReadSingleLittleEndian(bytes[20..]);
            state.Acceleration.X = BinaryPrimitives.ReadSingleLittleEndian(bytes[24..]);
            state.Acceleration.Y = BinaryPrimitives.ReadSingleLittleEndian(bytes[28..]);
            state.Acceleration.Z = BinaryPrimitives.ReadSingleLittleEndian(bytes[32..]);
            state.PrevSpeed.X = BinaryPrimitives.ReadSingleLittleEndian(bytes[36..]);
            state.PrevSpeed.Y = BinaryPrimitives.ReadSingleLittleEndian(bytes[40..]);
            state.PrevSpeed.Z = BinaryPrimitives.ReadSingleLittleEndian(bytes[44..]);
            state.PrevPosition.X = BinaryPrimitives.ReadSingleLittleEndian(bytes[48..]);
            state.PrevPosition.Y = BinaryPrimitives.ReadSingleLittleEndian(bytes[52..]);
            state.PrevPosition.Z = BinaryPrimitives.ReadSingleLittleEndian(bytes[56..]);
            state.FacingVector.X = BinaryPrimitives.ReadSingleLittleEndian(bytes[60..]);
            state.FacingVector.Y = BinaryPrimitives.ReadSingleLittleEndian(bytes[64..]);
            state.FacingVector.Z = BinaryPrimitives.ReadSingleLittleEndian(bytes[68..]);
            state.GunVec1.X = BinaryPrimitives.ReadSingleLittleEndian(bytes[72..]);
            state.GunVec1.Y = BinaryPrimitives.ReadSingleLittleEndian(bytes[76..]);
            state.GunVec1.Z = BinaryPrimitives.ReadSingleLittleEndian(bytes[80..]);
            state.GunVec2.X = BinaryPrimitives.ReadSingleLittleEndian(bytes[84..]);
            state.GunVec2.Y = BinaryPrimitives.ReadSingleLittleEndian(bytes[88..]);
            state.GunVec2.Z = BinaryPrimitives.ReadSingleLittleEndian(bytes[92..]);
            state.UpVector.X = BinaryPrimitives.ReadSingleLittleEndian(bytes[96..]);
            state.UpVector.Y = BinaryPrimitives.ReadSingleLittleEndian(bytes[100..]);
            state.UpVector.Z = BinaryPrimitives.ReadSingleLittleEndian(bytes[104..]);
            state.JumpPadAccel.X = BinaryPrimitives.ReadSingleLittleEndian(bytes[108..]);
            state.JumpPadAccel.Y = BinaryPrimitives.ReadSingleLittleEndian(bytes[112..]);
            state.JumpPadAccel.Z = BinaryPrimitives.ReadSingleLittleEndian(bytes[116..]);
            state.FieldC0.X = BinaryPrimitives.ReadSingleLittleEndian(bytes[120..]);
            state.FieldC0.Y = BinaryPrimitives.ReadSingleLittleEndian(bytes[124..]);
            state.FieldC0.Z = BinaryPrimitives.ReadSingleLittleEndian(bytes[128..]);
            state.HSpeedCap = BinaryPrimitives.ReadSingleLittleEndian(bytes[132..]);
            state.HSpeedMag = BinaryPrimitives.ReadSingleLittleEndian(bytes[136..]);
            state.Gravity = BinaryPrimitives.ReadSingleLittleEndian(bytes[140..]);
            state.Field70 = BinaryPrimitives.ReadSingleLittleEndian(bytes[144..]);
            state.Field74 = BinaryPrimitives.ReadSingleLittleEndian(bytes[148..]);
            state.Field78 = BinaryPrimitives.ReadSingleLittleEndian(bytes[152..]);
            state.Field7C = BinaryPrimitives.ReadSingleLittleEndian(bytes[156..]);
            state.Field80 = BinaryPrimitives.ReadSingleLittleEndian(bytes[160..]);
            state.Field84 = BinaryPrimitives.ReadSingleLittleEndian(bytes[164..]);
            state.AimY = BinaryPrimitives.ReadSingleLittleEndian(bytes[168..]);
            state.AltRollFbX = BinaryPrimitives.ReadSingleLittleEndian(bytes[172..]);
            state.AltRollFbZ = BinaryPrimitives.ReadSingleLittleEndian(bytes[176..]);
            state.AltRollLrX = BinaryPrimitives.ReadSingleLittleEndian(bytes[180..]);
            state.AltRollLrZ = BinaryPrimitives.ReadSingleLittleEndian(bytes[184..]);
            state.AltSpinSpeed = BinaryPrimitives.ReadSingleLittleEndian(bytes[188..]);
            state.AltTiltX = BinaryPrimitives.ReadSingleLittleEndian(bytes[192..]);
            state.AltTiltZ = BinaryPrimitives.ReadSingleLittleEndian(bytes[196..]);
            state.AltWobble = BinaryPrimitives.ReadSingleLittleEndian(bytes[200..]);
            state.Field44C = BinaryPrimitives.ReadSingleLittleEndian(bytes[204..]);
            state.AccelerationTimer = BinaryPrimitives.ReadUInt16LittleEndian(bytes[208..]);
            state.BoostCharge = BinaryPrimitives.ReadUInt16LittleEndian(bytes[210..]);
            state.BoostDamage = BinaryPrimitives.ReadUInt16LittleEndian(bytes[212..]);
            state.BoostAimLock = BinaryPrimitives.ReadUInt16LittleEndian(bytes[214..]);
            state.AltAttackCooldown = BinaryPrimitives.ReadUInt16LittleEndian(bytes[216..]);
            state.AltAttackTime = BinaryPrimitives.ReadUInt16LittleEndian(bytes[218..]);
            state.JumpPadControlLock = BinaryPrimitives.ReadUInt16LittleEndian(bytes[220..]);
            state.JumpPadControlLockMin = BinaryPrimitives.ReadUInt16LittleEndian(bytes[222..]);
            state.TimeSinceJumpPad = BinaryPrimitives.ReadUInt16LittleEndian(bytes[224..]);
            state.TimeSinceMorphCamera = BinaryPrimitives.ReadUInt16LittleEndian(bytes[226..]);
            state.FrozenTimer = BinaryPrimitives.ReadUInt16LittleEndian(bytes[228..]);
            state.DeathaltTimer = BinaryPrimitives.ReadUInt16LittleEndian(bytes[230..]);
            state.TimeStanding = BinaryPrimitives.ReadUInt16LittleEndian(bytes[232..]);
            state.TimeSinceStanding = BinaryPrimitives.ReadUInt16LittleEndian(bytes[234..]);
            state.TimeSinceGrounded = BinaryPrimitives.ReadUInt16LittleEndian(bytes[236..]);
            state.TimeBeforeLanding = BinaryPrimitives.ReadUInt16LittleEndian(bytes[238..]);
            state.Field449 = BinaryPrimitives.ReadUInt16LittleEndian(bytes[240..]);
            state.HorizColTimer = BinaryPrimitives.ReadUInt16LittleEndian(bytes[242..]);
            state.MovementMorphTicks = BinaryPrimitives.ReadUInt16LittleEndian(bytes[244..]);
            state.Flags1 = BinaryPrimitives.ReadUInt32LittleEndian(bytes[246..]);
            state.Flags2 = BinaryPrimitives.ReadUInt32LittleEndian(bytes[250..]);
            state.MovementBoostFrame = BinaryPrimitives.ReadUInt32LittleEndian(bytes[254..]);
            state.Slipperiness = BinaryPrimitives.ReadInt32LittleEndian(bytes[258..]);
            state.StandingEntity = BinaryPrimitives.ReadInt32LittleEndian(bytes[262..]);
            state.StandingPart = BinaryPrimitives.ReadInt32LittleEndian(bytes[266..]);
            state.LastJumpPad = BinaryPrimitives.ReadInt32LittleEndian(bytes[270..]);
            state.RollRight.X = BinaryPrimitives.ReadSingleLittleEndian(bytes[274..]);
            state.RollRight.Y = BinaryPrimitives.ReadSingleLittleEndian(bytes[278..]);
            state.RollRight.Z = BinaryPrimitives.ReadSingleLittleEndian(bytes[282..]);
            state.RollUp.X = BinaryPrimitives.ReadSingleLittleEndian(bytes[286..]);
            state.RollUp.Y = BinaryPrimitives.ReadSingleLittleEndian(bytes[290..]);
            state.RollUp.Z = BinaryPrimitives.ReadSingleLittleEndian(bytes[294..]);
            state.RollFacing.X = BinaryPrimitives.ReadSingleLittleEndian(bytes[298..]);
            state.RollFacing.Y = BinaryPrimitives.ReadSingleLittleEndian(bytes[302..]);
            state.RollFacing.Z = BinaryPrimitives.ReadSingleLittleEndian(bytes[306..]);
            state.RollContacts = BinaryPrimitives.ReadUInt32LittleEndian(bytes[310..]);
            state.PadId = BinaryPrimitives.ReadInt32LittleEndian(bytes[314..]);
            state.PadCooldown = BinaryPrimitives.ReadUInt16LittleEndian(bytes[318..]);
            state.TurretPosition.X = BinaryPrimitives.ReadSingleLittleEndian(bytes[320..]);
            state.TurretPosition.Y = BinaryPrimitives.ReadSingleLittleEndian(bytes[324..]);
            state.TurretPosition.Z = BinaryPrimitives.ReadSingleLittleEndian(bytes[328..]);
            state.TurretYSpeed = BinaryPrimitives.ReadSingleLittleEndian(bytes[332..]);
            state.TurretGrounded = BinaryPrimitives.ReadUInt32LittleEndian(bytes[336..]);
            state.LastTeleport = BinaryPrimitives.ReadInt32LittleEndian(bytes[340..]);
            state.TeleportSerial = BinaryPrimitives.ReadUInt32LittleEndian(bytes[344..]);
            return Finite(state.TurretPosition.X)
                && Finite(state.TurretPosition.Y)
                && Finite(state.TurretPosition.Z)
                && Finite(state.TurretYSpeed)
                && Finite(state.RollRight.X)
                && Finite(state.RollRight.Y)
                && Finite(state.RollRight.Z)
                && Finite(state.RollUp.X)
                && Finite(state.RollUp.Y)
                && Finite(state.RollUp.Z)
                && Finite(state.RollFacing.X)
                && Finite(state.RollFacing.Y)
                && Finite(state.RollFacing.Z)
                && Finite(state.Position.X)
                && Finite(state.Position.Y)
                && Finite(state.Position.Z)
                && Finite(state.Speed.X)
                && Finite(state.Speed.Y)
                && Finite(state.Speed.Z)
                && Finite(state.Acceleration.X)
                && Finite(state.Acceleration.Y)
                && Finite(state.Acceleration.Z)
                && Finite(state.PrevSpeed.X)
                && Finite(state.PrevSpeed.Y)
                && Finite(state.PrevSpeed.Z)
                && Finite(state.PrevPosition.X)
                && Finite(state.PrevPosition.Y)
                && Finite(state.PrevPosition.Z)
                && Finite(state.FacingVector.X)
                && Finite(state.FacingVector.Y)
                && Finite(state.FacingVector.Z)
                && Finite(state.GunVec1.X)
                && Finite(state.GunVec1.Y)
                && Finite(state.GunVec1.Z)
                && Finite(state.GunVec2.X)
                && Finite(state.GunVec2.Y)
                && Finite(state.GunVec2.Z)
                && Finite(state.UpVector.X)
                && Finite(state.UpVector.Y)
                && Finite(state.UpVector.Z)
                && Finite(state.JumpPadAccel.X)
                && Finite(state.JumpPadAccel.Y)
                && Finite(state.JumpPadAccel.Z)
                && Finite(state.FieldC0.X)
                && Finite(state.FieldC0.Y)
                && Finite(state.FieldC0.Z)
                && Finite(state.HSpeedCap)
                && Finite(state.HSpeedMag)
                && Finite(state.Gravity)
                && Finite(state.Field70)
                && Finite(state.Field74)
                && Finite(state.Field78)
                && Finite(state.Field7C)
                && Finite(state.Field80)
                && Finite(state.Field84)
                && Finite(state.AimY)
                && Finite(state.AltRollFbX)
                && Finite(state.AltRollFbZ)
                && Finite(state.AltRollLrX)
                && Finite(state.AltRollLrZ)
                && Finite(state.AltSpinSpeed)
                && Finite(state.AltTiltX)
                && Finite(state.AltTiltZ)
                && Finite(state.AltWobble)
                && Finite(state.Field44C)
                && state.Slipperiness >= 0 && state.Slipperiness < Metadata.TractionFactors.Length
                && state.StandingPart >= 0 && state.StandingPart < 2;
        }
        private static bool Finite(float value) => Single.IsFinite(value) && MathF.Abs(value) < 100000;
    }
}

namespace MphRead.Entities
{
    public partial class PlayerEntity
    {
        private bool ModUsesNetworkMovementInput => _movementReplay
            || (NetSession.Active && SlotIndex != NetSession.LocalSlot && !IsBot);
        private bool _movementReplay;
        private int _movementPadId = -1;
        private ushort _movementPadCooldown;
        internal bool ModCanReplayJumpPad(int id) => _movementPadId != id || _movementPadCooldown == 0;
        private ushort _movementMorphTicks;
        private uint _movementBoostFrame;
        private Vector3 _movementReplayAim;
        private Vector3 _movementTurretPosition;
        private float _movementTurretYSpeed;
        private bool _movementTurretGrounded;
        private int _movementLastTeleport = -1;
        private uint _movementTeleportSerial;
        internal void ModNoteMovementTeleport(int id) => _movementLastTeleport = id;
        internal void ModClearMovementTeleport(int id) { if (_movementLastTeleport == id) _movementLastTeleport = -1; }
        internal bool ModCanReplayTeleport(int id) => _movementLastTeleport != id;
        internal void ModReplayTeleport(Vector3 position, Vector3 facing, Formats.Culling.NodeRef node)
        {
            _movementTeleportSerial++;
            _facingVector = _gunVec1 = facing;
            Position = position;
            NodeRef = node;
            Speed = new Vector3(0, Speed.Y, 0);
        }
        internal Vector3 MovementVisualOffset;
        internal void ModMovementBoostConsumed(uint frame) => _movementBoostFrame = frame;
        internal Vector3 ModMovementDrawOffset => NetMovementPrediction.Active
            ? MovementVisualOffset * (1 - 0.25f * (float)Mods.Render.FrameTiming.Alpha) : Vector3.Zero;
        internal Matrix4 ModSmoothMovementView(Matrix4 view)
            => Matrix4.CreateTranslation(-ModMovementDrawOffset) * view;

        private const PlayerFlags1 MovementFlags1 = (PlayerFlags1)0x3DF0BFFF;
        private const PlayerFlags2 MovementFlags2 = PlayerFlags2.AltAttack | PlayerFlags2.BipedStuck
            | PlayerFlags2.GravityOverride | PlayerFlags2.NoFormSwitch | PlayerFlags2.BipedLock
            | PlayerFlags2.AltFormGravity | PlayerFlags2.SpireClimbing | PlayerFlags2.Halfturret;

        internal MovementState ModCaptureMovement()
        {
            return new MovementState
            {
                Position = Position,
                Speed = Speed,
                Acceleration = Acceleration,
                PrevSpeed = PrevSpeed,
                PrevPosition = PrevPosition,
                FacingVector = _facingVector,
                GunVec1 = _gunVec1,
                GunVec2 = _gunVec2,
                UpVector = _upVector,
                JumpPadAccel = _jumpPadAccel,
                FieldC0 = _fieldC0,
                HSpeedCap = _hSpeedCap,
                HSpeedMag = _hSpeedMag,
                Gravity = _gravity,
                Field70 = _field70,
                Field74 = _field74,
                Field78 = _field78,
                Field7C = _field7C,
                Field80 = _field80,
                Field84 = _field84,
                AimY = _aimY,
                AltRollFbX = _altRollFbX,
                AltRollFbZ = _altRollFbZ,
                AltRollLrX = _altRollLrX,
                AltRollLrZ = _altRollLrZ,
                AltSpinSpeed = _altSpinSpeed,
                AltTiltX = _altTiltX,
                AltTiltZ = _altTiltZ,
                AltWobble = _altWobble,
                Field44C = _field44C,
                AccelerationTimer = _accelerationTimer,
                BoostCharge = _boostCharge,
                BoostDamage = _boostDamage,
                BoostAimLock = _boostAimLock,
                AltAttackCooldown = _altAttackCooldown,
                AltAttackTime = _altAttackTime,
                JumpPadControlLock = _jumpPadControlLock,
                JumpPadControlLockMin = _jumpPadControlLockMin,
                TimeSinceJumpPad = _timeSinceJumpPad,
                TimeSinceMorphCamera = _timeSinceMorphCamera,
                FrozenTimer = _frozenTimer,
                DeathaltTimer = _deathaltTimer,
                TimeStanding = _timeStanding,
                TimeSinceStanding = _timeSinceStanding,
                TimeSinceGrounded = _timeSinceGrounded,
                TimeBeforeLanding = _timeBeforeLanding,
                Field449 = _field449,
                HorizColTimer = _horizColTimer,
                MovementMorphTicks = _movementMorphTicks,
                Flags1 = (uint)Flags1,
                Flags2 = (uint)Flags2,
                MovementBoostFrame = _movementBoostFrame,
                Slipperiness = _slipperiness,
                TurretPosition = Hunter == Hunter.Weavel ? _halfturret.Position : Vector3.Zero,
                TurretYSpeed = Hunter == Hunter.Weavel ? _halfturret.ModMovementYSpeed : 0,
                TurretGrounded = Hunter == Hunter.Weavel && _halfturret.ModMovementGrounded ? 1u : 0u,
                LastTeleport = _movementLastTeleport, TeleportSerial = _movementTeleportSerial,
                PadId = _movementPadId, PadCooldown = _movementPadCooldown,
                RollRight = _modelTransform.Row0.Xyz, RollUp = _modelTransform.Row1.Xyz, RollFacing = _modelTransform.Row2.Xyz,
                RollContacts = Hunter == Hunter.Spire && _spireAltVecs[0] != Vector3.Zero ? 1u : 0u,
                StandingEntity = _standingEntCol?.Entity.Id ?? -1,
                StandingPart = _standingEntCol != null && ReferenceEquals(_standingEntCol.Entity.EntityCollision[1], _standingEntCol) ? 1 : 0,
                LastJumpPad = _lastJumpPad?.Id ?? -1
            };
        }
        internal void ModRestoreMovement(in MovementState state)
        {
            _movementTurretPosition = state.TurretPosition;
            _movementTurretYSpeed = state.TurretYSpeed;
            _movementTurretGrounded = state.TurretGrounded != 0;
            _movementLastTeleport = state.LastTeleport;
            _movementTeleportSerial = state.TeleportSerial;
            _movementPadId = state.PadId; _movementPadCooldown = state.PadCooldown;
            Position = state.Position;
            Speed = state.Speed;
            Acceleration = state.Acceleration;
            PrevSpeed = state.PrevSpeed;
            PrevPosition = state.PrevPosition;
            _facingVector = state.FacingVector;
            _gunVec1 = state.GunVec1;
            _gunVec2 = state.GunVec2;
            _upVector = state.UpVector;
            _jumpPadAccel = state.JumpPadAccel;
            _fieldC0 = state.FieldC0;
            _hSpeedCap = state.HSpeedCap;
            _hSpeedMag = state.HSpeedMag;
            _gravity = state.Gravity;
            _field70 = state.Field70;
            _field74 = state.Field74;
            _field78 = state.Field78;
            _field7C = state.Field7C;
            _field80 = state.Field80;
            _field84 = state.Field84;
            _aimY = state.AimY;
            _altRollFbX = state.AltRollFbX;
            _altRollFbZ = state.AltRollFbZ;
            _altRollLrX = state.AltRollLrX;
            _altRollLrZ = state.AltRollLrZ;
            _altSpinSpeed = state.AltSpinSpeed;
            _altTiltX = state.AltTiltX;
            _altTiltZ = state.AltTiltZ;
            _altWobble = state.AltWobble;
            _field44C = state.Field44C;
            _accelerationTimer = state.AccelerationTimer;
            _boostCharge = state.BoostCharge;
            _boostDamage = state.BoostDamage;
            _boostAimLock = state.BoostAimLock;
            _altAttackCooldown = state.AltAttackCooldown;
            _altAttackTime = state.AltAttackTime;
            _jumpPadControlLock = state.JumpPadControlLock;
            _jumpPadControlLockMin = state.JumpPadControlLockMin;
            _timeSinceJumpPad = state.TimeSinceJumpPad;
            _timeSinceMorphCamera = state.TimeSinceMorphCamera;
            _frozenTimer = state.FrozenTimer;
            _deathaltTimer = state.DeathaltTimer;
            _timeStanding = state.TimeStanding;
            _timeSinceStanding = state.TimeSinceStanding;
            _timeSinceGrounded = state.TimeSinceGrounded;
            _timeBeforeLanding = state.TimeBeforeLanding;
            _field449 = state.Field449;
            _horizColTimer = state.HorizColTimer;
            _movementMorphTicks = state.MovementMorphTicks;
            Flags1 = (Flags1 & ~MovementFlags1) | ((PlayerFlags1)state.Flags1 & MovementFlags1);
            Flags2 = (Flags2 & ~MovementFlags2) | ((PlayerFlags2)state.Flags2 & MovementFlags2);
            _movementBoostFrame = state.MovementBoostFrame;
            _slipperiness = state.Slipperiness;
            _volumeUnxf = PlayerVolumes[(int)Hunter, IsAltForm ? 2 : 0];
            _volume = CollisionVolume.Move(_volumeUnxf, Position);
            _standingEntCol = state.StandingEntity >= 0 && _scene.TryGetEntity(state.StandingEntity, out var entity)
                ? entity.EntityCollision[state.StandingPart] : null;
            _lastJumpPad = state.LastJumpPad >= 0 && _scene.TryGetEntity(state.LastJumpPad, out var pad)
                ? pad as JumpPadEntity : null;
            _collidedEntCol = null;
            _modelTransform.Row0.Xyz = state.RollRight;
            _modelTransform.Row1.Xyz = state.RollUp;
            _modelTransform.Row2.Xyz = state.RollFacing;
            if (Hunter == Hunter.Spire)
                for (int i = 0; i < _spireAltVecs.Length; i++)
                    _spireAltVecs[i] = state.RollContacts == 0 ? Vector3.Zero
                        : Matrix.Vec3MultMtx3(Metadata.SpireAltVectors[i], _modelTransform);
        }
        private void ModTickMovementTimers()
        {
            if (_movementPadCooldown > 0) _movementPadCooldown--;
            if (_altAttackCooldown > 0)
            {
                _altAttackCooldown--;
            }
            if (_boostAimLock > 0)
            {
                _boostAimLock--;
            }
            if (_jumpPadControlLock > 0)
            {
                _jumpPadControlLock--;
            }
            if (_jumpPadControlLock == 0)
            {
                _lastJumpPad = null;
            }
            if (_jumpPadControlLockMin > 0)
            {
                _jumpPadControlLockMin--;
            }
            if (_timeSinceJumpPad != UInt16.MaxValue)
            {
                _timeSinceJumpPad++;
            }
            if (_timeSinceMorphCamera != UInt16.MaxValue)
            {
                _timeSinceMorphCamera++;
            }
        }

        // Called once by both the live tick and each replay tick. Animation
        // playback is presentation; network movement uses a captured timer.
        private void ModTickMovementMorph()
        {
            if (_movementMorphTicks == 0 || --_movementMorphTicks != 0) return;
            if (IsMorphing)
            {
                Flags1 &= ~PlayerFlags1.Morphing;
                if (_movementReplay) ModMovementForm(true);
                else UpdateForm(true);
            }
            else Flags1 &= ~PlayerFlags1.Unmorphing;
        }
        private void ModMovementForm(bool alt)
        {
            var volume = PlayerVolumes[(int)Hunter, alt ? 2 : 0];
            Position += _volumeUnxf.SpherePosition - volume.SpherePosition;
            _volumeUnxf = volume;
            _volume = CollisionVolume.Move(volume, Position);
            if (alt)
            {
                Flags1 |= PlayerFlags1.AltForm;
                InitAltTransform();
                _field80 = _field70; _field84 = _field74;
            }
            else { Flags1 &= ~PlayerFlags1.AltForm; _gunVec1 = _facingVector; }
        }
        private bool ModReplaySwitchForms()
        {
            if (IsMorphing || IsUnmorphing || _frozenTimer > 0 || _field6D0 || _deathaltTimer > 0
                || Hunter == Hunter.Guardian || Flags2.TestFlag(PlayerFlags2.NoFormSwitch)
                || (!IsAltForm && Flags2.TestFlag(PlayerFlags2.BipedStuck))
                || (IsAltForm && (MorphCamera != null || Flags1.TestFlag(PlayerFlags1.NoUnmorph)))) return false;
            var animation = IsAltForm ? PlayerAnimation.Unmorph : PlayerAnimation.Morph;
            _movementMorphTicks = (ushort)(_bipedModel2.Model.AnimationGroups.Node[(int)animation].FrameCount * 2);
            if (!IsAltForm)
            {
                _altRollFbX = _field70; _altRollFbZ = _field74;
                _altRollLrX = _gunVec2.X; _altRollLrZ = _gunVec2.Z;
                Flags1 |= PlayerFlags1.Morphing;
                InitAltTransform();
                if (Hunter == Hunter.Spire) Array.Clear(_spireAltVecs);
                if (Hunter == Hunter.Weavel)
                {
                    Flags2 |= PlayerFlags2.Halfturret;
                    _movementTurretPosition = Position.AddY(Fixed.ToFloat(Values.MinPickupHeight) + 0.45f);
                    _movementTurretYSpeed = 0;
                    _movementTurretGrounded = Flags1.TestFlag(PlayerFlags1.Standing);
                }
            }
            else
            {
                Flags1 |= PlayerFlags1.Unmorphing;
                Flags2 &= ~PlayerFlags2.Halfturret;
                _boostCharge = 0;
                EndAltAttack();
                ModMovementForm(false);
            }
            return true;
        }

        internal void ModFinishMovementReplay()
        {
            PrevPosition = Position;
            ModRefreshNodeRef(Position);
            if (Hunter == Hunter.Weavel && Flags2.TestFlag(PlayerFlags2.Halfturret))
                _halfturret.ModRestoreMovement(_movementTurretPosition, _movementTurretYSpeed, _movementTurretGrounded);
            var camera = IsAltForm || IsMorphing
                ? (Values.AltFormStrafe != 0 ? CameraType.Third2 : CameraType.Third1) : CameraType.First;
            if (CameraType != camera) SwitchCamera(camera, _facingVector);
            if (IsMorphing || IsUnmorphing)
            {
                var animation = IsMorphing ? PlayerAnimation.Morph : PlayerAnimation.Unmorph;
                if (Biped2Anim != animation) SetBipedAnimation(animation, AnimFlags.NoLoop);
                _bipedModel2.AnimInfo.Frame[0] = Math.Clamp(_bipedModel2.AnimInfo.FrameCount[0] - (_movementMorphTicks + 1) / 2,
                    0, Math.Max(0, _bipedModel2.AnimInfo.FrameCount[0] - 1));
            }
        }

        internal void ModReplayMovement(in IntentPacket command)
        {
            // Controls belong to the live input sampler. Restore them even if
            // a malformed map causes collision to throw during replay.
            Span<byte> controls = stackalloc byte[Controls.All.Length];
            for (int i = 0; i < controls.Length; i++)
            {
                var key = Controls.All[i];
                controls[i] = (byte)((key.IsDown ? 1 : 0) | (key.IsPressed ? 2 : 0) | (key.IsReleased ? 4 : 0));
            }
            bool mouse = Controls.MouseAim, keyboard = Controls.KeyboardAim;
            Vector3 cameraPosition = CameraInfo.Position;
            bool swipe = SwipeBoostRequested;
            float swipeX = SwipeBoostX, swipeY = SwipeBoostY;
            _movementReplay = true;
            try
            {
                Controls.ClearAll();
                Controls.MouseAim = Controls.KeyboardAim = false;
                void Bind(Keybind key, IntentButtons flag, IntentButtons buttons, uint presses)
                {
                    key.IsDown = (buttons & flag) != 0;
                    key.IsPressed = (presses & (uint)flag) != 0;
                }
                Bind(Controls.MoveLeft, IntentButtons.MoveLeft, command.Buttons, command.Presses[0]);
                Bind(Controls.MoveRight, IntentButtons.MoveRight, command.Buttons, command.Presses[0]);
                Bind(Controls.MoveUp, IntentButtons.MoveUp, command.Buttons, command.Presses[0]);
                Bind(Controls.MoveDown, IntentButtons.MoveDown, command.Buttons, command.Presses[0]);
                Bind(Controls.RolltLeft, IntentButtons.RollLeft, command.Buttons, command.Presses[0]);
                Bind(Controls.RollRight, IntentButtons.RollRight, command.Buttons, command.Presses[0]);
                Bind(Controls.RollUp, IntentButtons.RollUp, command.Buttons, command.Presses[0]);
                Bind(Controls.RollDown, IntentButtons.RollDown, command.Buttons, command.Presses[0]);
                Bind(Controls.Jump, IntentButtons.Jump, command.Buttons, command.Presses[0]);
                Bind(Controls.Morph, IntentButtons.Morph, command.Buttons, command.Presses[0]);
                Bind(Controls.Boost, IntentButtons.Boost, command.Buttons, command.Presses[0]);
                Bind(Controls.Zoom, IntentButtons.Zoom, command.Buttons, command.Presses[0]);
                Bind(Controls.AltAttack, IntentButtons.AltAttack, command.Buttons, command.Presses[0]);
                if (Hunter == Hunter.Weavel && Flags2.TestFlag(PlayerFlags2.Halfturret))
                    _halfturret.ModStepMovement(ref _movementTurretPosition, ref _movementTurretYSpeed, ref _movementTurretGrounded);
                foreach (var pad in _scene.GetJumpPadEntities()) pad.ModReplayMovement(this);
                foreach (var teleporter in _scene.GetTeleporterEntities()) teleporter.ModReplayMovement(this);
                PrevPosition = Position; PrevSpeed = Speed;
                if (IsAltForm) Flags1 |= PlayerFlags1.AltFormPrevious;
                else Flags1 &= ~PlayerFlags1.AltFormPrevious;
                Flags1 &= ~(PlayerFlags1.MovingBiped | PlayerFlags1.Walking | PlayerFlags1.Strafing);
                _crushBits = 0;
                ModTickMovementTimers();
                if (_frozenTimer > 0) _frozenTimer--;
                _movementReplayAim = command.Aim;
                if (_frozenTimer == 0 && (!IsAltForm && !IsMorphing || Values.AltFormStrafe != 0)) ModSetAim(command.Aim);
                var forward = NetMovementInput.Normalize(command.RollForward);
                if ((IsAltForm || IsMorphing) && forward != Vector2.Zero)
                {
                    _altRollFbX = forward.X; _altRollFbZ = forward.Y;
                    _altRollLrX = forward.Y; _altRollLrZ = -forward.X;
                }
                SwipeBoostRequested = false;
                if (command.BoostFrame != 0 && NetLifecycleTracker.Newer(command.BoostFrame, _movementBoostFrame)
                    && unchecked(command.Frame - command.BoostFrame) < IntentPacket.PressHistory)
                {
                    _movementBoostFrame = command.BoostFrame;
                    SwipeBoostRequested = true;
                    var direction = NetMovementInput.Normalize(command.BoostDirection);
                    SwipeBoostX = -Vector2.Dot(direction, new Vector2(_altRollLrX, _altRollLrZ));
                    SwipeBoostY = -Vector2.Dot(direction, new Vector2(_altRollFbX, _altRollFbZ));
                }
                if (IsAltForm || IsMorphing) ProcessAlt();
                else ProcessBiped();
                if (Flags1.TestFlag(PlayerFlags1.Boosting) && _hSpeedMag <= Fixed.ToFloat(Values.AltMinHSpeed))
                    Flags1 &= ~PlayerFlags1.Boosting;
                if (!Flags1.TestFlag(PlayerFlags1.Standing) && _timeSinceStanding != UInt16.MaxValue) _timeSinceStanding++;
                if (_field449 != UInt16.MaxValue) _field449++;
                ModTickMovementMorph();
            }
            finally
            {
                _movementReplay = false;
                CameraInfo.Position = cameraPosition;
                SwipeBoostRequested = swipe; SwipeBoostX = swipeX; SwipeBoostY = swipeY;
                Controls.MouseAim = mouse; Controls.KeyboardAim = keyboard;
                for (int i = 0; i < controls.Length; i++)
                {
                    var key = Controls.All[i];
                    key.IsDown = (controls[i] & 1) != 0;
                    key.IsPressed = (controls[i] & 2) != 0;
                    key.IsReleased = (controls[i] & 4) != 0;
                }
            }
        }
    }
}
