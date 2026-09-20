using System;
using MphRead.Mods;
using MphRead.Mods.Network;
using MphRead.Hud;

namespace MphRead.Entities
{
    /// <summary>
    /// Network-specific HUD helpers.
    ///
    /// Kept on a PlayerEntity partial because the stock HUD drawing methods
    /// are private. This keeps network presentation policy out of the core
    /// player simulation while still letting the HUD consume authoritative
    /// state where prediction would otherwise be misleading.
    /// </summary>
    public partial class PlayerEntity
    {
        /// <summary>
        /// Server-owned match rule. Null/offline sessions preserve the stock
        /// behaviour and show opponent health.
        /// </summary>
        private static bool ModHideOpponentHealth =>
            NetSession.ActiveMatchDefinition?.HideOpponentHealth == true;

        /// <summary>
        /// Column centres in the HUD's 256-wide space. The stock two sit at
        /// 160 and 215, which leaves no room for a third: "deaths" is six
        /// characters at eight pixels each and ends at 239. In a networked
        /// match the two of them move left to make room, and offline nothing
        /// moves at all.
        /// </summary>
        private static int ModOpponentHudHealth(PlayerEntity opponent) => NetHudHealth.Sample(opponent).Health;
        private bool ModHudHealthVisible => NetHudHealth.Visible(SlotIndex);
        private int ModHudHealth => !NetSession.Active || SlotIndex == NetSession.LocalSlot
            ? Health : ModOpponentHudHealth(this);

        private const float _scoreColumn1Net = 145;
        private const float _scoreColumn2Net = 193;
        private const float _scoreColumn1Solo = 160;
        private const float _scoreColumn2Solo = 215;

        /// <summary>
        /// How far left the scoreboard moves while the results screen's
        /// pickers are up.
        ///
        /// The scoreboard is drawn across the whole frame and the pickers --
        /// the hunter, and the map ballot under it -- own the right-hand
        /// column of it, so the two were drawn through each other: the ping
        /// column sat entirely behind the panel and "deaths" ran half under
        /// its edge. Worked out from where the panel actually is rather than
        /// stated, because that edge moves with the window's shape (the panel
        /// is measured in height units, see <c>EndPanelWidth</c>) and a number
        /// written down here would be right on one monitor.
        ///
        /// Clamped, because there is a shape where this cannot be solved: a
        /// 4:3 window has 180 units to the left of the panel and a three
        /// column scoreboard with names in it wants more than that. The clamp
        /// keeps the common shapes exact and lets the extreme one overlap a
        /// little rather than pushing the portraits off the left edge.
        /// </summary>
        internal float ModScoreSqueeze
        {
            get
            {
                if (!EndScreen.Available)
                {
                    return 0;
                }
                float panelLeft = 254 - EndPanelWidth * HudAspectFix;
                float rightmost = (NetSession.Active ? _scoreColumn2Net : _scoreColumn2Solo) + 24;
                return Math.Clamp(rightmost - (panelLeft - 3), 0, 64);
            }
        }

        internal float ModScoreColumn1 =>
            (NetSession.Active ? _scoreColumn1Net : _scoreColumn1Solo) - ModScoreSqueeze;

        internal float ModScoreColumn2 =>
            (NetSession.Active ? _scoreColumn2Net : _scoreColumn2Solo) - ModScoreSqueeze;

        /// <summary>
        /// Where the portrait, the stars and the nickname sit. Half the
        /// columns' shift: the names are already close to the first column and
        /// moving both by the same amount would keep them that way while the
        /// portraits walked off the left edge of the screen.
        /// </summary>
        internal float ModScoreNameColumn => 60 - ModScoreSqueeze / 2;

        private const float _pingColumnX = 236;

        /// <summary>
        /// Whether the ping column is drawn at all.
        ///
        /// Not on the results screen: it is the one column of the three that
        /// answers a question about *now* -- am I warping, is it me or the
        /// server -- and nobody is playing. It has been on Tab for the whole
        /// match, and dropping it is what buys the other two the room to get
        /// out from under the picker.
        /// </summary>
        private bool ModPingColumnDrawn => NetSession.Active && !EndScreen.Available;

        internal void ModDrawPingHeader(float posY)
        {
            if (!ModPingColumnDrawn)
            {
                return;
            }
            DrawText2D(_pingColumnX, posY, Align.Center, 0, "ping",
                new ColorRgba(0x3FEF), fontSpacing: 8);
        }

        internal void ModDrawPingRow(float posY, ColorRgba rowColor, int slot)
        {
            if (!ModPingColumnDrawn || slot < 0 || slot >= NetSession.SlotPing.Length)
            {
                return;
            }
            int ping = NetSession.SlotPing[slot];
            // Zero means the server has not timed this peer yet -- a dash says
            // that, where "0" would read as a perfect connection.
            string text = ping <= 0 ? "--" : (ping > 999 ? "999" : ping.ToString());
            DrawText2D(_pingColumnX, posY, Align.Center, 0, text, PingColor(ping), fontSpacing: 8);
        }

        /// <summary>
        /// Green, amber, red -- the same reading every shooter's scoreboard
        /// has used since Quake, and the reason to show the number at all: a
        /// player who is warping is asking whether it is them or the server.
        /// </summary>
        private static ColorRgba PingColor(int ping)
        {
            if (ping <= 0)
            {
                return new ColorRgba(120, 120, 140, 255);
            }
            if (ping < 80)
            {
                return new ColorRgba(110, 231, 135, 255);
            }
            if (ping < 160)
            {
                return new ColorRgba(255, 200, 80, 255);
            }
            return new ColorRgba(255, 110, 110, 255);
        }
    }
}
