using System;

namespace MphRead.Mods.Multiplayer
{
    public readonly record struct TeamLayout(byte TeamCount, byte TeamA, byte TeamB, byte TeamC = 0, byte TeamD = 0)
    {
        public int TotalPlayers => TeamA + TeamB + TeamC + TeamD;
        public byte Capacity(int team) => team switch { 0 => TeamA, 1 => TeamB, 2 => TeamC, 3 => TeamD, _ => 0 };
        public bool IsValid
        {
            get
            {
                if (TeamCount is < 2 or > 4 || TotalPlayers is < 2 or > 8) return false;
                for (int team = 0; team < 4; team++)
                    if (team < TeamCount ? Capacity(team) == 0 : Capacity(team) != 0) return false;
                return true;
            }
        }
        public override string ToString() => TeamCount switch
        {
            2 => $"{TeamA}v{TeamB}", 3 => $"{TeamA}v{TeamB}v{TeamC}",
            4 => $"{TeamA}v{TeamB}v{TeamC}v{TeamD}", _ => "FFA"
        };
    }

    public static class TeamRules
    {
        public const int NoTeam = -1;
        public static bool AreAllies(int first, int second) => GameState.Teams
            && first >= 0 && first < GameState.TeamCount && first == second;

        // Cross multiplication keeps normalized occupancy exact and deterministic.
        public static sbyte ChooseTeam(TeamLayout layout, ReadOnlySpan<int> counts)
        {
            int best = -1;
            for (int team = 0; team < layout.TeamCount; team++)
            {
                int capacity = layout.Capacity(team);
                if (capacity == 0 || counts[team] >= capacity) continue;
                if (best < 0 || counts[team] * layout.Capacity(best) < counts[best] * capacity
                    || (counts[team] * layout.Capacity(best) == counts[best] * capacity && counts[team] < counts[best])) best = team;
            }
            return (sbyte)best;
        }
    }
}
