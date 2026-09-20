using System;
using MphRead.Entities;
using MphRead.Formats;

namespace MphRead
{
    public static partial class GameState
    {
        public static bool IsResultTie
        {
            get
            {
                if (ActivePlayers == 0) return false;
                int leader = ResultSlots[0];
                for (int i = 1; i < ActivePlayers; i++)
                {
                    int slot = ResultSlots[i];
                    if (Standings[slot] == 0 && (!Teams
                        || PlayerEntity.Players[slot].TeamIndex != PlayerEntity.Players[leader].TeamIndex))
                    {
                        return true;
                    }
                }
                return false;
            }
        }

        // ResultSlots groups tied teams deterministically; Standings preserves the tie.
        internal static void UpdateStandings()
        {
            ActivePlayers = 0;
            Span<bool> represented = stackalloc bool[4];
            for (int slot = 0; slot < PlayerEntity.SlotCapacity; slot++)
            {
                Standings[slot] = TeamStandings[slot] = PlayerEntity.SlotCapacity - 1;
                PlayerEntity player = PlayerEntity.Players[slot];
                if (!player.LoadFlags.TestFlag(LoadFlags.Active)
                    || (uint)player.TeamIndex >= (uint)(Teams ? TeamCount : PlayerEntity.SlotCapacity)) continue;
                ResultSlots[ActivePlayers++] = slot;
                if (Teams) represented[player.TeamIndex] = true;
            }
            for (int i = 0; i < ActivePlayers; i++)
            {
                for (int j = i + 1; j < ActivePlayers; j++)
                {
                    int first = ResultSlots[i];
                    int second = ResultSlots[j];
                    int firstTeam = PlayerEntity.Players[first].TeamIndex;
                    int secondTeam = PlayerEntity.Players[second].TeamIndex;
                    int compare;
                    if (Teams && firstTeam != secondTeam)
                    {
                        compare = CompareTeams(firstTeam, secondTeam);
                        if (compare == 0) compare = secondTeam.CompareTo(firstTeam);
                    }
                    else
                    {
                        compare = ComparePlayers(first, second);
                        if (compare == 0) compare = second.CompareTo(first);
                    }
                    if (compare < 0) (ResultSlots[i], ResultSlots[j]) = (second, first);
                }
            }
            for (int i = 0; i < ActivePlayers; i++)
            {
                int slot = ResultSlots[i];
                int team = PlayerEntity.Players[slot].TeamIndex;
                int rank = 0;
                int memberRank = 0;
                if (Teams)
                {
                    for (int other = 0; other < TeamCount; other++)
                    {
                        if (represented[other] && CompareTeams(other, team) > 0) rank++;
                    }
                }
                for (int j = 0; j < ActivePlayers; j++)
                {
                    int other = ResultSlots[j];
                    if (ComparePlayers(other, slot) <= 0) continue;
                    if (!Teams) rank++;
                    else if (PlayerEntity.Players[other].TeamIndex == team) memberRank++;
                }
                Standings[slot] = rank;
                TeamStandings[slot] = memberRank;
            }
        }
    }
}
