using System;

namespace MphRead.Mods.Multiplayer
{
    public enum ResourceSpawnProfile : byte { Low, Standard, High }

    public readonly record struct MatchWorldProfile(byte EntityLayerPlayers, ResourceSpawnProfile Resources)
    {
        public bool IsValid => EntityLayerPlayers is >= 2 and <= 4 && Enum.IsDefined(Resources)
            && (EntityLayerPlayers == 2 ? Resources == ResourceSpawnProfile.Low
                : EntityLayerPlayers == 3 ? Resources == ResourceSpawnProfile.Standard : Resources != ResourceSpawnProfile.Low);
        public static MatchWorldProfile Resolve(int configuredPlayers)
        {
            int players = Math.Clamp(configuredPlayers, 2, 8);
            return new((byte)Math.Min(players, 4), players == 2 ? ResourceSpawnProfile.Low
                : players <= 4 ? ResourceSpawnProfile.Standard : ResourceSpawnProfile.High);
        }
    }
}
