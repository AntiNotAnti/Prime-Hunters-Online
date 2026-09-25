using System;
using System.Collections.Generic;
using MphRead.Hud;
using MphRead.Mods.Combat;
using MphRead.Mods.Multiplayer;
using MphRead.Mods.Render;
using OpenTK.Mathematics;

namespace MphRead.Entities
{
    public partial class PlayerEntity
    {
        private static readonly ColorRgba _killFeedInk = new(238, 242, 248, 255);
        private static readonly ColorRgba _killFeedLocal = new(255, 218, 92, 255);
        private static readonly ColorRgba _killFeedHeadshot = new(255, 214, 74, 255);
        private static readonly ColorRgba _killFeedTeamkill = new(255, 112, 112, 255);
        private static readonly ColorRgba _killFeedSpecial = new(185, 196, 215, 255);
        private static readonly Vector4 _killFeedRule = new(0.72f, 0.78f, 0.88f, 0.22f);
        private static readonly Vector4 _killFeedLocalRule = new(1f, 0.72f, 0.08f, 0.9f);

        /// <summary>
        /// Match-wide confirmed death history. Drawn before the spectator and
        /// pause early-outs in DrawHudObjects so free camera and replay viewing
        /// retain the same context as a live player HUD.
        /// </summary>
        private void ModDrawKillFeed()
        {
            if (!Features.KillFeedEnabled || !_scene.GameState.Multiplayer)
            {
                return;
            }

            if (_scene.GameState.MatchState != MatchState.InProgress)
            {
                // A Scene can survive the transition into the results/rematch
                // flow. Do not let the previous round's last five deaths bleed
                // into the next one.
                _scene.KillFeed.Clear();
                return;
            }

            _scene.KillFeed.PruneExpired();
            IReadOnlyList<KillFeedEntry> entries = _scene.KillFeed.Entries;
            if (entries.Count == 0)
            {
                return;
            }

            float aspect = HudAspectFix;
            float right = 253;
            float width = 92;
            float left = right - width * aspect;
            // Keep the feed tucked directly under the radar. The old 126-wide
            // opaque cards were easy to read but occupied a large slab of the
            // view; this lane is intentionally closer to a scoreboard ticker:
            // compact text, weapon glyph and a hairline separator.
            float top = MphRead.Mods.Render.Radar.Enabled ? 62.5f : 9.5f;
            const float rowHeight = 8.8f;
            const float rowContentHeight = 7.3f;

            int shown = 0;
            for (int i = 0; i < entries.Count && shown < KillFeed.MaxVisible; i++)
            {
                KillFeedEntry entry = entries[i];
                double age = entry.AgeSeconds;
                if (age >= KillFeed.LifetimeSeconds)
                {
                    continue;
                }

                float alpha = 1;
                double fadeStart = KillFeed.LifetimeSeconds - KillFeed.FadeSeconds;
                if (age > fadeStart)
                {
                    alpha = (float)Math.Clamp(
                        (KillFeed.LifetimeSeconds - age) / KillFeed.FadeSeconds, 0, 1);
                }

                float y = top + shown * rowHeight;
                bool localInvolved = entry.KillerSlot == _scene.Players.MainPlayerIndex
                    || entry.VictimSlot == _scene.Players.MainPlayerIndex;

                // No card/background. The arena remains visible between every
                // glyph; a faint lower rule is enough to make rapid multi-kills
                // scan as separate rows. Local-player rows get one narrow gold
                // rail, matching the competitive HUD's accent without turning
                // the whole entry into a banner.
                Vector4 rule = _killFeedRule;
                rule.W *= alpha * (localInvolved ? 1.35f : 1f);
                _scene.DrawHudFlatBox(left, y + rowContentHeight, right,
                    y + rowContentHeight + 0.32f, rule);
                if (shown == 0)
                {
                    Vector4 topRule = _killFeedRule;
                    topRule.W *= alpha * 0.7f;
                    _scene.DrawHudFlatBox(left, y, right, y + 0.24f, topRule);
                }
                if (localInvolved)
                {
                    Vector4 accent = _killFeedLocalRule;
                    accent.W *= alpha;
                    _scene.DrawHudFlatBox(left, y + 0.45f, left + 0.8f * aspect,
                        y + rowContentHeight - 0.35f, accent);
                }

                const float textScale = 0.43f;
                ColorRgba killerColor = KillFeedNameColor(entry.KillerSlot, entry.KillerTeam);
                ColorRgba victimColor = KillFeedNameColor(entry.VictimSlot, entry.VictimTeam);
                DrawText2D(left + 2.6f * aspect, y + 0.85f, Align.Left, 0,
                    KillFeedName(entry.KillerName), killerColor, alpha: alpha, scale: textScale);
                DrawText2D(right - 1.6f * aspect, y + 0.85f, Align.Right, 0,
                    KillFeedName(entry.VictimName), victimColor, alpha: alpha, scale: textScale);

                float center = left + width * aspect / 2;
                bool drewIcon = entry.Kind == KillFeedKind.Weapon
                    && DrawKillFeedWeaponIcon(entry.Beam, center, y + 0.5f, alpha);
                if (!drewIcon)
                {
                    DrawText2D(center, y + 1.05f, Align.Center, 0,
                        KillFeedLabel(entry.Kind), _killFeedSpecial, alpha: alpha, scale: 0.36f);
                }

                if (entry.FriendlyFire)
                {
                    DrawText2D(center - 7.5f * aspect, y + 1.15f, Align.Right, 0,
                        "TK", _killFeedTeamkill, alpha: alpha, scale: 0.31f);
                }
                if (entry.Headshot)
                {
                    DrawText2D(center + 7.5f * aspect, y + 1.15f, Align.Left, 0,
                        "HS", _killFeedHeadshot, alpha: alpha, scale: 0.31f);
                }

                shown++;
            }
        }

        private ColorRgba KillFeedNameColor(int slot, int team)
        {
            if (_scene.GameState.Teams && team >= 0)
            {
                return TeamVisuals.Get(team).Color;
            }
            if (slot == _scene.Players.MainPlayerIndex)
            {
                return _killFeedLocal;
            }
            return _killFeedInk;
        }

        private static string KillFeedName(string name)
        {
            if (String.IsNullOrWhiteSpace(name))
            {
                return "PLAYER";
            }
            if (name.Length <= 9)
            {
                return name;
            }
            return name[..6] + "...";
        }

        private static string KillFeedLabel(KillFeedKind kind) => kind switch
        {
            KillFeedKind.Alt => "ALT",
            KillFeedKind.Bomb => "BOMB",
            KillFeedKind.Burn => "BURN",
            KillFeedKind.Deathalt => "DALT",
            KillFeedKind.Suicide => "SELF",
            KillFeedKind.Environment => "WORLD",
            _ => "KILL"
        };

        private bool DrawKillFeedWeaponIcon(BeamType beam, float centerX, float y, float alpha)
        {
            int index = (int)beam;
            if (index < 0 || index >= _weaponListIcons.Length || index > (int)BeamType.OmegaCannon)
            {
                return false;
            }

            HudObjectInstance icon = _weaponListIcons[index];
            if (icon == null)
            {
                return false;
            }

            IconBounds bounds = _weaponListIconBounds[index];
            const float side = 5.8f;
            float scale = side / Math.Max(bounds.Width, bounds.Height);
            float aspect = HudAspectFix;
            float oldX = icon.PositionX;
            float oldY = icon.PositionY;
            float oldAlpha = icon.Alpha;
            try
            {
                SmoothHudIcon.Tint(icon, _weaponListSheetData, index, _weaponListColors[index], _scene);
                icon.Alpha = Features.HudOpacity * alpha;
                icon.PositionX = (centerX - bounds.CentreX * scale * aspect) / 256f;
                icon.PositionY = (y + side / 2 - bounds.CentreY * scale) / 192f;
                _scene.DrawHudObject(icon, mode: 1, scale: scale);
            }
            finally
            {
                icon.PositionX = oldX;
                icon.PositionY = oldY;
                icon.Alpha = oldAlpha;
            }
            return true;
        }
    }
}
