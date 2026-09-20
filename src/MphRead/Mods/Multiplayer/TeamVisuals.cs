using MphRead.Entities;
using MphRead.Formats;

namespace MphRead.Mods.Multiplayer
{
    public readonly record struct TeamPresentation(string Label, ColorRgba Color,
        ColorRgb ObjectiveColor, Team ModelTeam, int ModelRecolor)
    {
        public ColorRgba HudAccent => Color;
        public ColorRgb RadarColor => ObjectiveColor;
    }

    public static class TeamVisuals
    {
        private static readonly TeamPresentation[] _teams =
        {
            new("Team A", new ColorRgba(255, 156, 0, 255), new ColorRgb(31, 19, 0), Team.Orange, 4),
            new("Team B", new ColorRgba(0, 255, 0, 255), new ColorRgb(0, 31, 0), Team.Green, 5),
            new("Team C", new ColorRgba(41, 156, 255, 255), new ColorRgb(5, 19, 31), Team.None, -1),
            new("Team D", new ColorRgba(222, 66, 255, 255), new ColorRgb(27, 8, 31), Team.None, -1)
        };
        private static readonly TeamPresentation _neutral = new("Unassigned",
            new ColorRgba(255, 255, 255, 255), new ColorRgb(31, 31, 31), Team.None, -1);

        public static TeamPresentation Get(int teamIndex) =>
            (uint)teamIndex < _teams.Length ? _teams[teamIndex] : _neutral;

        public static void Apply(PlayerEntity player)
        {
            TeamPresentation visual = Get(player.TeamIndex);
            player.Team = visual.ModelTeam;
            // Original models only provide two team skins. C/D keep a normal hunter suit.
            if (visual.ModelRecolor >= 0) player.Recolor = visual.ModelRecolor;
            else if (player.Recolor >= 4) player.Recolor = 0;
        }
    }
}
