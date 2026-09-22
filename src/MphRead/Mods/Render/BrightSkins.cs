using System;
using MphRead.Entities;
using OpenTK.Mathematics;

namespace MphRead.Mods.Render
{
    /// <summary>Local surface colors for already-visible multiplayer bodies.</summary>
    public static class BrightSkins
    {
        public static Vector4? GetColor(PlayerEntity player)
        {
            // MPH authored only two team model palettes. Team C/D get a local
            // render-only fallback so 2v2v2/2v2v2v2 remain visually distinct.
            bool extendedTeam = player.OwningScene.GameState.Teams && player.TeamIndex >= 2
                && player.TeamIndex < Metadata.TeamColors.Length;
            if (!ShouldApply(RenderOptions.BrightSkins || extendedTeam, player.OwningScene.GameState.Multiplayer,
                player.IsMainPlayer, player.Health, player.Flags2, player.CurAlpha,
                player.BrightSkinStatusOverride))
            {
                return null;
            }
            return player.OwningScene.GameState.Teams ? GetTeamColor(player.TeamIndex)
                : GetSuitColor(player.Hunter, player.Recolor);
        }

        public static bool ShouldApply(PlayerEntity player)
        {
            return ShouldApply(RenderOptions.BrightSkins, player.OwningScene.GameState.Multiplayer, player.IsMainPlayer,
                player.Health, player.Flags2, player.CurAlpha, player.BrightSkinStatusOverride);
        }

        public static Vector4? GetOutlineColor(PlayerEntity player)
        {
            if (player.BrightSkinFrozenOverlay || !ShouldApply(RenderOptions.PlayerOutline != PlayerOutlineStyle.Off, player.OwningScene.GameState.Multiplayer,
                player.IsMainPlayer, player.Health, player.Flags2, player.CurAlpha, player.BrightSkinStatusOverride))
            {
                return null;
            }
            return ResolveOutlineColor(RenderOptions.PlayerOutline, player.OwningScene.GameState.Teams, player.TeamIndex);
        }

        internal static Vector4 ResolveOutlineColor(PlayerOutlineStyle style, bool teams, int teamIndex)
        {
            return style == PlayerOutlineStyle.Team && teams ? GetTeamColor(teamIndex) : new Vector4(1, 0.05f, 0.05f, 1);
        }

        internal static bool ShouldApply(bool enabled, bool multiplayer, bool mainPlayer,
            int health, PlayerFlags2 flags, float alpha, bool statusOverride)
        {
            // Passive Trace/Prime Hunter cloak and expiration fades do not set Cloaking.
            return enabled && multiplayer && !mainPlayer && health > 0 && alpha >= 1
                && !flags.TestFlag(PlayerFlags2.Cloaking) && !statusOverride;
        }

        internal static Vector4 GetSuitColor(Hunter hunter, int recolor)
        {
            ColorRgba color = HunterSuits.Color(hunter, recolor);
            return new Vector4(NormalizeBright(new Vector3(color.Red, color.Green, color.Blue) / 255f), 1);
        }

        internal static Vector4 GetTeamColor(int teamIndex)
        {
            // The same centralized, 5-bit colors used by objectives and the HUD.
            // No two-team switch here: additional definitions work without renderer changes.
            Vector3 rgb = teamIndex >= 0 && teamIndex < Metadata.TeamColors.Length
                ? Metadata.TeamColors[teamIndex] / 31f : new Vector3(0.6f);
            return new Vector4(NormalizeBright(rgb), 1);
        }

        internal static Vector3 NormalizeBright(Vector3 rgb)
        {
            float max = MathF.Max(rgb.X, MathF.Max(rgb.Y, rgb.Z));
            if (max <= 0)
            {
                return new Vector3(0.95f);
            }
            rgb *= 0.95f / max;
            float luminance = Vector3.Dot(rgb, new Vector3(0.2126f, 0.7152f, 0.0722f));
            // Scaling then clamping cannot lift saturated blue/red to the floor.
            // The minimum blend with white preserves hue while reducing saturation only as needed.
            const float minimumLuminance = 0.45f;
            if (luminance < minimumLuminance)
            {
                rgb = Vector3.Lerp(rgb, Vector3.One, (minimumLuminance - luminance) / (1 - luminance));
            }
            return Vector3.Clamp(rgb, Vector3.Zero, Vector3.One);
        }

        internal static Vector4? ForMaterial(Vector4? color, bool textured, float alpha, bool showTextures = true)
        {
            // Textured overrides multiply existing alpha; untextured overrides replace it.
            // Both vertex shaders output color.a=1, so reproduce mat_alpha only for that path.
            // The viewer's texture toggle also selects the untextured shader branch.
            if (color.HasValue && (!textured || !showTextures))
            {
                Vector4 value = color.Value;
                value.W = alpha;
                return value;
            }
            return color;
        }
    }
}

namespace MphRead.Entities
{
    public partial class PlayerEntity
    {
        internal bool BrightSkinStatusOverride => PaletteOverride != null || DoubleDamage || _targetAlpha < 1
            || _timeSinceDamage < Values.DamageFlashTime * 2;
        internal bool BrightSkinFrozenOverlay => _frozenGfxTimer > 0;
    }
}