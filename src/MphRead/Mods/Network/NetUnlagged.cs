using System;
using MphRead.Entities;
using OpenTK.Mathematics;

namespace MphRead.Mods.Network
{
    /// <summary>
    /// Backwards reconciliation: a shot is resolved against the world the
    /// shooter was actually looking at, not the one that exists by the time
    /// their trigger arrives.
    ///
    /// Ported from Q-Zandronum (IgeNiaI/Q-Zandronum, <c>unlagged.cpp</c>),
    /// itself Spleen's Skulltag work. The Doom original is
    /// <c>UNLAGGED_Reconcile</c> / <c>UNLAGGED_Restore</c> around a hitscan
    /// trace, plus Q-Zandronum's own addition -- the part this game actually
    /// needed -- <c>UNLAGGED_DoUnlagActors</c>, which does the same thing for
    /// a projectile and then walks it forward to the present.
    ///
    /// The fault it fixes here is not the Doom one, because the topology is
    /// not Doom's. Nobody is a neutral server: the first client to join
    /// simulates the match and everybody else feeds it intent through the
    /// relay. So for a client B shooting at C:
    ///
    ///   * B's screen shows C where the last snapshot put them, which the
    ///     authority composed one downstream trip ago from an intent C sent
    ///     one upstream trip before that.
    ///   * B presses fire. The intent reaches the authority one upstream trip
    ///     later, and the authority spawns the beam against the C it holds
    ///     *now*.
    ///
    /// The two differ by B's full round trip, and it is entirely one-sided: B
    /// aims at a hunter and the shot is resolved against wherever that hunter
    /// walked to in the meantime. At the Pi's 15 ms nobody notices. At 150 ms
    /// a strafing player is most of their own width away from where they are
    /// being shot at, which is the "my shots go through people" report, and
    /// no amount of smoothing the puppets can touch it -- the puppets are
    /// right, the clock is wrong.
    ///
    /// The authority's own player needs none of this and gets a rewind of
    /// zero: it aims at the puppets it holds and resolves against those same
    /// puppets, so the two already agree. Compensation is owed to exactly the
    /// people who are not running the simulation, in exactly the amount they
    /// are behind it -- which is what <see cref="IntentPacket.AckFrame"/> is
    /// for.
    ///
    /// What is deliberately *not* ported: Zandronum reconciles sectors and
    /// polyobjects as well as players, because a Doom map's floor is a moving
    /// hitbox. Nothing in an MPH room moves that a shot can be stopped by in
    /// the same way, and rewinding room geometry would mean unwinding the
    /// collision structures the whole engine indexes against. Players are the
    /// whole of it here.
    /// </summary>
    public static class NetUnlagged
    {
        private const int Slots = PlayerEntity.SlotCapacity;

        /// <summary>
        /// How many frames of position history are kept.
        ///
        /// Zandronum keeps <c>UNLAGGEDTICS</c> 35, which is one second at its
        /// 35 Hz. One second was the right amount of history there and is not
        /// quite enough here: this ring has to cover the round trip *plus*
        /// whatever a client is holding its puppets back by to smooth them
        /// (<see cref="NetSmoothing"/>), and the ceiling below has to sit
        /// comfortably inside it rather than against its edge. Two seconds at
        /// 60, rounded to a power of two so the ring index is a mask. Eight
        /// slots of position, form and liveness: about twelve kilobytes.
        /// </summary>
        public const int HistoryFrames = 128;

        /// <summary>
        /// The furthest back a shot may ever be resolved, whatever the packet
        /// claims.
        ///
        /// Two jobs, and the second is the one that matters. It keeps the
        /// rewind inside the history, which is arithmetic; and it bounds what
        /// a client can ask for, which is not. The ack is a number the sender
        /// chose, and a sender that chooses a very old one is asking to shoot
        /// at where everybody stood a second ago -- so this is the ceiling on
        /// how much of that anyone is allowed. 24 frames is 400 ms, which was
        /// chosen as "past any line the game is playable on" -- and measured
        /// against real players on the Japan server it is not. Nine thousand
        /// compensated shots came out at a mean of 19.6 frames with the worst
        /// pinned at exactly 24, which is a distribution running into its own
        /// ceiling rather than a tail touching it. Every shot the clamp
        /// reaches is resolved against a world its shooter never saw, and on
        /// a headshot band 0.3 units tall that is the whole band.
        ///
        /// So it is a ceiling that can be moved rather than a constant: the
        /// one number an A/B run has to be able to change without changing
        /// anything else. <c>-maxrewind N</c>.
        ///
        /// <b>And it moved, on this box, measured.</b> Two clients at a 320 ms
        /// round trip with 80 ms of jitter and 2% loss, the sniper scenario
        /// (<c>tools/hitrig</c>), under the old 24-frame ceiling:
        ///
        /// <code>
        /// rewound 99   mean 399 ms   clamped 88 (88.9%)   3.2 frames refused each
        /// depths asked: 23:4 24:7&lt;-ceiling 25:14 26:28 27:24 28:7 29:8 30:2 32:2 37:1 39:1 40:1
        /// </code>
        ///
        /// Nine shots in ten resolved against a world their shooter never saw,
        /// and the distribution's *mode* is two frames past the ceiling -- not
        /// a tail touching it, the whole thing folded onto it. The shooter's
        /// own machine resolved 16 of the 26 hits the authority credited it
        /// with, so four shots in ten landed with nothing happening on the
        /// screen that fired them, and two headshots in ten came back as body
        /// shots. 45 frames is 750 ms: past that distribution with room for
        /// the smoothing delay on top, still inside a history twice as deep,
        /// and still under the one second Q-Zandronum allows by bounding the
        /// rewind with its history and nothing else.
        /// </summary>
        public static int MaxRewindFrames { get; set; } = DefaultMaxRewindFrames;

        /// <summary>
        /// 750 ms. <see cref="LegacyMaxRewindFrames"/> is what every build up
        /// to protocol 6 used, and is the baseline arm of any A/B.
        /// </summary>
        public const int DefaultMaxRewindFrames = 45;

        /// <summary>The 400 ms ceiling every build before protocol 7 had.</summary>
        public const int LegacyMaxRewindFrames = 24;

        /// <summary>
        /// The furthest the ceiling may be raised. The history is
        /// <see cref="HistoryFrames"/> deep and the cell for the frame a shot
        /// is fired in has not been written yet, so a rewind of the full 64
        /// would read a cell as it is being overwritten.
        /// </summary>
        public const int MaxRewindCeiling = HistoryFrames - 8;

        /// <summary>
        /// Set the ceiling from the command line. A number the history cannot
        /// serve is refused rather than clamped quietly: a run whose ceiling
        /// is not the one it was asked for measures nothing.
        /// </summary>
        public static bool ConfigureMaxRewind(string? value)
        {
            if (!Int32.TryParse(value, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out int frames)
                || frames < 1 || frames > MaxRewindCeiling)
            {
                return false;
            }
            MaxRewindFrames = frames;
            return true;
        }

        /// <summary>
        /// Whether reconciliation runs at all. Off restores the previous
        /// behaviour exactly: shots resolve against the present, which is
        /// what every build before this one did.
        ///
        /// Zandronum spells this <c>sv_nounlagged</c> and defaults it on;
        /// this is the same decision, made once for the match by whoever
        /// simulates it, and exposed as <c>-nounlagged</c> so a run of the
        /// harness can measure both.
        /// </summary>
        public static bool Enabled { get; set; } = true;

        /// <summary>
        /// Whether a trigger pull recovered from a packet's press history is
        /// rewound by its own age as well as by the packet's ack.
        ///
        /// The case it covers: an intent is lost or arrives out of order, so
        /// the Shoot edge it carried reaches the simulation from the press
        /// history of a *later* packet -- with that packet's ack, which is
        /// newer by exactly the frames in between. The shot is then resolved
        /// against a world several frames ahead of the one it was aimed in,
        /// and the deeper the loss the worse it is. See
        /// <see cref="NetPlayerBridge.ShootPressAge"/>.
        ///
        /// Off by default and on with <c>-pressage</c>, so the two can be
        /// measured against each other rather than argued about. It is second
        /// order next to the ceiling -- it costs nothing on a clean line,
        /// because on a clean line the age is always zero.
        /// </summary>
        public static bool PressAgeEnabled { get; set; }

        /// <summary>
        /// Shots whose rewind was deepened because their trigger pull had been
        /// recovered from a press history, and the frames added across them.
        /// Zero on a line that is losing nothing, which is what makes a
        /// non-zero reading worth having.
        /// </summary>
        public static long StalePresses { get; private set; }
        public static long StalePressFrames { get; private set; }

        /// <summary>
        /// How far a clamped shot's victims were from where the shooter saw
        /// them, in units, and the worst of it.
        ///
        /// The reading the frame counts cannot give. "Refused four frames of
        /// rewind" is only a number of frames; what decides a headshot is how
        /// far a body moved in those frames, and that is a different quantity
        /// for a player standing still, strafing, and coming off a jump pad.
        /// The history holds both positions -- where the rewind went and where
        /// it was asked to go -- so the error is a subtraction, taken for every
        /// clamped shot at no cost.
        ///
        /// Against the geometry: the headshot band is 0.3 units tall and the
        /// body capsule is about 0.9 units across, so a vertical error over
        /// 0.3 has moved the band off the shot and a total error over 0.9 has
        /// moved the whole hunter.
        /// </summary>
        /// <summary>
        /// Clamped shots by shooter slot.
        ///
        /// Here because the error above came out with a vertical component of
        /// zero on a run whose target spent 60% of its frames in the air,
        /// which is either a broken measurement or a shot fired by somebody
        /// else at somebody who was standing still. The two are told apart by
        /// asking who pulled the trigger, and nothing else in the report can.
        /// </summary>
        public static readonly long[] ClampedByShooter = new long[Slots];

        public static long ClampErrorSamples { get; private set; }
        public static double ClampErrorSum { get; private set; }
        public static double ClampErrorVerticalSum { get; private set; }
        public static float ClampErrorWorst { get; private set; }
        public static float ClampErrorWorstVertical { get; private set; }

        /// <summary>
        /// Shots that were resolved somewhere other than the present, and how
        /// far back. <see cref="WorstRewind"/> is the deepest single rewind,
        /// which under a healthy line should sit near a client's round trip
        /// in frames and never near <see cref="MaxRewindFrames"/>.
        /// </summary>
        public static long ShotsCompensated { get; private set; }
        public static long FramesRewound { get; private set; }
        public static int WorstRewind { get; private set; }

        /// <summary>
        /// Shots whose shooter asked to be taken further back than
        /// <see cref="MaxRewindFrames"/> allows, and the frames of rewind
        /// refused across all of them.
        ///
        /// The pair the mean rewind cannot tell you. A mean of 19.6 frames
        /// under a ceiling of 24 is either a healthy distribution with a tail
        /// that happens to touch the top, or a distribution whose top half has
        /// been folded onto the ceiling -- and those two want opposite things
        /// done about them. This counts the folding directly: every clamped
        /// shot is one resolved against a world its shooter never saw, by
        /// <see cref="FramesRefused"/> / <see cref="ShotsClamped"/> frames on
        /// average.
        /// </summary>
        public static long ShotsClamped { get; private set; }
        public static long FramesRefused { get; private set; }

        /// <summary>
        /// The deepest rewind anybody *asked* for, as opposed to the deepest
        /// one served. Under a ceiling that is never reached the two are the
        /// same number; the gap between them is how far the ceiling is from
        /// covering the room.
        /// </summary>
        public static int WorstRequested { get; private set; }

        /// <summary>
        /// How many shots asked for each depth, so the shape of the
        /// distribution can be read rather than inferred from its mean.
        /// Indexed by requested frames, with everything past the ring's reach
        /// in the last cell -- an ack older than the history is a different
        /// fault and belongs in <see cref="HistoryMisses"/>, not in a bucket
        /// that would make the tail look fatter than it is.
        /// </summary>
        public static readonly long[] DepthHistogram = new long[HistoryFrames + 1];

        /// <summary>
        /// Beam steps run during catch-up, and how many of them ended in a
        /// collision.
        ///
        /// The pair says whether the projectile half is doing anything: steps
        /// with no hits means shots are being launched forward and missing
        /// exactly as they did before, which is either a genuinely clean
        /// match or a sign the rewind is not reaching the beams.
        /// </summary>
        public static long CatchUpSteps { get; private set; }
        public static long CatchUpHits { get; private set; }

        /// <summary>
        /// Shots whose rewind was refused because the history did not hold
        /// the frame asked for.
        ///
        /// Non-zero is not automatically wrong -- a client that has just
        /// joined has acked nothing yet -- but a number that keeps climbing
        /// during a match means acks are arriving older than
        /// <see cref="HistoryFrames"/>, and the shots behind them are being
        /// resolved against the present with no compensation at all.
        /// </summary>
        public static long HistoryMisses { get; private set; }

        // The ring. All slots are written in one pass, so one frame stamp per
        // cell covers the lot: a stamp that does not match the frame being
        // asked for means the cell has been overwritten by a later one.
        private static readonly ushort[,] _life = new ushort[Slots, HistoryFrames];
        private static readonly ushort[,] _generation = new ushort[Slots, HistoryFrames];

        public static void ResetSlot(int slot)
        {
            if (slot < 0 || slot >= Slots) return;
            for (int i = 0; i < HistoryFrames; i++)
            {
                _inPlay[slot, i] = false;
                _life[slot, i] = 0;
                _generation[slot, i] = 0;
            }
            _moved[slot] = false;
        }

        private static readonly Vector3[,] _position = new Vector3[Slots, HistoryFrames];
        private static readonly bool[,] _altForm = new bool[Slots, HistoryFrames];
        private static readonly bool[,] _inPlay = new bool[Slots, HistoryFrames];
        private static readonly uint[] _stamp = new uint[HistoryFrames];

        /// <summary>
        /// The newest frame in the history.
        ///
        /// Needed to tell the two ways a lookup can fail apart. The snapshot
        /// that records a frame is published *after* that frame's simulation
        /// step (see <c>NetHooks.AfterSimulation</c>), so the frame a shot is
        /// fired in is never in the history yet -- asking for it is normal and
        /// means "the present". Asking for something older than the ring is a
        /// gap, and a different thing entirely.
        /// </summary>
        private static uint _newest;

        // Where everybody was before the rewind, so Restore can put them back.
        private static readonly Vector3[] _restore = new Vector3[Slots];
        private static readonly bool[] _moved = new bool[Slots];
        private static bool _reconciled;

        // Which of the shooter's beam pool entries were alive before the shot,
        // so the ones it spawned can be told from the ones already in flight.
        // Grown from the pool rather than assumed: PlayerEntity asks for 16
        // today, and a silently short array here would quietly stop
        // compensating whichever beams fell off the end.
        private static bool[] _beamsBefore = new bool[16];
        private static PlayerEntity? _shooter;
        private static int _rewind;

        /// <summary>
        /// True while a shot is being spawned into the past.
        ///
        /// Zandronum's <c>unlagInProgress</c>, and here for the same reason:
        /// the state above is one shot's worth, so a second
        /// <see cref="BeginShot"/> reached from inside a catch-up step -- a
        /// beam whose collision kills a player, say -- would overwrite the
        /// first shot's bookkeeping and leave the world rewound with nothing
        /// holding the note to put it back.
        /// </summary>
        private static bool _inProgress;

        public static void Reset()
        {
            Array.Clear(_stamp);
            Array.Clear(_moved);
            _newest = 0;
            _reconciled = false;
            _inProgress = false;
            _shooter = null;
            _rewind = 0;
            ShotsCompensated = 0;
            FramesRewound = 0;
            WorstRewind = 0;
            ShotsClamped = 0;
            FramesRefused = 0;
            WorstRequested = 0;
            StalePresses = 0;
            StalePressFrames = 0;
            Array.Clear(ClampedByShooter);
            ClampErrorSamples = 0;
            ClampErrorSum = 0;
            ClampErrorVerticalSum = 0;
            ClampErrorWorst = 0;
            ClampErrorWorstVertical = 0;
            Array.Clear(DepthHistogram);
            CatchUpSteps = 0;
            CatchUpHits = 0;
            HistoryMisses = 0;
        }

        /// <summary>
        /// Record where everybody is, as of the snapshot numbered
        /// <paramref name="frame"/>.
        ///
        /// Called from <see cref="NetSession.BroadcastSnapshot"/> and nowhere
        /// else, deliberately. The history has to hold what the snapshot
        /// *said*, not what was true at some nearby moment: a client's ack
        /// names a snapshot, and the whole mechanism is the claim that cell
        /// <c>frame</c> is the picture that client was looking at. Recording
        /// anywhere else in the frame would make that claim off by however
        /// much the players moved in between, which is precisely the error
        /// being corrected.
        /// </summary>
        public static void Record(uint frame)
        {
            if (!Enabled)
            {
                return;
            }
            // A rewound world must never outlive the frame it was rewound in.
            // Everything below records where players *are*, and if a shot left
            // them displaced -- an exception between BeginShot and EndShot is
            // the only way, but it is a way -- this would file the rewound
            // positions as the truth and then publish them to everybody. One
            // call, once a frame, and a no-op on all but the pathological one.
            Restore();
            _inProgress = false;
            int index = (int)(frame % HistoryFrames);
            _stamp[index] = frame;
            _newest = frame;
            for (int i = 0; i < Slots; i++)
            {
                if (i >= PlayerEntity.Players.Count)
                {
                    _inPlay[i, index] = false;
                    continue;
                }
                PlayerEntity player = PlayerEntity.Players[i];
                bool active = player.LoadFlags.TestFlag(LoadFlags.Active) && player.ModIsInPlay;
                _life[i, index] = NetPlayerLifecycle.Get(i);
                _generation[i, index] = NetPlayerLifecycle.Generation(i);
                _inPlay[i, index] = active;
                if (active)
                {
                    _position[i, index] = player.Position;
                    _altForm[i, index] = player.IsAltForm;
                }
            }
        }

        /// <summary>
        /// Where the history says <paramref name="slot"/> stood at
        /// <paramref name="frame"/>, or false if it does not hold that frame
        /// or that player was not in play in it.
        ///
        /// The authority's own record, which is what makes a hit claim
        /// checkable: <see cref="NetHitClaims"/> refuses a claim whose point
        /// of impact is nowhere near the body this returns. Read-only -- it
        /// moves nobody, unlike <see cref="Reconcile"/>.
        /// </summary>
        public static bool PositionAt(int slot, uint frame, ushort expectedGeneration, ushort expectedLife, out Vector3 position)
        {
            position = Vector3.Zero;
            if (slot < 0 || slot >= Slots || frame == 0)
            {
                return false;
            }
            int index = (int)(frame % HistoryFrames);
            if (_stamp[index] != frame || !_inPlay[slot, index]
                || _life[slot, index] != expectedLife || _generation[slot, index] != expectedGeneration)
            {
                return false;
            }
            position = _position[slot, index];
            return Single.IsFinite(position.X) && Single.IsFinite(position.Y)
                && Single.IsFinite(position.Z);
        }

        /// <summary>
        /// How far behind the simulation the owner of <paramref name="slot"/>
        /// is, in frames, clamped to what can honestly be served.
        ///
        /// Zandronum's <c>UNLAGGED_Gametic</c>, with its two sources collapsed
        /// into one. It offers a choice between the client's acknowledged
        /// server tic and a figure derived from its measured ping
        /// (<c>cl_ping_unlagged</c>); the ack is the better of the two -- a
        /// ping is a smoothed average of a number that is not smooth, while
        /// the ack is the exact frame the client is answering -- and having
        /// only one of them means there is only one thing to be wrong.
        /// </summary>
        private static double RewindFor(int slot, out int requested)
        {
            requested = 0;
            if (slot < 0 || slot >= Slots || slot == NetSession.LocalSlot)
            {
                // The authority's own player already aims and resolves
                // against the same puppets. Nothing is owed.
                return 0;
            }
            if (!NetSession.RemoteIntentValid[slot])
            {
                return 0;
            }
            uint ack = NetSession.RemoteIntents[slot].AckFrame;
            uint now = NetSession.NetFrame;
            if (ack == 0 || ack >= now)
            {
                // Nothing acked yet (a client that has only just joined), or
                // an ack from a counter that has restarted and is ahead of
                // ours. Both mean "no usable answer", not "no delay" -- but
                // the present is the only defensible guess for either.
                return 0;
            }
            double depth = now - ack;
            // Less the fraction of a frame past that ack the shooter's world
            // actually sat at. A client that interpolates its puppets is
            // drawing a point between two snapshots, and it says which one and
            // how far past it; rewinding to the whole frame would put every
            // victim up to a frame of their own movement away from where they
            // were being aimed at -- which on a headshot band 0.3 units tall
            // is the whole band. Zero from a client that does not interpolate,
            // which is what every build before protocol 7 was.
            // IntentPacket.AckSubFrame.
            depth -= NetSession.RemoteIntents[slot].AckSubFrame / 256.0;
            // Plus however long the trigger pull sat in a press history before
            // it reached here. The ack belongs to the packet that carried the
            // edge, not to the frame the edge happened on, and those are the
            // same frame only when nothing was lost.
            if (PressAgeEnabled && slot < NetPlayerBridge.ShootPressAge.Length)
            {
                int age = NetPlayerBridge.ShootPressAge[slot];
                if (age > 0)
                {
                    depth += age;
                    StalePresses++;
                    StalePressFrames += age;
                }
            }
            // Recorded before the clamp, because the clamp is the thing being
            // measured. A depth past the ring is filed in the last cell rather
            // than dropped: it is still a shot that asked for more than it got.
            requested = (int)Math.Min(Math.Round(depth), HistoryFrames);
            if (depth > MaxRewindFrames)
            {
                depth = MaxRewindFrames;
            }
            return depth < 0 ? 0 : depth;
        }

        /// <summary>
        /// Whether this machine is the one whose shots decide anything.
        ///
        /// Everybody spawns beams -- a client fires its own gun for its own
        /// eyes -- but only the authority's collide with anybody. Rewinding
        /// on a machine whose shots are decoration would move other players'
        /// hitboxes underneath the person watching them, for no gain at all.
        /// </summary>
        private static bool Simulating => NetSession.Active
            && (NetSession.Role == NetRole.Host || NetSession.IsAuthority);

        /// <summary>
        /// Put the world back to what <paramref name="shooter"/> could see,
        /// ready for a shot to be spawned into it.
        ///
        /// Everybody except the shooter is moved. The shooter is left exactly
        /// where they are, for the same reason Zandronum leaves them ("the
        /// client is supposed to predict him"): their own position is the one
        /// thing they were not looking at a stale copy of. They told us where
        /// they are in the same packet as the trigger, and
        /// <see cref="NetPlayerBridge"/> has already put them there.
        /// </summary>
        public static void BeginShot(PlayerEntity shooter)
        {
            if (_inProgress)
            {
                return;
            }
            _shooter = null;
            _rewind = 0;
            if (!Enabled || !Simulating || shooter.IsBot)
            {
                return;
            }
            int slot = shooter.SlotIndex;
            double rewind = RewindFor(slot, out int requested);
            if (requested > 0 && requested < DepthHistogram.Length)
            {
                DepthHistogram[requested]++;
            }
            if (requested > WorstRequested)
            {
                WorstRequested = requested;
            }
            int served = (int)Math.Round(rewind);
            int weapon = NetShotDiagnostics.Bucket(shooter.CurrentWeapon);
            NetShotDiagnostics.RewindSamples[weapon]++;
            NetShotDiagnostics.RewindFrames[weapon] += served;
            if (requested > served) NetShotDiagnostics.RewindClamps[weapon]++;
            if (requested > served)
            {
                // The shooter asked to be taken back further than the ceiling
                // allows, so this shot is about to be resolved against a world
                // it never saw -- by exactly this many frames.
                ShotsClamped++;
                FramesRefused += requested - served;
                MeasureClampError(slot, requested, served);
            }
            if (rewind <= 0)
            {
                return;
            }
            // The exact point the shooter's screen was at, fraction and all.
            double target = NetSession.NetFrame - rewind;
            if (!Reconcile(slot, target))
            {
                HistoryMisses++;
                return;
            }
            _shooter = shooter;
            _rewind = (int)Math.Ceiling(rewind);
            ShotsCompensated++;
            FramesRewound += served;
            if (served > WorstRewind)
            {
                WorstRewind = served;
            }
            // Remember what was already in flight, so the beams this shot is
            // about to create can be picked out of the pool afterwards.
            BeamProjectileEntity[] beams = shooter.EquipInfo.Beams;
            if (_beamsBefore.Length < beams.Length)
            {
                _beamsBefore = new bool[beams.Length];
            }
            for (int i = 0; i < beams.Length; i++)
            {
                _beamsBefore[i] = beams[i].Lifespan > 0;
            }
            _inProgress = true;
        }

        /// <summary>
        /// The world-frame this shot is aimed in.
        ///
        /// On the machine running the match, for somebody else's shot, that is
        /// the frame the rewind is about to go to -- the world that shooter
        /// was looking at. For this machine's own player it is the point its
        /// playout clock is reading, which is the same quantity its intent
        /// acks. A bot, or a shot with neither, gets the present.
        /// </summary>
        internal static uint LaunchFrameFor(PlayerEntity shooter)
        {
            int slot = shooter.SlotIndex;
            if (Simulating && slot != NetSession.LocalSlot && !shooter.IsBot
                && slot >= 0 && slot < Slots && NetSession.RemoteIntentValid[slot])
            {
                uint ack = NetSession.RemoteIntents[slot].AckFrame;
                if (ack != 0 && ack <= NetSession.NetFrame)
                {
                    return ack;
                }
            }
            if (NetSmoothing.AckPoint(out uint read, out _))
            {
                return read;
            }
            return NetSession.AppliedSnapshotFrame != 0
                ? NetSession.AppliedSnapshotFrame
                : NetSession.NetFrame;
        }

        /// <summary>
        /// How far the ceiling put this shot's victims from where its shooter
        /// was looking at them.
        ///
        /// Both positions are already in the ring: the one the rewind will use
        /// and the one it was asked for. Nothing is moved and nothing is
        /// simulated -- this is two lookups and a subtraction, per victim, on
        /// the shots that were clamped and no others.
        /// </summary>
        private static void MeasureClampError(int shooterSlot, int requested, int served)
        {
            if (shooterSlot >= 0 && shooterSlot < Slots)
            {
                ClampedByShooter[shooterSlot]++;
            }
            uint now = NetSession.NetFrame;
            if (now < (uint)requested)
            {
                return;
            }
            int wanted = (int)((now - (uint)requested) % HistoryFrames);
            int got = (int)((now - (uint)served) % HistoryFrames);
            if (_stamp[wanted] != now - (uint)requested || _stamp[got] != now - (uint)served)
            {
                // One of the two is not in the ring any more, so the
                // subtraction would be between a position and whatever
                // overwrote it. A shot that cannot be measured is not a shot
                // with no error.
                return;
            }
            for (int i = 0; i < Slots && i < PlayerEntity.Players.Count; i++)
            {
                if (i == shooterSlot || !_inPlay[i, wanted] || !_inPlay[i, got])
                {
                    continue;
                }
                Vector3 error = _position[i, got] - _position[i, wanted];
                if (!Single.IsFinite(error.X) || !Single.IsFinite(error.Y)
                    || !Single.IsFinite(error.Z))
                {
                    continue;
                }
                float length = error.Length;
                float vertical = MathF.Abs(error.Y);
                ClampErrorSamples++;
                ClampErrorSum += length;
                ClampErrorVerticalSum += vertical;
                if (length > ClampErrorWorst)
                {
                    ClampErrorWorst = length;
                }
                if (vertical > ClampErrorWorstVertical)
                {
                    ClampErrorWorstVertical = vertical;
                }
            }
        }

        /// <summary>
        /// Move every player but <paramref name="exceptSlot"/> to where they
        /// stood at <paramref name="targetFrame"/>. Returns whether the history
        /// still held it.
        /// </summary>
        private static bool Reconcile(int exceptSlot, double targetFrame)
        {
            uint frame = (uint)Math.Floor(targetFrame);
            float fraction = (float)(targetFrame - frame);
            int index = (int)(frame % HistoryFrames);
            if (_stamp[index] != frame || frame == 0)
            {
                return false;
            }
            // The cell on the far side of the read point, if the ring still
            // holds it. Its absence is ordinary rather than a fault: the
            // newest cell has nothing after it, and a rewind of nearly zero
            // lands there.
            int next = (int)((frame + 1) % HistoryFrames);
            bool haveNext = fraction > 0.0001f && _stamp[next] == frame + 1;
            Restore();
            for (int i = 0; i < Slots && i < PlayerEntity.Players.Count; i++)
            {
                if (i == exceptSlot || !_inPlay[i, index]
                    || !NetPlayerLifecycle.Matches(i, _generation[i, index], _life[i, index]))
                {
                    continue;
                }
                PlayerEntity player = PlayerEntity.Players[i];
                if (!player.LoadFlags.TestFlag(LoadFlags.Active) || !player.ModIsInPlay)
                {
                    continue;
                }
                Vector3 was = _position[i, index];
                if (!Single.IsFinite(was.X) || !Single.IsFinite(was.Y) || !Single.IsFinite(was.Z))
                {
                    continue;
                }
                // Interpolated to the same point between the same two frames
                // the shooter was drawing, by the same fraction. Only when
                // both cells hold this player alive and in the same form, and
                // only when the two are near enough to be one movement -- a
                // respawn or a teleporter between them is a jump to blend
                // across, not a step. NetSmoothing does the same three checks
                // at the other end, which is what makes the two agree.
                if (haveNext && _inPlay[i, next] && _altForm[i, next] == _altForm[i, index]
                    && _life[i, next] == _life[i, index] && _generation[i, next] == _generation[i, index])
                {
                    Vector3 then = _position[i, next];
                    Vector3 travel = then - was;
                    if (Single.IsFinite(travel.X) && Single.IsFinite(travel.Y)
                        && Single.IsFinite(travel.Z) && travel.LengthSquared <= 16f)
                    {
                        was += travel * fraction;
                    }
                }
                // A position recorded in one form, applied to a body that has
                // since changed into the other, is the same standing spot in
                // the wrong reference frame -- and a biped cylinder is tall
                // enough for that to put a chest shot under the model. The
                // conversion is NetPlayerBridge's, for exactly this reason.
                _restore[i] = player.Position;
                _moved[i] = true;
                player.ModPlaceAt(NetPlayerBridge.InFormFor(player, was, _altForm[i, index]));
            }
            _reconciled = true;
            return true;
        }

        /// <summary>
        /// Undo the rewind. Safe to call when nothing was rewound, which is
        /// most frames.
        /// </summary>
        public static void Restore()
        {
            if (!_reconciled)
            {
                return;
            }
            for (int i = 0; i < Slots && i < PlayerEntity.Players.Count; i++)
            {
                if (!_moved[i])
                {
                    continue;
                }
                _moved[i] = false;
                PlayerEntity.Players[i].ModPlaceAt(_restore[i]);
            }
            _reconciled = false;
        }

        /// <summary>
        /// Walk the shot that was just spawned forward to the present, then
        /// put the world back.
        ///
        /// This is Q-Zandronum's <c>UNLAGGED_DoUnlagActors</c>, and it is the
        /// half that matters in this game. Doom's unlagged is built around
        /// hitscan, where rewinding the targets is the whole fix because the
        /// trace resolves in the instant it is fired. Almost nothing here is
        /// hitscan: a Missile, a Battlehammer round and a Magmaul shot all
        /// travel, so rewinding the world and spawning into it fixes only the
        /// first frame of the shot and then leaves the projectile crawling
        /// out of a muzzle a round trip late -- visibly behind its owner's
        /// screen, and easy to walk out of.
        ///
        /// So the beam is stepped once per frame it is owed, with the world
        /// re-reconciled at each step so that anything it runs into on the
        /// way is checked against where that player was at that moment. What
        /// arrives in the present is a shot already the right distance down
        /// range, having hit whatever it would have hit.
        ///
        /// The loop stops the moment every new beam is done -- collided, or
        /// out of lifespan -- which is what makes a point-blank shot cost one
        /// step instead of twenty-four.
        /// </summary>
        public static void EndShot(PlayerEntity shooter)
        {
            if (_shooter != shooter || _rewind <= 0)
            {
                Restore();
                _shooter = null;
                _inProgress = false;
                return;
            }
            int slot = shooter.SlotIndex;
            BeamProjectileEntity[] beams = shooter.EquipInfo.Beams;
            int newCount = 0;
            for (int i = 0; i < beams.Length; i++)
            {
                if (!_beamsBefore[i] && beams[i].Lifespan > 0)
                {
                    _beamsBefore[i] = true; // reused as "this one is ours, and still going"
                    newCount++;
                }
                else
                {
                    _beamsBefore[i] = false;
                }
            }
            if (newCount == 0)
            {
                // Nothing spawned: out of ammo, or a continuous beam being
                // replaced rather than created. Nothing to walk forward.
                Restore();
                _shooter = null;
                _inProgress = false;
                return;
            }
            for (int step = 1; step <= _rewind && newCount > 0; step++)
            {
                uint frame = NetSession.NetFrame - (uint)(_rewind - step);
                if (!Reconcile(slot, frame))
                {
                    if (frame <= _newest)
                    {
                        // A genuine gap: the history did not reach back this
                        // far. Stepping on against present positions would
                        // resolve the rest of this shot against a world nobody
                        // was ever shown, so stop and leave it where it got to
                        // -- at worst the behaviour with none of this.
                        HistoryMisses++;
                        break;
                    }
                    // Not a gap. The frame this shot is being fired in has no
                    // snapshot yet, because the snapshot is published after the
                    // step; so the last step of every catch-up asks for a frame
                    // that does not exist, and the right world for it is the
                    // one that does -- the present, which is what the catch-up
                    // is catching up to. Without this the loop stopped one step
                    // short of the present on every shot.
                    Restore();
                }
                for (int i = 0; i < beams.Length; i++)
                {
                    if (!_beamsBefore[i])
                    {
                        continue;
                    }
                    BeamProjectileEntity beam = beams[i];
                    bool hit = beam.Flags.TestFlag(BeamFlags.Collided);
                    CatchUpSteps++;
                    if (!beam.Process() || beam.Flags.TestFlag(BeamFlags.Collided))
                    {
                        if (!hit && beam.Flags.TestFlag(BeamFlags.Collided))
                        {
                            CatchUpHits++;
                        }
                        _beamsBefore[i] = false;
                        newCount--;
                    }
                }
            }
            Restore();
            _shooter = null;
            _rewind = 0;
            _inProgress = false;
        }

        /// <summary>
        /// One line for the harness reports and the diagnostics screen.
        /// </summary>
        public static string Describe()
        {
            if (!Enabled)
            {
                return "lag compensation: off";
            }
            if (ShotsCompensated == 0)
            {
                return "lag compensation: on, nothing to compensate "
                    + $"(history misses {HistoryMisses})";
            }
            double mean = FramesRewound / (double)ShotsCompensated;
            string text = $"lag compensation: {ShotsCompensated} shots rewound, "
                + $"mean {mean:F1} frames ({mean * 1000 / 60:F0} ms), worst {WorstRewind}, "
                + $"catch-up {CatchUpSteps} steps / {CatchUpHits} hits, "
                + $"history misses {HistoryMisses}";
            // The clamp, said plainly. Without this the only evidence that a
            // ceiling of MaxRewindFrames is doing anything is a "worst" that
            // happens to equal it, which is also what a single outlier looks
            // like.
            // A clamped shot is still a compensated one -- it is rewound, just
            // not as far as it asked -- so ShotsCompensated is the whole
            // population and the denominator, not something to add to.
            text += $"; ceiling {MaxRewindFrames} frames "
                + $"({MaxRewindFrames * 1000 / 60} ms), clamped {ShotsClamped}";
            if (ShotsClamped > 0)
            {
                text += $" ({ShotsClamped * 100.0 / Math.Max(1, ShotsCompensated):F1}% of shots, "
                    + $"mean {FramesRefused / (double)ShotsClamped:F1} frames refused)";
            }
            text += $", worst asked {WorstRequested}";
            if (ShotsClamped > 0)
            {
                var by = new System.Text.StringBuilder(", clamped by slot");
                for (int i = 0; i < ClampedByShooter.Length; i++)
                {
                    if (ClampedByShooter[i] > 0)
                    {
                        by.Append($" {i}:{ClampedByShooter[i]}");
                    }
                }
                text += by.ToString();
            }
            if (ClampErrorSamples > 0)
            {
                text += $", error {ClampErrorSum / ClampErrorSamples:F2} units mean "
                    + $"({ClampErrorVerticalSum / ClampErrorSamples:F2} vertical), "
                    + $"worst {ClampErrorWorst:F2} ({ClampErrorWorstVertical:F2} vertical)";
            }
            if (PressAgeEnabled)
            {
                text += $"; stale presses {StalePresses}";
                if (StalePresses > 0)
                {
                    text += $" (+{StalePressFrames / (double)StalePresses:F1} frames each)";
                }
            }
            return text;
        }

        /// <summary>
        /// The requested-depth distribution as one line of buckets, which is
        /// the reading the mean cannot give: a ceiling that is doing damage
        /// shows up here as a pile against the last bucket below it and
        /// nothing above, and a ceiling that is merely present shows up as a
        /// tail that thins out on its own.
        ///
        /// Empty depths are skipped rather than printed as zeros -- a run
        /// against one line occupies four or five buckets out of sixty-four.
        /// </summary>
        public static string DescribeDepths()
        {
            var text = new System.Text.StringBuilder("rewind depths asked (frames: shots):");
            bool any = false;
            for (int i = 0; i < DepthHistogram.Length; i++)
            {
                if (DepthHistogram[i] == 0)
                {
                    continue;
                }
                any = true;
                text.Append($" {i}:{DepthHistogram[i]}");
                if (i == MaxRewindFrames)
                {
                    text.Append("<-ceiling");
                }
            }
            return any ? text.ToString() : "rewind depths asked: none";
        }
    }
}
