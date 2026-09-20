using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using MphRead.Entities;
using OpenTK.Mathematics;

namespace MphRead.Mods.Network
{
    /// <summary>
    /// Wire format for MphRead's own LAN play. This is NOT the DS Wi-Fi
    /// protocol: it cannot talk to real hardware, melonDS, or Wiimmfi. It
    /// only connects MphRead instances to each other, which is why it can
    /// send gameplay intent instead of emulated 802.11 frames.
    ///
    /// The simulation runs in float (see Fixed.ToFloat -- the 20.12 values
    /// from the ROM are converted on load, not kept as integers), so two
    /// machines cannot be trusted to stay bit-identical from inputs alone.
    /// That rules out lockstep and makes the host authoritative: clients
    /// send intent, the host simulates, the host broadcasts resulting state.
    /// </summary>
    public enum PacketType : byte
    {
        Hello = 1,          // client -> host, join request
        Welcome = 2,        // host -> client, assigns a slot
        Intent = 3,         // client -> host, one frame of input
        Snapshot = 4,       // host -> clients, authoritative state
        Bye = 5,            // either direction, clean disconnect
        Ping = 6,
        Pong = 7,
        MatchState = 8,     // server -> clients, current map/mode/clock
        MapChange = 9,      // server -> clients, rotation advanced
        Roster = 10,        // server -> clients, who is in which slot
        Identify = 11,      // client -> server, my display name and hunter
        Authority = 12,     // server -> client, you are the simulation authority
        SlotIntent = 13,    // server -> authority, one peer's input, tagged with its slot
        StatusQuery = 14,   // anyone -> server, "what is running?" -- claims no slot
        StatusReply = 15,   // server -> asker, the running match plus the player cap
        MatchEnd = 16,      // authority -> server, somebody won or the clock ran out
        MasterHeartbeat = 17, // dedicated server -> master, "I am up, here is what I run"
        MasterQuery = 18,   // launcher -> master, "who is up?"
        MasterList = 19,    // master -> launcher, one page of the answer
        HostRequest = 20,   // launcher -> master, "run a game for me"
        HostReply = 21,     // master -> launcher, the port it is on, or why not
        Refused = 22,       // server -> client, "not you, and here is why"
        Chat = 23,          // client -> server -> everyone else, one line of text
        // 24 and 25 are left free for a voice channel. Speech is a stream of
        // frames rather than a line of text -- it wants its own type, its own
        // cadence and its own "who is talking" packet, and squeezing it into
        // Chat's Kind byte would put an audio codec inside the packet the
        // scoreboard reads. Nothing here needs to change when it arrives: the
        // server relays what it recognises and drops what it does not, so a
        // build that speaks voice and a build that does not can share a match.
        Vote = 26,          // client -> server, propose a map or answer a proposal
        VoteState = 27,     // server -> clients, the vote in progress
        MapChoices = 28,    // server -> clients, the ballot for the next map
        MapPick = 29,       // client -> server, which of them this player wants
        HitClaim = 30,      // client -> authority, "this shot of mine landed"
        HitVerdict = 31,    // authority -> client, what it did with those claims
        // Map transfer is negotiated before loading a custom room. All requests
        // are bounded and identify package hashes rather than peer filenames.
        MapOffer = 32,      // server -> client, "the next map is custom: name, hash, size"
        MapWant = 33,       // client -> server, "send it, from byte N"
        MapChunk = 34,      // server -> client, one piece of the .fpmap
        SessionState = 36, LobbyCommand = 37, LobbyCommandResult = 38,
        MatchLoaded = 39, MatchLoadFailed = 40,
        MapDone = 35,        // client -> server, "I have it and it hashes right"
    }

    /// <summary>
    /// Why a server would not admit a client.
    ///
    /// Until this existed a refusal was a silence: the server logged its
    /// reason and sent nothing, so a full server, a server running a
    /// different build and a server that was switched off were the same eight
    /// seconds of waiting followed by a guess -- every screen in the program
    /// said "it may be off, full, or UDP may be blocked", because that is
    /// genuinely all the client knew.
    ///
    /// Additive and ignorable in both directions, so it needs no protocol
    /// bump: a client built before this drops an unknown packet type on the
    /// floor exactly as it always did, and a server built before it simply
    /// never sends one -- which is why the client also keeps the StatusQuery
    /// fallback (see <c>NetLaunch.DescribeJoinFailure</c>) for the servers
    /// already deployed.
    /// </summary>
    public struct RefusedPacket
    {
        public const int Size = 3;

        public const byte ReasonFull = 1;
        public const byte ReasonProtocol = 2;
        public const byte ReasonKicked = 3;
        public const byte ReasonInMatch = 4;

        public byte Reason;
        public byte Players;
        public byte MaxPlayers;

        public void Write(Span<byte> dest)
        {
            dest[0] = Reason;
            dest[1] = Players;
            dest[2] = MaxPlayers;
        }

        public static RefusedPacket Read(ReadOnlySpan<byte> src)
        {
            return new RefusedPacket
            {
                Reason = src[0],
                Players = src.Length > 1 ? src[1] : (byte)0,
                MaxPlayers = src.Length > 2 ? src[2] : (byte)0
            };
        }

        public readonly string Describe(string where)
        {
            return Reason switch
            {
                ReasonKicked => "You were removed by the lobby owner.",
                ReasonInMatch => "This server does not allow joining a match in progress.",
                ReasonFull => $"{where} is full ({Players}/{MaxPlayers} players). "
                    + "Try again when somebody leaves.",
                ReasonProtocol => $"{where} is running a different version of the game. "
                    + "One of you needs updating.",
                _ => $"{where} would not admit this client."
            };
        }
    }

    /// <summary>
    /// "Start a server for me, on your machine."
    ///
    /// This is how a game gets hosted without anybody opening a port. The
    /// player's own router is the problem -- a server on a home machine is
    /// unreachable from outside unless UDP is forwarded to it, which most
    /// people cannot or will not do -- and the fix that needs no cooperation
    /// from it is not to put the server there. The directory already runs on a
    /// machine with a reachable port; it starts the match there instead, and
    /// the host joins it by connecting *out*, exactly like every other player.
    ///
    /// Punching a hole through the NAT was the other candidate and is what a
    /// peer-to-peer game would have to do. It is not worth it here: this
    /// engine's netcode is already "everyone connects to one relay", so
    /// putting the relay somewhere reachable is the whole of the work, and it
    /// has no failure mode -- hole punching has one for every symmetric NAT.
    /// </summary>
    public struct HostRequestPacket
    {
        public const int MaxRoomBytes = 40;
        public const int MaxNameBytes = 32;
        public const int Size = 1 + 1 + 1 + 2 + 2 + MaxRoomBytes + MaxNameBytes;

        /// <summary>
        /// How many maps a requested rotation may carry, and what one costs on
        /// the wire.
        ///
        /// The cap is the datagram rather than a policy: the fixed block is 79
        /// bytes, an entry is 41, and sixteen entries plus the current tail fit
        /// comfortably under <see cref="NetConfig.MaxPacketSize"/>. Keep this
        /// derived from the packet budget rather than duplicating its numeric
        /// value in prose.
        /// </summary>
        public const int MaxRotation = 16;
        public const int RotationEntrySize = MaxRoomBytes + 1;

        public byte Protocol;
        public byte MaxPlayers;
        public byte Mode;
        /// <summary>Match length in seconds. Zero means no limit.</summary>
        public ushort TimeLimit;
        public ushort PointGoal;
        public string RoomKey;
        public string ServerName;

        /// <summary>
        /// Every map the asker wants played, in order, or an empty list.
        ///
        /// Written *after* the fixed block rather than into it, which is the
        /// whole reason this needed no protocol bump: a directory built before
        /// rotations existed length-checks the payload against
        /// <see cref="Size"/> and reads exactly that many bytes, so the tail is
        /// invisible to it and it plays <see cref="RoomKey"/> on a loop -- the
        /// behaviour it always had. Entry zero is that same first map, so the
        /// two halves of the packet never disagree about what starts.
        /// </summary>
        public IReadOnlyList<(string RoomKey, GameMode Mode)>? Rotation;

        /// <summary>How many bytes this request takes, tail included.</summary>
        public ServerSessionPolicy Policy;
        public bool AllowJoinInProgress = true;
        public bool RequireReady = true;
        public MatchFormat Format;
        public HostRequestPacket() { RoomKey = ""; ServerName = ""; }
        public int Length => Size + 1 + Math.Min(Rotation?.Count ?? 0, MaxRotation) * RotationEntrySize + 4;

        public void Write(Span<byte> dest)
        {
            dest[0] = Protocol;
            dest[1] = MaxPlayers;
            dest[2] = Mode;
            BinaryPrimitives.WriteUInt16LittleEndian(dest[3..], TimeLimit);
            BinaryPrimitives.WriteUInt16LittleEndian(dest[5..], PointGoal);
            NetText.Write(dest.Slice(7, MaxRoomBytes), RoomKey);
            NetText.Write(dest.Slice(7 + MaxRoomBytes, MaxNameBytes), ServerName);
            int count = Math.Min(Rotation?.Count ?? 0, MaxRotation);
            dest[Size] = (byte)count;
            for (int i = 0; i < count; i++)
            {
                int at = Size + 1 + i * RotationEntrySize;
                NetText.Write(dest.Slice(at, MaxRoomBytes), Rotation![i].RoomKey);
                dest[at + MaxRoomBytes] = (byte)Rotation![i].Mode;
            }
            int tail = Size + 1 + count * RotationEntrySize;
            dest[tail] = (byte)Policy;
            dest[tail + 1] = AllowJoinInProgress ? (byte)1 : (byte)0;
            dest[tail + 2] = RequireReady ? (byte)1 : (byte)0;
            dest[tail + 3] = (byte)Format;
        }

        public static HostRequestPacket Read(ReadOnlySpan<byte> src)
        {
            if (src.Length < Size + 5 || src[Size] > MaxRotation) return default;
            int tail = Size + 1 + src[Size] * RotationEntrySize;
            if (src.Length != tail + 4 || src[tail] > 1 || src[tail + 1] > 1
                || src[tail + 2] > 1 || src[tail + 3] > (byte)MatchFormat.TwoVsTwoVsTwoVsTwo) return default;
            return new HostRequestPacket
            {
                Protocol = src[0],
                MaxPlayers = src[1],
                Mode = src[2],
                TimeLimit = BinaryPrimitives.ReadUInt16LittleEndian(src[3..]),
                PointGoal = BinaryPrimitives.ReadUInt16LittleEndian(src[5..]),
                RoomKey = NetText.Read(src.Slice(7, MaxRoomBytes)),
                ServerName = NetText.Read(src.Slice(7 + MaxRoomBytes, MaxNameBytes)),
                Policy = (ServerSessionPolicy)src[tail], AllowJoinInProgress = src[tail + 1] != 0,
                RequireReady = src[tail + 2] != 0, Format = (MatchFormat)src[tail + 3],
                Rotation = ReadRotation(src)
            };
        }

        /// <summary>
        /// The tail, or null when the sender is an older launcher that wrote
        /// none. Every length is checked rather than trusted: the count byte
        /// is the asker's and a truncated datagram must not read past the end
        /// of what arrived.
        /// </summary>
        private static List<(string, GameMode)>? ReadRotation(ReadOnlySpan<byte> src)
        {
            if (src.Length <= Size)
            {
                return null;
            }
            int count = Math.Min((int)src[Size], MaxRotation);
            if (count == 0)
            {
                return null;
            }
            var maps = new List<(string, GameMode)>(count);
            for (int i = 0; i < count; i++)
            {
                int at = Size + 1 + i * RotationEntrySize;
                if (at + RotationEntrySize > src.Length)
                {
                    break;
                }
                string room = NetText.Read(src.Slice(at, MaxRoomBytes));
                if (room.Length == 0)
                {
                    continue;
                }
                byte mode = src[at + MaxRoomBytes];
                maps.Add((room, Enum.IsDefined(typeof(GameMode), mode)
                    ? (GameMode)mode : GameMode.Battle));
            }
            return maps.Count > 0 ? maps : null;
        }
    }

    /// <summary>Where the game the directory just started is listening, or why it did not.</summary>
    public struct HostReplyPacket
    {
        public const int MaxReasonBytes = 96;
        public const int Size = 1 + 2 + MaxReasonBytes + 16;

        public Guid OwnerToken;
        public bool Started;
        public ushort Port;
        public string Reason;

        public void Write(Span<byte> dest)
        {
            dest[0] = (byte)(Started ? 1 : 0);
            BinaryPrimitives.WriteUInt16LittleEndian(dest[1..], Port);
            NetText.Write(dest.Slice(3, MaxReasonBytes), Reason);
            OwnerToken.TryWriteBytes(dest.Slice(3 + MaxReasonBytes, 16));
        }

        public static HostReplyPacket Read(ReadOnlySpan<byte> src)
        {
            return new HostReplyPacket
            {
                Started = src[0] != 0,
                OwnerToken = new Guid(src.Slice(3 + MaxReasonBytes, 16)),
                Port = BinaryPrimitives.ReadUInt16LittleEndian(src[1..]),
                Reason = NetText.Read(src.Slice(3, MaxReasonBytes))
            };
        }
    }

    /// <summary>
    /// What a launcher needs to show a server on a list, answered without
    /// joining.
    ///
    /// A Hello would answer the same questions, but it takes a slot to do it:
    /// polling with Hello churns the roster, can be refused outright when the
    /// server is full -- reporting a busy server as a dead one -- and on an
    /// empty server briefly makes the poller the simulation authority. This
    /// asks and leaves nothing behind.
    ///
    /// A server built before this packet existed ignores it, so the caller
    /// falls back to the Hello probe rather than reporting the server down.
    /// </summary>
    public struct ServerStatusPacket
    {
        /// <summary>What the server calls itself on a browser's list.</summary>
        public const int MaxNameBytes = 32;
        public const int Size = MatchStatePacket.Size + 2 + MaxNameBytes;

        /// <summary>
        /// The same packet with one byte of capability on the end.
        ///
        /// After the name rather than inside the block, so a server built
        /// before it existed is read exactly as it always was and a launcher
        /// built before it existed never looks: the length check is what
        /// separates the two, and neither side needed a protocol bump.
        /// </summary>
        public const int SizeWithFlags = Size + 5;

        /// <summary>Bit 0: this server will open a new match on a port of its own.</summary>
        public const byte FlagCanHost = 1;

        public SessionPhase Phase;
        public MatchFormat Format;
        public bool LobbyEnabled, AllowJoinInProgress;
        public MatchStatePacket Match;
        public byte MaxPlayers;
        public byte Protocol;

        /// <summary>
        /// What this server can do beyond running the match it is running.
        ///
        /// Zero for a server that did not say, which reads as "cannot" -- and
        /// unlike the directory's own flag that is the *right* default here:
        /// hosting on a game server is off unless an admin passed
        /// <c>-hostports</c>, so silence and no really are the same answer.
        /// </summary>
        public byte Flags;
        /// <summary>
        /// The name an admin gave this server, or an empty string. A list of
        /// addresses is not a list of servers -- people pick the one they
        /// recognise, and a numeric address is recognisable to nobody.
        /// </summary>
        public string ServerName;

        public void Write(Span<byte> dest)
        {
            Match.Write(dest);
            dest[MatchStatePacket.Size] = MaxPlayers;
            dest[MatchStatePacket.Size + 1] = Protocol;
            NetText.Write(dest.Slice(MatchStatePacket.Size + 2, MaxNameBytes), ServerName);
            if (dest.Length >= SizeWithFlags)
            {
                dest[Size] = Flags;
                dest[Size + 1] = (byte)Phase; dest[Size + 2] = (byte)Format;
                dest[Size + 3] = LobbyEnabled ? (byte)1 : (byte)0;
                dest[Size + 4] = AllowJoinInProgress ? (byte)1 : (byte)0;
            }
        }

        public static ServerStatusPacket Read(ReadOnlySpan<byte> src)
        {
            return new ServerStatusPacket
            {
                Match = MatchStatePacket.Read(src),
                MaxPlayers = src[MatchStatePacket.Size],
                Protocol = src[MatchStatePacket.Size + 1],
                ServerName = src.Length >= Size
                    ? NetText.Read(src.Slice(MatchStatePacket.Size + 2, MaxNameBytes))
                    : "",
                Flags = src.Length > Size ? src[Size] : (byte)0,
                Phase = src.Length >= SizeWithFlags ? (SessionPhase)src[Size + 1] : SessionPhase.InMatch,
                Format = src.Length >= SizeWithFlags ? (MatchFormat)src[Size + 2] : MatchFormat.Auto,
                LobbyEnabled = src.Length >= SizeWithFlags && src[Size + 3] != 0,
                AllowJoinInProgress = src.Length < SizeWithFlags || src[Size + 4] != 0
            };
        }
    }

    /// <summary>
    /// Fixed-width ASCII in a packet, written and read the same way
    /// everywhere.
    ///
    /// Every name on the wire had its own private copy of this and they had
    /// started to disagree about what to do with a byte the in-game font
    /// cannot draw. One copy, one answer.
    /// </summary>
    public static class NetText
    {
        public static void Write(Span<byte> dest, string? value)
        {
            dest.Clear();
            if (String.IsNullOrEmpty(value))
            {
                return;
            }
            int count = Math.Min(value.Length, dest.Length);
            for (int i = 0; i < count; i++)
            {
                char c = value[i];
                dest[i] = (byte)(c < 32 || c > 126 ? '?' : c);
            }
        }

        public static string Read(ReadOnlySpan<byte> src)
        {
            int length = 0;
            while (length < src.Length && src[length] != 0)
            {
                length++;
            }
            return length == 0 ? String.Empty : Encoding.ASCII.GetString(src[..length]);
        }
    }

    /// <summary>
    /// One dedicated server, as the master list knows it.
    ///
    /// The master is a directory and nothing else: servers announce
    /// themselves to it every few seconds, it forgets the ones that stop, and
    /// a launcher asking for the list gets back address, port and whatever
    /// each server last said about itself. It never relays gameplay, so it
    /// costs a Raspberry Pi nothing to run beside the server it is listed in.
    ///
    /// Latency is deliberately absent: the master could only report its own
    /// round trip to each server, which is not the number a player wants.
    /// The launcher measures its own, by asking each server directly, which
    /// is also what proves the entry is still real.
    /// </summary>
    public struct MasterEntryPacket
    {
        public const int MaxNameBytes = 32;
        public const int MaxRoomBytes = 40;
        // address, port, players, max, mode, protocol, name, room
        public const int Size = 4 + 2 + 1 + 1 + 1 + 1 + MaxNameBytes + MaxRoomBytes;

        /// <summary>IPv4, network order, as the master saw the heartbeat arrive.</summary>
        public uint Address;
        public ushort Port;
        public byte Players;
        public byte MaxPlayers;
        public byte Mode;
        public byte Protocol;
        public string ServerName;
        public string RoomKey;

        public void Write(Span<byte> dest)
        {
            BinaryPrimitives.WriteUInt32BigEndian(dest, Address);
            BinaryPrimitives.WriteUInt16LittleEndian(dest[4..], Port);
            dest[6] = Players;
            dest[7] = MaxPlayers;
            dest[8] = Mode;
            dest[9] = Protocol;
            NetText.Write(dest.Slice(10, MaxNameBytes), ServerName);
            NetText.Write(dest.Slice(10 + MaxNameBytes, MaxRoomBytes), RoomKey);
        }

        public static MasterEntryPacket Read(ReadOnlySpan<byte> src)
        {
            return new MasterEntryPacket
            {
                Address = BinaryPrimitives.ReadUInt32BigEndian(src),
                Port = BinaryPrimitives.ReadUInt16LittleEndian(src[4..]),
                Players = src[6],
                MaxPlayers = src[7],
                Mode = src[8],
                Protocol = src[9],
                ServerName = NetText.Read(src.Slice(10, MaxNameBytes)),
                RoomKey = NetText.Read(src.Slice(10 + MaxNameBytes, MaxRoomBytes))
            };
        }
    }

    /// <summary>
    /// What a dedicated server tells the master about itself.
    ///
    /// The address is not in it: the master takes that from the datagram it
    /// arrived in, so a server behind a router announces the address people
    /// can actually reach rather than the one it sees on its own interface.
    /// The port is, because that one the server does know and the source port
    /// of a heartbeat is not necessarily the one it listens on.
    /// </summary>
    public struct MasterHeartbeatPacket
    {
        public const int Size = 1 + 2 + 1 + 1 + 1 + MasterEntryPacket.MaxNameBytes
            + MasterEntryPacket.MaxRoomBytes;

        public byte Protocol;
        public ushort Port;
        public byte Players;
        public byte MaxPlayers;
        public byte Mode;
        public string ServerName;
        public string RoomKey;

        public void Write(Span<byte> dest)
        {
            dest[0] = Protocol;
            BinaryPrimitives.WriteUInt16LittleEndian(dest[1..], Port);
            dest[3] = Players;
            dest[4] = MaxPlayers;
            dest[5] = Mode;
            NetText.Write(dest.Slice(6, MasterEntryPacket.MaxNameBytes), ServerName);
            NetText.Write(dest.Slice(6 + MasterEntryPacket.MaxNameBytes,
                MasterEntryPacket.MaxRoomBytes), RoomKey);
        }

        public static MasterHeartbeatPacket Read(ReadOnlySpan<byte> src)
        {
            return new MasterHeartbeatPacket
            {
                Protocol = src[0],
                Port = BinaryPrimitives.ReadUInt16LittleEndian(src[1..]),
                Players = src[3],
                MaxPlayers = src[4],
                Mode = src[5],
                ServerName = NetText.Read(src.Slice(6, MasterEntryPacket.MaxNameBytes)),
                RoomKey = NetText.Read(src.Slice(6 + MasterEntryPacket.MaxNameBytes,
                    MasterEntryPacket.MaxRoomBytes))
            };
        }
    }

    /// <summary>
    /// What map is running, in what mode, and how much of it is left.
    ///
    /// Sent to a client the moment it connects and repeated periodically, so
    /// arriving mid-match is the normal case rather than a special one: the
    /// joiner loads the running map and adopts the server's clock instead of
    /// starting its own. Also carries the rotation's next map so clients can
    /// preload and switch without a gap.
    /// </summary>
    public struct MatchStatePacket
    {
        public ulong AuthorityEpoch;
        public const int MaxNameBytes = 40;
        public const int Size = 1 + 4 + 4 + 1 + 1 + 2 + 2 + MaxNameBytes + MaxNameBytes + 8;

        public byte Mode;              // GameMode
        public float TimeRemaining;    // seconds left in this match
        public float TimeElapsed;      // seconds since the match started
        public byte PlayerCount;
        public byte Flags;             // bit 0 = match in progress, bit 1 = ending
        /// <summary>
        /// The score that wins this match, from the server's rotation file.
        ///
        /// Sent because it decides when the match ends, and a client that
        /// used its own would stop playing at a different moment from
        /// everybody else -- which is the same class of bug the match clock
        /// had before the server started publishing that.
        /// </summary>
        public ushort PointGoal;
        /// <summary>
        /// Which match this is, counting from the server's start.
        ///
        /// The room key alone cannot answer "is this a new match": a server
        /// hosting one map -- which is what the launcher's "Host a game" sets
        /// up -- plays the same room over and over, so a client watching only
        /// the name saw nothing change and sat on its results screen for the
        /// rest of the session. This changes every time the server starts a
        /// round, whatever it is being played on.
        /// </summary>
        public ushort MatchId;
        public string RoomKey;
        public string NextRoomKey;

        public const byte FlagInProgress = 1 << 0;
        /// <summary>
        /// The match is over and the server is running out the results
        /// sequence before it rotates. Clients show the winner, and -- this
        /// is the part that matters -- stop adopting the match clock, which
        /// would otherwise overwrite the countdown the results screen runs
        /// on.
        /// </summary>
        public const byte FlagEnding = 1 << 1;
        /// <summary>
        /// Same-team damage counts. Server-decided and broadcast rather than
        /// left to each client's own local setting -- see
        /// <see cref="DedicatedServer.FriendlyFire"/>.
        /// </summary>
        public const byte FlagFriendlyFire = 1 << 2;
        /// <summary>
        /// The shadow freeze glitch is switched **off** on this server, so an
        /// affinity Judicator's ice wave freezes what is in front of it rather
        /// than everything within 60 degrees at any height. Server-decided for
        /// the same reason friendly fire is: the machine resolving a shot
        /// decides who it hit.
        ///
        /// Stated as the negative deliberately. Zero is the cartridge's own
        /// behaviour, so a packet from anything that does not know about this
        /// rule -- a demo recorded before it, a server built before it --
        /// plays exactly as it always did.
        /// </summary>
        public const byte FlagNoShadowFreeze = 1 << 3;
        /// <summary>
        /// Bits 4-5: the damage level every machine in this match scales its
        /// hits by, as the level plus one, so that <b>zero means "this server
        /// did not say"</b>.
        ///
        /// It was a per-machine *setting* -- <c>GameState.DamageLevel</c>, read
        /// out of each player's own settings file and multiplied into every
        /// hit inside <c>TakeDamage</c>. Low is 0.75, high is 1.25, so two
        /// machines that disagreed about it disagreed about the damage of
        /// every shot of every weapon by up to a third, in the one direction
        /// nothing can correct: the shooter's client resolves its own hits now
        /// (<see cref="NetHitPrediction"/>) and the authority resolves them
        /// again a round trip later, and where the numbers differ the client
        /// runs a victim's health down faster than the authority does and
        /// eventually predicts a kill on somebody who is standing up. Three
        /// uncharged missiles are 96 of a hunter's 99; at the high level they
        /// are 120.
        ///
        /// The same class of rule as friendly fire and the ice wave above it,
        /// and settled the same way: the machine resolving a shot decides what
        /// it did. Two spare bits of a byte that was already being sent, so
        /// there is no protocol change -- a server built before this sends
        /// zero and every client keeps the behaviour it always had.
        /// </summary>
        public const byte FlagDamageShift = 4;
        public const byte FlagDamageMask = 0b11 << FlagDamageShift;
        /// <summary>
        /// Bit 6: weapon pickups are the picking hunter's affinity variant.
        /// Only meaningful when the damage bits say this server states its
        /// rules at all, since a lone zero bit cannot be told from silence --
        /// and it matters for the same reason: an affinity Battlehammer deals
        /// 18 where the plain one deals 12.
        /// </summary>
        public const byte FlagAffinityWeapons = 1 << 6;

        public readonly bool Ending => (Flags & FlagEnding) != 0;
        public readonly bool FriendlyFire => (Flags & FlagFriendlyFire) != 0;
        public readonly bool ShadowFreeze => (Flags & FlagNoShadowFreeze) == 0;

        /// <summary>
        /// The damage level this server plays at, or -1 when it did not say.
        /// </summary>
        public readonly int DamageLevel
        {
            get
            {
                int stated = (Flags & FlagDamageMask) >> FlagDamageShift;
                return stated == 0 ? -1 : stated - 1;
            }
        }

        /// <summary>Whether this server states its damage rules at all.</summary>
        public readonly bool StatesRules => (Flags & FlagDamageMask) != 0;

        public readonly bool AffinityWeapons => (Flags & FlagAffinityWeapons) != 0;

        /// <summary>
        /// Pack the two rules into the spare bits of the flags byte. A level
        /// outside 0-2 is "do not say", which is what an older server sends.
        /// </summary>
        public static byte RuleFlags(int damageLevel, bool affinityWeapons)
        {
            if (damageLevel < 0 || damageLevel > 2)
            {
                return 0;
            }
            byte flags = (byte)((damageLevel + 1) << FlagDamageShift);
            if (affinityWeapons)
            {
                flags |= FlagAffinityWeapons;
            }
            return flags;
        }

        public void Write(Span<byte> dest)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(dest[95..], AuthorityEpoch);
            dest[0] = Mode;
            BinaryPrimitives.WriteSingleLittleEndian(dest[1..], TimeRemaining);
            BinaryPrimitives.WriteSingleLittleEndian(dest[5..], TimeElapsed);
            dest[9] = PlayerCount;
            dest[10] = Flags;
            BinaryPrimitives.WriteUInt16LittleEndian(dest[11..], PointGoal);
            BinaryPrimitives.WriteUInt16LittleEndian(dest[13..], MatchId);
            WriteName(dest[15..], RoomKey);
            WriteName(dest[(15 + MaxNameBytes)..], NextRoomKey);
        }

        public static MatchStatePacket Read(ReadOnlySpan<byte> src)
        {
            return new MatchStatePacket
            {
                AuthorityEpoch = BinaryPrimitives.ReadUInt64LittleEndian(src[95..]),
                Mode = src[0],
                TimeRemaining = BinaryPrimitives.ReadSingleLittleEndian(src[1..]),
                TimeElapsed = BinaryPrimitives.ReadSingleLittleEndian(src[5..]),
                PlayerCount = src[9],
                Flags = src[10],
                PointGoal = BinaryPrimitives.ReadUInt16LittleEndian(src[11..]),
                MatchId = BinaryPrimitives.ReadUInt16LittleEndian(src[13..]),
                RoomKey = ReadName(src[15..]),
                NextRoomKey = ReadName(src[(15 + MaxNameBytes)..])
            };
        }

        private static void WriteName(Span<byte> dest, string? value)
        {
            dest[..MaxNameBytes].Clear();
            if (string.IsNullOrEmpty(value))
            {
                return;
            }
            // Room keys are ASCII in the metadata; truncate rather than throw
            // so an unexpected long name degrades instead of dropping the packet.
            int count = Math.Min(value.Length, MaxNameBytes);
            for (int i = 0; i < count; i++)
            {
                dest[i] = (byte)value[i];
            }
        }

        private static string ReadName(ReadOnlySpan<byte> src)
        {
            int length = 0;
            while (length < MaxNameBytes && src[length] != 0)
            {
                length++;
            }
            return length == 0 ? string.Empty : Encoding.ASCII.GetString(src[..length]);
        }
    }

    /// <summary>
    /// One frame of player intent, device-independent. Deliberately not
    /// KeyboardState/MouseState: those are host-machine concepts. This is
    /// the same abstraction PlayerAi already writes into Controls, which is
    /// why a remote player can reuse the bot injection path verbatim.
    /// </summary>
    [Flags]
    public enum IntentButtons : uint
    {
        None = 0,
        MoveLeft = 1u << 0,
        MoveRight = 1u << 1,
        MoveUp = 1u << 2,
        MoveDown = 1u << 3,
        Shoot = 1u << 4,
        Zoom = 1u << 5,
        Jump = 1u << 6,
        Morph = 1u << 7,
        Boost = 1u << 8,
        AltAttack = 1u << 9,
        ScanVisor = 1u << 10,
        NextWeapon = 1u << 11,
        PrevWeapon = 1u << 12,
        RollLeft = 1u << 13,
        RollRight = 1u << 14,
        RollUp = 1u << 15,
        RollDown = 1u << 16,
        /// <summary>
        /// Not a button: whether the sender is *currently* zoomed.
        ///
        /// Zoom was the last thing in this packet still being reconstructed on
        /// the receiver from a rising edge, and reconstruction is exactly what
        /// the ammo and the weapon are here to avoid. UpdateZoom is a toggle,
        /// and it is ignored unless the player already holds a weapon that can
        /// zoom -- so at 250 ms, where a puppet's weapon runs a quarter of a
        /// second behind its owner's, the press arrives before the Imperialist
        /// does, the toggle is skipped, the press is spent, and the owner
        /// never presses again because on its own screen it is already zoomed.
        /// Measured against the Pi: 485 frames zoomed on the owner and zero on
        /// all five machines watching.
        ///
        /// A state rather than an edge cannot be missed twice. It costs
        /// nothing -- the mask had fifteen bits spare.
        /// </summary>
        ZoomedState = 1u << 17,
        /// <summary>
        /// Not a button either: which form the sender was in when it measured
        /// the position in this packet.
        ///
        /// Position means two different things depending on form. UpdateForm
        /// shifts it by the distance between the two collision volumes'
        /// centres on the way into alt and back again on the way out, so the
        /// same standing spot is a different number in each. A puppet whose
        /// form has not caught up with its owner's is therefore placed in the
        /// wrong reference frame, and its hitbox sits that far off the body
        /// everyone can see -- vertically, on a biped cylinder only 1.6 units
        /// tall. Reported from play as a player who could not be hurt in
        /// biped form while alt form worked perfectly.
        ///
        /// Sending the form is what lets the receiver convert instead of
        /// guess. Free: another spare bit.
        /// </summary>
        AltFormState = 1u << 18,
        /// <summary>
        /// Whether the sender considers itself alive and on the map.
        ///
        /// Without it the authority cannot tell "here is where I am" from
        /// "here is where my body is lying". A dead player keeps sending
        /// intents, and they keep carrying the spot it died on -- so the
        /// authority puts the puppet on a spawn point and the very next
        /// packet drags it back onto the corpse, which is then published as
        /// the position it respawned at.
        ///
        /// The frame number cannot answer this. It was tried: ignore intents
        /// composed before the spawn. But the owner's counter keeps rising
        /// while it is still dead, so the barrier is cleared within two
        /// frames and the corpse position wins anyway. Measured from a real
        /// session, seven respawns out of seven landed exactly on the spot of
        /// death, to two decimal places, on both machines.
        /// </summary>
        InPlayState = 1u << 19,
        /// <summary>
        /// Not a button either: the sender has stopped playing and is
        /// watching (<see cref="Mods.SpectatorMode"/>).
        ///
        /// Here rather than only in the snapshot because the snapshot travels
        /// the wrong way for this. A spectator sets the flag on its own
        /// machine; the flag reaches everyone else through the authority's
        /// snapshot; and the authority learns nothing about a client except
        /// through this packet. So a client that spectated was hidden and
        /// non-solid on its own screen alone -- on every other machine, the
        /// authority's included, it was still a solid, shootable player
        /// standing exactly where it had stopped, and could be killed for
        /// points while its owner was watching from the ceiling. Measured
        /// against the Pi: 1501 frames spectating on the owner, 0 on the
        /// observer.
        ///
        /// A spare bit, deliberately: an older build ignores it and behaves
        /// exactly as it did before, so this needs no protocol bump.
        /// </summary>
        SpectatingState = 1u << 20,
        /// <summary>
        /// Not a button either: the sender is ready for the next match.
        ///
        /// The results screen's Ready button, which shortens the wait before
        /// the rotation when everybody has pressed it. A state and not an
        /// edge, for the reason <see cref="ZoomedState"/> is one: the answer
        /// has to survive a lost packet, and a player who is ready stays ready
        /// whether or not the datagram that said so arrived.
        ///
        /// Another spare bit, so an older client simply never reads as ready
        /// and the server waits the full time for it -- which is the old
        /// behaviour exactly, and why this needs no protocol bump.
        /// </summary>
        ReadyState = 1u << 21
    }

    /// <summary>
    /// Who is in which slot.
    ///
    /// Names are the check that matters for "are we in the same match":
    /// positions can look plausible while two clients are actually alone in
    /// their own scenes, but a name can only appear on your scoreboard if it
    /// travelled from the other machine.
    /// </summary>
    public struct RosterPacket
    {
        public const int MaxNameBytes = 16;
        public const int MaxSlots = PlayerEntity.SlotCapacity;
        // Slot, hunter, suit colour, round trip time and name per entry. The
        // hunter travels with the name because both answer the same question
        // -- who is in this slot -- and because a client that never learns it
        // draws every other player as whichever hunter this machine happens to
        // have picked. The colour is the same fact one step further: without
        // it every client picked its own, so two people on the same hunter
        // were the same figure in the same suit on every screen. The ping
        // rides along for the same reason: it is a property of who is in the
        // slot, the server is the only party that can measure it for
        // everybody, and it already sends this packet every second.
        public const int EntrySize = 1 + 1 + 1 + 2 + MaxNameBytes + 4;
        public const int HeaderSize = 17;
        public const int Size = HeaderSize + MaxSlots * EntrySize;
        public ushort SessionRevision;
        public ushort MatchId;
        public ulong AuthorityEpoch;
        public uint Revision;
        public ushort[] Generations;

        public byte Count;
        public sbyte[] Teams;
        public bool[] LobbyReady;
        public byte[] Slots;      // slot index per entry
        public byte[] Hunters;    // Hunter enum value per entry
        public byte[] Colors;     // suit palette asked for, 0-3
        public ushort[] Pings;    // round trip to the server, milliseconds
        public string[] Names;

        public static RosterPacket Create()
        {
            return new RosterPacket
            {
                Count = 0,
                Generations = new ushort[MaxSlots],
                Slots = new byte[MaxSlots],
                Teams = new sbyte[MaxSlots],
                LobbyReady = new bool[MaxSlots],
                Hunters = new byte[MaxSlots],
                Colors = new byte[MaxSlots],
                Pings = new ushort[MaxSlots],
                Names = new string[MaxSlots]
            };
        }

        public void Write(Span<byte> dest)
        {
            dest[..Size].Clear();
            dest[0] = Count;
            BinaryPrimitives.WriteUInt16LittleEndian(dest[1..], MatchId);
            BinaryPrimitives.WriteUInt64LittleEndian(dest[3..], AuthorityEpoch);
            BinaryPrimitives.WriteUInt32LittleEndian(dest[11..], Revision);
            BinaryPrimitives.WriteUInt16LittleEndian(dest[15..], SessionRevision);
            int offset = HeaderSize;
            for (int i = 0; i < Count && i < MaxSlots; i++)
            {
                dest[offset] = Slots[i];
                dest[offset + 1] = Hunters[i];
                dest[offset + 2] = Colors[i];
                BinaryPrimitives.WriteUInt16LittleEndian(dest[(offset + 3)..], Pings[i]);
                WriteName(dest.Slice(offset + 5, MaxNameBytes), Names[i]);
                dest[offset + 7 + MaxNameBytes] = unchecked((byte)Teams[i]);
                dest[offset + 8 + MaxNameBytes] = LobbyReady[i] ? (byte)1 : (byte)0;
                BinaryPrimitives.WriteUInt16LittleEndian(dest[(offset + 21)..], Generations[i]);
                offset += EntrySize;
            }
        }

        public static bool TryRead(ReadOnlySpan<byte> src, out RosterPacket roster)
        {
            roster = default;
            if (src.Length != Size || src[0] > MaxSlots) return false;
            int seen = 0;
            for (int i = 0; i < src[0]; i++)
            {
                int offset = HeaderSize + i * EntrySize;
                int slot = src[offset];
                int team = unchecked((sbyte)src[offset + 7 + MaxNameBytes]);
                if (slot >= MaxSlots || (seen & (1 << slot)) != 0 || src[offset + 1] >= 7
                    || src[offset + 2] > 3 || team < -1 || team > 3 || src[offset + 8 + MaxNameBytes] > 1)
                    return false;
                seen |= 1 << slot;
            }
            roster = Read(src);
            return true;
        }

        public static RosterPacket Read(ReadOnlySpan<byte> src)
        {
            RosterPacket roster = Create();
            roster.Count = Math.Min(src[0], (byte)MaxSlots);
            roster.MatchId = BinaryPrimitives.ReadUInt16LittleEndian(src[1..]);
            roster.AuthorityEpoch = BinaryPrimitives.ReadUInt64LittleEndian(src[3..]);
            roster.Revision = BinaryPrimitives.ReadUInt32LittleEndian(src[11..]);
            roster.SessionRevision = BinaryPrimitives.ReadUInt16LittleEndian(src[15..]);
            int offset = HeaderSize;
            for (int i = 0; i < roster.Count; i++)
            {
                roster.Slots[i] = src[offset];
                roster.Hunters[i] = src[offset + 1];
                roster.Colors[i] = src[offset + 2];
                roster.Pings[i] = BinaryPrimitives.ReadUInt16LittleEndian(src[(offset + 3)..]);
                roster.Names[i] = ReadName(src.Slice(offset + 5, MaxNameBytes));
                roster.Teams[i] = unchecked((sbyte)src[offset + 7 + MaxNameBytes]);
                roster.LobbyReady[i] = src[offset + 8 + MaxNameBytes] != 0;
                roster.Generations[i] = BinaryPrimitives.ReadUInt16LittleEndian(src[(offset + 21)..]);
                offset += EntrySize;
            }
            return roster;
        }

        private static void WriteName(Span<byte> dest, string? value)
        {
            dest.Clear();
            if (string.IsNullOrEmpty(value))
            {
                return;
            }
            int count = Math.Min(value.Length, MaxNameBytes);
            for (int i = 0; i < count; i++)
            {
                char c = value[i];
                // The in-game font is ASCII; substitute rather than emit
                // bytes the HUD cannot draw.
                dest[i] = (byte)(c < 32 || c > 126 ? '?' : c);
            }
        }

        private static string ReadName(ReadOnlySpan<byte> src)
        {
            int length = 0;
            while (length < src.Length && src[length] != 0)
            {
                length++;
            }
            return length == 0 ? string.Empty : Encoding.ASCII.GetString(src[..length]);
        }
    }

    /// <summary>
    /// One line somebody typed.
    ///
    /// Additive and ignorable in both directions, exactly like
    /// <see cref="RefusedPacket"/>, so it needs no protocol bump: a server
    /// built before this drops the type on the floor as it always did -- the
    /// sender still sees its own line, nobody else does -- and a client built
    /// before it is never sent one. What that buys is that chat can be
    /// deployed without taking every match offline; what it costs is that
    /// "nobody answered me" on an old server looks exactly like "nobody
    /// answered me", which is why <c>ChatBox</c> says so once per session
    /// when the server never echoes anything back.
    ///
    /// The slot and the name are written by the *server*, not trusted from
    /// the sender: a client can put anything in these fields, and a line
    /// attributed to somebody else is the whole of what a chat exploit is.
    /// The client fills them in anyway, so a demo recorded against a server
    /// that predates chat still replays with a name attached.
    ///
    /// Fixed size, like every other packet here. 114 bytes for something sent
    /// a handful of times a match is not worth a length prefix, and a fixed
    /// layout is the one that cannot be read short.
    /// </summary>
    public struct ChatPacket
    {
        public const int MaxNameBytes = 16;
        /// <summary>
        /// Room for a sentence and no more. The HUD draws these in the DS's
        /// 256-unit space at half size, which is about 90 characters across
        /// once a name and a colon are in front of them, so a longer message
        /// could not be read even if it were carried.
        /// </summary>
        public const int MaxTextBytes = 96;
        public const int Size = 1 + 1 + MaxNameBytes + MaxTextBytes;

        /// <summary>Everyone in the match.</summary>
        public const byte KindSay = 0;
        /// <summary>
        /// One team. Reserved rather than implemented: the modes that have
        /// teams are here, but nothing yet asks the server which side a slot
        /// is on, so the server relays this as <see cref="KindSay"/> instead
        /// of quietly delivering a team line to the other team.
        /// </summary>
        public const byte KindTeam = 1;
        /// <summary>The server itself talking -- joins, leaves, refusals.</summary>
        public const byte KindSystem = 2;

        public byte Slot;
        public byte Kind;
        public string Name;
        public string Text;

        public void Write(Span<byte> dest)
        {
            dest[..Size].Clear();
            dest[0] = Slot;
            dest[1] = Kind;
            WriteAscii(dest.Slice(2, MaxNameBytes), Name);
            WriteAscii(dest.Slice(2 + MaxNameBytes, MaxTextBytes), Text);
        }

        public static ChatPacket Read(ReadOnlySpan<byte> src)
        {
            return new ChatPacket
            {
                Slot = src[0],
                Kind = src[1],
                Name = ReadAscii(src.Slice(2, MaxNameBytes)),
                Text = ReadAscii(src.Slice(2 + MaxNameBytes, MaxTextBytes))
            };
        }

        /// <summary>
        /// The same substitution <see cref="RosterPacket"/> makes, and for the
        /// same reason: the in-game font is ASCII, so a byte it cannot draw
        /// has to be replaced here rather than discovered by the HUD. It also
        /// means a hostile client cannot put a control character on anybody
        /// else's screen -- everything below 32 becomes a question mark on the
        /// way in as well as on the way out.
        /// </summary>
        internal static void WriteAscii(Span<byte> dest, string? value)
        {
            dest.Clear();
            if (String.IsNullOrEmpty(value))
            {
                return;
            }
            int count = Math.Min(value.Length, dest.Length);
            for (int i = 0; i < count; i++)
            {
                char c = value[i];
                dest[i] = (byte)(c < 32 || c > 126 ? '?' : c);
            }
        }

        internal static string ReadAscii(ReadOnlySpan<byte> src)
        {
            int length = 0;
            while (length < src.Length && src[length] != 0)
            {
                length++;
            }
            if (length == 0)
            {
                return String.Empty;
            }
            Span<char> chars = stackalloc char[length];
            for (int i = 0; i < length; i++)
            {
                byte b = src[i];
                chars[i] = b < 32 || b > 126 ? '?' : (char)b;
            }
            return new string(chars);
        }
    }

    public struct IntentPacket
    {
        public ushort MatchId;
        public ulong AuthorityEpoch;
        public ushort SlotGeneration;
        public ushort LifeId;
        /// <summary>
        /// How many frames of rising edges each packet carries. A button held
        /// for one frame -- morph, weapon switch, alt attack -- exists in
        /// exactly one packet, and UDP loses packets: half of them never
        /// arrived, so a player morphed on their own screen and stayed a
        /// biped on everyone else's. Repeating the last few frames of presses
        /// means an action survives three consecutive drops, and the frame
        /// number each one belongs to lets the receiver take each press once.
        /// </summary>
        public const int PressHistory = 8;
        public const int Size = 4 + 4 + 12 + 1 + 4 * PressHistory + 12 + 2 + 2 + 4 + 1 + 14;

        /// <summary>
        /// Four bytes appended <b>past</b> <see cref="Size"/>, carrying the
        /// state that decides what this player's next shot is worth.
        ///
        /// <b>Why it is sent at all.</b> Everything else about a shot was
        /// re-derived on the authority from the buttons in this packet, and
        /// for the three quantities below that re-derivation is a second
        /// simulation of the shooter -- the same mistake the aim deltas and
        /// the ammo count were fixed by, with the same symptom. The charge is
        /// a count of frames the trigger was held, and this packet is sent
        /// every *other* frame over a line that reorders and drops, so the
        /// authority's count is the owner's give or take a few; on a
        /// partial-charge weapon the damage is a continuous function of that
        /// count, so the two machines put different numbers on the same shot
        /// every time it is fired. Double damage and the Prime Hunter bonus
        /// are worse than that: they are pickups and a mode state, collected
        /// by each machine's own simulation, so the authority's copy of a
        /// shooter can simply not have one -- a factor of two on every shot,
        /// with no packet anywhere that would say so.
        ///
        /// Appended rather than folded in, so nothing about the protocol
        /// moves: every receiver reads exactly <see cref="Size"/> bytes and
        /// then asks whether there are four more, and a build from before this
        /// finds none and behaves exactly as it always did.
        /// </summary>
        public const int StateSize = 4;
        public const int FullSize = Size + StateSize;

        /// <summary>
        /// <c>EquipInfo.ChargeLevel</c> as the owner holds it, clamped to a
        /// byte -- the longest charge in the game is 300 frames doubled, which
        /// is the Omega Cannon's and is not chargeable, and every real one is
        /// under 180. Latched at the frame of the newest trigger release in
        /// this packet, because that is the charge the shot was fired with;
        /// the current value otherwise.
        /// </summary>
        public byte ChargeLevel;

        /// <summary>
        /// <c>_boostDamage</c>: what this player's alt-form ram is worth,
        /// which is its boost charge scaled by the hunter's own alt-attack
        /// damage. Latched the same way, since the charge is spent the moment
        /// the ram starts.
        /// </summary>
        public byte BoostDamage;

        /// <summary>The two multipliers, as state rather than as an edge.</summary>
        public byte ShotFlags;

        public const byte FlagDoubleDamage = 1 << 0;
        /// <summary>
        /// Whether the sender believes it is the Prime Hunter, which is worth
        /// x1.5 on every shot. Sent but <b>not applied</b>: who the Prime
        /// Hunter is is the authority's own state, and a client asserting it
        /// would be asserting a damage bonus. It travels so that a mismatch
        /// shows up in a log rather than only in a health bar.
        /// </summary>
        public const byte FlagPrimeHunter = 1 << 1;

        /// <summary>
        /// Whether the sender included the block at all. False for a client
        /// built before it, and the one thing the authority must check before
        /// overwriting a puppet's charge with a zero nobody sent.
        /// </summary>
        public bool HasState;

        public uint Frame;          // client's frame counter, for ordering
        public IntentButtons Buttons;
        /// <summary>Rising edges for Frame, Frame-1, ... Frame-(PressHistory-1).</summary>
        public uint[] Presses;
        /// <summary>
        /// Where the sender's gun points, as a direction rather than as this
        /// frame's mouse movement.
        ///
        /// Deltas were the obvious encoding and the wrong one. Aim is applied
        /// by rotating the receiver's copy, so a single dropped datagram --
        /// UDP, so routine -- left the two machines holding permanently
        /// different aim for the same player, with no mechanism that could
        /// ever bring them back together. The shooter saw its crosshair on an
        /// opponent while the authority, which decides what is hit, had the
        /// gun pointing somewhere else, so its shots simply never connected.
        /// An absolute direction re-agrees on every packet that does arrive.
        /// </summary>
        public Vector3 Aim;
        /// <summary>
        /// Where the sender actually is.
        ///
        /// Sent rather than re-derived, because deriving it meant simulating
        /// the same player twice -- once on their own machine from their
        /// keyboard, once on the authority from these buttons -- and two
        /// simulations of one player drift apart the moment a packet is lost.
        /// They then disagree about collision, and the correction yanks the
        /// player back and forth several times a second: a 10-unit jump, then
        /// the local collision pushing it straight back, forever. Whoever is
        /// playing a character is the one who knows where it is.
        /// </summary>
        public Vector3 Position;
        public byte WeaponSelect;   // 0xFF = no direct weapon switch this frame
        /// <summary>
        /// Universal ammo and missiles, as the owner counts them.
        ///
        /// Sent for the same reason the position is: everyone simulates this
        /// player's shots, only the owner collects this player's pickups, and
        /// the two answers part company within a round. A beam whose cost
        /// exceeds the shooter's ammo is not spawned at all, so a puppet that
        /// has run dry on the authority's machine makes its owner's shots
        /// vanish on the one machine that decides what they hit -- which
        /// looks, from every screen, like a player who cannot be damaged.
        /// </summary>
        public ushort AmmoUa;
        public ushort AmmoMissiles;

        /// <summary>
        /// The newest snapshot frame this client had applied when it composed
        /// this packet -- which is to say, the moment in the authority's
        /// simulation that its screen was showing.
        ///
        /// The one number lag compensation needs. Everything else in this
        /// packet says what the player did; this says what they were looking
        /// at while they did it, and without it the authority can only guess
        /// -- from a smoothed ping, which is an average of a quantity that is
        /// not smooth, and which is measured over a path the intent did not
        /// necessarily take.
        ///
        /// Zero from a client that has not received a snapshot yet, and from
        /// the authority itself, which is never behind its own simulation.
        /// Both mean "do not rewind": see
        /// <see cref="Mods.Network.NetUnlagged.RewindFor"/>, which refuses an
        /// ack it cannot serve rather than serving it approximately.
        /// </summary>
        public uint AckFrame;

        /// <summary>
        /// How far past <see cref="AckFrame"/> the world this client was
        /// looking at actually sat, in 1/256ths of a frame.
        ///
        /// A client that interpolates its puppets is not drawing any one
        /// snapshot: it draws a point between two of them, deliberately a
        /// fixed distance behind the newest, because that is what turns a
        /// stream of positions arriving irregularly into motion. The integer
        /// ack alone cannot name that point, and rounding it costs up to a
        /// frame of rewind -- which on a headshot band 0.3 units tall is the
        /// whole band for anybody moving.
        ///
        /// Zero from a client that does not interpolate, which is what every
        /// build before protocol 7 was, and what <c>-nointerp</c> still is.
        /// The authority lerps between history[AckFrame] and
        /// history[AckFrame + 1] by this fraction; at zero that is exactly
        /// the behaviour it always had.
        /// </summary>
        public byte AckSubFrame;

        public void Write(Span<byte> dest)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(dest[74..], MatchId);
            BinaryPrimitives.WriteUInt64LittleEndian(dest[76..], AuthorityEpoch);
            BinaryPrimitives.WriteUInt16LittleEndian(dest[84..], SlotGeneration);
            BinaryPrimitives.WriteUInt16LittleEndian(dest[86..], LifeId);
            BinaryPrimitives.WriteUInt32LittleEndian(dest[0..], Frame);
            BinaryPrimitives.WriteUInt32LittleEndian(dest[4..], (uint)Buttons);
            BinaryPrimitives.WriteSingleLittleEndian(dest[8..], Aim.X);
            BinaryPrimitives.WriteSingleLittleEndian(dest[12..], Aim.Y);
            BinaryPrimitives.WriteSingleLittleEndian(dest[16..], Aim.Z);
            dest[20] = WeaponSelect;
            for (int i = 0; i < PressHistory; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(dest[(21 + i * 4)..],
                    Presses != null && i < Presses.Length ? Presses[i] : 0);
            }
            int at = 21 + PressHistory * 4;
            BinaryPrimitives.WriteSingleLittleEndian(dest[at..], Position.X);
            BinaryPrimitives.WriteSingleLittleEndian(dest[(at + 4)..], Position.Y);
            BinaryPrimitives.WriteSingleLittleEndian(dest[(at + 8)..], Position.Z);
            BinaryPrimitives.WriteUInt16LittleEndian(dest[(at + 12)..], AmmoUa);
            BinaryPrimitives.WriteUInt16LittleEndian(dest[(at + 14)..], AmmoMissiles);
            BinaryPrimitives.WriteUInt32LittleEndian(dest[(at + 16)..], AckFrame);
            dest[at + 20] = AckSubFrame;
            if (dest.Length >= FullSize)
            {
                dest[Size] = ChargeLevel;
                dest[Size + 1] = BoostDamage;
                dest[Size + 2] = ShotFlags;
                dest[Size + 3] = 0;
            }
        }

        public static IntentPacket Read(ReadOnlySpan<byte> src)
        {
            var presses = new uint[PressHistory];
            for (int i = 0; i < PressHistory; i++)
            {
                presses[i] = BinaryPrimitives.ReadUInt32LittleEndian(src[(21 + i * 4)..]);
            }
            return new IntentPacket
            {
                MatchId = BinaryPrimitives.ReadUInt16LittleEndian(src[74..]),
                AuthorityEpoch = BinaryPrimitives.ReadUInt64LittleEndian(src[76..]),
                SlotGeneration = BinaryPrimitives.ReadUInt16LittleEndian(src[84..]),
                LifeId = BinaryPrimitives.ReadUInt16LittleEndian(src[86..]),
                Frame = BinaryPrimitives.ReadUInt32LittleEndian(src[0..]),
                Buttons = (IntentButtons)BinaryPrimitives.ReadUInt32LittleEndian(src[4..]),
                Aim = new Vector3(
                    BinaryPrimitives.ReadSingleLittleEndian(src[8..]),
                    BinaryPrimitives.ReadSingleLittleEndian(src[12..]),
                    BinaryPrimitives.ReadSingleLittleEndian(src[16..])),
                WeaponSelect = src[20],
                Presses = presses,
                Position = new Vector3(
                    BinaryPrimitives.ReadSingleLittleEndian(src[(21 + PressHistory * 4)..]),
                    BinaryPrimitives.ReadSingleLittleEndian(src[(25 + PressHistory * 4)..]),
                    BinaryPrimitives.ReadSingleLittleEndian(src[(29 + PressHistory * 4)..])),
                AmmoUa = BinaryPrimitives.ReadUInt16LittleEndian(src[(33 + PressHistory * 4)..]),
                AmmoMissiles = BinaryPrimitives.ReadUInt16LittleEndian(src[(35 + PressHistory * 4)..]),
                AckFrame = BinaryPrimitives.ReadUInt32LittleEndian(src[(37 + PressHistory * 4)..]),
                AckSubFrame = src[41 + PressHistory * 4],
                // Only when it is actually there. A client from before this
                // block sends Size bytes and nothing more, and reading zeros
                // out of the end of its datagram would tell the authority that
                // its charge is nothing and its powerups are gone.
                HasState = src.Length >= FullSize,
                ChargeLevel = src.Length >= FullSize ? src[Size] : (byte)0,
                BoostDamage = src.Length >= FullSize ? src[Size + 1] : (byte)0,
                ShotFlags = src.Length >= FullSize ? src[Size + 2] : (byte)0
            };
        }
    }

    /// <summary>
    /// Authoritative per-player state. Position/Speed/facing are what a
    /// remote client cannot derive on its own once float drift is possible;
    /// health/weapon/team are cheap enough to resend every snapshot rather
    /// than tracking deltas at this stage.
    /// </summary>
    public struct DamageEvent
    {
        // Victim identity is implicit in the enclosing PlayerState. Attacker
        // life is diagnostic-only; generation is enough to reject attribution
        // after a slot changes hands. Knockback is bounded to +/-1.5, so a
        // signed 16-bit fixed-point component preserves it to sub-millimetre
        // precision while cutting the event from 26 bytes to 15.
        public const int Size = 15;
        private const float DirectionScale = 16384f;

        public ushort EventId, AttackerGeneration, Damage;
        public byte AttackerSlot, Beam, Flags;
        public Vector3 Direction;

        private static short PackDirection(float value)
            => (short)Math.Clamp((int)MathF.Round(value * DirectionScale), short.MinValue, short.MaxValue);

        private static float UnpackDirection(short value) => value / DirectionScale;

        public readonly void Write(Span<byte> dest)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(dest, EventId);
            BinaryPrimitives.WriteUInt16LittleEndian(dest[2..], AttackerGeneration);
            BinaryPrimitives.WriteUInt16LittleEndian(dest[4..], Damage);
            dest[6] = AttackerSlot;
            dest[7] = Beam;
            dest[8] = Flags;
            BinaryPrimitives.WriteInt16LittleEndian(dest[9..], PackDirection(Direction.X));
            BinaryPrimitives.WriteInt16LittleEndian(dest[11..], PackDirection(Direction.Y));
            BinaryPrimitives.WriteInt16LittleEndian(dest[13..], PackDirection(Direction.Z));
        }

        public static DamageEvent Read(ReadOnlySpan<byte> src) => new DamageEvent
        {
            EventId = BinaryPrimitives.ReadUInt16LittleEndian(src),
            AttackerGeneration = BinaryPrimitives.ReadUInt16LittleEndian(src[2..]),
            Damage = BinaryPrimitives.ReadUInt16LittleEndian(src[4..]),
            AttackerSlot = src[6],
            Beam = src[7],
            Flags = src[8],
            Direction = new Vector3(
                UnpackDirection(BinaryPrimitives.ReadInt16LittleEndian(src[9..])),
                UnpackDirection(BinaryPrimitives.ReadInt16LittleEndian(src[11..])),
                UnpackDirection(BinaryPrimitives.ReadInt16LittleEndian(src[13..])))
        };
    }

    public struct PlayerState
    {
        public ushort SlotGeneration;
        public ushort LifeId;
        public const int Size = 54 + DamageEvent.Size * DamageHistory;

        public byte SlotIndex;
        public byte Flags;          // bit 0 = active, bit 1 = alt form, bit 2 = spawned
        public Vector3 Position;
        public Vector3 Speed;
        public Vector3 Facing;
        public ushort Health;
        public byte CurrentWeapon;
        public byte Team;
        /// <summary>
        /// Counts hits the authority has resolved against this player, so a
        /// receiver can tell a new one from a snapshot it has already seen.
        /// Comparing health instead would replay a repeated snapshot as a
        /// fresh hit and miss two that cancelled out.
        /// </summary>
        public ushort DamageEventId;
        public const int DamageHistory = 4;
        public DamageEvent Damage0, Damage1, Damage2, Damage3;
        public readonly DamageEvent EventAt(int index) => index switch
        {
            0 => Damage0, 1 => Damage1, 2 => Damage2, 3 => Damage3,
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };
        public byte AttackerSlot;   // 0xFF = nobody
        public byte DamageBeam;     // BeamType, 0xFF = not a beam
        public byte DamageFlags;    // headshot / deathalt / burn
        public Vector3 HitDirection;
        /// <summary>
        /// The score, from the machine that keeps it.
        ///
        /// Each client used to count only the deaths it had witnessed, so a
        /// player joining a running match started everyone at zero and its
        /// scoreboard never agreed with anybody else's again. The authority
        /// resolves every kill, so its tally is the one worth sending.
        /// </summary>
        public short Points;
        public ushort Kills;
        public ushort Deaths;

        public const byte FlagActive = 1 << 0;
        public const byte FlagAltForm = 1 << 1;
        /// <summary>
        /// The authority has placed this player at a spawn point. Receivers
        /// use it to tell "standing in the map" from "waiting at the origin
        /// with no health": both look identical in position and health
        /// alone, and treating the second as the first put motionless bodies
        /// at (0,0,0) on every other client.
        /// </summary>
        public const byte FlagSpawned = 1 << 2;
        /// <summary>
        /// Aiming down the Imperialist's sight. Visible to everyone else as
        /// the laser, so it has to travel; it was measured at 2488 frames on
        /// the player holding it and 92 on everyone watching.
        /// </summary>
        public const byte FlagZoomed = 1 << 3;
        /// <summary>
        /// Spectating: hidden and non-solid on every client, not just the
        /// one whose local input is frozen. See <see cref="Mods.SpectatorMode"/>.
        /// </summary>
        public const byte FlagSpectating = 1 << 4;
        /// <summary>
        /// Frozen solid by an affinity Judicator.
        ///
        /// The freeze is produced inside <c>TakeDamage</c>, from the beam
        /// entity that landed the hit -- and a beam entity only ever exists on
        /// the machine that resolved it. <c>NetDamage.Replay</c> has no beam
        /// to give, so every machine but the authority replayed the damage
        /// without the affliction: the victim went on walking about on their
        /// own screen while the authority held them still, and the authority,
        /// which pins a puppet to the position its owner last reported, drew a
        /// player encased in ice sliding around the room. Reported from play
        /// as "frozen players who keep moving", from the person hosting.
        ///
        /// So the state travels rather than the cause. Additive: the byte and
        /// the packet are the size they were, an older build ignores the bit,
        /// and an older authority simply never sets it.
        /// </summary>
        public const byte FlagFrozen = 1 << 5;
        /// <summary>
        /// Disrupted by an affinity Volt Driver's charged shot.
        ///
        /// The same fault as the freeze, one weapon along, and the last bit of
        /// it that was still showing: the disruption is applied inside
        /// <c>TakeDamage</c> from the beam that landed the hit, so on every
        /// machine but the authority's the victim was hit by a charged Volt
        /// Driver and nothing happened at all -- no aim disruption and, above
        /// all, none of the screen distortion the weapon is *known* by. The
        /// shooter watched their charge land and the target play on, and the
        /// target had no idea what had hit them. Reported as "the Volt
        /// Driver's charged shot is missing the screen-distortion effect".
        ///
        /// The distortion itself was never missing: the shader, the shift
        /// table and the four-state machine that drives it are all there in
        /// <c>PlayerHud</c> and <c>Renderer</c>, and they work perfectly for
        /// whoever happens to be the authority. Nothing was ever setting them
        /// off for anybody else.
        /// </summary>
        public const byte FlagDisrupted = 1 << 6;
        /// <summary>
        /// Burning, from an affinity Magmaul's charged shot.
        ///
        /// Third of the same three, and the same story: the flames are spawned
        /// in <c>TakeDamage</c> from the beam, so a victim on any machine but
        /// the authority's took the damage over time -- which is relayed like
        /// any other hit -- while standing there not on fire. "Hunters taking
        /// burn damage do not display the burning visual effect".
        ///
        /// Cosmetic on arrival, and deliberately so: the burn's own tick calls
        /// TakeDamage, and <see cref="NetDamage.Suppress"/> drops that on
        /// every machine that is not resolving the match. What travels is the
        /// fire; the damage keeps coming the way all damage does.
        /// </summary>
        public const byte FlagBurning = 1 << 7;

        public void Write(Span<byte> dest)
        {
            dest[0] = SlotIndex;
            dest[1] = Flags;
            WriteVec(dest[2..], Position);
            WriteVec(dest[14..], Speed);
            WriteVec(dest[26..], Facing);
            BinaryPrimitives.WriteUInt16LittleEndian(dest[38..], Health);
            dest[40] = CurrentWeapon;
            dest[41] = Team;
            BinaryPrimitives.WriteInt16LittleEndian(dest[42..], Points);
            BinaryPrimitives.WriteUInt16LittleEndian(dest[44..], Kills);
            BinaryPrimitives.WriteUInt16LittleEndian(dest[46..], Deaths);
            BinaryPrimitives.WriteUInt16LittleEndian(dest[48..], SlotGeneration);
            BinaryPrimitives.WriteUInt16LittleEndian(dest[50..], LifeId);
            BinaryPrimitives.WriteUInt16LittleEndian(dest[52..], DamageEventId);
            for (int i = 0; i < DamageHistory; i++)
            {
                EventAt(i).Write(dest[(54 + i * DamageEvent.Size)..]);
            }
        }

        public static PlayerState Read(ReadOnlySpan<byte> src)
        {
            var state = new PlayerState
            {
                SlotIndex = src[0],
                Flags = src[1],
                Position = ReadVec(src[2..]),
                Speed = ReadVec(src[14..]),
                Facing = ReadVec(src[26..]),
                Health = BinaryPrimitives.ReadUInt16LittleEndian(src[38..]),
                CurrentWeapon = src[40],
                Team = src[41],
                Points = BinaryPrimitives.ReadInt16LittleEndian(src[42..]),
                Kills = BinaryPrimitives.ReadUInt16LittleEndian(src[44..]),
                Deaths = BinaryPrimitives.ReadUInt16LittleEndian(src[46..]),
                SlotGeneration = BinaryPrimitives.ReadUInt16LittleEndian(src[48..]),
                LifeId = BinaryPrimitives.ReadUInt16LittleEndian(src[50..]),
                DamageEventId = BinaryPrimitives.ReadUInt16LittleEndian(src[52..]),
                Damage0 = DamageEvent.Read(src[54..]),
                Damage1 = DamageEvent.Read(src[(54 + DamageEvent.Size)..]),
                Damage2 = DamageEvent.Read(src[(54 + 2 * DamageEvent.Size)..]),
                Damage3 = DamageEvent.Read(src[(54 + 3 * DamageEvent.Size)..])
            };

            // Keep the existing in-memory convenience fields without paying
            // for a second copy of the newest damage metadata on the wire.
            DamageEvent latest = default;
            for (int i = DamageHistory - 1; i >= 0; i--)
            {
                DamageEvent candidate = state.EventAt(i);
                if (candidate.EventId == state.DamageEventId)
                {
                    latest = candidate;
                    break;
                }
            }
            state.AttackerSlot = latest.EventId == 0 ? (byte)0xFF : latest.AttackerSlot;
            state.DamageBeam = latest.EventId == 0 ? (byte)0xFF : latest.Beam;
            state.DamageFlags = latest.Flags;
            state.HitDirection = latest.Direction;
            return state;
        }

        private static void WriteVec(Span<byte> dest, Vector3 v)
        {
            BinaryPrimitives.WriteSingleLittleEndian(dest[0..], v.X);
            BinaryPrimitives.WriteSingleLittleEndian(dest[4..], v.Y);
            BinaryPrimitives.WriteSingleLittleEndian(dest[8..], v.Z);
        }

        private static Vector3 ReadVec(ReadOnlySpan<byte> src)
        {
            return new Vector3(
                BinaryPrimitives.ReadSingleLittleEndian(src[0..]),
                BinaryPrimitives.ReadSingleLittleEndian(src[4..]),
                BinaryPrimitives.ReadSingleLittleEndian(src[8..]));
        }
    }

    /// <summary>
    /// Host -> clients. Carries both RNG words: Rng.cs reproduces the game's
    /// original LCG exactly and its state is global, so resyncing it keeps
    /// host-side and client-side effects (damage rolls, AI jitter) agreeing
    /// without replicating every consumer of randomness.
    /// </summary>
    public struct SnapshotHeader
    {
        public ushort MatchId;
        public ulong AuthorityEpoch;
        public const int Size = 4 + 4 + 4 + 1 + 10;

        public uint Frame;
        public uint Rng1;
        public uint Rng2;
        public byte PlayerCount;

        public void Write(Span<byte> dest)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(dest[13..], MatchId);
            BinaryPrimitives.WriteUInt64LittleEndian(dest[15..], AuthorityEpoch);
            BinaryPrimitives.WriteUInt32LittleEndian(dest[0..], Frame);
            BinaryPrimitives.WriteUInt32LittleEndian(dest[4..], Rng1);
            BinaryPrimitives.WriteUInt32LittleEndian(dest[8..], Rng2);
            dest[12] = PlayerCount;
        }

        public static SnapshotHeader Read(ReadOnlySpan<byte> src)
        {
            return new SnapshotHeader
            {
                MatchId = BinaryPrimitives.ReadUInt16LittleEndian(src[13..]),
                AuthorityEpoch = BinaryPrimitives.ReadUInt64LittleEndian(src[15..]),
                Frame = BinaryPrimitives.ReadUInt32LittleEndian(src[0..]),
                Rng1 = BinaryPrimitives.ReadUInt32LittleEndian(src[4..]),
                Rng2 = BinaryPrimitives.ReadUInt32LittleEndian(src[8..]),
                PlayerCount = src[12]
            };
        }
    }

    /// <summary>
    /// One hit a client resolved on its own machine and is asking the
    /// authority to make real.
    ///
    /// <b>Why this exists at all.</b> Lag compensation already resolves a
    /// remote shot against the world its shooter was looking at, and instant
    /// hit registration already lets that shooter see the hit land on the
    /// frame they fired it. Both are the authority and the client running the
    /// *same* test on the *same* positions, which is why they normally agree.
    /// What neither can do is survive the cases where they cannot run the same
    /// test:
    ///
    /// * the rewind ran into its ceiling, so the authority resolved the shot
    ///   against a world the shooter never saw (measured at 85% of shots on a
    ///   320 ms line under the old 400 ms ceiling);
    /// * the trigger pull arrived out of a press history and the authority
    ///   cannot tell how old it is;
    /// * the shooter was killed during the round trip, so the authority never
    ///   ran the shot at all -- its copy of that player was already dead when
    ///   the intent arrived. This is the one a player calls unfair rather than
    ///   laggy: they watched the shot land and then watched the body get up.
    ///
    /// A claim is the shooter's own answer to those, carried explicitly. The
    /// authority does not take it on trust -- see
    /// <see cref="Mods.Network.NetHitClaims"/> for the five things it checks --
    /// but where the claim is defensible the shooter's screen is what counts.
    /// It is exactly reciprocal: every client's claims are checked the same
    /// way by the same code, so nobody is favoured by having the worse line.
    ///
    /// Several claims travel in one packet, and unanswered ones are repeated
    /// until a verdict arrives, for the reason
    /// <see cref="IntentPacket.PressHistory"/> repeats presses: UDP loses
    /// packets, and a lost claim is a kill that did not happen.
    /// </summary>
    public struct HitClaimPacket
    {
        public ushort MatchId;
        public ulong AuthorityEpoch;
        public ushort ShooterGeneration;
        public ushort ShooterLifeId;
        public ushort VictimGeneration;
        public ushort VictimLifeId;
        public const int Size = 2 + 4 + 4 + 4 + 1 + 1 + 2 + 1 + 12 + 18;

        /// <summary>How many claims one datagram may carry.</summary>
        public const int MaxPerPacket = 6;

        /// <summary>Beam value meaning "not a beam" -- an alt-form attack, a bomb.</summary>
        public const byte NoBeam = 0xFF;

        /// <summary>The shooter resolved this as a headshot.</summary>
        public const byte FlagHeadshot = 1 << 0;
        /// <summary>The shooter's own copy of the victim died of this hit.</summary>
        public const byte FlagLethal = 1 << 1;
        /// <summary>Judicator ice, so the authority can freeze the victim too.</summary>
        public const byte FlagFrozen = 1 << 2;
        /// <summary>Magmaul fire.</summary>
        public const byte FlagBurning = 1 << 3;
        /// <summary>Volt Driver disruption.</summary>
        public const byte FlagDisrupted = 1 << 4;

        /// <summary>
        /// Rolling, per shooter, so a verdict can name a claim and a repeat
        /// can be recognised as the same one rather than applied twice.
        /// </summary>
        public ushort ClaimId;
        /// <summary>The shooter's own frame counter when it resolved the hit.</summary>
        public uint Frame;
        /// <summary>
        /// The authority frame whose world this was resolved against -- the
        /// same number <see cref="IntentPacket.AckFrame"/> carries, and what
        /// the authority rewinds to in order to check the claim. It is also
        /// the timestamp the kill arbitration orders shots by: two players who
        /// killed each other are separated by which of them pulled the trigger
        /// in the earlier world, not by which packet arrived first.
        /// </summary>
        public uint AckFrame;
        /// <summary>
        /// The world-frame the shot that caused this hit was <b>launched</b>
        /// in -- <c>BeamProjectileEntity.ModLaunchFrame</c>, stamped on every
        /// machine that spawns a beam.
        ///
        /// <b>This is what identifies the shot, and nothing else can.</b>
        /// Pairing a claim with the authority's own resolution of the same
        /// shot by *when they arrived* cannot be made exact: the two are
        /// separated by a round trip, and for anything that travels by however
        /// far the two copies of the projectile drifted apart over its flight
        /// as well -- which grows with range. Measured, a window sized to the
        /// round trip still let one hit in thirty through at zero latency and
        /// applied it on top of the authority's: the victim took the damage,
        /// then took it again when the shot they could see arrived. The launch
        /// frame is the same number on both machines by construction and does
        /// not care how far the shot flew.
        ///
        /// Zero for a hit with no beam behind it -- an alt form's attack, a
        /// bomb, the void -- which fall back to the time window.
        /// </summary>
        public uint LaunchFrame;
        public byte VictimSlot;
        public byte Beam;
        /// <summary>
        /// Damage as the shooter applied it, after every multiplier its own
        /// machine knows about. Checked against what that weapon can possibly
        /// deal before it is believed.
        /// </summary>
        public ushort Damage;
        public byte Flags;
        /// <summary>
        /// Where the shooter says the hit landed. The whole of the geometric
        /// check: the authority looks the victim up in its own history at
        /// <see cref="AckFrame"/> and refuses a claim whose point is nowhere
        /// near the body it finds there.
        /// </summary>
        public Vector3 HitPoint;

        public void Write(Span<byte> dest)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(dest[31..], MatchId);
            BinaryPrimitives.WriteUInt64LittleEndian(dest[33..], AuthorityEpoch);
            BinaryPrimitives.WriteUInt16LittleEndian(dest[41..], ShooterGeneration);
            BinaryPrimitives.WriteUInt16LittleEndian(dest[43..], ShooterLifeId);
            BinaryPrimitives.WriteUInt16LittleEndian(dest[45..], VictimGeneration);
            BinaryPrimitives.WriteUInt16LittleEndian(dest[47..], VictimLifeId);
            BinaryPrimitives.WriteUInt16LittleEndian(dest[0..], ClaimId);
            BinaryPrimitives.WriteUInt32LittleEndian(dest[2..], Frame);
            BinaryPrimitives.WriteUInt32LittleEndian(dest[6..], AckFrame);
            BinaryPrimitives.WriteUInt32LittleEndian(dest[10..], LaunchFrame);
            dest[14] = VictimSlot;
            dest[15] = Beam;
            BinaryPrimitives.WriteUInt16LittleEndian(dest[16..], Damage);
            dest[18] = Flags;
            BinaryPrimitives.WriteSingleLittleEndian(dest[19..], HitPoint.X);
            BinaryPrimitives.WriteSingleLittleEndian(dest[23..], HitPoint.Y);
            BinaryPrimitives.WriteSingleLittleEndian(dest[27..], HitPoint.Z);
        }

        public static HitClaimPacket Read(ReadOnlySpan<byte> src)
        {
            return new HitClaimPacket
            {
                MatchId = BinaryPrimitives.ReadUInt16LittleEndian(src[31..]),
                AuthorityEpoch = BinaryPrimitives.ReadUInt64LittleEndian(src[33..]),
                ShooterGeneration = BinaryPrimitives.ReadUInt16LittleEndian(src[41..]),
                ShooterLifeId = BinaryPrimitives.ReadUInt16LittleEndian(src[43..]),
                VictimGeneration = BinaryPrimitives.ReadUInt16LittleEndian(src[45..]),
                VictimLifeId = BinaryPrimitives.ReadUInt16LittleEndian(src[47..]),
                ClaimId = BinaryPrimitives.ReadUInt16LittleEndian(src[0..]),
                Frame = BinaryPrimitives.ReadUInt32LittleEndian(src[2..]),
                AckFrame = BinaryPrimitives.ReadUInt32LittleEndian(src[6..]),
                LaunchFrame = BinaryPrimitives.ReadUInt32LittleEndian(src[10..]),
                VictimSlot = src[14],
                Beam = src[15],
                Damage = BinaryPrimitives.ReadUInt16LittleEndian(src[16..]),
                Flags = src[18],
                HitPoint = new Vector3(
                    BinaryPrimitives.ReadSingleLittleEndian(src[19..]),
                    BinaryPrimitives.ReadSingleLittleEndian(src[23..]),
                    BinaryPrimitives.ReadSingleLittleEndian(src[27..]))
            };
        }
    }

    /// <summary>
    /// What the authority did with the claims one client sent it.
    ///
    /// Its job is not to tell the shooter whether the hit landed -- the
    /// snapshot already carries that, as it always did. It is to tell the
    /// shooter it may stop asking, and, on a refusal, to say so within one
    /// round trip instead of leaving the prediction to time out over two
    /// seconds with a victim's health held wrong for the whole of it.
    ///
    /// A reason travels with every refusal because "the shot did not count"
    /// is not a diagnosis, and the three refusals mean completely different
    /// things: one is a line problem, one is a fair trade, and one is a claim
    /// the authority thinks is a lie.
    /// </summary>
    public struct HitVerdictPacket
    {
        public const int HeaderSize = 15;
        public const int EntrySize = 3;
        public const int MaxPerPacket = 16;

        /// <summary>The authority applied it. The shooter's screen was right.</summary>
        public const byte ResultApplied = 0;
        /// <summary>
        /// The authority had already resolved this hit itself, so the claim
        /// changed nothing. The normal outcome on a healthy line, and the one
        /// that says the rewind is doing its job without help.
        /// </summary>
        public const byte ResultDuplicate = 1;
        /// <summary>
        /// The shooter was already dead, in their own clock, when they fired.
        /// Somebody killed them in the world they were looking at, before they
        /// pulled the trigger, and this is the arbitration doing what it is
        /// for. Not a fault and not a line problem.
        /// </summary>
        public const byte ResultDeadShooter = 2;
        /// <summary>
        /// The victim was already dead, or gone, or not in play at the frame
        /// claimed. Costs the shooter nothing: somebody else got there first.
        /// </summary>
        public const byte ResultDeadVictim = 3;
        /// <summary>
        /// The authority could not find the victim anywhere near where the
        /// claim says the hit landed, or the damage is more than that weapon
        /// can deal, or the claim is older than the history. This is the one
        /// worth logging: on a clean conscience it means the two machines have
        /// drifted, and otherwise it means somebody is making hits up.
        /// </summary>
        public const byte ResultRefused = 4;
        /// <summary>The claim named a frame the history no longer holds.</summary>
        public const byte ResultTooOld = 5;
        public const byte ResultWrongLife = 6;
        public const byte ResultGeometry = 7;
        public const byte ResultDamageLimit = 8;
        public const byte ResultInvalidLaunch = 9;
        public const byte ResultNoDamage = 10;

        public ushort ClaimId;
        public byte Result;

        public static void Write(Span<byte> dest, ReadOnlySpan<(ushort Id, byte Result)> entries,
            ushort matchId, ulong epoch, ushort generation, ushort lifeId)
        {
            dest[0] = (byte)entries.Length;
            BinaryPrimitives.WriteUInt16LittleEndian(dest[1..], matchId);
            BinaryPrimitives.WriteUInt64LittleEndian(dest[3..], epoch);
            BinaryPrimitives.WriteUInt16LittleEndian(dest[11..], generation);
            BinaryPrimitives.WriteUInt16LittleEndian(dest[13..], lifeId);
            for (int i = 0; i < entries.Length; i++)
            {
                int at = HeaderSize + i * EntrySize;
                BinaryPrimitives.WriteUInt16LittleEndian(dest[at..], entries[i].Id);
                dest[at + 2] = entries[i].Result;
            }
        }

        public static string Describe(byte result)
        {
            return result switch
            {
                ResultWrongLife => "wrong lifecycle",
                ResultGeometry => "hit point outside reconciliation radius",
                ResultDamageLimit => "damage exceeds weapon limit",
                ResultInvalidLaunch => "launch frame follows hit frame",
                ResultNoDamage => "authority damage rules prevented the hit",
                ResultApplied => "applied",
                ResultDuplicate => "already resolved",
                ResultDeadShooter => "shooter was already dead when it fired",
                ResultDeadVictim => "victim was already down",
                ResultRefused => "refused",
                ResultTooOld => "older than the history",
                _ => "unknown"
            };
        }
    }

    public static class NetConfig
    {
        public const ushort DefaultPort = 27888;
        // Keep application datagrams within the IPv6 minimum-MTU budget after
        // UDP/IP headers. Compact PlayerState leaves worst-case 8-player
        // snapshots comfortably below this bound.
        public const int MaxPacketSize = 1232;
        /// <summary>
        /// Bumped when the wire format changes in a way an older build would
        /// misread rather than notice. Version 2 added the ping to the roster:
        /// its entries grew from 18 bytes to 20, and a version 1 client would
        /// have accepted the longer packet and read every name at the wrong
        /// offset. Version 3 added the shooter's ammo to the intent, the
        /// end-of-match handshake, and a name to the status reply. A mismatch
        /// is refused at Hello, with a line in the server log, which is a far
        /// better failure than garbled names.
        ///
        /// Version 4 is the odd one: nothing in the layout moved. It is a
        /// refusal on *behaviour*, because a version 3 build reads every byte
        /// correctly and then plays a different game -- its own player frozen
        /// where it stands, its shots leaving from its ankles, its respawns
        /// putting it back inside whatever it died in. Two of those are worse
        /// coming from the authority than from anyone else, and the authority
        /// is simply the first client to connect, so one stale copy joining
        /// first hands every one of those faults to everybody in the match.
        /// Nothing in the wire would have noticed; this is what makes the
        /// server say no.
        ///
        /// Version 5 grows the intent by four bytes for
        /// <see cref="IntentPacket.AckFrame"/>, which lag compensation reads
        /// to decide how far back a client's shot belongs. It is appended
        /// rather than inserted, so nothing before it moved -- but the packet
        /// is longer, and a version 4 authority handed one would read the
        /// whole thing correctly and then resolve every remote shot against
        /// the present, which is the fault this exists to fix. The layout
        /// change is what forces the refusal; the behaviour is why it is
        /// worth forcing.
        ///
        /// Version 6 puts a suit colour beside the hunter, in Identify and in
        /// the roster, so that two people playing the same hunter are two
        /// different figures on every screen (see
        /// <see cref="Mods.Network.PlayerColors"/>). Both packets are
        /// *inserted* into rather than appended to: the roster's entries grow
        /// from 20 bytes to 21 and every name after the first moves, which a
        /// version 5 client would read as garbage rather than notice. This is
        /// the same shape of change version 2 was, and it is refused the same
        /// way.
        ///
        /// Version 6 also spends the last two bits of the player state's flag
        /// byte on <see cref="PlayerState.FlagDisrupted"/> and
        /// <see cref="PlayerState.FlagBurning"/>, which cost no space and
        /// would not have needed a bump of their own -- an older build ignores
        /// a bit it does not know. They are mentioned here because the byte is
        /// now full: the next flag needs somewhere to live.
        ///
        /// Version 7 is hit registration changing hands. Three things move at
        /// once and each of them alone would force the bump:
        ///
        /// * <see cref="PacketType.HitClaim"/> and
        ///   <see cref="PacketType.HitVerdict"/>. A client now tells the
        ///   authority which of its own shots landed, and the authority either
        ///   agrees, finds it has already resolved the same hit, or refuses it
        ///   with a reason. A version 6 server drops both on the floor -- which
        ///   is safe, and is also a match where every shot still waits for the
        ///   authority's own answer, so the feature is silently absent rather
        ///   than half present.
        /// * <see cref="IntentPacket.AckSubFrame"/>. The intent grows by one
        ///   byte, appended, so nothing before it moved -- but a version 6
        ///   authority would read the packet correctly and rewind to a whole
        ///   frame while the shooter was looking at a point between two of
        ///   them, which is the error this exists to remove.
        /// * The rewind ceiling's default moves from 24 frames to 45. That is
        ///   behaviour rather than layout, and on its own it would be a
        ///   <see cref="ProtocolVersion"/> 4-style refusal: a server one build
        ///   behind resolves 85% of a 320 ms line's shots against a world
        ///   nobody was looking at, measured.
        ///
        /// Version 8 changes continuous-weapon damage timing. Player beams
        /// now use one per-stream firing phase on every machine. Packet layout
        /// is unchanged, but version 7 peers would simulate different ammo
        /// and damage events, so mixed builds must be refused.
        /// Version 10 integrates lifecycle, persistent lobby, team-resource and
        /// continuous-phase branches. Rosters retain generations, teams and ready
        /// flags with separate roster/session revisions; SessionState has an epoch.
        /// Snapshots retain lifecycle identities, team clocks and health spawners.
        /// Version 11 also requires custom-map identity/hash negotiation before
        /// loading a room. Its map-transfer packet IDs remain 32-35.
        /// Version 12 adds the synchronized hidden-opponent-health rule (bit 6),
        /// explicit claim refusal reasons and launch-preserving projectile behavior.
        /// Version 13 compacts PlayerState damage history: victim identity is
        /// implicit in the enclosing state and knockback uses bounded 16-bit
        /// fixed-point components. Mixed v12/v13 peers must be refused because
        /// PlayerState and DamageEvent sizes changed.
        /// Version 14 uses previously reserved health-spawner flag bits to carry
        /// the slot that consumed a pickup, so replicas can play local pickup
        /// feedback only after authority confirmation. Entry size is unchanged,
        /// but v13 readers reject those bits, so mixed peers must be refused.
        /// </summary>
        public const int ProtocolVersion = 14;
        /// <summary>
        /// Frames between intent packets. One, so every frame.
        ///
        /// This is the rate at which a remote player exists, not just the rate
        /// it is corrected at: a puppet is pinned to the position its owner
        /// reported (see NetPlayerBridge.RestoreReportedPosition, which runs
        /// after the engine's own movement step), so whatever the engine
        /// simulates in between is thrown away. At 2 that made every player
        /// but your own move in 30 Hz steps -- on a 60 Hz screen, in a 60 Hz
        /// simulation -- and a recorded demo, where every player is a puppet,
        /// stepped from end to end.
        ///
        /// It was 2 because the server relays N*(N-1) intents per frame and at
        /// six players that was losing enough of them to leave gaps. What made
        /// that true was a transport whose send queue dropped the *newest*
        /// packets when it filled, which is the opposite of what a position
        /// stream wants and was fixed since (see NETWORK-DIAGNOSTICS). Doubled
        /// traffic is the cost: about 100 bytes on the wire per player per
        /// frame, so 42 KB/s into each client of an eight-player match.
        ///
        /// The feature check samples both sides of a comparison on this
        /// cadence, which is now every frame.
        /// </summary>
        public const int IntentSendInterval = 1;
        // A client that has sent nothing for this long is dropped. Generous
        // on purpose: loading a room is synchronous and sends nothing while
        // it runs, and a client dropped mid-load used to be gone for good --
        // it had a slot, so it never said hello again, and every packet it
        // sent afterwards was from an endpoint the server no longer knew.
        public const double TimeoutSeconds = 30.0;
    }

    /// <summary>
    /// One player's move in a map vote: proposing one, or answering the
    /// proposal that is on the table.
    ///
    /// Nothing in here says who is voting. The endpoint the datagram arrived
    /// from is the only thing about a sender that cannot be typed into a text
    /// box, so the server reads the slot from that and ignores anything the
    /// packet might claim -- exactly as <see cref="ChatPacket"/> does, and for
    /// the same reason: a ballot that can be cast on somebody else's behalf is
    /// not a vote.
    /// </summary>
    public struct VotePacket
    {
        /// <summary>As long as the longest room key, which lives in
        /// <see cref="MatchStatePacket.MaxNameBytes"/>.</summary>
        public const int MaxRoomBytes = MatchStatePacket.MaxNameBytes;
        public const int Size = 1 + MaxRoomBytes;

        /// <summary>Put this map to the room.</summary>
        public const byte KindPropose = 0;
        public const byte KindYes = 1;
        public const byte KindNo = 2;

        public byte Kind;
        /// <summary>The map being proposed. Empty on a ballot.</summary>
        public string RoomKey;

        public void Write(Span<byte> dest)
        {
            dest[..Size].Clear();
            dest[0] = Kind;
            ChatPacket.WriteAscii(dest.Slice(1, MaxRoomBytes), RoomKey);
        }

        public static VotePacket Read(ReadOnlySpan<byte> src)
        {
            return new VotePacket
            {
                Kind = src[0],
                RoomKey = ChatPacket.ReadAscii(src.Slice(1, MaxRoomBytes))
            };
        }
    }

    /// <summary>
    /// What the room is being asked, and how the answer is going.
    ///
    /// Broadcast on a timer rather than sent once per change, for the reason
    /// <see cref="MatchStatePacket"/> is: UDP drops, and a client that missed
    /// the one packet would show no prompt at all while everybody else voted.
    /// A client that joins mid-vote gets the same picture from the next tick.
    /// </summary>
    public struct VoteStatePacket
    {
        public const int MaxRoomBytes = MatchStatePacket.MaxNameBytes;
        public const int MaxNameBytes = ChatPacket.MaxNameBytes;
        public const int Size = 1 + MaxRoomBytes + MaxNameBytes + 1 + 1 + 1 + 1 + 2;

        /// <summary>Nothing on the table. The rest of the packet is cleared.</summary>
        public const byte StateIdle = 0;
        public const byte StateRunning = 1;
        public const byte StatePassed = 2;
        public const byte StateFailed = 3;

        public byte State;
        public string RoomKey;
        /// <summary>Who called it, for the line the prompt reads.</summary>
        public string Proposer;
        public byte Yes;
        public byte No;
        /// <summary>How many players could vote when this was counted.</summary>
        public byte Eligible;
        /// <summary>How many yeses it takes. Sent rather than recomputed so
        /// the number on the prompt is the number the server will act on.</summary>
        public byte Needed;
        /// <summary>Seconds left to vote, or -- when idle -- until the room
        /// may call another one.</summary>
        public ushort Seconds;

        public void Write(Span<byte> dest)
        {
            dest[..Size].Clear();
            dest[0] = State;
            ChatPacket.WriteAscii(dest.Slice(1, MaxRoomBytes), RoomKey);
            ChatPacket.WriteAscii(dest.Slice(1 + MaxRoomBytes, MaxNameBytes), Proposer);
            int at = 1 + MaxRoomBytes + MaxNameBytes;
            dest[at] = Yes;
            dest[at + 1] = No;
            dest[at + 2] = Eligible;
            dest[at + 3] = Needed;
            BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(at + 4, 2), Seconds);
        }

        public static VoteStatePacket Read(ReadOnlySpan<byte> src)
        {
            int at = 1 + MaxRoomBytes + MaxNameBytes;
            return new VoteStatePacket
            {
                State = src[0],
                RoomKey = ChatPacket.ReadAscii(src.Slice(1, MaxRoomBytes)),
                Proposer = ChatPacket.ReadAscii(src.Slice(1 + MaxRoomBytes, MaxNameBytes)),
                Yes = src[at],
                No = src[at + 1],
                Eligible = src[at + 2],
                Needed = src[at + 3],
                Seconds = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(at + 4, 2))
            };
        }
    }

    /// <summary>
    /// The short list of maps the results screen offers, and how the room has
    /// voted on it so far.
    ///
    /// A different thing from <see cref="VoteStatePacket"/>, which is a
    /// question put to the room mid-match and answered yes or no. This is the
    /// intermission's own ballot: the server names a handful of maps when a
    /// match ends, everybody picks one off the results screen while they are
    /// reading the scoreboard, and the one in front when the countdown runs
    /// out is the one loaded. Nobody has to propose anything and nobody is
    /// interrupted, because there is nothing to interrupt -- which is the
    /// whole reason the choice belongs here rather than in a vote.
    ///
    /// Broadcast on the same timer as everything else rather than once per
    /// change, for the reason <see cref="MatchStatePacket"/> is: UDP drops,
    /// and a client that missed the one packet would sit through the
    /// intermission with no ballot on screen while everybody else voted.
    ///
    /// Additive in both directions, so it needs no protocol bump: a server
    /// built before this never sends one and the results screen simply shows
    /// the map the rotation was going to play anyway, which is what it showed
    /// before; a client built before it drops an unknown type on the floor.
    /// </summary>
    public struct MapChoicesPacket
    {
        /// <summary>
        /// How many maps the tally can carry: one per player, since that is
        /// the most distinct maps a room can have picked at once.
        ///
        /// The ballot is not a short list any more -- every map is votable and
        /// the client scrolls its own room list -- so what travels is only
        /// what has been picked. Eight entries is the worst case and the
        /// packet is still under three hundred bytes.
        /// </summary>
        public const int MaxChoices = 8;
        public const int MaxRoomBytes = MatchStatePacket.MaxNameBytes;
        public const int Size = 4 + MaxChoices * (MaxRoomBytes + 1);

        /// <summary>
        /// Whether the ballot is open at all.
        ///
        /// Its own byte rather than "Count is zero", because a ballot with
        /// nothing picked yet is the state it spends its first seconds in and
        /// is not the same as no ballot -- one is a list to scroll and the
        /// other is a results screen that says NEXT and nothing else.
        /// </summary>
        public byte Open;

        /// <summary>How many maps below have votes.</summary>
        public byte Count;
        public string[] RoomKeys;
        /// <summary>Votes cast for each, in the same order.</summary>
        public byte[] Votes;
        /// <summary>
        /// How many players could vote when this was counted, for the "3 of 8"
        /// the rows read.
        ///
        /// There is no threshold to send beside it: the map with the most
        /// votes is the one taken, full stop. A mid-match vote needs a bar to
        /// clear because it interrupts people who did not ask to be asked; an
        /// intermission does not, and a bar there only produces the case
        /// nobody wants -- a room that voted, did not reach seventy per cent,
        /// and is sent somewhere none of them picked.
        /// </summary>
        public byte Eligible;

        public void Write(Span<byte> dest)
        {
            dest[..Size].Clear();
            int count = Math.Clamp((int)Count, 0, MaxChoices);
            dest[0] = (byte)count;
            dest[1] = Eligible;
            dest[2] = Open;
            for (int i = 0; i < count; i++)
            {
                int at = 4 + i * (MaxRoomBytes + 1);
                ChatPacket.WriteAscii(dest.Slice(at, MaxRoomBytes),
                    RoomKeys != null && i < RoomKeys.Length ? RoomKeys[i] : "");
                dest[at + MaxRoomBytes] = Votes != null && i < Votes.Length ? Votes[i] : (byte)0;
            }
        }

        public static MapChoicesPacket Read(ReadOnlySpan<byte> src)
        {
            int count = Math.Clamp((int)src[0], 0, MaxChoices);
            var keys = new string[count];
            var votes = new byte[count];
            for (int i = 0; i < count; i++)
            {
                int at = 4 + i * (MaxRoomBytes + 1);
                keys[i] = ChatPacket.ReadAscii(src.Slice(at, MaxRoomBytes));
                votes[i] = src[at + MaxRoomBytes];
            }
            return new MapChoicesPacket
            {
                Count = (byte)count,
                RoomKeys = keys,
                Votes = votes,
                Eligible = src[1],
                Open = src[2]
            };
        }
    }

    /// <summary>
    /// Which map off the ballot this player wants next. Empty means "no
    /// opinion", which is also how a pick is taken back.
    ///
    /// Re-sendable, unlike a vote's ballot: this is asked during an
    /// intermission with a countdown on screen, so changing your mind while
    /// the picture is still up is the normal case rather than a way to game a
    /// race. The server keeps the last one it heard from each slot.
    /// </summary>
    public struct MapPickPacket
    {
        public const int MaxRoomBytes = MatchStatePacket.MaxNameBytes;
        public const int Size = MaxRoomBytes;

        public string RoomKey;

        public void Write(Span<byte> dest)
        {
            dest[..Size].Clear();
            ChatPacket.WriteAscii(dest[..MaxRoomBytes], RoomKey);
        }

        public static MapPickPacket Read(ReadOnlySpan<byte> src)
        {
            return new MapPickPacket
            {
                RoomKey = ChatPacket.ReadAscii(src[..MaxRoomBytes])
            };
        }
    }
}
