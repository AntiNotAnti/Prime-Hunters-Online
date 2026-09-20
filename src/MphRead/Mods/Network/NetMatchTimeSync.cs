using System;
using System.Buffers.Binary;
using MphRead.Entities;

namespace MphRead.Mods.Network
{
    internal static class NetMatchTimeSync
    {
        public const int Size = PlayerEntity.SlotCapacity * sizeof(float) * 2;
        public static void Write(Span<byte> dest)
        {
            for (int i = 0; i < PlayerEntity.SlotCapacity; i++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(dest[(i * 8)..], GameState.Time[i]);
                BinaryPrimitives.WriteSingleLittleEndian(dest[(i * 8 + 4)..], GameState.TeamTime[i]);
            }
        }
        public static bool Validate(ReadOnlySpan<byte> src)
        {
            if (src.Length != Size) return false;
            for (int i = 0; i < Size; i += sizeof(float))
            {
                float value = BinaryPrimitives.ReadSingleLittleEndian(src[i..]);
                if (!Single.IsFinite(value) || value < -1) return false;
            }
            return true;
        }
        public static void Receive(ReadOnlySpan<byte> src)
        {
            for (int i = 0; i < PlayerEntity.SlotCapacity; i++)
            {
                GameState.Time[i] = BinaryPrimitives.ReadSingleLittleEndian(src[(i * 8)..]);
                GameState.TeamTime[i] = BinaryPrimitives.ReadSingleLittleEndian(src[(i * 8 + 4)..]);
            }
        }
    }
}
