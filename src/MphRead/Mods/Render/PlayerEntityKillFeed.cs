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
            float right = 254;
            float width = 126;
            float left = right - width * aspect;
            // The radar occupies roughly HUD y 10..61. When it is present the
            // feed starts beneath it; otherwise it uses the same upper-right
            // lane without colliding with the FPS counter.
            float top = MphRead.Mods.Render.Radar.Enabled ? 64 : 10;
            const float rowHeight = 12;
            const float panelHeight = 10;

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

                _scene.DrawHudFlatBox(left, y, right, y + panelHeight,
                    new Vector4(0, 0, 0, (localInvolved ? 0.68f : 0.54f) * alpha));
                if (localInvolved)
                {
                    _scene.DrawHudFlatBox(left, y, left + 1.2f * aspect, y + panelHeight,
                        new Vector4(1f, 0.75f, 0.2f, 0.9f * alpha));
                }

                const float textScale = 0.55f;
                ColorRgba killerColor = KillFeedNameColor(entry.KillerSlot, entry.KillerTeam);
                ColorRgba victimColor = KillFeedNameColor(entry.VictimSlot, entry.VictimTeam);
                DrawText2D(left + 4 * aspect, y + 1.1f, Align.Left, 0,
                    KillFeedName(entry.KillerName), killerColor, alpha: alpha, scale: textScale);
                DrawText2D(right - 4 * aspect, y + 1.1f, Align.Right, 0,
                    KillFeedName(entry.VictimName), victimColor, alpha: alpha, scale: textScale);

                float center = left + width * aspect / 2;
                bool drewIcon = entry.Kind == KillFeedKind.Weapon
                    && DrawKillFeedWeaponIcon(entry.Beam, center, y + 1, alpha);
                if (!drewIcon)
                {
                    DrawText2D(center, y + 1.5f, Align.Center, 0,
                        KillFeedLabel(entry.Kind), _killFeedSpecial, alpha: alpha, scale: 0.48f);
                }

                if (entry.FriendlyFire)
                {
                    DrawText2D(center - 11 * aspect, y + 1.8f, Align.Right, 0,
                        "TK", _killFeedTeamkill, alpha: alpha, scale: 0.42f);
                }
                if (entry.Headshot)
                {
                    DrawText2D(center + 11 * aspect, y + 1.8f, Align.Left, 0,
                        "HS", _killFeedHeadshot, alpha: alpha, scale: 0.42f);
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
            if (name.Length <= 10)
            {
                return name;
            }
            return name[..7] + "...";
        }

        private static string KillFeedLabel(KillFeedKind kind) => kind switch
        {
            KillFeedKind.Alt => "ALT",
            KillFeedKind.Bomb => "BOMB",
            KillFeedKind.Burn => "BURN",
            KillFeedKind.Deathalt => "DEATHALT",
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
            const float side = 8;
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
