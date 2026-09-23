using System;

namespace MphRead.Mods.Launcher
{
    /// <summary>The active platform queues a local rematch after the results countdown.</summary>
    public static class OfflineRematch
    {
        public static Func<string, bool>? StartNext { get; set; }
        public static bool Continue() => StartNext?.Invoke(MapPick.Chosen()) == true;

        public static bool TryPlan(LaunchPlan played, string? selected, out LaunchPlan next)
        {
            next = default;
            if (played.Kind != LaunchKind.Offline || played.IsPlaytest) return false;
            string room = String.IsNullOrWhiteSpace(selected) ? played.RoomKey : selected;
            if (String.IsNullOrWhiteSpace(room) || MapPick.IsReturnToLobby(room)) return false;
            next = played with { RoomKey = room };
            return true;
        }
    }
}
