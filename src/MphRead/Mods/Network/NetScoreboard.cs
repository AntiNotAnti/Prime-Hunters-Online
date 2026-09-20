using MphRead.Entities;

namespace MphRead.Mods.Network
{
    /// <summary>
    /// The scoreboard's half of "whoever is arriving is not whoever left".
    ///
    /// Every other per-slot record the net code keeps is cleared when a slot
    /// changes hands -- the reported positions and frame numbers, the spawn
    /// barriers, the damage sequence, the wire's intent validity (see
    /// NetPlayerBridge, NetDamage and NetSession's own ForgetSlot). The score
    /// was the one that was not, and it is the one a player can see: kill
    /// somebody, leave the match, come back into the same slot, and the kill
    /// was still on the board with your name against it.
    ///
    /// It has to be cleared on every machine and not only on the one that
    /// left, because the scoreboard belongs to the authority: it publishes
    /// Points, Kills and Deaths for every slot in each snapshot and every
    /// other client adopts them (NetPlayerBridge.ApplyState). A client that
    /// cleared its own copy would have it handed straight back.
    ///
    /// Team totals are not touched. They are recomputed from these every
    /// update (GameState.UpdateStandings sums Points into TeamPoints), so
    /// clearing the slot is what clears the team, and clearing the team
    /// directly would be wrong in a team mode anyway -- the points were the
    /// team's, and the team is still playing.
    /// </summary>
    public static class NetScoreboard
    {
        /// <summary>Everything the match attributes to one slot, for a slot about to change hands.</summary>
        public static void ForgetSlot(int slot)
        {
            if (slot < 0 || slot >= PlayerEntity.SlotCapacity)
            {
                return;
            }
            GameState.Points[slot] = 0;
            GameState.Kills[slot] = 0;
            GameState.Deaths[slot] = 0;
            GameState.Time[slot] = 0;
            GameState.Suicides[slot] = 0;
            GameState.FriendlyKills[slot] = 0;
            GameState.HeadshotKills[slot] = 0;
            GameState.DamageCount[slot] = 0;
            GameState.AltDamageCount[slot] = 0;
            GameState.BeamDamageDealt[slot] = 0;
            GameState.BeamDamageMax[slot] = 0;
            GameState.OctolithScores[slot] = 0;
            GameState.OctolithDrops[slot] = 0;
            GameState.OctolithStops[slot] = 0;
            GameState.NodesCaptured[slot] = 0;
            GameState.NodesLost[slot] = 0;
            GameState.KillsAsPrime[slot] = 0;
            GameState.PrimesKilled[slot] = 0;
            for (int beam = 0; beam < 9; beam++)
            {
                GameState.BeamKills[slot, beam] = 0;
            }
        }
    }
}
