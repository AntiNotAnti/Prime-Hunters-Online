using System;
using MphRead.Entities;

namespace MphRead.Mods.Multiplayer
{
    /// <summary>Asset-free scoring regressions; rendered combat/objectives still require map tests.</summary>
    internal static class TeamGameplayTest
    {
        public static void Run(Action<bool, string> check)
        {
            // These checks never initialize/render an entity or process a world frame.
            PlayerEntity.Construct(null!);
            try
            {
                GameState.Teams = true;
                GameState.TeamCount = 4;
                GameState.Mode = GameMode.BattleTeams;
                int[] assignments = { 0, 1, 2, 3, 3, 2, 1, 0 };
                for (int i = 0; i < PlayerEntity.SlotCapacity; i++)
                {
                    PlayerEntity player = PlayerEntity.Players[i];
                    player.TeamIndex = assignments[i];
                    player.LoadFlags = LoadFlags.Active | LoadFlags.Initial;
                    player.Health = 99;
                    GameState.Points[i] = GameState.Kills[i] = GameState.Deaths[i] = 0;
                    GameState.TeamPoints[i] = GameState.TeamKills[i] = GameState.TeamDeaths[i] = 0;
                    GameState.Time[i] = GameState.TeamTime[i] = 0;
                }
                GameState.TeamPoints[0] = 10;
                GameState.TeamPoints[1] = 8;
                GameState.TeamPoints[2] = 9;
                GameState.TeamPoints[3] = 11;
                GameState.Points[7] = 20; // Individual score must not reorder teams.
                GameState.UpdateStandings();
                check(GameState.ActivePlayers == 8 && PlayerEntity.Players[GameState.ResultSlots[0]].TeamIndex == 3,
                    "standings include all four teams and prioritize team score");
                check(GameState.Standings[7] == 1 && GameState.Standings[4] == 0
                    && GameState.TeamStandings[7] == 0 && GameState.TeamStandings[0] == 1,
                    "non-parity teams, slot-seven rank and individual rank");
                for (int team = 0; team < 4; team++) GameState.TeamPoints[team] = 5;
                GameState.UpdateStandings();
                check(GameState.IsResultTie && Array.TrueForAll(GameState.Standings, rank => rank == 0),
                    "four-way team tie");
                GameState.TeamPoints[3] = 4;
                GameState.UpdateStandings();
                check(GameState.IsResultTie && GameState.Standings[3] == 3, "three-way team tie");
                GameState.TeamPoints[2] = 3;
                GameState.UpdateStandings();
                check(GameState.IsResultTie && GameState.Standings[5] == 3, "two-way team tie");
                GameState.Mode = GameMode.BountyTeams;
                GameState.TeamKills[1] = 1;
                GameState.UpdateStandings();
                check(!GameState.IsResultTie && PlayerEntity.Players[GameState.ResultSlots[0]].TeamIndex == 1,
                    "Bounty team kills break equal objective score");

                GameState.Mode = GameMode.SurvivalTeams;
                GameState.PointGoal = 2;
                for (int i = 0; i < 8; i++)
                {
                    PlayerEntity.Players[i].Health = assignments[i] == 0 ? 0 : 99;
                    GameState.TeamDeaths[i] = 3;
                }
                GameState.MatchTime = 60;
                GameState.UpdateSurvival(1 / 60f);
                check(GameState.MatchTime == 60, "Survival continues with three surviving teams after A eliminated");
                for (int i = 0; i < 8; i++) PlayerEntity.Players[i].Health = assignments[i] == 3 ? 99 : 0;
                GameState.UpdateSurvival(1 / 60f);
                check(GameState.MatchTime == 0 && GameState.Time[3] == -1 && GameState.Time[4] == -1,
                    "Survival ends with team D and preserves both winners");

                check(TeamRules.AreAllies(0, 0) && TeamRules.AreAllies(3, 3)
                    && !TeamRules.AreAllies(0, 3) && !TeamRules.AreAllies(-1, -1), "validated allied combat identities");
                GameState.Teams = false;
                check(!TeamRules.AreAllies(0, 0), "FFA never grants team immunity");
                for (int i = 0; i < 8; i++) PlayerEntity.Players[i].TeamIndex = i;
                GameState.Mode = GameMode.Battle;
                GameState.UpdateStandings();
                check(GameState.ResultSlots[0] == 7 && GameState.Standings[7] == 0, "FFA slot seven ranks first");
                for (int i = 0; i < 8; i++) PlayerEntity.Players[i].LoadFlags = LoadFlags.None;
                GameState.UpdateStandings();
                check(GameState.ActivePlayers == 0 && !GameState.IsResultTie, "empty result list is safe");
                check(NodeDefenseEntity.NoTeam < 0 && TeamVisuals.Get(2).Label == "Team C"
                    && TeamVisuals.Get(3).ModelRecolor == -1, "neutral ownership and C/D model fallback");
            }
            finally
            {
                PlayerEntity.Reset();
                GameState.Teams = false;
                GameState.TeamCount = 2;
                GameState.Mode = GameMode.SinglePlayer;
                GameState.ActivePlayers = 0;
            }
        }
    }
}
