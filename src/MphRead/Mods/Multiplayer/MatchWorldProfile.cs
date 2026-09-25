using System;

namespace MphRead.Mods.Multiplayer
{
    public enum ResourceSpawnProfile : byte { Low, Standard, High, Vanilla }

    public readonly record struct MatchWorldProfile(byte EntityLayerPlayers, ResourceSpawnProfile Resources)
    {
        public bool IsValid => EntityLayerPlayers is >= 2 and <= 4 && Enum.IsDefined(Resources)
            && (EntityLayerPlayers switch
            {
                2 => Resources is ResourceSpawnProfile.Low or ResourceSpawnProfile.Vanilla,
                3 => Resources == ResourceSpawnProfile.Standard,
                4 => Resources is ResourceSpawnProfile.Standard or ResourceSpawnProfile.High,
                _ => false
            });

        public static MatchWorldProfile Resolve(int configuredPlayers)
        {
            int players = Math.Clamp(configuredPlayers, 2, 8);
            return new((byte)Math.Min(players, 4), players == 2 ? ResourceSpawnProfile.Low
                : players <= 4 ? ResourceSpawnProfile.Standard : ResourceSpawnProfile.High);
        }
    }
}
