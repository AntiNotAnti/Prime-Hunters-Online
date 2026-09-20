using MphRead.Entities;

namespace MphRead.Mods
{
    /// <summary>
    /// Where the engine says "a jump pad launched this player" and "a
    /// teleporter moved this player".
    ///
    /// The map audit used to count jump pads and teleporters and stop there,
    /// which answers whether a map contains one, not whether it works. The
    /// difference is not academic: a teleporter's per-slot state was sized
    /// for four players, and with eight it threw on the frame somebody
    /// stepped on it -- an inventory of the room would have called that map
    /// healthy right up to the crash.
    ///
    /// Kept per slot, because the audit stands one player on one pad while
    /// seven others are still running the tour: a global counter going up
    /// answers a different question from the one being asked.
    ///
    /// Off unless a test turns it on, and when off it costs one boolean test
    /// on the frame a player touches a pad.
    /// </summary>
    public static class WorldEvents
    {
        /// <summary>Set by the audit; nothing in a normal session turns it on.</summary>
        public static bool Watching { get; set; }

        private static readonly int[] _jumpPads = new int[PlayerEntity.SlotCapacity];
        private static readonly int[] _teleports = new int[PlayerEntity.SlotCapacity];
        private static readonly int[] _lastJumpPadId = new int[PlayerEntity.SlotCapacity];
        private static readonly int[] _lastTeleporterId = new int[PlayerEntity.SlotCapacity];

        public static void Reset()
        {
            for (int i = 0; i < PlayerEntity.SlotCapacity; i++)
            {
                _jumpPads[i] = 0;
                _teleports[i] = 0;
                _lastJumpPadId[i] = -1;
                _lastTeleporterId[i] = -1;
            }
        }

        public static int JumpPadsFor(int slot) => Valid(slot) ? _jumpPads[slot] : 0;

        public static int TeleportsFor(int slot) => Valid(slot) ? _teleports[slot] : 0;

        /// <summary>Entity id of the last pad that launched this slot, or -1.</summary>
        public static int LastJumpPadId(int slot) => Valid(slot) ? _lastJumpPadId[slot] : -1;

        public static int LastTeleporterId(int slot) => Valid(slot) ? _lastTeleporterId[slot] : -1;

        public static void NoteJumpPad(PlayerEntity player, int entityId)
        {
            if (!Watching || !Valid(player.SlotIndex))
            {
                return;
            }
            _jumpPads[player.SlotIndex]++;
            _lastJumpPadId[player.SlotIndex] = entityId;
        }

        public static void NoteTeleport(PlayerEntity player, int entityId)
        {
            if (!Watching || !Valid(player.SlotIndex))
            {
                return;
            }
            _teleports[player.SlotIndex]++;
            _lastTeleporterId[player.SlotIndex] = entityId;
        }

        private static bool Valid(int slot) => slot >= 0 && slot < PlayerEntity.SlotCapacity;
    }
}
