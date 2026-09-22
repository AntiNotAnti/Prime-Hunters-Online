using System;
using MphRead.Entities;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;

namespace MphRead.Mods.Network
{
    /// <summary>
    /// Headless authoritative server. No window and no GL, so it still runs
    /// on a small ARM64 box from the command line -- but it needs the game
    /// files now, because it runs the match.
    ///
    /// <b>A dedicated server used to be a relay and is not one any more.</b>
    /// The relay made the first client to connect the simulation authority:
    /// the server assigned slots, forwarded intents to that client, and fanned
    /// its snapshots back out. Everything wrong with it was the same thing --
    /// the match was run by a player's machine. That player's own shots
    /// resolved in the frame they were fired while everybody else's took a
    /// round trip; their line going down took the match with it until somebody
    /// else was promoted; and the one machine nobody could inspect was the one
    /// deciding what everybody hit.
    ///
    /// So <see cref="RunsTheMatch"/> defaults to true and a server that cannot
    /// simulate does not start. That is a deliberate break --
    /// <see cref="ServerSim.Available"/> wants the game files, which the relay
    /// never did -- and it is the reason <c>SERVER.md</c> no longer says a
    /// server needs none.
    ///
    /// The old relay/client-authority path remains only as a compatibility and
    /// test path behind <see cref="RunsTheMatch"/> = false. Normal standalone,
    /// local-hosted and directory-hosted games all run authoritative simulation
    /// in a dedicated process.
    /// </summary>
    public sealed partial class DedicatedServer
    {
        private sealed class Peer
        {
            public IPEndPoint EndPoint = null!;
            public int SlotIndex = -1;
            public double LastSeen;
            public uint LastIntentFrame;
            public string Name = "";
            public byte Hunter;
            /// <summary>The suit this player asked for, 0-3. See PlayerColors.</summary>
            public byte Color;
            /// <summary>Round trip in milliseconds, smoothed. 0 = not measured yet.</summary>
            public int Ping;
            public double PingSentAt;
            public byte PingId;
            public bool PingPending;
            /// <summary>
            /// Chat allowance left, in messages. See <see cref="HandleChat"/>.
            /// Starts full so a player can say hello the moment they arrive.
            /// </summary>
            public double ChatCredit = ChatBurst;
            public double ChatCreditAt;
            /// <summary>
            /// Lines dropped since the last one that got through. Only there
            /// so the log says "this peer is flooding" once rather than once
            /// per dropped packet, which would make the flood the log's
            /// problem as well as the relay's.
            /// </summary>
            public int ChatDropped;
            /// <summary>
            /// Whether this player has pressed Ready on the results screen.
            /// Cleared when a new match starts, so it always describes the
            /// match that is ending and not the one before it.
            /// </summary>
            public bool PostMatchReady;
            public bool LobbyReady;
            public sbyte TeamIndex = -1;
            public readonly Dictionary<uint, LobbyCommandResultPacket> Commands = new();
            public readonly Queue<uint> CommandOrder = new();
            /// <summary>
            /// How this player answered the vote on the table: 0 not yet,
            /// 1 yes, 2 no. Cleared when a vote resolves rather than when one
            /// starts, so a ballot cast cannot be quietly re-cast by
            /// reconnecting into the same slot mid-vote.
            /// </summary>
            public byte Ballot;
            /// <summary>
            /// Which map off the intermission's ballot this player wants, or
            /// "" for none. The last one heard stands, unlike
            /// <see cref="Ballot"/>: this is asked with a countdown on screen
            /// and changing your mind while the picture is up is the normal
            /// case rather than a way to game a race.
            /// </summary>
            public string Pick = "";
            /// <summary>
            /// Who this peer says it is, independent of where it is. See
            /// <see cref="NetSession.ClientId"/>. Zero from a client built
            /// before this existed, which gets the old behaviour.
            /// </summary>
            public uint ClientId;
            /// <summary>Opaque short-lived career attribution ticket. The UDP
            /// server never trusts or decodes it; HTTPS ingestion verifies it.</summary>
            public string CareerTicket = "";
            /// <summary>When this player last put a map to the room.</summary>
            public double LastProposal = Double.NegativeInfinity;
        }

        // ------------------------------------------------------------- voting
        //
        // Players change the map without an admin, and without anybody being
        // able to change it on their own. The rules are the ones a Quake
        // server has always used and are all here rather than spread between
        // the clients: a client draws the prompt and sends a ballot, and
        // every decision -- who may call one, when, what counts as passing --
        // is made once, on the machine that will act on the result.

        /// <summary>
        /// The share of connected players who must say yes.
        ///
        /// Counted against everybody connected, not against everybody who
        /// answered: a vote that passes 2-1 in an eight-player match is five
        /// people having the map changed under them by two. Silence is a no,
        /// which is what makes the threshold mean anything.
        /// </summary>
        private const double VoteThreshold = 0.70;

        /// <summary>How long the room has to answer.</summary>
        private const double VoteSeconds = 30.0;

        /// <summary>
        /// How long after a vote resolves before anybody may call another.
        ///
        /// The room-wide half of the anti-spam rule. Without it a player
        /// whose map lost proposes it again immediately and the prompt is
        /// never off the screen, which is the failure mode this is here to
        /// avoid -- not the load, the nagging.
        /// </summary>
        private const double VoteCooldownSeconds = 90.0;

        /// <summary>
        /// And the per-player half: one proposal each per this long, so a
        /// single player cannot use up every cooldown window in the match.
        /// </summary>
        private const double ProposalCooldownSeconds = 180.0;

        /// <summary>
        /// Fewer players than this and a vote is pointless -- one person
        /// voting for their own map passes 1 of 1 every time, and they can
        /// have the map without the ceremony.
        /// </summary>
        private const int VoteMinimumPlayers = 2;

        /// <summary>
        /// The loop's clock, kept where code reached from a packet can read
        /// it. <see cref="Remove"/> is called from three places that do not
        /// carry the time and has to be able to re-count a vote.
        /// </summary>
        private double _now;

        private bool _voteRunning;
        private string _voteRoom = "";
        private GameMode _voteMode = GameMode.Battle;
        private string _voteProposer = "";
        private int _voteProposerSlot = -1;
        private double _voteStartedAt;
        private double _voteResolvedAt = Double.NegativeInfinity;
        /// <summary>What the last vote did, for the packet clients read while
        /// nothing is running. See VoteStatePacket.</summary>
        private byte _voteResult = VoteStatePacket.StateIdle;

        /// <summary>
        /// Whether players may change the map by voting. On by default: a
        /// server nobody can steer is a server people leave. <c>-novote</c>
        /// turns it off for admins who would rather set the rotation and have
        /// it respected.
        /// </summary>
        public bool AllowMapVotes { get; set; } = true;

        private readonly List<Peer> _peers = new();
        private readonly byte[] _scratch = new byte[NetConfig.MaxPacketSize];
        private readonly int _port;
        private readonly int _maxPlayers;
        private readonly MapRotation _rotation;
        private NetTransport? _transport;
        private Peer? _authority;
        /// <summary>
        /// The match simulated in this process. Null only in the explicit
        /// RunsTheMatch=false compatibility/test path.
        /// </summary>
        private ServerSim? _sim;
        private byte[]? _lastSnapshot;
        private volatile bool _running;
        private double _matchStarted;
        /// <summary>
        /// When the match ended, or -1 while one is being played.
        ///
        /// A match ends on this server for two reasons and they used to be
        /// handled as one: the clock running out, which the server saw for
        /// itself, and somebody reaching the score, which only the machine
        /// simulating the match can know. The second case rotated nothing at
        /// all -- the winner was announced, every client faded to black, and
        /// each one dropped back to its own launcher. Both now enter the same
        /// short intermission, which is what gives the results screen time to
        /// play before everyone is moved together.
        /// </summary>
        private double _matchEndedAt = -1;

        /// <summary>
        /// Counts the matches this server has started. Published so a client
        /// can tell a new round from the one it is already playing, which the
        /// map name cannot do when the rotation is one map long.
        /// </summary>
        private ushort _matchId = 1;
        private ulong _authorityEpoch = (ulong)DateTime.UtcNow.Ticks;
        private uint _rosterRevision;
        private readonly ushort[] _slotGenerations = new ushort[PlayerEntity.SlotCapacity];
        private readonly ushort[] _slotLives = new ushort[PlayerEntity.SlotCapacity];
        private uint _snapshotFrame;
        private bool _snapshotSeen;

        /// <summary>
        /// How long the results are left on screen before the map changes.
        ///
        /// The client's own end-of-match sequence is three seconds of the
        /// winner's camera and <see cref="GameState.MatchEndingSeconds"/> of
        /// the results; a second on top of that means the fade to black
        /// belongs to the rotation rather than cutting the results short.
        ///
        /// It has to cover the whole sequence, because the results screen is
        /// where the next hunter and the next suit are chosen now (see
        /// Mods.EndScreen) -- a server that rotated early would take the
        /// question away mid-answer.
        /// </summary>
        private const double EndSequenceSeconds = 3.0 + GameState.MatchEndingSeconds + 1.0;

        /// <summary>
        /// Post-match is a fixed decision window. Map selection is itself the
        /// action players take between rounds; requiring a second Ready step
        /// made the ballot feel stuck and could stretch every match by thirty
        /// seconds. The floor above already covers the complete results
        /// sequence, hunter/suit choice and the ballot.
        /// </summary>
        private static double EndSequenceFor() => EndSequenceSeconds;

        /// <summary>
        /// What this server calls itself on a browser's list. Defaults to the
        /// machine name, because an unnamed row in a list of servers is worse
        /// than a dull one.
        /// </summary>
        public string ServerName { get; set; } = Environment.MachineName;

        /// <summary>
        /// How many peers are connected, for a pool deciding whether a game it
        /// started is still being played.
        ///
        /// Read from another thread on purpose. It is one int, it is only ever
        /// used to answer "has this been empty for minutes", and taking a lock
        /// on the relay's hot path to make a housekeeping check exact would be
        /// the wrong trade.
        /// </summary>
        public int PeerCount => _peers.Count;

        /// <summary>
        /// Whether anybody has ever been in this match.
        ///
        /// The difference between "started a moment ago and the host is still
        /// loading the map" and "was being played and everyone has left" --
        /// which want completely different amounts of patience from whatever
        /// is deciding when to shut it down.
        /// </summary>
        public bool EverOccupied { get; private set; }

        /// <summary>Whether the listener is up, so a pool can wait for it.</summary>
        public bool Listening => _transport != null;

        /// <summary>The port actually bound, which is not the requested one when that was zero.</summary>
        public int BoundPort => _transport?.LocalPort ?? _port;

        /// <summary>
        /// Where to announce this server, or null to stay unlisted. See
        /// <see cref="MasterReporter"/>.
        /// </summary>
        public MasterReporter? Reporter { get; set; }

        /// <summary>
        /// Whether same-team damage counts, for the whole session. This is
        /// the server's call, broadcast in every <see cref="MatchStatePacket"/>
        /// so every client applies the same rule -- each client's own local
        /// setting used to be what decided this, which meant the host turning
        /// it on in Match rules never reached anyone else.
        /// </summary>
        public bool FriendlyFire { get; set; }

        /// <summary>
        /// The damage level this server plays at, broadcast so that nobody's
        /// copy of <c>TakeDamage</c> uses a different multiplier from the
        /// machine resolving the shot.
        ///
        /// <b>Always medium, which is x1.</b> It is not an option and there is
        /// no flag for it: see <see cref="GameState.DamageLevel"/>. Sent
        /// anyway, because sending it is what puts right a client that has one
        /// of the other two from somewhere.
        /// </summary>
        public int DamageLevel => 1;

        /// <summary>
        /// Whether weapon pickups are the picking hunter's affinity variant,
        /// broadcast for the same reason: the affinity weapons are a different
        /// row of the damage table. <c>-affinityweapons</c>.
        /// </summary>
        public bool AffinityWeapons { get; set; }

        /// <summary>
        /// Whether the shadow freeze glitch is allowed here. On by default,
        /// because it is what the cartridge does and a server that quietly
        /// changed the game would be a surprising one; <c>-noshadowfreeze</c>
        /// turns it off for the room. See GameState.ShadowFreeze.
        /// </summary>
        public bool ShadowFreeze { get; set; } = true;

        /// <summary>
        /// Whether this server keeps itself on the newest release.
        ///
        /// Opt-in, and set by exactly one caller: the standalone
        /// <c>-server</c> path, which is the process's whole reason for
        /// existing. The other two <see cref="DedicatedServer"/>s in the
        /// program must not -- a listen host is a server inside somebody's
        /// game, and a hosted match is one of several inside the directory's
        /// process, so a swap decided here would take down a program that was
        /// doing something else as well, and several of them would race to
        /// decide it.
        /// </summary>
        public bool AutoUpdate { get; set; }

        /// <summary>
        /// Canonical server replay recording/retention for any server process
        /// that runs the authoritative match. The legacy RunsTheMatch=false
        /// compatibility path does not produce canonical server replays.
        /// </summary>
        public ServerReplayPolicy ReplayPolicy { get; set; } = ServerReplayPolicy.Default;

        /// <summary>
        /// Whether this server runs the match itself.
        ///
        /// <b>True by default, and the standalone <c>-server</c> process never
        /// changes it.</b> That is the whole of "authoritative by default":
        /// there is no flag to pass and no relay to fall back to, and a server
        /// that cannot build a world refuses to start rather than becoming
        /// something else. <c>-simulate</c> and <c>-authority</c> are still
        /// accepted so deployed units keep starting, and do nothing.
        ///
        /// False is retained only for compatibility and deterministic tests of
        /// the old client-authority protocol. Normal hosting does not select it:
        /// <see cref="NetHostSession"/>, <see cref="NetMaster"/> and
        /// <see cref="HostPool"/> start isolated server processes so each match
        /// gets its own static <see cref="NetSession"/> and server authority.
        /// </summary>
        public bool RunsTheMatch { get; init; } = true;

        /// <summary>
        /// The extra matches this server is running for other people, on
        /// ports of its own. Empty and refusing everything unless an admin
        /// passed <c>-hostports</c>. See <see cref="HostPool"/>.
        /// </summary>
        public HostPool Hosts { get; }

        /// <summary>True when this server is the match's simulation authority.</summary>
        public bool Simulating => _sim != null && _sim.Running;

        public DedicatedServer(int port = NetConfig.DefaultPort, int maxPlayers = 4,
                               MapRotation? rotation = null)
        {
            _port = port;
            _maxPlayers = Math.Clamp(maxPlayers, 2, MphRead.Entities.PlayerEntity.SlotCapacity);
            _rotation = rotation ?? new MapRotation();
            Hosts = new HostPool
            {
                Log = Log,
                // A game this server opens announces itself the way this
                // server does, to the same directory: a hosted match nobody
                // can find is a match nobody joins.
                ReporterFactory = () => Reporter == null
                    ? null
                    : new MasterReporter(Reporter.Host, Reporter.Port)
            };
        }

        public void Run(CancellationToken cancel = default)
        {
            _transport = new NetTransport(_port);
            _running = true;
            Log($"listening on UDP {_transport.LocalPort}, up to {_maxPlayers} players");
            if (RunsTheMatch)
            {
                // This process never draws. Enter before lobby prewarm so a
                // pre-parsed room model skips display-list/texture decode just
                // like the authoritative scene will.
                Mods.Headless.Enter();
                ServerReplayRecorder.Configure(ReplayPolicy);
                CareerReportOutbox.Start();
            }
            _lobbyMatch = DefinitionFor(_rotation.Current);
            _phase = SessionPolicy == ServerSessionPolicy.Lobby ? SessionPhase.Lobby : SessionPhase.InMatch;
            if (_phase == SessionPhase.Lobby && RunsTheMatch)
                Mods.RoomPrewarm.Begin(_lobbyMatch.RoomKey);
            if (_phase == SessionPhase.InMatch) StartSimulation();
            Log(Simulating
                ? "this server runs the match itself"
                : "compatibility mode: the first client to connect runs the match");
            Log($"rotation: {_rotation.Entries.Count} map(s), starting on {_rotation.Current}");
            Log(Hosts.Describe());

            // The bound port, taken once: the heartbeat has to advertise the
            // port players dial, which is not the requested one when the
            // requested one was zero.
            var listenPort = (ushort)_transport.LocalPort;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            double lastReport = 0;
            double lastStateBroadcast = 0;
            _matchStarted = 0;
            try
            {
                while (_running && !cancel.IsCancellationRequested)
                {
                    double now = clock.Elapsed.TotalSeconds;
                    _now = now;
                    foreach (ReceivedPacket packet in _transport.Drain())
                    {
                        Handle(packet, now);
                    }
                    DropTimedOut(now);
                    // After the packets and before anything that reads the
                    // world: the intents that arrived this pass are the input
                    // to the steps this pass owes, exactly as a client applies
                    // what arrived before it steps.
                    CheckLoadBarrier(now);
                    if (_phase is SessionPhase.InMatch or SessionPhase.PostMatch) _sim?.Advance(now);
                    EnsureCareerMatchStarted(now);

                    // The server owns the match clock, not the authority client:
                    // that is what lets a joiner adopt a running match's timer
                    // instead of starting its own, and what keeps the rotation
                    // advancing even while players come and go.
                    float limit = CurrentDefinition.TimeLimitSeconds;
                    if (_phase == SessionPhase.InMatch && _matchEndedAt < 0 && limit > 0 && _peers.Count > 0
                        && now - _matchStarted >= limit)
                    {
                        EndMatch(now, "time limit");
                    }
                    else if (_matchEndedAt >= 0 && now - _matchEndedAt >= EndSequenceFor())
                    {
                        // Persistent lobbies always stop at the lobby after the
                        // report. Continuous servers keep their rotation behavior.
                        if (SessionPolicy == ServerSessionPolicy.Lobby) ReturnToLobby();
                        else AdvanceMap(now);
                    }
                    // Repeated rather than sent once: UDP drops, and a client that
                    // missed the state packet would otherwise sit on a stale map.
                    if (now - lastStateBroadcast >= 1.0)
                    {
                        lastStateBroadcast = now;
                        // Order matters only in that the roster carries the last
                        // measurement: ping first, publish second.
                        PingPeers(now);
                        BroadcastSessionState();
                        if (_phase is SessionPhase.InMatch or SessionPhase.PostMatch) BroadcastMatchState(now);
                        BroadcastRoster();
                        // A vote nobody finishes answering has to time out,
                        // and a client that missed a VoteState packet has to
                        // get another one. Both are this cadence's job.
                        Tally(now);
                        BroadcastVoteState(now);
                        // UDP drops, and a client that missed the ballot would
                        // sit out the whole intermission with nothing to pick
                        // from. Sent only while there is one, so this costs
                        // nothing during a match.
                        if (_ballotOpen)
                        {
                            BroadcastMapChoices();
                        }
                        if (_authority != null && !RunsTheMatch)
                        {
                            NotifyAuthority(_authority);
                        }
                        Reporter?.Beat(now, ServerName, listenPort,
                            (byte)_peers.Count, (byte)_maxPlayers,
                            (byte)CurrentDefinition.Mode, CurrentDefinition.RoomKey);
                    }
                    // The games this server is running for other people,
                    // reaped here rather than on their own threads: a match
                    // that ended on Tuesday is a port nobody can use until
                    // somebody notices.
                    Hosts.Reap(now);
                    // Newest release, checked on a timer and applied the
                    // moment there is nobody to interrupt. It says yes at most
                    // once, and only with an empty server, so a busy one keeps
                    // playing and swaps when the last person leaves.
                    //
                    // Hosted games count as active work too. They run in child
                    // server processes tracked by Hosts, and restarting this parent
                    // would tear those children down while people are playing.
                    if (AutoUpdate
                        && Update.ServerUpdate.ShouldRestart(_peers.Count + Hosts.Count))
                    {
                        Log("shutting down to come back on the new build");
                        _running = false;
                        break;
                    }
                    if (now - lastReport >= 30)
                    {
                        lastReport = now;
                        Log($"{_peers.Count} peer(s) connected"
                            + (Simulating ? ", authority = this server"
                                : _authority != null ? $", authority = slot {_authority.SlotIndex}"
                                : ", no authority")
                            + $", map {CurrentDefinition.RoomKey}"
                            + (limit > 0 ? $", {Math.Max(0, limit - (now - _matchStarted)):0} s left" : "")
                            + (_transport is { PacketsDropped: > 0 }
                                ? $", {_transport.PacketsDropped} packet(s) dropped" : ""));
                        if (_sim != null)
                        {
                            // The one number that decides whether this box can
                            // host: a step is owed 16.7 ms and the worst one is
                            // what a stutter is made of.
                            Log($"sim: {_sim.Describe()}");
                            // And what the rewind is doing, which nothing else
                            // prints now that no client is the authority.
                            Log($"sim: {_sim.DescribeUnlagged()}");
                            Log($"sim: {_sim.DescribeRewindDepths()}");
                            string? claimLine = _sim.DescribeClaims();
                            if (claimLine != null)
                            {
                                Log($"sim: {claimLine}");
                            }
                            Log($"sim: {_sim.DescribeShots()}");
                            foreach (string line in _sim.DescribeAgreement().Split('\n'))
                            {
                                Log($"sim: {line}");
                            }
                        }
                    }
                    // Pace around the simulation's absolute next deadline.
                    // Empty non-simulating servers can still sleep deeply; an
                    // active authority sleeps most of the gap, yields near the
                    // boundary, and only spins for the final fraction.
                    PaceLoop(clock);
                }
            }
            finally
            {
                Shutdown(listenPort);
            }
        }

        /// <summary>
        /// Keep packet handling responsive while placing 60 Hz authority steps
        /// on their absolute deadlines. Sleep for the bulk of the remaining
        /// time, yield close to the deadline, and spin only for the final tiny
        /// fraction so scheduler granularity does not become server jitter.
        /// </summary>
        private void PaceLoop(System.Diagnostics.Stopwatch clock)
        {
            if (_peers.Count == 0 && !Simulating)
            {
                Thread.Sleep(20);
                return;
            }

            if (_sim?.Running == true)
            {
                double remaining = _sim.SecondsUntilNextStep(clock.Elapsed.TotalSeconds);
                // Windows Sleep(1) can still inherit a coarse scheduler tick.
                // Use it only when there is enough room for that worst case;
                // Unix sleeps are fine much closer to the deadline.
                double coarseSleepRoom = OperatingSystem.IsWindows() ? 0.012 : 0.002;
                if (remaining > coarseSleepRoom)
                {
                    Thread.Sleep(1);
                    return;
                }
                if (remaining > 0.00025)
                {
                    Thread.Yield();
                    return;
                }
                if (remaining > 0)
                {
                    long deadline = System.Diagnostics.Stopwatch.GetTimestamp()
                        + (long)(remaining * System.Diagnostics.Stopwatch.Frequency);
                    while (System.Diagnostics.Stopwatch.GetTimestamp() < deadline)
                    {
                        Thread.SpinWait(32);
                    }
                    return;
                }
            }

            Thread.Yield();
        }

        /// <summary>
        /// Come off the list and give the socket back.
        ///
        /// In a finally, not after the loop: this used to be plain statements
        /// at the end of Run, so anything thrown inside the loop skipped the
        /// farewell entirely and left the game listed -- offered to players,
        /// answering nothing -- until the directory timed it out. A hosted
        /// game runs inside the player's own process, where an exception is
        /// swallowed by the thread wrapper and nothing says so.
        /// </summary>
        private void Shutdown(ushort listenPort)
        {
            Log("shutting down");
            Hosts.StopAll("the server is shutting down");
            _running = false;
            try
            {
                // Say so, rather than letting the directory work it out from
                // fifty seconds of silence. A server that has just been
                // stopped is a server nobody should still be offered.
                Reporter?.Farewell(listenPort);
            }
            catch (Exception)
            {
                // Nothing left to tell, and nothing left to do about it.
            }
            Reporter?.Dispose();
            Reporter = null;
            _transport?.Dispose();
            _transport = null;
            // After the socket, so nothing arrives for a world that is being
            // torn down. Stop() also ends the NetSession this process held as
            // the authority, which is what a restarting server has to have
            // done before it starts another.
            ServerReplayRecorder.Stop();
            _sim?.Stop();
            _sim = null;
            NetHitClaims.VerdictSink = null;
        }

        /// <summary>
        /// Begin the intermission. Announced immediately rather than waiting
        /// for the next periodic state, because the clients are about to show
        /// their results screens and the flag is what stops them adopting the
        /// match clock over the countdown that screen runs on.
        /// </summary>
        private void EndMatch(double now, string reason)
        {
            if (_matchEndedAt >= 0 || _phase != SessionPhase.InMatch)
            {
                return;
            }
            _matchEndedAt = now;
            CompleteCareerMatch(now, reason);
            foreach (Peer peer in _peers) peer.PostMatchReady = false;
            CancelMapVote(now);
            SetPhase(SessionPhase.PostMatch);

            // A persistent lobby is the between-match decision point now. Keep
            // the report/hunter picker, but do not ask a second map question on
            // top of it. Continuous servers retain the results ballot/rotation.
            if (SessionPolicy == ServerSessionPolicy.Lobby) CloseBallot();
            else OpenBallot();

            Log($"match over on {CurrentDefinition.RoomKey} ({reason}); "
                + (SessionPolicy == ServerSessionPolicy.Lobby
                    ? $"returning to lobby in {EndSequenceFor():0} s"
                    : $"{_rotation.Next.RoomKey} in {EndSequenceFor():0} s"
                        + (_ballotOpen ? "; ballot open" : "")));
            BroadcastMatchState(now);
            BroadcastMapChoices();
        }

        private void AdvanceMap(double now)
        {
            ServerReplayRecorder.Stop(matchEnded: true);
            RotationEntry entry = _rotation.Advance();
            _phase = SessionPhase.InMatch;
            NormalizeTeams();
            _matchStarted = now;
            _matchEndedAt = -1;
            _matchId = NetLifecycleTracker.Next(_matchId);
            _snapshotSeen = false;
            Array.Clear(_slotLives);
            foreach (Peer connected in _peers) connected.LastIntentFrame = 0;
            TouchLobbyRevision("rotation advanced");
            // Votes belong to one match. No prompt, result or cooldown may
            // leak into the next room.
            CancelMapVote(now);
            // Ready describes the match that just ended. Carried into the next
            // one it would rotate the following map the moment it finished.
            for (int i = 0; i < _peers.Count; i++)
            {
                _peers[i].PostMatchReady = false;
            }
            // The ballot has been acted on; there is no next map to choose
            // again until this one is over. Closed *after* Advance has taken
            // what the picks made pending, which is the line above it.
            CloseBallot();
            BroadcastMapChoices();
            Log($"rotating to {entry}");
            MatchStatePacket state = BuildState(now);
            // The simulation follows a rotation the way every client does:
            // NetRoomChange watches the match state and loads the new room as
            // a *transition* rather than rebuilding the scene. Reusing that
            // path rather than restarting the sim is deliberate -- it is the
            // one that carries the fixes for the intro camera sequence and the
            // settling window, and a second path here would have neither.
            if (_sim != null)
            {
                NetSession.ApplyMatchState(state, rotated: true);
            }
            state.Write(_scratch);
            for (int i = 0; i < _peers.Count; i++)
            {
                _transport?.Send(_peers[i].EndPoint, PacketType.MapChange,
                    _scratch.AsSpan(0, MatchStatePacket.Size));
            }
        }

        private MatchStatePacket BuildState(double now)
        {
            MatchDefinition entry = CurrentDefinition;
            float elapsed = _phase is SessionPhase.Lobby or SessionPhase.Starting ? 0 : (float)(now - _matchStarted);
            bool ending = _matchEndedAt >= 0;
            return new MatchStatePacket
            {
                Mode = (byte)entry.Mode,
                TimeRemaining = ending || entry.TimeLimitSeconds <= 0
                    ? 0
                    : Math.Max(0, entry.TimeLimitSeconds - elapsed),
                TimeElapsed = elapsed,
                PlayerCount = (byte)_peers.Count,
                Flags = (byte)((ending ? MatchStatePacket.FlagEnding : MatchStatePacket.FlagInProgress)
                    | (entry.FriendlyFire ? MatchStatePacket.FlagFriendlyFire : 0)
                    | (entry.ShadowFreeze ? 0 : MatchStatePacket.FlagNoShadowFreeze)
                    | MatchStatePacket.RuleFlags(DamageLevel, entry.AffinityWeapons)),
                PointGoal = entry.PointGoal,
                MatchId = _matchId,
                AuthorityEpoch = _authorityEpoch,
                RoomKey = entry.RoomKey,
                // In a persistent lobby the next match is not committed until
                // somebody starts it from the lobby, so the results screen must
                // not promise a map that can still be changed there.
                NextRoomKey = SessionPolicy == ServerSessionPolicy.Lobby
                    ? ""
                    : (_returnToLobbyPending ? "" : _rotation.Next.RoomKey)
            };
        }

        private void BroadcastMatchState(double now)
        {
            if (_sim != null) NetSession.ApplySessionState(BuildSessionState());
            MatchStatePacket state = BuildState(now);
            // The simulation follows the clock, the mode and the map exactly
            // as a client does -- including the flag that says the match is
            // ending, which is what starts its results sequence.
            if (_sim != null)
            {
                NetSession.ApplyMatchState(state, rotated: false);
            }
            state.Write(_scratch);
            ServerReplayRecorder.Record(PacketType.MatchState,
                _scratch.AsSpan(0, MatchStatePacket.Size));
            if (_peers.Count == 0)
            {
                return;
            }
            for (int i = 0; i < _peers.Count; i++)
            {
                _transport?.Send(_peers[i].EndPoint, PacketType.MatchState,
                    _scratch.AsSpan(0, MatchStatePacket.Size));
            }
        }

        // ------------------------------------------------- the simulation

        /// <summary>
        /// Build the world this server is the authority for, or refuse to run.
        ///
        /// <b>There is no fallback, and that is the change.</b> The fallback
        /// was the relay, and the relay handed the match to a player's
        /// machine. A server that cannot build a world cannot run a match, and
        /// saying so at startup is better than starting and being something
        /// else. An installation that has been running without the game files
        /// stops here, with the reason, on the first start after the update.
        ///
        /// Skipped entirely when <see cref="RunsTheMatch"/> is false. That mode
        /// exists for compatibility/tests; normal hosted games run in isolated
        /// server processes with RunsTheMatch=true.
        /// </summary>
        /// <exception cref="ProgramException">
        /// The world could not be built. Thrown rather than logged and limped
        /// past: the process exits, systemd reports a failed unit, and the
        /// operator sees a stopped server instead of one quietly running
        /// somebody else's match.
        /// </exception>
        private void StartSimulation()
        {
            if (!RunsTheMatch)
            {
                return;
            }
            if (!ServerSim.Available(out string why))
            {
                Log($"cannot run the match: {why}");
                Log("a dedicated server runs the match itself now, so this one will not "
                    + "start. Put the game files on this machine and paths.txt beside "
                    + "the binary -- see SERVER.md");
                throw new ProgramException($"the server cannot run the match: {why}");
            }
            var sim = new ServerSim();
            MatchDefinition entry = CurrentDefinition;
            if (!sim.Start(entry.RoomKey, entry.Mode, _maxPlayers, SendSnapshot,
                () => EndMatch(_now, "score"), BuildRoster(), BuildSessionState()))
            {
                Log($"cannot run the match: the room \"{entry.RoomKey}\" would not load");
                throw new ProgramException($"the server could not load \"{entry.RoomKey}\"");
            }
            _sim = sim;
            Mods.RoomPrewarm.Release(entry.RoomKey);
            // This server arbitrates its clients' hit claims for as long as it
            // is running the match, so it needs a way to answer them.
            // NetHitClaims.
            NetHitClaims.VerdictSink = SendVerdicts;
            SyncSimulationState(_now);
        }

        /// <summary>
        /// A snapshot the simulation in this process just composed, out to
        /// everybody.
        ///
        /// Every peer without exception, unlike the relay path, which skips
        /// the sender: the sender here is the server, and it is in nobody's
        /// slot.
        /// </summary>
        private void SendSnapshot(ReadOnlySpan<byte> payload)
        {
            _lastSnapshot = payload.ToArray();
            EnsureCanonicalReplay(payload);
            ServerReplayRecorder.Record(PacketType.Snapshot, payload);
            for (int i = 0; i < _peers.Count; i++)
            {
                _transport?.Send(_peers[i].EndPoint, PacketType.Snapshot, payload);
            }
        }

        private void EnsureCanonicalReplay(ReadOnlySpan<byte> snapshot)
        {
            if (!ServerReplayRecorder.Enabled || ServerReplayRecorder.IsRecording
                || _sim == null || _peers.Count == 0)
            {
                return;
            }

            try
            {
                SessionStatePacket session = BuildSessionState();
                MatchStatePacket state = BuildState(_now);
                RosterPacket roster = BuildRoster();
                byte[] sessionPacket = new byte[1 + SessionStatePacket.Size];
                sessionPacket[0] = (byte)PacketType.SessionState;
                session.Write(sessionPacket.AsSpan(1));
                byte[] statePacket = new byte[1 + MatchStatePacket.Size];
                statePacket[0] = (byte)PacketType.MatchState;
                state.Write(statePacket.AsSpan(1));
                byte[] rosterPacket = new byte[1 + RosterPacket.Size];
                rosterPacket[0] = (byte)PacketType.Roster;
                roster.Write(rosterPacket.AsSpan(1));
                byte[] snapshotPacket = new byte[1 + snapshot.Length];
                snapshotPacket[0] = (byte)PacketType.Snapshot;
                snapshot.CopyTo(snapshotPacket.AsSpan(1));

                var players = new List<ReplayPlayerInfo>(roster.Count);
                for (int i = 0; i < roster.Count; i++)
                {
                    players.Add(new ReplayPlayerInfo(roster.Slots[i], roster.Hunters[i], -1,
                        roster.Names[i]));
                }

                var metadata = new ReplayMetadata
                {
                    Type = ReplayType.FullMatch,
                    RoomKey = _rotation.Current.RoomKey,
                    Mode = _rotation.Current.Mode,
                    MapHash = ReplayMapIdentity.Compute(_rotation.Current.RoomKey),
                    Players = players,
                    Bootstrap = new ReplayBootstrap
                    {
                        Packets = new[] { sessionPacket, statePacket, rosterPacket, snapshotPacket }
                    }
                };
                ServerReplayRecorder.Start(metadata);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or InvalidDataException or KeyNotFoundException)
            {
                Log($"canonical replay unavailable: {ex.Message}");
            }
        }

        /// <summary>
        /// Tell the simulation what this server has just told everybody else.
        ///
        /// The roster and the match state are the two things a client follows
        /// to know who is playing and on what; the simulation follows exactly
        /// the same two, through exactly the same code, because it is the same
        /// engine. Applying the packets rather than reaching into the scene is
        /// what keeps it that way -- a second path here would be free to
        /// disagree with every client at once.
        /// </summary>
        private void SyncSimulationState(double now)
        {
            if (_sim == null)
            {
                return;
            }
            NetSession.ApplySessionState(BuildSessionState());
            NetSession.ApplyMatchState(BuildState(now), rotated: false);
            NetSession.ApplyRoster(BuildRoster());
        }

        public void Stop() => _running = false;

        private void Handle(ReceivedPacket packet, double now)
        {
            switch (packet.Type)
            {
                case PacketType.LobbyCommand: HandleLobbyCommand(packet, now); break;
                case PacketType.MatchLoaded: HandleMatchLoaded(packet, now); break;
                case PacketType.MatchLoadFailed: HandleMatchLoadFailed(packet); break;
                case PacketType.Hello:
                    HandleHello(packet, now);
                    break;
                case PacketType.Intent:
                    HandleIntent(packet, now);
                    break;
                case PacketType.Snapshot:
                    HandleSnapshot(packet, now);
                    break;
                case PacketType.Bye:
                    HandleBye(packet);
                    break;
                case PacketType.Identify:
                    HandleIdentify(packet, now);
                    break;
                case PacketType.CareerIdentity:
                    HandleCareerIdentity(packet, now);
                    break;
                case PacketType.Ping:
                    _transport?.Send(packet.Sender, PacketType.Pong, ReadOnlySpan<byte>.Empty);
                    break;
                case PacketType.Pong:
                    HandlePong(packet, now);
                    break;
                case PacketType.HostRequest:
                    HandleHostRequest(packet, now);
                    break;
                case PacketType.StatusQuery:
                    SendStatus(packet.Sender, now);
                    break;
                case PacketType.MatchEnd:
                    HandleMatchEnd(packet, now);
                    break;
                case PacketType.Chat:
                    HandleChat(packet, now);
                    break;
                case PacketType.Vote:
                    HandleVote(packet, now);
                    break;
                case PacketType.MapPick:
                    HandleMapPick(packet, now);
                    break;
                case PacketType.HitClaim:
                    HandleHitClaim(packet, now);
                    break;
            }
        }

        /// <summary>
        /// The hits one client says it landed.
        ///
        /// The slot comes from the endpoint the datagram arrived on and from
        /// nowhere else, exactly as it does for chat and for a vote: a claim
        /// that could be made on somebody else's behalf is not a claim. What
        /// happens to it after that is <see cref="NetHitClaims"/>'s, which
        /// runs inside the simulation -- this handler and the simulation step
        /// are the same thread, which is what makes it safe to apply damage
        /// from here.
        ///
        /// A server that is not running the match has no history to check a
        /// claim against and no simulation to apply it to, so it drops them.
        /// The client repeats a few times, gives up, and plays the game every
        /// build before protocol 7 played.
        /// </summary>
        private void HandleHitClaim(ReceivedPacket packet, double now)
        {
            if (_phase != SessionPhase.InMatch) return;
            Peer? peer = Find(packet.Sender);
            if (peer == null || peer.SlotIndex < 0 || !Simulating)
            {
                return;
            }
            peer.LastSeen = now;
            NetHitClaims.Receive(peer.SlotIndex, packet.Payload);
        }

        /// <summary>
        /// Answer one client's claims. Hung off
        /// <see cref="NetHitClaims.VerdictSink"/> when the simulation starts.
        /// </summary>
        private void SendVerdicts(int slot, ReadOnlySpan<(ushort Id, byte Result)> verdicts)
        {
            if (verdicts.Length == 0 || _transport == null)
            {
                return;
            }
            for (int i = 0; i < _peers.Count; i++)
            {
                if (_peers[i].SlotIndex != slot)
                {
                    continue;
                }
                HitVerdictPacket.Write(_scratch, verdicts, NetSession.CurrentMatchId, NetSession.AuthorityEpoch,
                    NetPlayerLifecycle.Generation(slot), NetPlayerLifecycle.Get(slot));
                _transport.Send(_peers[i].EndPoint, PacketType.HitVerdict,
                    _scratch.AsSpan(0, HitVerdictPacket.HeaderSize + verdicts.Length * HitVerdictPacket.EntrySize));
                return;
            }
        }

        /// <summary>
        /// A proposal or a ballot, from whoever the endpoint says sent it.
        /// </summary>
        private void HandleVote(ReceivedPacket packet, double now)
        {
            Peer? peer = Find(packet.Sender);
            if (peer == null || peer.SlotIndex < 0 || packet.Payload.Length < VotePacket.Size)
            {
                return;
            }
            peer.LastSeen = now;
            if (!AllowMapVotes)
            {
                return;
            }
            VotePacket vote = VotePacket.Read(packet.Payload);
            if (vote.Kind == VotePacket.KindPropose)
            {
                StartVote(peer, vote.RoomKey, now);
                return;
            }
            if (!_voteRunning || vote.Kind != VotePacket.KindYes && vote.Kind != VotePacket.KindNo)
            {
                return;
            }
            // First answer stands. Letting a ballot be changed turns the last
            // second of the vote into a race, and the player who wanted to
            // change their mind can say so out loud instead.
            if (peer.Ballot != 0)
            {
                return;
            }
            peer.Ballot = vote.Kind;
            BroadcastVoteState(now);
            Tally(now);
        }

        // ------------------------------------------- the intermission ballot

        /// <summary>
        /// Whether the results screen's ballot is open: a match has ended and
        /// the room may say where it goes next.
        /// </summary>
        private bool _ballotOpen;
        /// <summary>
        /// The post-match plurality currently prefers returning to the
        /// persistent lobby instead of immediately starting another match.
        /// This is a ballot outcome, not a second ready gate.
        /// </summary>
        private bool _returnToLobbyPending;

        /// <summary>
        /// The tally, rebuilt from the peers' picks whenever one changes.
        /// Only maps somebody has picked are in it -- every map is votable and
        /// the client scrolls its own room list, so what travels is the
        /// answer and not the question.
        /// </summary>
        private readonly List<string> _tallyRooms = new();
        private readonly List<int> _tallyVotes = new();

        /// <summary>
        /// Open the ballot. Called as the match ends, before the state goes
        /// out, so the first results screen anybody draws already has a list
        /// to scroll.
        /// </summary>
        private void OpenBallot()
        {
            _returnToLobbyPending = false;
            _ballotOpen = AllowMapVotes;
            _tallyRooms.Clear();
            _tallyVotes.Clear();
            for (int i = 0; i < _peers.Count; i++)
            {
                _peers[i].Pick = "";
            }
        }

        private void CloseBallot()
        {
            _returnToLobbyPending = false;
            _ballotOpen = false;
            _tallyRooms.Clear();
            _tallyVotes.Clear();
            for (int i = 0; i < _peers.Count; i++)
            {
                _peers[i].Pick = "";
            }
        }

        /// <summary>
        /// One player's pick: any map this server can load, rather than one of
        /// a short list it drew up.
        ///
        /// The ballot used to be four maps chosen here, which is a different
        /// thing from what people want out of a vote -- "the next map" is not
        /// a multiple-choice question, and the four on offer were never the
        /// one somebody had in mind. So the client scrolls its own room list
        /// and names what it wants; the only validation left is the one that
        /// matters, which is that the key loads something.
        /// </summary>
        private void HandleMapPick(ReceivedPacket packet, double now)
        {
            Peer? peer = Find(packet.Sender);
            if (peer == null || peer.SlotIndex < 0 || packet.Payload.Length < MapPickPacket.Size)
            {
                return;
            }
            peer.LastSeen = now;
            if (!_ballotOpen)
            {
                return;
            }
            string key = MapPickPacket.Read(packet.Payload).RoomKey;
            if (key.Length > 0)
            {
                bool returnToLobby = SessionPolicy == ServerSessionPolicy.Lobby
                    && PostMatchChoice.IsReturnToLobby(key);
                if (!returnToLobby)
                {
                    string? resolved = ResolveRoomKey(key);
                    if (resolved == null || String.Equals(resolved, CurrentDefinition.RoomKey,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        // No map, or the one they are standing in. Answered
                        // privately rather than announced, StartVote's rule.
                        Tell(peer, resolved == null ? $"no map called \"{key}\""
                            : "that is the map you are on");
                        return;
                    }
                    key = resolved;
                }
            }
            if (String.Equals(peer.Pick, key, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            string was = peer.Pick;
            peer.Pick = key;
            Recount();
            if (key.Length > 0 && VotesFor(key) == 1)
            {
                // Said out loud only when this player is the first behind that
                // map: somebody proposing one is news, and the eighth person
                // to agree with them is a number on a screen everybody is
                // already looking at.
                string who = peer.Name.Length > 0 ? peer.Name : $"Player{peer.SlotIndex + 1}";
                string choice = PostMatchChoice.IsReturnToLobby(key)
                    ? "to return to the lobby" : $"{key} next";
                Announce($"{who} wants {choice} -- pick it to agree; "
                    + "the choice with the most votes wins");
            }
            else if (key.Length == 0 && was.Length > 0)
            {
                Log($"slot {peer.SlotIndex} took back its pick of {was}");
            }
            ApplyLeader();
            // NextRoomKey is carried in match state. Publish it with the tally so
            // the NEXT line cannot lag behind the ballot by the periodic one-second tick.
            BroadcastMatchState(now);
            BroadcastMapChoices();
        }

        private int VotesFor(string roomKey)
        {
            for (int i = 0; i < _tallyRooms.Count; i++)
            {
                if (String.Equals(_tallyRooms[i], roomKey, StringComparison.OrdinalIgnoreCase))
                {
                    return _tallyVotes[i];
                }
            }
            return 0;
        }

        /// <summary>
        /// Rebuild the tally from the picks, most-wanted first. Sorted here so
        /// every client draws the same order and a row cannot jump under
        /// somebody's cursor differently on two machines.
        /// </summary>
        private void Recount()
        {
            _tallyRooms.Clear();
            _tallyVotes.Clear();
            for (int i = 0; i < _peers.Count; i++)
            {
                string key = _peers[i].Pick;
                if (key.Length == 0)
                {
                    continue;
                }
                int at = -1;
                for (int j = 0; j < _tallyRooms.Count; j++)
                {
                    if (String.Equals(_tallyRooms[j], key, StringComparison.OrdinalIgnoreCase))
                    {
                        at = j;
                        break;
                    }
                }
                if (at < 0)
                {
                    _tallyRooms.Add(key);
                    _tallyVotes.Add(1);
                }
                else
                {
                    _tallyVotes[at]++;
                }
            }
            for (int i = 1; i < _tallyRooms.Count; i++)
            {
                for (int j = i; j > 0 && _tallyVotes[j] > _tallyVotes[j - 1]; j--)
                {
                    (_tallyVotes[j], _tallyVotes[j - 1]) = (_tallyVotes[j - 1], _tallyVotes[j]);
                    (_tallyRooms[j], _tallyRooms[j - 1]) = (_tallyRooms[j - 1], _tallyRooms[j]);
                }
            }
        }

        /// <summary>
        /// The map with the most votes becomes the one the rotation plays --
        /// as the picks arrive rather than when the countdown ends.
        ///
        /// A plurality and no threshold. A mid-match vote needs one because it
        /// interrupts seven people who did not ask to be asked, so it had
        /// better be something most of them want; an intermission interrupts
        /// nothing, and a bar there only produces the outcome nobody voted for
        /// -- a room that picked three different maps and is sent to a fourth
        /// because none of the three reached seventy per cent.
        ///
        /// Applying it early is what makes the screen honest: NextRoomKey is
        /// read off the rotation, so the NEXT line and the ring on the list
        /// are the same fact rather than two guesses about it, and a player
        /// who changes the answer with three seconds left sees it change.
        /// Falling back the other way matters just as much: the last pick
        /// taken back, or the only voter leaving, puts the borrowed turn back
        /// and the rotation's own next map returns to the NEXT line. A tie
        /// goes to the map that got there first, which is where
        /// <see cref="Recount"/>'s stable order comes in.
        /// </summary>
        private void ApplyLeader()
        {
            _returnToLobbyPending = false;
            if (_tallyRooms.Count == 0 || _tallyVotes[0] <= 0)
            {
                _rotation.ClearPending();
                return;
            }
            string leader = _tallyRooms[0];
            if (SessionPolicy == ServerSessionPolicy.Lobby
                && PostMatchChoice.IsReturnToLobby(leader))
            {
                _returnToLobbyPending = true;
                _rotation.ClearPending();
                return;
            }
            _rotation.PlayNext(leader, ModeForRoom(leader));
        }

        /// <summary>The ballot and its tally, to everybody.</summary>
        private void BroadcastMapChoices()
        {
            if (_peers.Count == 0)
            {
                return;
            }
            int count = Math.Min(_tallyRooms.Count, MapChoicesPacket.MaxChoices);
            var keys = new string[count];
            var votes = new byte[count];
            for (int i = 0; i < count; i++)
            {
                keys[i] = _tallyRooms[i];
                votes[i] = (byte)Math.Clamp(_tallyVotes[i], 0, 255);
            }
            var packet = new MapChoicesPacket
            {
                Open = (byte)(_ballotOpen ? 1 : 0),
                Count = (byte)count,
                RoomKeys = keys,
                Votes = votes,
                Eligible = (byte)Math.Clamp(_peers.Count, 0, 255)
            };
            packet.Write(_scratch);
            for (int i = 0; i < _peers.Count; i++)
            {
                _transport?.Send(_peers[i].EndPoint, PacketType.MapChoices,
                    _scratch.AsSpan(0, MapChoicesPacket.Size));
            }
        }

        /// <summary>
        /// The same review the mid-match vote gets when somebody leaves, and
        /// for the same reason: the threshold is computed against who is
        /// connected, so a map can clear it or fall back under it because a
        /// player left rather than because anybody picked anything.
        /// </summary>
        private void ReviewPicks()
        {
            if (!_ballotOpen)
            {
                return;
            }
            Recount();
            ApplyLeader();
            BroadcastMatchState(_now);
            BroadcastMapChoices();
        }

        /// <summary>
        /// Put a map to the room, if this player is allowed to right now.
        ///
        /// Every refusal is answered privately rather than announced: a
        /// "your vote was refused" line broadcast to everybody is itself a
        /// way of spamming the room, which is what the cooldowns exist to
        /// stop.
        /// </summary>
        private void StartVote(Peer peer, string roomKey, double now)
        {
            if (_phase != SessionPhase.InMatch) return;
            if (_voteRunning)
            {
                Tell(peer, "a vote is already running");
                return;
            }
            if (_peers.Count < VoteMinimumPlayers)
            {
                Tell(peer, "not enough players to hold a vote");
                return;
            }
            double sinceVote = now - _voteResolvedAt;
            if (sinceVote < VoteCooldownSeconds)
            {
                Tell(peer, $"another vote may be called in {VoteCooldownSeconds - sinceVote:0} s");
                return;
            }
            double sinceMine = now - peer.LastProposal;
            if (sinceMine < ProposalCooldownSeconds)
            {
                Tell(peer, $"you may propose again in {ProposalCooldownSeconds - sinceMine:0} s");
                return;
            }
            // The room key has to name a map this server can actually load.
            // Nothing else validates it: the rotation is not the limit --
            // voting for a map the admin did not list is the point -- but a
            // key that loads nothing would rotate the whole match into a
            // room that does not exist.
            string? resolved = ResolveRoomKey(roomKey);
            if (resolved == null)
            {
                Tell(peer, $"no map called \"{roomKey}\"");
                return;
            }
            if (String.Equals(resolved, CurrentDefinition.RoomKey, StringComparison.OrdinalIgnoreCase))
            {
                Tell(peer, "that is the map you are on");
                return;
            }
            _voteRunning = true;
            _voteRoom = resolved;
            // The mode the rotation would play this map in if it lists it,
            // and this match's own mode otherwise. A vote is about the map;
            // silently changing the mode as well is not what was asked.
            _voteMode = ModeForRoom(resolved);
            _voteProposer = peer.Name.Length > 0 ? peer.Name : $"Player{peer.SlotIndex + 1}";
            _voteProposerSlot = peer.SlotIndex;
            _voteStartedAt = now;
            _voteResult = VoteStatePacket.StateIdle;
            peer.LastProposal = now;
            for (int i = 0; i < _peers.Count; i++)
            {
                _peers[i].Ballot = 0;
            }
            // The caller's own yes. Somebody who proposes a map has said what
            // they think of it, and making them press the key as well is a
            // vote that can fail 0-0.
            peer.Ballot = VotePacket.KindYes;
            Announce($"{_voteProposer} proposes {resolved} -- F1 to accept, F2 to deny");
            Log($"vote started by slot {peer.SlotIndex} for {resolved} ({_voteMode})");
            BroadcastVoteState(now);
            Tally(now);
        }

        /// <summary>
        /// Count what has been cast, and act if the answer is already
        /// settled. Called on every ballot and once a second, so a vote that
        /// everybody has answered does not sit out the rest of its thirty
        /// seconds.
        /// </summary>
        private void Tally(double now)
        {
            if (!_voteRunning)
            {
                return;
            }
            (int yes, int no, int eligible, int needed) = CountVotes();
            if (yes >= needed)
            {
                ResolveVote(now, passed: true, $"{yes} of {eligible}");
                return;
            }
            // Cannot be reached any more: the noes plus the people who have
            // not answered are fewer than what is still missing.
            if (eligible - no < needed)
            {
                ResolveVote(now, passed: false, $"{yes} of {eligible}");
                return;
            }
            if (now - _voteStartedAt >= VoteSeconds)
            {
                ResolveVote(now, passed: false, $"{yes} of {eligible}");
            }
        }

        /// <summary>
        /// Forget a mid-match vote at a match/session boundary. This is a hard
        /// reset rather than a failed result: the next lobby/match must not
        /// inherit the old room, ballots, or cooldown.
        /// </summary>
        private void CancelMapVote(double now)
        {
            bool publish = _voteRunning || _voteResult != VoteStatePacket.StateIdle;
            _voteRunning = false;
            _voteRoom = "";
            _voteProposer = "";
            _voteProposerSlot = -1;
            _voteStartedAt = 0;
            _voteResolvedAt = Double.NegativeInfinity;
            _voteResult = VoteStatePacket.StateIdle;
            for (int i = 0; i < _peers.Count; i++)
            {
                _peers[i].Ballot = 0;
            }
            if (publish)
            {
                BroadcastVoteState(now);
            }
        }

        private (int Yes, int No, int Eligible, int Needed) CountVotes()
        {
            int yes = 0;
            int no = 0;
            for (int i = 0; i < _peers.Count; i++)
            {
                if (_peers[i].Ballot == VotePacket.KindYes)
                {
                    yes++;
                }
                else if (_peers[i].Ballot == VotePacket.KindNo)
                {
                    no++;
                }
            }
            int eligible = _peers.Count;
            // Ceiling, so 70% of three players is three and not two: the
            // threshold is a floor to clear, and rounding it down would let a
            // vote pass on less than what was asked for.
            int needed = Math.Max(1, (int)Math.Ceiling(eligible * VoteThreshold));
            return (yes, no, eligible, needed);
        }

        private void ResolveVote(double now, bool passed, string count)
        {
            string room = _voteRoom;
            GameMode mode = _voteMode;
            _voteRunning = false;
            _voteResolvedAt = now;
            _voteResult = passed ? VoteStatePacket.StatePassed : VoteStatePacket.StateFailed;
            for (int i = 0; i < _peers.Count; i++)
            {
                _peers[i].Ballot = 0;
            }
            if (passed)
            {
                Announce($"vote passed ({count}) -- changing to {room}");
                Log($"vote passed ({count}) for {room}");
                _rotation.PlayNext(room, mode);
                if (SessionPolicy == ServerSessionPolicy.Lobby) EndMatch(now, "map vote passed");
                else AdvanceMap(now);
            }
            else
            {
                Announce($"vote failed ({count}) -- staying on {CurrentDefinition.RoomKey}");
                Log($"vote failed ({count}) for {room}");
            }
            BroadcastVoteState(now);
        }

        /// <summary>
        /// Drop a vote that no longer has a room to be held in. Called when a
        /// peer leaves: the thresholds are computed against who is connected,
        /// so a vote can pass or become unreachable because somebody
        /// disconnected rather than because anybody voted.
        /// </summary>
        private void ReviewVote(double now)
        {
            if (!_voteRunning)
            {
                return;
            }
            if (_peers.Count < VoteMinimumPlayers)
            {
                ResolveVote(now, passed: false, "not enough players");
                return;
            }
            Tally(now);
        }

        /// <summary>
        /// Turn what a player clicked into a room key this server can load,
        /// or null. Case-insensitive, because the key is a display name with
        /// spaces in it and nobody should have to match its capitals.
        /// </summary>
        private static string? ResolveRoomKey(string roomKey)
        {
            if (String.IsNullOrWhiteSpace(roomKey))
            {
                return null;
            }
            string wanted = roomKey.Trim();
            // The compiled-in room table, which custom maps in the server's
            // own maps folder are already part of (see CustomRooms.AppendRooms).
            // This lookup itself performs no file I/O; authoritative servers
            // still require the operator's game files to build/simulate rooms.
            foreach (KeyValuePair<string, RoomMetadata> entry in Metadata.RoomMetadata)
            {
                if (entry.Value.Multiplayer
                    && String.Equals(entry.Key, wanted, StringComparison.OrdinalIgnoreCase))
                {
                    return entry.Key;
                }
            }
            return null;
        }

        private GameMode ModeForRoom(string roomKey)
        {
            foreach (RotationEntry entry in _rotation.Entries)
            {
                if (String.Equals(entry.RoomKey, roomKey, StringComparison.OrdinalIgnoreCase))
                {
                    return entry.Mode;
                }
            }
            return CurrentDefinition.Mode;
        }

        /// <summary>The vote as it stands, to everybody.</summary>
        private void BroadcastVoteState(double now)
        {
            if (_peers.Count == 0)
            {
                return;
            }
            var state = new VoteStatePacket
            {
                State = _voteRunning ? VoteStatePacket.StateRunning : _voteResult,
                RoomKey = _voteRunning ? _voteRoom : "",
                Proposer = _voteRunning ? _voteProposer : ""
            };
            if (_voteRunning)
            {
                (int yes, int no, int eligible, int needed) = CountVotes();
                state.Yes = (byte)yes;
                state.No = (byte)no;
                state.Eligible = (byte)eligible;
                state.Needed = (byte)needed;
                state.Seconds = (ushort)Math.Max(0, VoteSeconds - (now - _voteStartedAt));
            }
            else if (AllowMapVotes)
            {
                double wait = VoteCooldownSeconds - (now - _voteResolvedAt);
                state.Seconds = (ushort)Math.Clamp(wait, 0, UInt16.MaxValue);
            }
            else
            {
                // No cooldown that will ever expire, which is how a client
                // tells "wait a minute" from "not on this server".
                state.Seconds = UInt16.MaxValue;
            }
            state.Write(_scratch);
            for (int i = 0; i < _peers.Count; i++)
            {
                _transport?.Send(_peers[i].EndPoint, PacketType.VoteState,
                    _scratch.AsSpan(0, VoteStatePacket.Size));
            }
        }

        /// <summary>
        /// A system line to one player. <see cref="Announce"/> for everybody.
        /// </summary>
        private void Tell(Peer peer, string text)
        {
            var chat = new ChatPacket
            {
                Slot = 0xFF,
                Kind = ChatPacket.KindSystem,
                Name = String.Empty,
                Text = text
            };
            chat.Write(_scratch);
            _transport?.Send(peer.EndPoint, PacketType.Chat,
                _scratch.AsSpan(0, ChatPacket.Size));
        }

        /// <summary>
        /// Sustained chat rate, in messages a second, and how many may be
        /// sent back to back before that rate starts to bite.
        ///
        /// A relay with no limit here is a relay that will multiply one
        /// client's flood by the number of people in the match and send it to
        /// all of them -- the one packet type in this protocol whose contents
        /// a player chooses freely and whose cost is paid by everybody else.
        /// Three in a row then one every two seconds is faster than anybody
        /// types and slower than anything worth calling a flood.
        /// </summary>
        private const double ChatRatePerSecond = 0.5;
        private const double ChatBurst = 3;

        /// <summary>
        /// Pass one player's line on to everybody else.
        ///
        /// The slot and the name are overwritten with what this server knows
        /// about the sender rather than taken from the packet. A client can
        /// put any name it likes in those fields, and a line that appears to
        /// come from somebody else is the entire attack: the endpoint a
        /// datagram arrived from is the only thing here that cannot be typed
        /// into a text box.
        ///
        /// Not sent back to the sender, which echoes its own line locally --
        /// see <c>ChatBox.Submit</c> for why that is the better half of the
        /// round trip to skip.
        /// </summary>
        private void HandleChat(ReceivedPacket packet, double now)
        {
            Peer? peer = Find(packet.Sender);
            if (peer == null || peer.SlotIndex < 0 || packet.Payload.Length < ChatPacket.Size)
            {
                return;
            }
            peer.LastSeen = now;
            ChatPacket chat = ChatPacket.Read(packet.Payload);
            if (chat.Text.Length == 0)
            {
                return;
            }
            // A leaky bucket rather than a minimum interval: an interval
            // punishes two quick lines of the same thought exactly as hard as
            // a hundred, and the thing worth stopping is the hundred.
            peer.ChatCredit = Math.Min(ChatBurst,
                peer.ChatCredit + (now - peer.ChatCreditAt) * ChatRatePerSecond);
            peer.ChatCreditAt = now;
            if (peer.ChatCredit < 1)
            {
                if (peer.ChatDropped++ == 0)
                {
                    Log($"chat from slot {peer.SlotIndex} ({peer.EndPoint}) dropped: too fast");
                }
                return;
            }
            peer.ChatCredit -= 1;
            peer.ChatDropped = 0;
            chat.Slot = (byte)peer.SlotIndex;
            chat.Name = peer.Name.Length > 0 ? peer.Name : $"Player{peer.SlotIndex}";
            bool teamOnly = chat.Kind == ChatPacket.KindTeam && GameState.IsTeamMode(CurrentDefinition.Mode) && peer.TeamIndex >= 0;
            chat.Kind = teamOnly ? ChatPacket.KindTeam : ChatPacket.KindSay;
            chat.Write(_scratch);
            for (int i = 0; i < _peers.Count; i++)
            {
                if (_peers[i] != peer && (!teamOnly || _peers[i].TeamIndex == peer.TeamIndex))
                {
                    _transport?.Send(_peers[i].EndPoint, PacketType.Chat,
                        _scratch.AsSpan(0, ChatPacket.Size));
                }
            }
            Log($"chat {chat.Name}: {chat.Text}");
        }

        /// <summary>
        /// The server itself saying something to everybody.
        ///
        /// No slot and no name -- <see cref="ChatPacket.KindSystem"/> is drawn
        /// in its own colour with nobody's name in front of it, because a
        /// notice attributed to a player reads as that player having typed it.
        /// Not rate limited: nothing a client sends can cause one.
        /// </summary>
        private void Announce(string text)
        {
            if (_peers.Count == 0)
            {
                return;
            }
            var chat = new ChatPacket
            {
                Slot = 0xFF,
                Kind = ChatPacket.KindSystem,
                Name = String.Empty,
                Text = text
            };
            chat.Write(_scratch);
            for (int i = 0; i < _peers.Count; i++)
            {
                _transport?.Send(_peers[i].EndPoint, PacketType.Chat,
                    _scratch.AsSpan(0, ChatPacket.Size));
            }
        }

        /// <summary>
        /// Answer "what is running here?" without touching the roster.
        ///
        /// The asker is a launcher deciding whether to show this server as
        /// worth joining, not a player: it gets no slot, no peer entry and no
        /// effect on the match clock, so it can be asked every few seconds
        /// while somebody reads the screen.
        /// </summary>
        private void SendStatus(IPEndPoint sender, double now)
        {
            var status = new ServerStatusPacket
            {
                Match = BuildState(now), Phase = _phase, Format = CurrentDefinition.Format,
                LobbyEnabled = SessionPolicy == ServerSessionPolicy.Lobby, AllowJoinInProgress = AllowJoinInProgress,
                MaxPlayers = (byte)_maxPlayers,
                Protocol = NetConfig.ProtocolVersion,
                ServerName = ServerName,
                // What this box can do besides the match it is running. The
                // launcher's create-server screen asks every server on the
                // directory's list exactly this, on the port it already pings
                // them on -- which is why hosting elsewhere needed no second
                // directory and no second port.
                Flags = (byte)(Hosts.CanHost ? ServerStatusPacket.FlagCanHost : 0)
            };
            status.Write(_scratch);
            _transport?.Send(sender, PacketType.StatusReply,
                _scratch.AsSpan(0, ServerStatusPacket.SizeWithFlags));
        }

        /// <summary>
        /// "Open a game for me, on a port of your own."
        ///
        /// The same request the directory answers, answered by an ordinary
        /// server on its own port. That is the whole of what lets a player
        /// host in Tokyo: only a process running in Tokyo can start a server
        /// there, and this is that process -- it is already listed, already
        /// pinged and already reachable, so nothing new has to be deployed,
        /// opened or found.
        /// </summary>
        private void HandleHostRequest(ReceivedPacket packet, double now)
        {
            var reply = new HostReplyPacket();
            if (packet.Payload.Length < HostRequestPacket.Size)
            {
                reply.Reason = "malformed request";
            }
            else
            {
                HostRequestPacket request = HostRequestPacket.Read(packet.Payload);
                if (request.Protocol != NetConfig.ProtocolVersion)
                {
                    reply.Reason = $"this server speaks protocol {NetConfig.ProtocolVersion}, "
                        + $"your build speaks {request.Protocol}";
                }
                else if (!Hosts.CanHost)
                {
                    reply.Reason = "this server does not open new games";
                }
                else
                {
                    reply = Hosts.Start(request, packet.Sender, now);
                }
            }
            reply.Write(_scratch);
            _transport?.Send(packet.Sender, PacketType.HostReply,
                _scratch.AsSpan(0, HostReplyPacket.Size));
            if (!reply.Started)
            {
                Log($"refused a game for {packet.Sender}: {reply.Reason}");
            }
        }

        private void HandleHello(ReceivedPacket packet, double now)
        {
            if (packet.Payload.Length < 1 || packet.Payload[0] != NetConfig.ProtocolVersion)
            {
                Log($"rejected {packet.Sender}: protocol mismatch");
                SendRefusal(packet.Sender, RefusedPacket.ReasonProtocol);
                return;
            }
            uint clientId = packet.Payload.Length >= 6
                ? BinaryPrimitives.ReadUInt32LittleEndian(packet.Payload.Slice(2, 4))
                : 0;
            Peer? peer = Find(packet.Sender);
            if (peer != null && peer.ClientId != clientId)
            {
                Remove(peer, "replaced connection");
                peer = null;
            }
            if (peer == null && clientId != 0)
            {
                // The same player, from an address this server has not seen.
                //
                // A connection that drops and comes back comes back through a
                // new NAT binding, so the endpoint -- the only thing that used
                // to identify a peer -- changes. Every reconnection therefore
                // read as a new player: a second slot with a second hunter,
                // while the slot the player actually held stood frozen in the
                // room until it timed out half a minute later. Matching on
                // who rather than on where is the whole fix; the peer keeps
                // its slot, its name, its hunter and its score, and simply
                // starts being spoken to at the new address.
                for (int i = 0; i < _peers.Count; i++)
                {
                    if (_peers[i].ClientId == clientId)
                    {
                        peer = _peers[i];
                        Log($"slot {peer.SlotIndex} ({peer.Name}) came back on "
                            + $"{packet.Sender}, was {peer.EndPoint}");
                        peer.EndPoint = packet.Sender;
                        // The authority is held as a reference to this same
                        // object, so nothing else has to be told.
                        break;
                    }
                }
            }
            if (peer == null)
            {
                // Honour the slot the client asks for when it is free. A
                // client that says hello again is usually one this server
                // dropped while it was loading a room, and handing it a
                // different slot swaps two players' identities mid-match.
                int slot = -1;
                if (packet.Payload.Length >= 2 && packet.Payload[1] != 0xFF
                    && packet.Payload[1] < _maxPlayers && SlotFree(packet.Payload[1]))
                {
                    slot = packet.Payload[1];
                }
                if (slot < 0)
                {
                    slot = NextFreeSlot();
                }
                if (slot < 0)
                {
                    Log($"rejected {packet.Sender}: session full");
                    SendRefusal(packet.Sender, RefusedPacket.ReasonFull);
                    return;
                }
                if (_peers.Count == 0 && SessionPolicy == ServerSessionPolicy.Continuous)
                {
                    AbandonCareerMatch();
                    // Restart the match clock for the first arrival. The clock
                    // runs whether or not anybody is connected, so a server
                    // left alone overnight greets its next player with a round
                    // that has no time left on it -- which the client adopts,
                    // ending the match before it has drawn a frame.
                    _matchStarted = now;
                    // And it is not mid-results either: an intermission that
                    // was running when the last player left has nobody to show
                    // it to.
                    _matchEndedAt = -1;
                    _phase = SessionPhase.InMatch;
                    CloseBallot();
                    _matchId = NetLifecycleTracker.Next(_matchId);
                    _snapshotSeen = false;
                    _lastSnapshot = null;
                    Array.Clear(_slotLives);
                }
                if (_phase == SessionPhase.InMatch && !AllowJoinInProgress)
                { SendRefusal(packet.Sender, RefusedPacket.ReasonInMatch); return; }
                sbyte team = ChooseTeam(CurrentDefinition);
                if (LobbyRules.TeamCount(CurrentDefinition) > 0 && team < 0)
                { SendRefusal(packet.Sender, RefusedPacket.ReasonFull); return; }
                _slotGenerations[slot] = NetLifecycleTracker.Next(_slotGenerations[slot]);
                _slotLives[slot] = 0;
                peer = new Peer { EndPoint = packet.Sender, SlotIndex = slot, ClientId = clientId, TeamIndex = team };
                _peers.Add(peer);
                CareerPeerJoined(peer);
                EverOccupied = true;
                // Slot 0 is the authority's slot, matching what a listen host
                // would occupy, so clients need no special case for either.
                //
                // Unless this server is simulating, in which case no client is
                // ever made the authority and slot 0 is an ordinary slot. A
                // client is told it is the authority by being sent
                // PacketType.Authority and in no other way, so simply not
                // sending it is the whole of the change on the wire: an older
                // client joining a simulating server behaves correctly without
                // knowing anything has moved.
                if (_authority == null && !RunsTheMatch)
                {
                    _authority = peer;
                    _authorityEpoch++;
                    _snapshotSeen = false;
                    Log($"{packet.Sender} joined as slot {slot} (authority)");
                    NotifyAuthority(peer);
                }
                else
                {
                    Log($"{packet.Sender} joined as slot {slot}");
                    if (Simulating && _lastSnapshot != null)
                    {
                        // A world to stand in before the next one is composed.
                        // Without it a joiner sees an empty room for a frame,
                        // which is the same gap NotifyAuthority closes for the
                        // client it promotes.
                        _transport?.Send(peer.EndPoint, PacketType.Snapshot, _lastSnapshot);
                    }
                }
            }
            peer.ClientId = clientId;
            ClaimOwner(peer, packet.Payload);
            peer.LastSeen = now;
            // Re-answered on every Hello; the first Welcome may have been lost.
            _scratch[0] = (byte)peer.SlotIndex;
            BinaryPrimitives.WriteUInt32LittleEndian(_scratch.AsSpan(1), clientId);
            BinaryPrimitives.WriteUInt16LittleEndian(_scratch.AsSpan(5), _matchId);
            BinaryPrimitives.WriteUInt64LittleEndian(_scratch.AsSpan(7), _authorityEpoch);
            BinaryPrimitives.WriteUInt16LittleEndian(_scratch.AsSpan(15), _slotGenerations[peer.SlotIndex]);
            _transport?.Send(peer.EndPoint, PacketType.Welcome, _scratch.AsSpan(0, 17));
            // Immediately follow with the running match, so a client that
            // arrives mid-round loads the right map and adopts the server's
            // clock rather than starting a fresh one of its own.
            MatchStatePacket state = BuildState(now);
            state.Write(_scratch);
            _transport?.Send(peer.EndPoint, PacketType.MatchState,
                _scratch.AsSpan(0, MatchStatePacket.Size));
            TouchLobbyRevision($"peer slot {peer.SlotIndex} connected");
        }

        /// <summary>
        /// The authority saying the match it is simulating is over.
        ///
        /// Only the authority, and only once: everyone else's copy of the
        /// scoreboard comes from the authority's snapshot, so a client
        /// reporting the end is reporting the authority's own conclusion back
        /// to it -- and a peer that could end matches on its own would be a
        /// peer that could rotate the server whenever it liked.
        ///
        /// Repeated by the sender until the state comes back with the flag
        /// set, so losing this datagram costs a second rather than the
        /// rotation.
        /// </summary>
        private void HandleMatchEnd(ReceivedPacket packet, double now)
        {
            Peer? peer = Find(packet.Sender);
            if (peer == null)
            {
                return;
            }
            peer.LastSeen = now;
            // Refused from every client while this server simulates, the same
            // way a client's Snapshot is: the machine that decides a match is
            // won is the machine that keeps the score, and that is this one.
            // _authority is null then, so the test below already refuses
            // everybody -- said out loud because "any peer can end the match"
            // is not a thing to leave resting on a null check.
            if (Simulating || peer != _authority)
            {
                return;
            }
            if (packet.Payload.Length != 10
                || BinaryPrimitives.ReadUInt16LittleEndian(packet.Payload) != _matchId
                || BinaryPrimitives.ReadUInt64LittleEndian(packet.Payload[2..]) != _authorityEpoch) return;
            EndMatch(now, "a player reached the goal");
        }

        /// <summary>
        /// Say no out loud. See <see cref="RefusedPacket"/> for why this is
        /// worth a packet: the alternative, and what this used to be, is a
        /// silence the client cannot tell from a server that is switched off.
        ///
        /// One datagram per refused Hello, and a refused client retries about
        /// twice a second for eight seconds, so this is not a reflection risk
        /// worth rate-limiting: the reply is three bytes to an address that
        /// just sent a Hello of its own.
        /// </summary>
        private void SendRefusal(IPEndPoint to, byte reason)
        {
            var refusal = new RefusedPacket
            {
                Reason = reason,
                Players = (byte)_peers.Count,
                MaxPlayers = (byte)_maxPlayers
            };
            refusal.Write(_scratch);
            _transport?.Send(to, PacketType.Refused, _scratch.AsSpan(0, RefusedPacket.Size));
        }

        private void HandleIdentify(ReceivedPacket packet, double now)
        {
            Peer? peer = Find(packet.Sender);
            if (peer == null)
            {
                return;
            }
            peer.LastSeen = now;
            if (packet.Payload.Length < 1)
            {
                return;
            }
            if (packet.Payload.Length < 2)
            {
                return;
            }
            if (_phase == SessionPhase.Starting && (_expectedLoadedSlots & (1 << peer.SlotIndex)) != 0) return;
            byte hunter = packet.Payload[0];
            byte color = packet.Payload[1];
            if (hunter >= Launcher.Hunters.Playable || color > 3) return;
            string name = System.Text.Encoding.ASCII
                .GetString(packet.Payload[2..])
                .TrimEnd('\0')
                .Trim();
            if (name.Length == 0)
            {
                return;
            }
            if (name.Length > RosterPacket.MaxNameBytes)
            {
                name = name[..RosterPacket.MaxNameBytes];
            }
            if (peer.Name == name && peer.Hunter == hunter && peer.Color == color)
            {
                return;
            }
            bool firstName = peer.Name.Length == 0;
            int previousHunter = peer.Hunter;
            if (peer.Hunter != hunter || peer.Color != color) peer.LobbyReady = false;
            peer.Name = name;
            peer.Hunter = hunter;
            peer.Color = color;
            CareerIdentityChanged(peer, previousHunter);
            Log($"slot {peer.SlotIndex} is \"{name}\" playing {(Hunter)hunter} "
                + $"in suit {color + 1}");
            if (firstName)
            {
                // The first line anybody sees in a match, and the only one
                // that is worth sending. It is also the answer to "is chat
                // working at all here": a player who joins and sees nothing
                // when the next person arrives is on a server that predates
                // chat, which otherwise looks exactly like nobody talking.
                Announce($"{name} joined");
            }
            TouchLobbyRevision($"slot {peer.SlotIndex} identity changed");
        }

        /// <summary>
        /// Send every client the full slot->name mapping. This is what lets
        /// each player see who else is actually in the match, which is the
        /// check that distinguishes "connected" from "in the same game".
        /// </summary>
        /// <summary>
        /// Tell a peer it owns the simulation. Without this a client on a
        /// dedicated server is only ever NetRole.Client, so nothing ever
        /// broadcasts snapshots and no player sees another move.
        /// </summary>
        private void NotifyAuthority(Peer peer)
        {
            MatchStatePacket match = BuildState(_now);
            match.Write(_scratch);
            _transport?.Send(peer.EndPoint, PacketType.MatchState, _scratch.AsSpan(0, MatchStatePacket.Size));
            RosterPacket roster = BuildRoster();
            roster.Write(_scratch);
            _transport?.Send(peer.EndPoint, PacketType.Roster, _scratch.AsSpan(0, RosterPacket.Size));
            if (_lastSnapshot != null)
            {
                SnapshotHeader header = SnapshotHeader.Read(_lastSnapshot);
                if (header.MatchId == _matchId)
                {
                    // Seed the successor from the last world, in its new stream.
                    // Frame zero cannot block its own local simulation clock.
                    byte[] seed = (byte[])_lastSnapshot.Clone();
                    header.AuthorityEpoch = _authorityEpoch;
                    header.Frame = 0;
                    header.Write(seed);
                    _transport?.Send(peer.EndPoint, PacketType.Snapshot, seed);
                }
            }
            _scratch[0] = (byte)peer.SlotIndex;
            BinaryPrimitives.WriteUInt16LittleEndian(_scratch.AsSpan(1), _matchId);
            BinaryPrimitives.WriteUInt64LittleEndian(_scratch.AsSpan(3), _authorityEpoch);
            BinaryPrimitives.WriteUInt16LittleEndian(_scratch.AsSpan(11), _slotGenerations[peer.SlotIndex]);
            _transport?.Send(peer.EndPoint, PacketType.Authority, _scratch.AsSpan(0, 13));
        }

        private void PingPeers(double now)
        {
            for (int i = 0; i < _peers.Count; i++)
            {
                Peer peer = _peers[i];
                if (peer.PingPending && now - peer.PingSentAt < 5)
                {
                    // Still waiting on the last one. Leave the previous value
                    // standing rather than reporting a peer as unreachable for
                    // one dropped datagram.
                    continue;
                }
                peer.PingId++;
                peer.PingSentAt = now;
                peer.PingPending = true;
                _scratch[0] = peer.PingId;
                _transport?.Send(peer.EndPoint, PacketType.Ping, _scratch.AsSpan(0, 1));
            }
        }

        private void HandlePong(ReceivedPacket packet, double now)
        {
            Peer? peer = Find(packet.Sender);
            if (peer == null || !peer.PingPending)
            {
                return;
            }
            // The id makes a late reply to an earlier ping unusable rather than
            // wrong: without it a Pong that took three seconds to arrive would
            // be measured against the ping sent one second ago.
            if (packet.Payload.Length < 1 || packet.Payload[0] != peer.PingId)
            {
                return;
            }
            peer.PingPending = false;
            peer.LastSeen = now;
            int rtt = (int)Math.Round((now - peer.PingSentAt) * 1000);
            rtt = Math.Clamp(rtt, 0, 9999);
            // Smoothed, because one late datagram is not a worse connection.
            peer.Ping = peer.Ping == 0 ? rtt : (peer.Ping * 2 + rtt) / 3;
        }

        private RosterPacket BuildRoster()
        {
            RosterPacket roster = RosterPacket.Create();
            roster.MatchId = _matchId;
            roster.AuthorityEpoch = _authorityEpoch;
            roster.Revision = ++_rosterRevision;
            roster.SessionRevision = _sessionRevision;
            for (int i = 0; i < _peers.Count && i < RosterPacket.MaxSlots; i++)
            {
                roster.Slots[roster.Count] = (byte)_peers[i].SlotIndex;
                roster.Generations[roster.Count] = _slotGenerations[_peers[i].SlotIndex];
                roster.Teams[roster.Count] = _peers[i].TeamIndex;
                roster.LobbyReady[roster.Count] = _peers[i].LobbyReady;
                roster.Hunters[roster.Count] = _peers[i].Hunter;
                roster.Colors[roster.Count] = _peers[i].Color;
                roster.Pings[roster.Count] = (ushort)Math.Clamp(_peers[i].Ping, 0, 9999);
                roster.Names[roster.Count] = _peers[i].Name.Length > 0
                    ? _peers[i].Name
                    : $"Player{_peers[i].SlotIndex + 1}";
                roster.Count++;
            }
            return roster;
        }

        private void BroadcastRoster()
        {
            RosterPacket roster = BuildRoster();
            // Before the early return below. A roster is also how the
            // simulation learns that the last player has left, and an empty
            // one has to reach it or it goes on simulating somebody who is no
            // longer there.
            if (_sim != null)
            {
                NetSession.ApplyMatchState(BuildState(_now), rotated: false);
                NetSession.ApplyRoster(roster);
            }
            roster.Write(_scratch);
            ServerReplayRecorder.Record(PacketType.Roster,
                _scratch.AsSpan(0, RosterPacket.Size));
            if (_peers.Count == 0)
            {
                return;
            }
            for (int i = 0; i < _peers.Count; i++)
            {
                _transport?.Send(_peers[i].EndPoint, PacketType.Roster,
                    _scratch.AsSpan(0, RosterPacket.Size));
            }
        }

        /// <summary>
        /// How far behind a peer's newest intent frame a packet may be and
        /// still be a reordered straggler rather than a restarted counter.
        /// </summary>

        private void HandleIntent(ReceivedPacket packet, double now)
        {
            if (_phase is SessionPhase.Lobby or SessionPhase.Starting) return;
            Peer? peer = Find(packet.Sender);
            // No authority and not simulating means nobody would act on this.
            // When this server is the authority there is no client to wait
            // for, which is the whole point.
            if (peer == null || (_authority == null && !Simulating))
            {
                return;
            }
            if (packet.Payload.Length < IntentPacket.Size || packet.Payload.Length > IntentPacket.FullSize) return;
            peer.LastSeen = now;
            if (packet.Payload.Length >= IntentPacket.Size)
            {
                IntentPacket intent = IntentPacket.Read(packet.Payload);
                ushort life = _sim != null ? NetPlayerLifecycle.Get(peer.SlotIndex) : _slotLives[peer.SlotIndex];
                if (intent.MatchId != _matchId || intent.AuthorityEpoch != _authorityEpoch
                    || intent.SlotGeneration != _slotGenerations[peer.SlotIndex] || intent.LifeId != life) return;
                if (_sim != null)
                {
                    // Straight into the simulation, one hop earlier than a
                    // client authority got it -- and through the same call a
                    // client makes when a SlotIntent arrives, so the ordering
                    // rule that guards a rejoining player's restarted frame
                    // counter is the one that has already been debugged.
                    //
                    // Before the ordering check below rather than after: that
                    // one guards the *relay*, and its state is the peer's
                    // LastIntentFrame, which is updated whether or not this
                    // packet is relayed onward.
                    NetSession.AcceptSlotIntent(peer.SlotIndex, intent);
                }
                // UDP reorders; an older frame must not replace a newer one.
                //
                // Unless it is far enough behind to be a different session
                // rather than a straggler: a client's counter restarts at
                // zero when it joins, and a Peer that survived a reconnect --
                // one that came back to the same endpoint before this server
                // noticed it had gone -- would otherwise have every packet of
                // its new session refused until the counter climbed back past
                // the old one. Same ten seconds NetSession allows.
                if (peer.LastIntentFrame != 0 && !NetLifecycleTracker.Newer(intent.Frame, peer.LastIntentFrame))
                {
                    return;
                }
                peer.LastIntentFrame = intent.Frame;
                ServerReplayRecorder.RecordSlotIntent(peer.SlotIndex, packet.Payload);
                // Only meaningful between the end of one match and the start
                // of the next; read unconditionally because it costs nothing
                // and a client that sets it early is simply ready early.
                peer.PostMatchReady = intent.Buttons.HasFlag(IntentButtons.ReadyState);
            }
            // Tag with the sender's slot. A receiver is a client with no peer
            // list, so it cannot work out who an endpoint belongs to; without
            // this the authority dropped every relayed intent and simulated
            // nobody.
            _scratch[0] = (byte)peer.SlotIndex;
            packet.Payload.CopyTo(_scratch.AsSpan(1));
            for (int i = 0; i < _peers.Count; i++)
            {
                // To everyone, not just the authority. Input is what makes a
                // player do anything visible -- fire, morph, lay a bomb, swing
                // an alt attack -- and a client that only ever received
                // positions drew opponents that slid around the level in
                // silence: no beams, no morph animation, no bombs. Position
                // still comes from the authority's snapshot; this is what
                // fills in everything a position cannot express.
                if (_peers[i] != peer)
                {
                    _transport?.Send(_peers[i].EndPoint, PacketType.SlotIntent,
                        _scratch.AsSpan(0, packet.Payload.Length + 1));
                }
            }
        }

        private void HandleSnapshot(ReceivedPacket packet, double now)
        {
            if (_phase is SessionPhase.Lobby or SessionPhase.Starting) return;
            Peer? peer = Find(packet.Sender);
            if (peer == null)
            {
                return;
            }
            peer.LastSeen = now;
            // Only the authority's view of the world is forwarded; anything
            // else would let a client overwrite everyone's state. When this
            // server is the authority that is every client without exception,
            // and _authority is null, so the test below already refuses them
            // -- said explicitly because it is the security property the whole
            // refactor rests on and it should not read as an accident.
            if (Simulating || peer != _authority)
            {
                return;
            }
            if (packet.Payload.Length < SnapshotHeader.Size) return;
            SnapshotHeader header = SnapshotHeader.Read(packet.Payload);
            int timeOffset = SnapshotHeader.Size + header.PlayerCount * PlayerState.Size;
            int healthOffset = timeOffset + NetMatchTimeSync.Size;
            if (header.MatchId != _matchId || header.AuthorityEpoch != _authorityEpoch
                || header.PlayerCount > PlayerEntity.SlotCapacity
                || healthOffset > packet.Payload.Length
                || !NetMatchTimeSync.Validate(packet.Payload.Slice(timeOffset, NetMatchTimeSync.Size))
                || !NetHealthSync.Validate(packet.Payload[healthOffset..])
                || BinaryPrimitives.ReadUInt16LittleEndian(packet.Payload[healthOffset..]) != _matchId
                || (_snapshotSeen && !NetLifecycleTracker.Newer(header.Frame, _snapshotFrame))) return;
            int occupied = 0;
            for (int i = 0; i < header.PlayerCount; i++)
            {
                PlayerState state = PlayerState.Read(packet.Payload[(SnapshotHeader.Size + i * PlayerState.Size)..]);
                if (state.SlotIndex >= _slotLives.Length || (occupied & (1 << state.SlotIndex)) != 0
                    || state.SlotGeneration != _slotGenerations[state.SlotIndex]) return;
                occupied |= 1 << state.SlotIndex;
            }
            // Commit only after the entire packet has passed validation.
            for (int i = 0; i < header.PlayerCount; i++)
            {
                PlayerState state = PlayerState.Read(packet.Payload[(SnapshotHeader.Size + i * PlayerState.Size)..]);
                _slotLives[state.SlotIndex] = state.LifeId;
            }
            _snapshotSeen = true;
            _snapshotFrame = header.Frame;
            _lastSnapshot = packet.Payload.ToArray();
            for (int i = 0; i < _peers.Count; i++)
            {
                if (_peers[i] != peer)
                {
                    _transport?.Send(_peers[i].EndPoint, PacketType.Snapshot, packet.Payload);
                }
            }
        }

        private void HandleBye(ReceivedPacket packet)
        {
            Peer? peer = Find(packet.Sender);
            if (peer != null)
            {
                Remove(peer, "left");
            }
        }

        private void DropTimedOut(double now)
        {
            for (int i = _peers.Count - 1; i >= 0; i--)
            {
                if (now - _peers[i].LastSeen > NetConfig.TimeoutSeconds)
                {
                    Remove(_peers[i], "timed out");
                }
            }
        }

        private void Remove(Peer peer, string reason)
        {
            CareerPeerLeaving(peer);
            _peers.Remove(peer);
            LobbyPeerRemoved(peer);
            BroadcastRoster();
            // A vote is counted against everybody connected, so somebody
            // leaving can decide one that nobody has voted in since. The
            // results screen's ballot is counted the same way and moves for
            // the same reason.
            ReviewVote(_now);
            ReviewPicks();
            Log($"{peer.EndPoint} {reason} (slot {peer.SlotIndex})");
            if (peer.Name.Length > 0)
            {
                Announce($"{peer.Name} {reason}");
            }
            if (RunsTheMatch || _authority != peer)
            {
                return;
            }
            // Promote rather than end the session: the remaining players keep
            // playing, and the new authority's snapshots simply take over.
            _authority = _peers.Count > 0 ? _peers[0] : null;
            _authorityEpoch++;
            _snapshotSeen = false;
            BroadcastMatchState(_now);
            BroadcastRoster();
            Log(_authority != null
                ? $"authority moved to slot {_authority.SlotIndex}"
                : "no peers left; waiting for a new authority");
            if (_authority != null)
            {
                NotifyAuthority(_authority);
            }
        }

        private Peer? Find(IPEndPoint endPoint)
        {
            for (int i = 0; i < _peers.Count; i++)
            {
                if (_peers[i].EndPoint.Equals(endPoint))
                {
                    return _peers[i];
                }
            }
            return null;
        }

        private bool SlotFree(int slot)
        {
            for (int i = 0; i < _peers.Count; i++)
            {
                if (_peers[i].SlotIndex == slot)
                {
                    return false;
                }
            }
            return true;
        }

        private int NextFreeSlot()
        {
            for (int slot = 0; slot < _maxPlayers; slot++)
            {
                bool taken = false;
                for (int i = 0; i < _peers.Count; i++)
                {
                    if (_peers[i].SlotIndex == slot)
                    {
                        taken = true;
                        break;
                    }
                }
                if (!taken)
                {
                    return slot;
                }
            }
            return -1;
        }

        private static void Log(string message)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [server] {message}");
        }
    }
}
