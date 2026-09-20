using MphRead.Formats;
using OpenTK.Mathematics;

namespace MphRead.Mods.Render
{
    /// <summary>
    /// A round motion-tracker overlay, top-right under the FPS counter.
    /// Nothing like it shipped on the DS -- the bottom screen never showed
    /// nearby players or items -- so there is no HUD sprite to cut it from
    /// and every shape it draws is the same flat-fill trick
    /// <see cref="Crosshair"/> already uses. Reusing the real
    /// <c>hud_icon_player</c> locator-icon model for the hunter blip was
    /// tried and dropped: that asset is a 3D model meant to be drawn from
    /// the pass that still has the perspective camera live, and calling it
    /// from this flat 2D HUD pass picks up the wrong projection. No compact
    /// icon exists for a weapon or a power-up in the retail data either way,
    /// so every blip is a flat shape, coloured by category.
    ///
    /// On by default -- Settings → Game → HUD → "Radar" (called "Motion
    /// tracker" until asked otherwise; it sat on the same page as the
    /// "Hunter radar" match rule for that reason, and now does not). Also
    /// reachable from the command line (<c>-radar on</c>) for screenshot
    /// commands that open no launcher. Used to offer a choice of four
    /// looks; cut down to the one that stuck, on request.
    /// </summary>
    public static class Radar
    {
        public static bool Enabled { get; set; } = true;

        /// <summary>The filled disc behind everything else. Off by default --
        /// the dial reads fine as blips over the game with no backing plate,
        /// and a solid disc in the corner is the one thing on this HUD that
        /// blocks a view a player might want.</summary>
        public static bool ShowBackground { get; set; } = false;

        /// <summary>
        /// The range ring(s) and the forward cone -- the dial's outline, as
        /// opposed to what is on it. Off with <see cref="ShowBackground"/>
        /// also off leaves only the hunter/weapon/power-up blips floating
        /// with nothing around them, and the centre marker standing in for
        /// this player, which stays regardless -- a reading with no "you are
        /// here" at all is not a reading.
        /// </summary>
        public static bool ShowOutlines { get; set; } = true;

        /// <summary>World units the outer ring represents.</summary>
        public const float Range = 24f;

        /// <summary>
        /// A weapon pickup (or the ammo/upgrade for one) versus everything
        /// else an item spawner can drop. The DS data has no third bucket for
        /// "ammo" that a radar reader would tell apart from "gun", so ammo is
        /// grouped with the gun it feeds.
        /// </summary>
        public static bool IsWeaponItem(ItemType type)
        {
            switch (type)
            {
                case ItemType.VoltDriver:
                case ItemType.Battlehammer:
                case ItemType.Imperialist:
                case ItemType.Judicator:
                case ItemType.Magmaul:
                case ItemType.ShockCoil:
                case ItemType.OmegaCannon:
                case ItemType.AffinityWeapon:
                case ItemType.PickWpnMissile:
                case ItemType.MissileExpansion:
                case ItemType.UASmall:
                case ItemType.UABig:
                case ItemType.MissileSmall:
                case ItemType.MissileBig:
                case ItemType.UAExpansion:
                    return true;
                default:
                    return false;
            }
        }

        public readonly struct Palette
        {
            /// <summary>The round backdrop.</summary>
            public readonly Vector4 Background;
            /// <summary>The range ring(s).</summary>
            public readonly Vector4 Ring;
            /// <summary>The forward view-cone lines, or alpha 0 for none.</summary>
            public readonly Vector4 Cone;
            /// <summary>The centre marker standing in for this player.</summary>
            public readonly Vector4 Player;
            public readonly Vector4 Hunter;
            public readonly Vector4 Weapon;
            public readonly Vector4 Powerup;

            public Palette(Vector4 background, Vector4 ring, Vector4 cone, Vector4 player,
                Vector4 hunter, Vector4 weapon, Vector4 powerup)
            {
                Background = background;
                Ring = ring;
                Cone = cone;
                Player = player;
                Hunter = hunter;
                Weapon = weapon;
                Powerup = powerup;
            }
        }

        public static readonly Palette PaletteOf = new Palette(
            background: new Vector4(0.05f, 0.08f, 0.1f, 0.5f),
            ring: new Vector4(0.6f, 0.85f, 0.9f, 1f),
            cone: new Vector4(0.6f, 0.85f, 0.9f, 1f),
            player: new Vector4(0.92f, 0.94f, 0.98f, 1f),
            hunter: new Vector4(0.35f, 0.95f, 0.35f, 1f),
            weapon: new Vector4(1f, 0.65f, 0.2f, 1f),
            powerup: new Vector4(1f, 0.4f, 0.8f, 1f));
    }
}
