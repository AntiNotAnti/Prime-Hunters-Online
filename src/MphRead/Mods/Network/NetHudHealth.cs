using MphRead.Entities;

namespace MphRead.Mods.Network
{
    public readonly record struct HudHealthSample(int Health, uint SnapshotFrame, ushort LifeId, bool Authoritative);

    public static class NetHudHealth
    {
        public static bool HideOpponents => NetSession.Active
            && NetSession.ServerSession is { Match.HideOpponentHealth: true };

        // A followed player is still somebody else's player. Playback has no privileged HP view.
        public static bool Visible(int slot) => !HideOpponents
            || (slot == NetSession.LocalSlot && !Mods.SpectatorMode.IsSpectating && !DemoPlayback.IsActive);

        public static HudHealthSample Sample(PlayerEntity player)
        {
            int slot = player.SlotIndex;
            if (NetSession.Active && !NetSession.IsAuthority && !NetSession.IsHost
                && slot >= 0 && slot < NetSession.RemoteStates.Length && NetSession.RemoteStateValid[slot])
            {
                PlayerState state = NetSession.RemoteStates[slot];
                if (state.SlotIndex == slot && state.LifeId != 0
                    && NetPlayerLifecycle.Matches(slot, state.SlotGeneration, state.LifeId))
                    return new(state.Health, NetSession.LastSnapshotFrame, state.LifeId, true);
            }
            bool authoritative = !NetSession.Active || NetSession.IsAuthority || NetSession.IsHost;
            return new(player.Health, authoritative ? NetSession.NetFrame : 0, NetPlayerLifecycle.Get(slot), authoritative);
        }
    }
}
