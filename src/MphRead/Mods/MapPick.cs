using System;
using System.Collections.Generic;
using System.Linq;
using MphRead.Mods.Network;

namespace MphRead.Mods
{
    /// <summary>
    /// Where everybody goes next, chosen on the results screen.
    ///
    /// The ten seconds between two matches were the one moment in a session
    /// with nothing in them: the scoreboard is read in three, the hunter
    /// picker answers a question in two, and the map -- the thing that
    /// actually decides what the next seven minutes are like -- was announced
    /// rather than asked. It was announced from
    /// <c>MatchStatePacket.NextRoomKey</c>, one line reading "NEXT: COMBAT
    /// HALL", and the only way to have any say in it was to call a mid-match
    /// vote, which interrupts the match it is called in. That is the wrong
    /// moment for the question in every way: the right one is when there is
    /// nothing else to do.
    ///
    /// So the results screen carries the whole map list and the room votes on
    /// it. It is the same callvote, moved: picking a map is proposing it,
    /// picking the map somebody else picked is validating it, and picking a
    /// different one is proposing that instead -- the alternative to agreeing
    /// is naming another map rather than saying no to this one, which is the
    /// only part of a vote that changes shape here. **The map with the most
    /// votes is the one loaded** -- no threshold, because an intermission
    /// interrupts nobody and a bar to clear only produces the outcome nobody
    /// voted for, a room that picked three maps and is sent to a fourth. With
    /// nothing picked at all the rotation plays the map it was always going
    /// to.
    ///
    /// <para>
    /// **Every map, not a short list.** A four-map ballot drawn up by the
    /// server was the first shape of this and it asked the wrong question:
    /// "where next" is not multiple choice, and the four on offer were never
    /// the one somebody had in mind. The list is therefore this machine's own
    /// room list, scrolled, and what travels on the wire is only the tally --
    /// the answer, not the question. Maps somebody has picked are **pulled to
    /// the top** so the room can see what is being voted for without having to
    /// scroll to find it, which is most of what makes a vote a vote.
    /// </para>
    ///
    /// <para>
    /// What it costs, against the F1/F2 prompt it replaces: nothing is
    /// interrupted, because there is nothing to interrupt. A vote called
    /// mid-match is a question put to seven people who are in the middle of a
    /// fight, which is why the old one needed a cooldown and a minimum player
    /// count to stop it being a nuisance. Ten seconds of results screen needs
    /// neither.
    /// </para>
    ///
    /// <para>
    /// Offline the same list is offered and the answer starts the next match,
    /// which is what a rotation is when there is nobody else in it. One player
    /// is the whole room, so the pick is the answer and there is nothing to
    /// count.
    /// </para>
    ///
    /// <para>
    /// A mirror of the server's picture, like <see cref="MapVote"/> and for
    /// the same reason: the tally on screen is read off the last packet rather
    /// than off what this machine believes it sent, because a pick is a
    /// datagram and can be lost. What this machine chose is kept separately
    /// (<see cref="Picked"/>) only so the row can be ringed straight away.
    /// </para>
    /// </summary>
    public static class MapPick
    {
        /// <summary>
        /// Reserved ballot value for leaving the results screen in the persistent lobby
        /// instead of immediately starting another match. It travels through the existing
        /// MapPick packet so the wire format stays compatible with the other pending PRs.
        /// </summary>
        public const string ReturnToLobbyKey = PostMatchChoice.ReturnToLobbyKey;

        public static bool HasReturnToLobbyOption => NetSession.PersistentLobby;

        public static bool IsReturnToLobby(string roomKey) =>
            PostMatchChoice.IsReturnToLobby(roomKey);

        /// <summary>Every map that can be voted for, most-wanted first.</summary>
        private static readonly List<string> _order = new();

        /// <summary>The server's tally: maps with votes, and how many.</summary>
        private static readonly Dictionary<string, int> _tally =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The tally's order, which is the server's -- see <see cref="Reorder"/>.</summary>
        private static readonly List<string> _voted = new();

        /// <summary>This machine's own room list, read once per results
        /// screen: it is a metadata walk, not something to do per frame.</summary>
        private static readonly List<string> _rooms = new();

        /// <summary>Whether the room may vote: the server says so, or there is
        /// nobody to ask and this machine decides for itself.</summary>
        public static bool Open { get; private set; }

        /// <summary>Whether there is a list to draw.</summary>
        public static bool Available => Open && _order.Count > 0 && EndScreen.Available;

        /// <summary>The list as drawn: every map, the voted ones first.</summary>
        public static IReadOnlyList<string> Order => _order;

        public static int VotesFor(string roomKey)
        {
            return _tally.TryGetValue(roomKey, out int votes) ? votes : 0;
        }

        /// <summary>
        /// How many players there are to vote, for the "3 of 8" a row reads.
        /// The server's number, not a count of the scoreboard: it is the one
        /// that matches what the server is counting.
        /// </summary>
        public static int Eligible { get; private set; }

        /// <summary>The map in front, which is the one that will be loaded, or
        /// "" while nobody has picked anything.</summary>
        public static string Leader => _order.Count > 0 && VotesFor(_order[0]) > 0
            ? _order[0] : "";

        /// <summary>
        /// What this machine picked, or "" for nothing. Kept locally as well
        /// as counted by the server so the row rings the moment it is clicked
        /// rather than a round trip later.
        /// </summary>
        public static string Picked { get; private set; } = "";

        /// <summary>Which row the keyboard, the pad and the wheel are on.</summary>
        public static int Cursor { get; private set; }

        /// <summary>The first row drawn: the list is longer than the panel.</summary>
        public static int Scroll { get; private set; }

        /// <summary>How many rows the panel draws, published by the draw so
        /// the scrolling here and the picture agree.</summary>
        public static int Window { get; private set; } = 4;

        /// <summary>
        /// What a row calls a map: its in-game name, made unambiguous.
        ///
        /// Several rooms share one in-game name -- AD1 TRANSFER LOCK and AD1
        /// TRANSFER LOCK BT are both "Transfer Lock", and there are more of
        /// these than you would expect -- so a list drawn from the names alone
        /// offers two identical rows and no way to tell which is which. The
        /// key's own prefix is what tells them apart on every screen that
        /// shows the key, so a duplicated name takes it.
        /// </summary>
        public static string NameOf(string roomKey)
        {
            if (roomKey.Length == 0)
            {
                return "";
            }
            if (_labels.TryGetValue(roomKey, out string? label))
            {
                return label;
            }
            return PlainName(roomKey);
        }

        private static string PlainName(string roomKey)
        {
            if (IsReturnToLobby(roomKey))
            {
                return "Return to lobby";
            }
            try
            {
                (RoomMetadata? meta, _) = Metadata.GetRoomByName(roomKey);
                return meta?.InGameName ?? roomKey;
            }
            catch (Exception)
            {
                return roomKey;
            }
        }

        private static readonly Dictionary<string, string> _labels =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Work out the labels once, when the list is read. A name is used as
        /// it is unless another room in this list answers to it, in which case
        /// both take the key's leading token -- "TRANSFER LOCK AD1" and
        /// "TRANSFER LOCK AD1 BT", which is at least two different rows.
        /// </summary>
        private static void BuildLabels()
        {
            _labels.Clear();
            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < _rooms.Count; i++)
            {
                string name = PlainName(_rooms[i]);
                seen[name] = seen.TryGetValue(name, out int count) ? count + 1 : 1;
            }
            for (int i = 0; i < _rooms.Count; i++)
            {
                string key = _rooms[i];
                string name = PlainName(key);
                if (seen[name] <= 1 || String.Equals(name, key, StringComparison.OrdinalIgnoreCase))
                {
                    _labels[key] = name;
                    continue;
                }
                // Whatever of the key the name does not already carry, which
                // for this game's rooms is the AD1/MP3/CTF1 prefix and the BT
                // suffix -- the two things that actually differ.
                string extra = key;
                foreach (string word in name.Split(' '))
                {
                    extra = extra.Replace(word, "", StringComparison.OrdinalIgnoreCase);
                }
                extra = String.Join(' ', extra.Split(' ',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                _labels[key] = extra.Length > 0 ? $"{name} {extra}" : name;
            }
        }

        // ---------------------------------------------------------- the list

        /// <summary>
        /// Read the room list and open the ballot locally. Called when the
        /// results screen comes up, with the map just played so it can be left
        /// off -- there is no point voting for where you already are.
        /// </summary>
        public static void Begin(string currentRoom, bool open)
        {
            _rooms.Clear();
            try
            {
                IReadOnlyList<string> rooms = ThumbnailGenerator.MultiplayerRooms();
                for (int i = 0; i < rooms.Count; i++)
                {
                    if (!String.Equals(rooms[i], currentRoom, StringComparison.OrdinalIgnoreCase))
                    {
                        _rooms.Add(rooms[i]);
                    }
                }
            }
            catch (Exception)
            {
                // No game files, no list. The results screen still works.
            }
            if (NetSession.PersistentLobby)
            {
                _rooms.Add(ReturnToLobbyKey);
            }
            BuildLabels();
            Open = open && _rooms.Count > 0;
            Cursor = 0;
            Scroll = 0;
            Reorder();
        }

        /// <summary>
        /// Votes first, in the server's own order, then everything else as the
        /// room list had it.
        ///
        /// The order comes from the server rather than from a sort here: two
        /// machines ordering equal counts differently would put a row under
        /// one player's cursor and a different one under another's, and the
        /// list moves while people are clicking on it.
        /// </summary>
        private static void Reorder()
        {
            _order.Clear();
            for (int i = 0; i < _voted.Count; i++)
            {
                if (_rooms.Contains(_voted[i], StringComparer.OrdinalIgnoreCase))
                {
                    _order.Add(_voted[i]);
                }
            }
            for (int i = 0; i < _rooms.Count; i++)
            {
                if (!_order.Contains(_rooms[i], StringComparer.OrdinalIgnoreCase))
                {
                    _order.Add(_rooms[i]);
                }
            }
            Cursor = Math.Clamp(Cursor, 0, Math.Max(0, _order.Count - 1));
            ClampScroll();
        }

        /// <summary>Take the server's tally.</summary>
        public static void Apply(MapChoicesPacket packet)
        {
            Eligible = packet.Eligible;
            bool open = packet.Open != 0;
            _tally.Clear();
            _voted.Clear();
            for (int i = 0; i < packet.Count && i < packet.RoomKeys.Length; i++)
            {
                string key = packet.RoomKeys[i];
                if (key.Length == 0)
                {
                    continue;
                }
                _tally[key] = i < packet.Votes.Length ? packet.Votes[i] : 0;
                _voted.Add(key);
            }
            if (!open)
            {
                Open = false;
                Picked = "";
                _order.Clear();
                return;
            }
            if (!Open && _rooms.Count > 0)
            {
                Open = true;
            }
            // Keep the row the cursor was on rather than its number: the order
            // moves every time anybody votes, and a cursor that stays on the
            // index is a cursor that wanders while you are reading.
            string was = Cursor >= 0 && Cursor < _order.Count ? _order[Cursor] : "";
            Reorder();
            if (was.Length > 0)
            {
                int at = IndexOf(was);
                if (at >= 0)
                {
                    Cursor = at;
                }
            }
        }

        public static int IndexOf(string roomKey)
        {
            for (int i = 0; i < _order.Count; i++)
            {
                if (String.Equals(_order[i], roomKey, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>Which row this machine picked, or -1.</summary>
        public static int PickedIndex => Picked.Length == 0 ? -1 : IndexOf(Picked);

        /// <summary>
        /// Forget everything. Called when the results screen goes, which is
        /// the start of the next match.
        /// </summary>
        public static void Reset()
        {
            _order.Clear();
            _rooms.Clear();
            _tally.Clear();
            _voted.Clear();
            _labels.Clear();
            Picked = "";
            Open = false;
            Cursor = 0;
            Scroll = 0;
            Eligible = 0;
            _hits = Array.Empty<EndScreen.Hit>();
        }

        /// <summary>
        /// The map this machine would load if the screen ended now, or
        /// nothing. Only the offline path acts on this, where the one player
        /// is the whole room and their pick is the vote; online the server
        /// counts and says what is next in the match state, which is the only
        /// answer every client agrees on -- a client working it out for itself
        /// would be a second answer free to disagree.
        /// </summary>
        public static string Chosen()
        {
            return Picked;
        }

        // --------------------------------------------------------- the input

        /// <summary>
        /// Pick a row, or take the pick back by choosing it again -- the
        /// second press being an undo rather than nothing is what makes a
        /// mis-click recoverable on a screen that is up for ten seconds.
        /// </summary>
        public static void Choose(int index)
        {
            if (index < 0 || index >= _order.Count)
            {
                return;
            }
            Cursor = index;
            ScrollToCursor();
            string key = _order[index];
            Picked = String.Equals(Picked, key, StringComparison.OrdinalIgnoreCase) ? "" : key;
            if (NetSession.Active)
            {
                NetSession.SendMapPick(Picked);
                return;
            }
            // Nobody to count it, so this machine keeps its own tally -- the
            // row still has to show what was chosen, and the ring alone does
            // not say "one vote".
            _tally.Clear();
            _voted.Clear();
            if (Picked.Length > 0)
            {
                _tally[Picked] = 1;
                _voted.Add(Picked);
            }
            string was = Cursor < _order.Count ? _order[Cursor] : "";
            Reorder();
            int at = IndexOf(was);
            if (at >= 0)
            {
                Cursor = at;
                ScrollToCursor();
            }
        }

        public static void ChooseCursor()
        {
            Choose(Cursor);
        }

        /// <summary>Move the cursor, scrolling the list to keep it on screen.</summary>
        public static void Step(int by)
        {
            if (_order.Count == 0)
            {
                return;
            }
            Cursor = Math.Clamp(Cursor + by, 0, _order.Count - 1);
            ScrollToCursor();
        }

        /// <summary>
        /// The wheel: the list moves under the pointer rather than the cursor
        /// moving with the list, which is what a wheel does everywhere else.
        /// </summary>
        public static void Wheel(int by)
        {
            if (_order.Count == 0)
            {
                return;
            }
            Scroll = Math.Clamp(Scroll + by, 0, Math.Max(0, _order.Count - Window));
        }

        /// <summary>
        /// Keep the view inside the list without moving it. Only something
        /// the player did may move it: the wheel scrolls without touching the
        /// cursor, so snapping to the cursor when the server's tally arrives
        /// threw the list back to the top once a second.
        /// </summary>
        private static void ClampScroll()
        {
            Scroll = Math.Clamp(Scroll, 0, Math.Max(0, _order.Count - Math.Max(1, Window)));
        }

        private static void ScrollToCursor()
        {
            int window = Math.Max(1, Window);
            if (Cursor < Scroll)
            {
                Scroll = Cursor;
            }
            else if (Cursor >= Scroll + window)
            {
                Scroll = Cursor - window + 1;
            }
            Scroll = Math.Clamp(Scroll, 0, Math.Max(0, _order.Count - window));
        }

        /// <summary>
        /// Say it again, in case the last one was lost. Called once a second
        /// while the screen is up: the packet is thirty-odd bytes and a pick
        /// that never arrived is a player whose vote silently did not count.
        /// </summary>
        public static void Resend()
        {
            if (Open && NetSession.Active)
            {
                // Empty is a real answer: it withdraws a previous pick. Re-send it too,
                // otherwise one lost withdrawal datagram leaves a stale vote on the server.
                NetSession.SendMapPick(Picked);
            }
        }

        // -------------------------------------------------------- the layout

        private static EndScreen.Hit[] _hits = Array.Empty<EndScreen.Hit>();

        /// <summary>
        /// Where the rows were drawn and how many there were, published by the
        /// draw rather than worked out twice. <see cref="EndScreen.NoteLayout"/>'s
        /// rule and its reason: two computations of one layout drift, and the
        /// one that drifts is the invisible one.
        /// </summary>
        public static void NoteLayout(EndScreen.Hit[] rows, int window)
        {
            _hits = rows;
            if (window > 0 && window != Window)
            {
                Window = window;
                ScrollToCursor();
            }
        }

        /// <summary>Which row of the list the pointer is over, or -1.</summary>
        public static int Hovered()
        {
            if (!Available)
            {
                return -1;
            }
            for (int i = 0; i < _hits.Length; i++)
            {
                if (_hits[i].Contains(EndScreen.PointerX, EndScreen.PointerY))
                {
                    return Scroll + i;
                }
            }
            return -1;
        }

        /// <summary>A left click, offered before the game sees it.</summary>
        public static bool HandleClick()
        {
            int row = Hovered();
            if (row < 0)
            {
                return false;
            }
            Choose(row);
            return true;
        }
    }
}
