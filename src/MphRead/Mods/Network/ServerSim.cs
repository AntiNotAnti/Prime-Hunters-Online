using System;
using System.Diagnostics;
using MphRead.Entities;
using MphRead.Mods.Input;
using OpenTK.Mathematics;

namespace MphRead.Mods.Network
{
    /// <summary>
    /// The match, simulated by the authoritative server.
    ///
    /// The authority is not a property of being a player. It is the machine
    /// every player's intent is resolved by. Normal online matches run that
    /// authority here, on the server, rather than on whichever client happened
    /// to join first. The server uses the same engine code as the clients, not
    /// a second reimplementation: the engine's simulation needs no GL context:
    ///
    /// <c>Scene.OnSimulationFrame</c> and everything under it -- input, the
    /// entity step, collision, beams, damage -- contains no GL call anywhere,
    /// the split having already put every one of them in
    /// <c>Scene.OnDrawFrame</c>. So the server can run the real engine, and
    /// the "second answer" objection disappears: it is the same answer,
    /// compiled from the same file.
    ///
    /// What this buys, in order of how much it matters:
    ///
    /// - **Nobody is at zero latency.** The authority resolved its own shots
    ///   against its own present and everybody else's against a rewind
    ///   (<see cref="NetUnlagged"/>); it was the one player in the match who
    ///   could not be wrong about where anyone was. Now every player,
    ///   including whoever used to be slot 0, is compensated by exactly their
    ///   own round trip and nobody is compensated by zero.
    /// - **The match stops depending on a player's machine.** No handover when
    ///   the authority leaves, no stand-down when their line blips, no
    ///   half-second of nobody simulating while the server picks a successor.
    /// - **The scoreboard has one author.** Kills, points and the end of the
    ///   match are decided where the clock already lived.
    ///
    /// Server authority by itself does not shorten the network round trip.
    /// Responsiveness for a client's own outgoing hits comes from
    /// <see cref="NetHitPrediction"/>: the client presents its local result
    /// immediately and later reconciles it with the authority. Remote lethal
    /// damage is deliberately held for the authority, while self-damage and
    /// self-death can resolve locally.
    ///
    /// Movement is authoritative here as well. Clients still predict their
    /// own controls immediately, but <see cref="IntentPacket.Position"/> is no
    /// longer trusted to place a player or their muzzle. The server runs the
    /// real movement/collision step from the input stream and snapshots echo
    /// the newest owner input frame actually processed, which lets each client
    /// reconcile its prediction against the same instant without adding input
    /// latency. See <c>.claude/multiplayer/NETWORK-SERVERAUTH.md</c>.
    /// </summary>
    public sealed class ServerSim
    {
        private Scene? _scene;
        private string _room = "";
        private GameMode _mode = GameMode.Battle;

        /// <summary>Simulation steps run since this sim was started.</summary>
        public long Frames { get; private set; }

        /// <summary>
        /// Total time spent inside <see cref="Step"/>, for the one question an
        /// operator actually has: is this box keeping up with 60 Hz.
        /// </summary>
        public double StepSeconds { get; private set; }

        /// <summary>Longest single step, which is what a stutter is made of.</summary>
        public double WorstStepSeconds { get; private set; }

        /// <summary>Steps that took longer than the 16.7 ms they were owed.</summary>
        public long OverrunSteps { get; private set; }

        /// <summary>Steps that threw. Non-zero is a bug, not a slow machine.</summary>
        public long StepFailures { get; private set; }

        /// <summary>Authority deadlines dropped because the server fell too far behind.</summary>
        public long DroppedSteps { get; private set; }

        /// <summary>Times a long stall forced the absolute schedule to re-base.</summary>
        public long Stalls { get; private set; }

        // Absolute wall-clock deadline for the next 60 Hz step. Advancing the
        // deadline by a fixed period rather than "now + period" prevents loop
        // jitter from becoming clock drift.
        private double _nextStepAt = -1;

        /// <summary>
        /// Run whatever steps the wall clock says are owed.
        ///
        /// The same fixed 60 Hz contract the game window runs, but scheduled
        /// against absolute wall-clock deadlines rather than from the time the
        /// previous loop happened to wake. Advancing `nextStepAt` by exactly
        /// one period prevents scheduler jitter from turning into long-term
        /// drift while preserving the engine's frame-counted timers.
        /// </summary>
        public void Advance(double now)
        {
            if (_scene == null)
            {
                return;
            }
            if (_nextStepAt < 0)
            {
                _nextStepAt = now + Render.FrameTiming.StepSeconds;
                return;
            }

            if (now - _nextStepAt > StallSeconds)
            {
                Stalls++;
                _nextStepAt = now + Render.FrameTiming.StepSeconds;
                return;
            }

            int steps = 0;
            while (now >= _nextStepAt && steps < Render.FrameTiming.MaxCatchUpSteps)
            {
                Step();
                _nextStepAt += Render.FrameTiming.StepSeconds;
                steps++;
            }
            if (now >= _nextStepAt)
            {
                long dropped = (long)((now - _nextStepAt) / Render.FrameTiming.StepSeconds) + 1;
                DroppedSteps += dropped;
                _nextStepAt += dropped * Render.FrameTiming.StepSeconds;
            }
        }

        /// <summary>
        /// Time until the next authoritative simulation deadline. The server
        /// loop uses this only for pacing; Advance remains the sole owner of
        /// whether a step actually runs.
        /// </summary>
        public double SecondsUntilNextStep(double now)
        {
            if (_scene == null || _nextStepAt < 0) return 0;
            return Math.Max(0, _nextStepAt - now);
        }

        /// <summary>
        /// Longer than this between passes is a stall, not a slow pass. A
        /// quarter of a second, as the window's accumulator uses.
        /// </summary>
        private const double StallSeconds = 0.25;

        public bool Running => _scene != null;

        /// <summary>
        /// The room the simulation is in *now*, which after a rotation is not
        /// the one it was started with.
        ///
        /// Read off the scene rather than remembered, because a rotation is a
        /// room transition inside the same scene (`NetRoomChange`) and nothing
        /// tells this class it happened. Remembering it made the server report
        /// its starting map for the rest of its life, which reads exactly like
        /// a server whose simulation failed to follow the rotation -- and the
        /// clients had followed it perfectly.
        /// </summary>
        public string Room
        {
            get
            {
                if (_scene == null)
                {
                    return "";
                }
                string current = Metadata.GetRoomById(_scene.RoomId, noThrow: true)?.Name ?? "";
                return current.Length > 0 ? current : _room;
            }
        }

        /// <summary>
        /// Whether this machine could simulate at all, asked before a socket
        /// is opened rather than discovered on the first join.
        ///
        /// A normal dedicated game server must be able to build the authoritative
        /// world. That requires the user's extracted game files and a valid
        /// paths.txt. Returning false here is a startup failure for the normal
        /// server path; it must not silently fall back to client authority.
        /// </summary>
        public static bool Available(out string reason)
        {
            try
            {
                string root = MphRead.Paths.FileSystem;
                if (String.IsNullOrEmpty(root) || !System.IO.Directory.Exists(root))
                {
                    reason = "no game files are set up on this machine (see paths.txt)";
                    return false;
                }
            }
            catch (Exception ex)
            {
                reason = $"game files could not be located ({ex.Message})";
                return false;
            }
            if (!SyntheticInput.Available(out string inputReason))
            {
                reason = $"a keyboard could not be synthesised ({inputReason})";
                return false;
            }
            reason = "";
            return true;
        }

        /// <summary>
        /// Build the world and take the authority.
        ///
        /// The order is the launcher's, and for the launcher's reasons:
        /// players exist before the room does, because
        /// <c>Scene.AddRoom</c> only lists the slots that are already there
        /// and <c>Scene.AddPlayer</c> is inert once it has run. The one
        /// difference is <c>localSlot: -1</c> -- there is no player at this
        /// keyboard, so no slot is exempt from being a puppet.
        /// </summary>
        public bool Start(string roomKey, GameMode mode, int maxPlayers,
            SnapshotSink sink, Action matchEnded, RosterPacket? roster = null, SessionStatePacket? session = null)
        {
            Stop();
            Mods.Headless.Enter();
            try
            {
                _room = roomKey;
                _mode = mode;
                // Before any player is created. Offline this is four, a DS
                // match's cap, and Create hands back null past it -- which
                // would leave a server advertising eight slots and simulating
                // the first four.
                PlayerEntity.MaxPlayers = Math.Clamp(maxPlayers, 2, PlayerEntity.SlotCapacity);
                NetSession.StartServerAuthority(sink, matchEnded);
                if (roster is { } players) NetSession.ApplyRoster(players);
                if (session is { } state) NetSession.ApplySessionState(state);
                // A size, because the scene divides by it when it builds a
                // projection. Nothing here ever builds one; this is the DS's
                // own, so a stray aspect ratio is at least the right one.
                var scene = new Scene(new Vector2i(256, 192),
                    SyntheticInput.CreateKeyboard(), SyntheticInput.CreateMouse(),
                    _ => { }, () => { });
                // Samus for every unoccupied slot, as a placeholder only: each
                // slot's real hunter arrives on the roster and PlayerColors
                // settles it every frame thereafter, the same as on a client.
                NetLaunch.BuildPlayers(scene, Hunter.Samus, localRecolor: 0,
                    teams: GameState.IsTeamMode(mode), localSlot: -1);
                scene.AddRoom(roomKey, mode, playerCount: NetLaunch.RoomPlayerCount);
                scene.OnLoad();
                _scene = scene;
                Frames = 0;
                StepSeconds = 0;
                WorstStepSeconds = 0;
                OverrunSteps = 0;
                StepFailures = 0;
                DroppedSteps = 0;
                Stalls = 0;
                _nextStepAt = -1;
                return true;
            }
            catch (Exception ex)
            {
                // The whole exception, not its message. A server that cannot
                // load a room is a server nobody can play on, and the one
                // thing worth knowing is which of the load's steps it died in
                // -- on a machine where nobody will be attaching a debugger.
                Console.WriteLine($"[sim] could not load \"{roomKey}\": {ex}");
                NetLog.Event($"server simulation failed to start: {ex}");
                Stop();
                // Loading can fail before _scene is assigned. Release the partial
                // authority session and assets so another lobby start can retry.
                NetSession.Stop();
                Read.ClearCache();
                return false;
            }
        }

        /// <summary>
        /// One 60 Hz step of the real engine.
        ///
        /// <c>OnSimulationFrame</c> and not <c>OnUpdateFrame</c>: the second
        /// one draws, and there is nothing here to draw with. Everything the
        /// authority owes the match happens inside this call -- the intents
        /// that arrived are applied, every player is stepped, shots are
        /// resolved against the rewound world, damage is recorded, and
        /// <c>NetHooks.AfterSimulation</c> publishes the snapshot through the
        /// sink.
        /// </summary>
        public void Step()
        {
            if (_scene == null)
            {
                return;
            }
            long start = Stopwatch.GetTimestamp();
            try
            {
                _scene.OnSimulationFrame();
            }
            catch (Exception ex)
            {
                // A server must not die of one bad frame. A client that throws
                // here takes down one player's game; this would take down
                // everybody's match and the relay with it.
                //
                // The first one gets its stack and the rest get a line: a
                // fault in the step usually repeats sixty times a second, so
                // printing the trace every time buries the trace.
                StepFailures++;
                if (StepFailures == 1)
                {
                    Console.WriteLine($"[sim] step failed: {ex}");
                }
                else
                {
                    Console.WriteLine($"[sim] step failed: {ex.Message}");
                }
                NetLog.Event($"server simulation step failed: {ex}");
            }
            double elapsed = Stopwatch.GetElapsedTime(start).TotalSeconds;
            Frames++;
            StepSeconds += elapsed;
            if (elapsed > WorstStepSeconds)
            {
                WorstStepSeconds = elapsed;
            }
            if (elapsed > 1 / 60.0)
            {
                OverrunSteps++;
            }
        }

        public void Stop()
        {
            if (_scene == null)
            {
                return;
            }
            _scene = null;
            _room = "";
            NetSession.Stop();
            // The room's models, collision and entity lists, which are held in
            // a static cache keyed by path: without this a rotation through
            // twenty maps keeps all twenty.
            Read.ClearCache();
            GC.Collect(generation: 2, GCCollectionMode.Forced, blocking: true, compacting: true);
        }

        /// <summary>
        /// What lag compensation did, for the periodic server report.
        ///
        /// This has to be printed here or it is printed nowhere. Every
        /// `-netcheck` report carries the rewind figures, and every one of
        /// them now reads "nothing to compensate" -- correctly, because no
        /// client is the authority any more. The one machine that rewinds
        /// anything is this one, and it writes no report; without this the
        /// measurement that says lag compensation is working at all
        /// disappeared the moment the authority moved.
        ///
        /// The number to read is the mean rewind against the round trips of
        /// the players connected: see NETWORK-UNLAGGED.md.
        /// </summary>
        public string DescribeUnlagged() => NetUnlagged.Describe() + "\n" + NetShotDiagnostics.Describe() + NetTimingDiagnostics.Describe();

        /// <summary>
        /// The requested-rewind distribution. Only the simulating machine has
        /// one, and it is the reading that says whether the ceiling is a
        /// safety rail or a wall the room is standing against.
        /// </summary>
        public string DescribeRewindDepths() => NetUnlagged.DescribeDepths();

        /// <summary>
        /// What the machine running the match did with the hits its clients
        /// said they landed. Only this machine has the numbers -- a client
        /// sees its own claims answered and nothing about anybody else's --
        /// and the pair worth reading is <c>applied</c> against
        /// <c>already resolved</c>: the second is the rewind doing its job
        /// unaided, the first is what it could not reach.
        /// </summary>
        public string? DescribeClaims() => NetHitClaims.Describe();

        /// <summary>
        /// Whether each client's own arithmetic for a shot came out the same
        /// as this machine's, a weapon at a time.
        ///
        /// The measurement nothing else can take: a claim carries the number
        /// the *shooter* computed for a shot, and this machine pairs it with
        /// its own hit for the same shot in order to refuse it as a duplicate
        /// -- so the comparison is free and it is exact. Both sides run the
        /// same table, so anything but 100% agreement means one of them is
        /// reading a quantity the other was never sent, and the weapon it
        /// happens on says which. NetHitClaims.DescribeAgreement.
        /// </summary>
        public string DescribeAgreement() => NetHitClaims.DescribeAgreement();

        /// <summary>
        /// How many beams each slot's gun spawned *here*.
        ///
        /// The number that says whether the two machines agree about how often
        /// a trigger goes off at all, which nothing else asks. A client's own
        /// report counts the same thing for itself, and in a rig run the two
        /// came out at 23 and 253: the shooter and the authority were not
        /// disagreeing about where a shot went, they were disagreeing about
        /// how many there were. A hit rate compared across that gap is
        /// comparing two different volleys.
        /// </summary>
        public string DescribeShots()
        {
            var text = new System.Text.StringBuilder("shots spawned here (slot: beams):");
            bool any = false;
            for (int i = 0; i < NetDamage.Fired.Length; i++)
            {
                if (NetDamage.Fired[i] == 0)
                {
                    continue;
                }
                any = true;
                text.Append($" {i}:{NetDamage.Fired[i]}");
            }
            return any ? text.ToString() : "shots spawned here: none";
        }

        /// <summary>One line for the periodic server report.</summary>
        public string Describe()
        {
            if (_scene == null)
            {
                return "not simulating";
            }
            double mean = Frames > 0 ? StepSeconds / Frames * 1000 : 0;
            return $"{Room} ({GameState.Mode}), {Frames} step(s), "
                + $"{mean:0.00} ms mean, {WorstStepSeconds * 1000:0.0} ms worst, "
                + $"{OverrunSteps} overrun, {DroppedSteps} dropped, {Stalls} stall(s)"
                + (StepFailures > 0 ? $", {StepFailures} FAILED" : "");
        }
    }
}
