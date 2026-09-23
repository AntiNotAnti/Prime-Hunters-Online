using System;
using System.Text;
using MphRead.Formats;
using MphRead.Hud;
using MphRead.Mods.Multiplayer;

namespace MphRead.Entities
{
    public partial class PlayerEntity
    {
        private void ModDrawTeamScoreboard()
        {
            bool timed = _scene.GameState.Mode == GameMode.SurvivalTeams || _scene.GameState.Mode == GameMode.DefenderTeams;
            bool deaths = _scene.GameState.Mode == GameMode.BattleTeams || _scene.GameState.Mode == GameMode.SurvivalTeams;
            ColorRgba ink = new ColorRgba(239, 239, 247, 255);
            float y = 16;
            if (_scene.GameState.MatchState != MatchState.InProgress)
            {
                var winners = new StringBuilder(_scene.GameState.IsResultTie ? "TIE: " : "WINNER: ");
                int last = -1;
                for (int i = 0; i < _scene.GameState.ActivePlayers; i++)
                {
                    int slot = _scene.GameState.ResultSlots[i];
                    int team = _scene.Players.Items[slot].TeamIndex;
                    if (_scene.GameState.Standings[slot] != 0 || last == team) continue;
                    if (last != -1) winners.Append(" / ");
                    winners.Append((char)('A' + team));
                    last = team;
                }
                DrawText2D(ModScoreNameColumn - 18, 4, Align.Left, 0, winners.ToString(), ink, scale: 0.75f);
            }
            DrawText2D(ModScoreNameColumn - 18, y, Align.Left, 0, "TEAMS", ink, scale: 0.75f);
            DrawText2D(ModScoreColumn1, y, Align.Center, 0, timed ? "TIME" : "POINTS", ink);
            DrawText2D(ModScoreColumn2, y, Align.Center, 0, deaths ? "DEATHS" : "KILLS", ink);
            ModDrawPingHeader(y);
            y += 14;
            int previous = -1;
            // Eight compact player rows plus four team headers fit in the 192-unit HUD.
            for (int i = 0; i < _scene.GameState.ActivePlayers; i++)
            {
                int slot = _scene.GameState.ResultSlots[i];
                PlayerEntity player = _scene.Players.Items[slot];
                int team = player.TeamIndex;
                TeamPresentation visual = TeamVisuals.Get(team);
                if (team != previous)
                {
                    DrawText2D(ModScoreNameColumn - 18, y, Align.Left, 0, visual.Label, visual.Color, scale: 0.85f);
                    DrawText2D(ModScoreColumn1, y, Align.Center, 0,
                        TeamScoreValue(timed, _scene.GameState.TeamTime[team], _scene.GameState.TeamPoints[team]), visual.Color);
                    DrawText2D(ModScoreColumn2, y, Align.Center, 0,
                        (deaths ? _scene.GameState.TeamDeaths[team] : _scene.GameState.TeamKills[team]).ToString(), visual.Color);
                    previous = team;
                    y += 12;
                }
                int nameLength = Math.Clamp((int)((ModScoreColumn1 - ModScoreNameColumn - 6) / (6.4f * HudAspectFix)), 4, 20);
                string name = (player.IsMainPlayer ? "> " : "  ") + _scene.GameState.Nicknames[slot];
                DrawText2D(ModScoreNameColumn - 18, y, Align.Left, 0, name, ink, maxLength: nameLength, scale: 0.8f);
                DrawText2D(ModScoreColumn1, y, Align.Center, 0,
                    TeamScoreValue(timed, _scene.GameState.Time[slot], _scene.GameState.Points[slot]), ink);
                DrawText2D(ModScoreColumn2, y, Align.Center, 0,
                    (deaths ? _scene.GameState.Deaths[slot] : _scene.GameState.Kills[slot]).ToString(), ink);
                ModDrawPingRow(y, ink, slot);
                y += 13;
            }
        }

        private string TeamScoreValue(bool timed, float time, int points) => !timed ? points.ToString()
            : time < 0 ? "MAX" : FormatTime(TimeSpan.FromSeconds(time));
    }
}
