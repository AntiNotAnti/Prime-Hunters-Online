using System;
using MphRead.Hud;
using MphRead.Mods;
using MphRead.Mods.Multiplayer;
using MphRead.Mods.Network;
using OpenTK.Mathematics;

namespace MphRead.Entities
{
    public partial class PlayerEntity
    {
        /// <summary>
        /// Full post-match table, styled like the live kill feed: no opaque
        /// slab, thin separators, team-coloured names, and a narrow gold rail
        /// on the local player's row.
        ///
        /// Online the rows come from the server's complete report packet.
        /// Offline bot matches use the same counters directly.
        /// </summary>
        private void ModDrawPostMatchReport()
        {
            if (!_scene.GameState.Multiplayer
                || _scene.GameState.MatchState != MatchState.Ending)
            {
                return;
            }

            bool networked = NetSession.Active;
            int count = networked
                ? NetSession.PostMatchReportCount
                : Math.Clamp(_scene.GameState.ActivePlayers, 0, PlayerEntity.SlotCapacity);

            float aspect = HudAspectFix;
            float left = 4f;
            float right = EndScreen.PanelAvailable
                ? 254f - EndPanelWidth * aspect - 3f * aspect
                : 252f;
            // Very narrow Android/4:3 combinations still need a readable lane.
            right = Math.Max(right, left + 142f * aspect);
            float width = right - left;

            const float titleY = 20f;
            const float headerY = 34f;
            const float firstRowY = 44f;
            const float rowHeight = 15.6f;
            const float contentHeight = 13.5f;

            DrawText2D(left + 1.8f * aspect, titleY, Align.Left, 0,
                "POST-MATCH REPORT", _killFeedInk, scale: 0.48f);
            DrawText2D(right - 1.2f * aspect, titleY + 0.5f, Align.Right, 0,
                count > 0 ? $"{count} PLAYER{(count == 1 ? "" : "S")}" : "RESULTS",
                _killFeedSpecial, scale: 0.34f);

            Vector4 titleRule = _killFeedRule;
            titleRule.W *= 1.15f;
            _scene.DrawHudFlatBox(left, titleY + 8.2f, right, titleY + 8.55f, titleRule);

            if (networked && count == 0)
            {
                DrawText2D(left + width / 2f, 82f, Align.Center, 0,
                    "SYNCING RESULTS...", _killFeedSpecial, scale: 0.43f);
                return;
            }

            float kdX = left + width * 0.52f;
            float accX = left + width * 0.67f;
            float dmgX = left + width * 0.86f;
            DrawText2D(left + 7f * aspect, headerY, Align.Left, 0,
                "PLAYER", _killFeedSpecial, scale: 0.31f);
            DrawText2D(kdX, headerY, Align.Center, 0,
                "K/D", _killFeedSpecial, scale: 0.31f);
            DrawText2D(accX, headerY, Align.Center, 0,
                "ACC", _killFeedSpecial, scale: 0.31f);
            DrawText2D(dmgX, headerY, Align.Center, 0,
                "DMG OUT", _killFeedSpecial, scale: 0.31f);

            int localSlot = networked && NetSession.LocalSlot >= 0
                ? NetSession.LocalSlot
                : _scene.Players.MainPlayerIndex;
            int rows = Math.Min(count, PlayerEntity.SlotCapacity);
            SceneGameState state = _scene.GameState;

            for (int i = 0; i < rows; i++)
            {
                int slot;
                int team;
                string name;
                if (networked)
                {
                    slot = NetSession.PostMatchReportSlot(i);
                    team = NetSession.PostMatchReportTeam(i);
                    name = NetSession.PostMatchReportName(i);
                }
                else
                {
                    slot = state.ResultSlots[i];
                    if ((uint)slot >= PlayerEntity.SlotCapacity)
                    {
                        continue;
                    }
                    PlayerEntity player = _scene.Players.Items[slot];
                    team = player.TeamIndex;
                    name = state.Nicknames[slot];
                }
                if ((uint)slot >= PlayerEntity.SlotCapacity)
                {
                    continue;
                }

                float y = firstRowY + i * rowHeight;
                bool local = slot == localSlot;

                Vector4 rule = _killFeedRule;
                rule.W *= local ? 1.45f : 1f;
                if (i == 0)
                {
                    Vector4 topRule = _killFeedRule;
                    topRule.W *= 0.75f;
                    _scene.DrawHudFlatBox(left, y - 1f, right, y - 0.7f, topRule);
                }
                _scene.DrawHudFlatBox(left, y + contentHeight, right,
                    y + contentHeight + 0.34f, rule);
                if (local)
                {
                    _scene.DrawHudFlatBox(left, y + 0.25f,
                        left + 0.9f * aspect, y + contentHeight - 0.35f,
                        _killFeedLocalRule);
                }

                ColorRgba nameColor = state.Teams && team >= 0
                    ? TeamVisuals.Get(team).Color
                    : local ? _killFeedLocal : _killFeedInk;

                DrawText2D(left + 2f * aspect, y + 0.8f, Align.Left, 0,
                    $"#{i + 1}", _killFeedSpecial, scale: 0.31f);
                DrawText2D(left + 8.2f * aspect, y + 0.35f, Align.Left, 0,
                    PostMatchName(name), nameColor, scale: 0.43f);

                int shots = Math.Max(0, state.ShotsFired[slot]);
                int hits = Math.Min(shots, Math.Max(0, state.ShotsHit[slot]));
                int accuracy = shots == 0 ? 0
                    : (int)Math.Round(hits * 100.0 / shots);

                DrawText2D(kdX, y + 0.55f, Align.Center, 0,
                    $"{state.Kills[slot]}/{state.Deaths[slot]}",
                    _killFeedInk, scale: 0.40f);
                DrawText2D(accX, y + 0.55f, Align.Center, 0,
                    $"{accuracy}%", _killFeedInk, scale: 0.40f);
                DrawText2D(dmgX, y + 0.55f, Align.Center, 0,
                    state.MatchDamageDealt[slot].ToString(),
                    _killFeedInk, scale: 0.40f);

                DrawText2D(right - 1.2f * aspect, y + 7.15f, Align.Right, 0,
                    $"DMG IN {state.MatchDamageTaken[slot]}   HS {state.HeadshotKills[slot]}   BEST {state.LongestKillStreak[slot]}",
                    _killFeedSpecial, scale: 0.29f);
            }
        }

        private static string PostMatchName(string name)
        {
            if (String.IsNullOrWhiteSpace(name))
            {
                return "PLAYER";
            }
            name = name.ToUpperInvariant();
            return name.Length <= 12 ? name : name[..9] + "...";
        }
    }
}
