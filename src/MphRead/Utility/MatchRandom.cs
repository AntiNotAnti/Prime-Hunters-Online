using System;

namespace MphRead
{
    public sealed class MatchRandom
    {
        public const uint Rng1StartValue = 0x3DE9179BU;
        public const uint Rng2StartValue = 0;

        public uint Rng1 { get; private set; } = Rng1StartValue;
        public uint Rng2 { get; private set; } = Rng2StartValue;

        public static uint CallRng(ref uint rng, uint value)
        {
            rng *= 0x7FF8A3ED;
            rng += 0x2AA01D31;
            return (uint)((rng >> 16) * (long)value / 0x10000L);
        }

        public uint GetRandomInt1(int value)
        {
            return GetRandomInt1((uint)value);
        }

        public uint GetRandomInt2(int value)
        {
            return GetRandomInt2((uint)value);
        }

        public uint GetRandomInt1(uint value)
        {
            Rng1 *= 0x7FF8A3ED;
            Rng1 += 0x2AA01D31;
            return (uint)((Rng1 >> 16) * (long)value / 0x10000L);
        }

        public uint GetRandomInt2(uint value)
        {
            Rng2 *= 0x7FF8A3ED;
            Rng2 += 0x2AA01D31;
            return (uint)((Rng2 >> 16) * (long)value / 0x10000L);
        }

        public void SetRng1(uint value)
        {
            Rng1 = value;
        }

        public void SetRng2(uint value)
        {
            Rng2 = value;
        }

        public void DoDamageShake(int damage)
        {
            int shake = (int)(damage * 40.96f);
            if (shake < 204)
            {
                shake = 204;
            }
            DoCameraShake(shake);
        }

        public void DoCameraShake(int shake)
        {
            Console.WriteLine($"shake {shake}");
            uint rng = Rng2;
            int frames = 0;
            while (shake > 0)
            {
                frames++;
                GetRandomInt2(1);
                GetRandomInt2(1);
                GetRandomInt2(1);
                shake = (int)((3481L * shake + 2048) >> 12);
                if (shake < 41)
                {
                    shake = 0;
                }
            }
            int calls = frames * 3;
            Console.WriteLine($"{frames} frame{(frames == 1 ? "" : "s")}, {calls} calls");
            Console.WriteLine($"rng {rng:X8} --> {Rng2:X8}");
        }
    }
}
