namespace MphRead.Mods.Render
{
    /// <summary>Cosmetic trail offsets derived only from simulation state.</summary>
    internal static class LockjawTrailNoise
    {
        public static float Sample(ulong tick, int ownerSlot, int sourceBomb,
            int targetBomb, int segment, int axis)
        {
            unchecked
            {
                uint value = 0x9E3779B9;
                value = (value ^ (uint)tick) * 0x01000193;
                value = (value ^ (uint)(tick >> 32)) * 0x01000193;
                value = (value ^ (uint)ownerSlot) * 0x01000193;
                value = (value ^ (uint)sourceBomb) * 0x01000193;
                value = (value ^ (uint)targetBomb) * 0x01000193;
                value = (value ^ (uint)segment) * 0x01000193;
                value = (value ^ (uint)axis) * 0x01000193;
                value ^= value >> 16;
                value *= 0x7FEB352D;
                value ^= value >> 15;
                value *= 0x846CA68B;
                value ^= value >> 16;
                return (value & 0xFFFF) / 65536f * 0.5f - 0.25f;
            }
        }
    }
}
