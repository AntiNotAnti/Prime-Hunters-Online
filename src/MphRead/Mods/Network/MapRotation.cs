using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace MphRead.Mods.Network
{
    /// <summary>
    /// One entry in the server's map cycle.
    /// </summary>
    public sealed class RotationEntry
    {
        public string RoomKey { get; init; } = "MP3 PROVING GROUND";
        public GameMode Mode { get; init; } = GameMode.Battle;
        /// <summary>Match length in seconds. Zero means "no time limit".</summary>
        public float TimeLimit { get; init; } = 7 * 60;
        public int PointGoal { get; init; } = 7;

        public override string ToString()
        {
            return $"{RoomKey} ({Mode}, {TimeLimit / 60:0.#} min, {PointGoal} pts)";
        }
    }

    /// <summary>
    /// Server-side map cycle, in the shape Quake 3 admins expect: a plain
    /// text file listing maps in order, the server advancing to the next one
    /// when the time limit or point goal is reached, and wrapping at the end.
    ///
    /// Deliberately a file rather than compiled-in defaults -- a dedicated
    /// server is usually reconfigured by someone with shell access and no
    /// build toolchain.
    /// </summary>
    public sealed class MapRotation
    {
        private readonly List<RotationEntry> _entries = new();
        private int _index;

        public IReadOnlyList<RotationEntry> Entries => _entries;
        public RotationEntry Current => _override
            ?? (_entries.Count > 0 ? _entries[_index] : _fallback);

        /// <summary>
        /// What comes after this match. A map the room has voted in jumps the
        /// queue; otherwise it is the cycle's own next entry, which is where
        /// the cycle resumes after a voted map as well -- the vote borrows a
        /// turn, it does not move the index.
        /// </summary>
        public RotationEntry Next => _pending
            ?? (_entries.Count > 0 ? _entries[(_index + 1) % _entries.Count] : _fallback);
        public int Index => _index;

        private static readonly RotationEntry _fallback = new();

        /// <summary>
        /// A rotation of exactly one match, for a player hosting from the
        /// launcher: they picked a map, and a rotation file they have never
        /// heard of should not send them somewhere else after seven minutes.
        /// </summary>
        public static MapRotation SingleMatch(string roomKey, GameMode mode, float timeLimit, int pointGoal)
        {
            var rotation = new MapRotation();
            rotation._entries.Add(new RotationEntry
            {
                RoomKey = roomKey,
                Mode = mode == GameMode.None ? GameMode.Battle : mode,
                TimeLimit = timeLimit,
                PointGoal = pointGoal
            });
            return rotation;
        }

        /// <summary>
        /// The cycle a player built in the launcher, in the order they built
        /// it.
        ///
        /// The time limit and point goal are one pair for the whole list
        /// rather than one per entry: the screen asks for a match, not for a
        /// config file, and four numbers per map is the shape of the thing
        /// this was written to avoid making anybody edit.
        /// </summary>
        public static MapRotation FromList(
            IReadOnlyList<(string RoomKey, GameMode Mode)> maps,
            float timeLimit, int pointGoal)
        {
            var rotation = new MapRotation();
            foreach ((string room, GameMode mode) in maps)
            {
                if (room.Length == 0)
                {
                    continue;
                }
                rotation._entries.Add(new RotationEntry
                {
                    RoomKey = room,
                    Mode = mode == GameMode.None ? GameMode.Battle : mode,
                    TimeLimit = timeLimit,
                    PointGoal = pointGoal
                });
            }
            if (rotation._entries.Count == 0)
            {
                rotation._entries.Add(_fallback);
            }
            return rotation;
        }

        /// <summary>
        /// The same cycle as a rotation file, for a server started as its own
        /// process -- which is handed its maps on disk rather than on the wire.
        /// </summary>
        public static void WriteList(string path,
            IReadOnlyList<(string RoomKey, GameMode Mode)> maps,
            float timeLimit, int pointGoal)
        {
            var lines = new List<string>
            {
                $"# Written by {Mods.Branding.Name}'s create-server screen.",
                "# One match per line:  ROOM KEY | mode | minutes | points",
                ""
            };
            foreach ((string room, GameMode mode) in maps)
            {
                if (room.Length == 0)
                {
                    continue;
                }
                lines.Add(String.Format(CultureInfo.InvariantCulture,
                    "{0} | {1} | {2:0.#} | {3}", room,
                    mode == GameMode.None ? GameMode.Battle : mode,
                    timeLimit / 60, pointGoal));
            }
            File.WriteAllLines(path, lines);
        }

        /// <summary>
        /// A map to play once, before the cycle carries on from where it was.
        ///
        /// What a passed vote leaves behind. Not an insertion into the list:
        /// the rotation is the admin's, a vote borrows the next slot in it
        /// rather than editing it, and a map the room voted for once should
        /// not come round again every cycle afterwards.
        /// </summary>
        private RotationEntry? _pending;

        public void PlayNext(string roomKey, GameMode mode)
        {
            // The time limit and point goal of whatever this server is
            // already playing to. A vote is about the map; changing how long
            // the match runs as a side effect of it is not what was asked.
            RotationEntry current = Current;
            _pending = new RotationEntry
            {
                RoomKey = roomKey,
                Mode = mode == GameMode.None ? current.Mode : mode,
                TimeLimit = current.TimeLimit,
                PointGoal = current.PointGoal
            };
        }

        /// <summary>
        /// Put the borrowed turn back, because whatever claimed it no longer
        /// has the room's agreement -- a results-screen vote whose leader
        /// changed, or one that fell back under the threshold when somebody
        /// took their pick back or left. Without this a map could be taken by
        /// a majority that existed for one second.
        /// </summary>
        public void ClearPending()
        {
            _pending = null;
        }

        /// <summary>Advance to the next map, wrapping at the end of the cycle.</summary>
        public RotationEntry Advance()
        {
            if (_pending != null)
            {
                // Taken rather than stepped over: the cycle's own index does
                // not move, so the map that was coming next is still coming
                // next once this one is done.
                _override = _pending;
                _pending = null;
                return Current;
            }
            if (_override != null)
            {
                // The borrowed turn is over. Step the index as a normal
                // advance would have: the cycle was on entry N when the vote
                // came in, so what follows the voted map is N+1 -- not N
                // again, which would replay the map the vote was called to
                // get away from.
                _override = null;
            }
            if (_entries.Count > 0)
            {
                _index = (_index + 1) % _entries.Count;
            }
            return Current;
        }

        /// <summary>The one-off map being played right now, if any.</summary>
        private RotationEntry? _override;

        /// <summary>
        /// Load a rotation file. Format, one match per line:
        ///
        ///   ROOM KEY | mode | minutes | points
        ///
        /// Only the room key is required. '#' starts a comment.
        /// </summary>
        public static MapRotation Load(string path)
        {
            var rotation = new MapRotation();
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw;
                int comment = line.IndexOf('#');
                if (comment >= 0)
                {
                    line = line[..comment];
                }
                line = line.Trim();
                if (line.Length == 0)
                {
                    continue;
                }
                string[] parts = line.Split('|');
                string roomKey = parts[0].Trim();
                if (roomKey.Length == 0)
                {
                    continue;
                }
                GameMode mode = GameMode.Battle;
                if (parts.Length > 1 && Enum.TryParse(parts[1].Trim().Replace(" ", ""),
                    ignoreCase: true, out GameMode parsedMode))
                {
                    mode = parsedMode;
                }
                float timeLimit = 7 * 60;
                if (parts.Length > 2 && Single.TryParse(parts[2].Trim(),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out float minutes))
                {
                    timeLimit = minutes * 60;
                }
                int pointGoal = 7;
                if (parts.Length > 3 && Int32.TryParse(parts[3].Trim(), out int parsedPoints))
                {
                    pointGoal = parsedPoints;
                }
                rotation._entries.Add(new RotationEntry
                {
                    RoomKey = roomKey,
                    Mode = mode,
                    TimeLimit = timeLimit,
                    PointGoal = pointGoal
                });
            }
            return rotation;
        }

        /// <summary>A starter rotation, written when no file exists yet.</summary>
        public static void WriteDefault(string path)
        {
            File.WriteAllLines(path, new[]
            {
                "# MphRead dedicated server map rotation.",
                "# One match per line:  ROOM KEY | mode | minutes | points",
                "# Mode and the numbers are optional; '#' starts a comment.",
                "# Room keys are the names MphRead uses internally -- run the",
                "# game's console menu to see the full list.",
                "",
                "MP1 SANCTORUS      | Battle | 7 | 7",
                "MP3 PROVING GROUND | Battle | 7 | 7",
                "MP4 HIGHGROUND     | Battle | 7 | 7",
                "MP2 HARVESTER      | Battle | 7 | 7",
                "MP6 HEADSHOT       | Battle | 7 | 7"
            });
        }

        public static MapRotation LoadOrCreate(string path)
        {
            if (!File.Exists(path))
            {
                WriteDefault(path);
                Console.WriteLine($"[server] wrote a starter rotation to {path}");
            }
            MapRotation rotation = Load(path);
            if (rotation._entries.Count == 0)
            {
                Console.WriteLine($"[server] {path} has no usable entries; using a single default map");
                rotation._entries.Add(_fallback);
            }
            return rotation;
        }
    }
}
