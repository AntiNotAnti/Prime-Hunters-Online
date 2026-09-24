using System;
using System.Collections.Generic;

namespace MphRead.Mods.Launcher
{
    /// <summary>
    /// The save slots behind the adventure menu, and what a screen needs to
    /// say about each one.
    ///
    /// The engine has had the whole of adventure mode all along -- a
    /// <see cref="StorySave"/> with the checkpoint, the octoliths, the weapons
    /// and the logbook, written to Savedata/save###.json by
    /// <see cref="GameState.CommitSave"/>. What was missing was any way to
    /// reach it without the console menu, and one detail that made it look
    /// broken when reached: saving is gated on <see cref="Menu.SaveSlot"/>,
    /// which is 0 by default, and 0 means "no slot" -- so CommitSave returned
    /// without writing and a finished session left nothing behind. Choosing a
    /// slot here is what turns saving on.
    ///
    /// Platform-neutral on purpose: all three front screens (WinForms,
    /// Avalonia, console) drive the same logic from here rather than each
    /// deciding for itself where a new game starts.
    /// </summary>
    public static class AdventureSave
    {
        /// <summary>Slots offered by the menu. Slot 0 is "none" to the engine.</summary>
        public const int SlotCount = 3;

        /// <summary>
        /// Where a new game begins: UNIT2_LAND, the Celestial Archives landing
        /// site. A fresh <see cref="StorySave"/> unlocks Celestial Archives 1
        /// and 2 (Areas = 0xC) and nothing else, so it is the only place a new
        /// game could start -- and it is where the retail game starts.
        /// </summary>
        private const int _newGameRoomId = 45;

        // Indexed by the area id GetAreaInfo returns. Its own comments are the
        // source; there is no name table in the metadata to borrow.
        private static readonly string[] _areaNames =
        {
            "Alinos", "Alinos", "Celestial Archives", "Celestial Archives",
            "Vesper Defense Outpost", "Vesper Defense Outpost",
            "Arcterra", "Arcterra", "Oubliette"
        };

        /// <summary>What one slot holds, for a menu row.</summary>
        public readonly struct SlotInfo
        {
            public byte Slot { get; init; }
            public bool Used { get; init; }
            /// <summary>Where the game would resume, e.g. "Celestial Archives".</summary>
            public string Area { get; init; }
            public int Octoliths { get; init; }
            public int Health { get; init; }
            public int HealthMax { get; init; }

            /// <summary>One line for a menu row: the whole of what a slot says.</summary>
            public string Describe()
            {
                if (!Used)
                {
                    return "Empty";
                }
                return $"{Area} — {Octoliths}/8 octoliths";
            }
        }

        public static SlotInfo Read(byte slot)
        {
            StorySave? save = GameState.PeekSave(slot);
            if (save == null)
            {
                return new SlotInfo { Slot = slot, Used = false, Area = "" };
            }
            return new SlotInfo
            {
                Slot = slot,
                Used = true,
                Area = AreaName(save),
                Octoliths = save.CountFoundOctoliths(),
                Health = save.Health,
                HealthMax = save.HealthMax
            };
        }

        public static IReadOnlyList<SlotInfo> ReadAll()
        {
            var slots = new List<SlotInfo>(SlotCount);
            for (byte slot = 1; slot <= SlotCount; slot++)
            {
                slots.Add(Read(slot));
            }
            return slots;
        }

        private static string AreaName(StorySave save)
        {
            int roomId = save.CheckpointRoomId;
            if (roomId < 0)
            {
                return _areaNames[2];
            }
            int areaId = Metadata.GetAreaInfo(roomId);
            if (areaId < 0 || areaId >= _areaNames.Length)
            {
                return "Unknown";
            }
            return _areaNames[areaId];
        }

        /// <summary>
        /// Point the foreground state at a slot and load it, or start it fresh.
        ///
        /// Kept for callers that already own the foreground state. Match startup
        /// should use the overload that names the scene state explicitly: a new
        /// live <see cref="Scene"/> gets its own <see cref="SceneGameState"/>,
        /// and preparing the old bootstrap state before that scene exists loses
        /// the new-game save when the scene is constructed.
        /// </summary>
        public static string Begin(byte slot, bool newGame)
        {
            return Begin(GameState.Current, slot, newGame);
        }

        /// <summary>
        /// Prepare the exact state the Adventure scene will run.
        ///
        /// A new run deliberately does not read the slot first. Besides being
        /// the meaning of "new game", that lets a slot whose old JSON is stale
        /// or damaged be replaced instead of crashing while trying to load data
        /// the player explicitly asked to discard.
        /// </summary>
        public static string Begin(SceneGameState state, byte slot, bool newGame)
        {
            if (slot < 1 || slot > SlotCount)
            {
                throw new ArgumentOutOfRangeException(nameof(slot),
                    $"Adventure save slots are 1-{SlotCount}.");
            }

            Menu.SaveSlot = slot;
            if (newGame)
            {
                // A new game must not inherit the slot's old progress, and
                // must not write over it until the player actually saves.
                state.StartNewSave();
            }
            else
            {
                state.LoadSave();
            }
            return StartRoom(state.StorySave);
        }

        /// <summary>
        /// The room a save resumes in: its checkpoint, or the landing site of
        /// whichever area the checkpoint sits in when there is none yet.
        /// </summary>
        public static string StartRoom(StorySave save)
        {
            int roomId = save.CheckpointRoomId;
            if (roomId < 0)
            {
                roomId = _newGameRoomId;
            }
            RoomMetadata? meta = Metadata.GetRoomById(roomId, noThrow: true);
            if (meta == null)
            {
                meta = Metadata.GetRoomById(_newGameRoomId, noThrow: true);
            }
            return meta?.Name ?? "";
        }
    }
}
