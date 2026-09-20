using System;
using MphRead.Entities;
using OpenTK.Mathematics;

namespace MphRead.Mods.Network
{
    /// <summary>
    /// Remote players are drawn at a point between two snapshots, a fixed
    /// distance behind the newest one, instead of being snapped to whichever
    /// snapshot arrived last.
    ///
    /// <b>The fault.</b> A snapshot is composed sixty times a second and
    /// arrives when the line lets it. On a clean line that is sixty times a
    /// second and nobody notices; on a bad one the gaps are 0, 0, 3, 1, 0, 4
    /// frames, and a puppet written straight from each arrival stands still
    /// for three frames and then jumps three frames' worth. That is the
    /// stutter -- <i>ça saccade</i> -- and it is not lost packets or a slow
    /// machine. It is a 60 Hz stream being played back at the rate it arrived
    /// rather than the rate it was made.
    ///
    /// <b>The fix is the standard one and it is a clock, not a filter.</b> The
    /// positions are buffered and read back on a clock of this client's own
    /// that ticks once per simulation frame, held a few frames behind the
    /// newest snapshot so there is always something on both sides of the read
    /// point to interpolate between. Late packets have somewhere to land; a
    /// lost one is covered by the two either side of it. Nothing is
    /// extrapolated, ever: guessing forward from the last known position puts
    /// a player through a wall and then snaps them back, which is a worse
    /// artefact than the one being removed.
    ///
    /// <b>Why it does not cost hit registration, which is the thing to be
    /// careful of here.</b> Smoothing moves the puppet a shooter is aiming at
    /// away from the position the authority has filed under that frame -- and
    /// the authority's rewind puts everybody back to a frame, so a blend of
    /// three frames is a shot resolved against none of them. The answer is
    /// that the read point is a *number*, so it can be sent: the intent
    /// carries <see cref="IntentPacket.AckFrame"/> and, new in protocol 7,
    /// <see cref="IntentPacket.AckSubFrame"/>, and the authority interpolates
    /// its own history between the same two frames by the same fraction. The
    /// shooter and the authority are then looking at exactly the same world
    /// again -- more exactly than before, since the old integer ack was itself
    /// a rounding of up to a frame.
    ///
    /// That is also why this is not a render-only effect. Drawing a smoothed
    /// puppet while collision ran against an unsmoothed one would put the
    /// hitbox somewhere the player is not, which is the oldest mistake in
    /// netcode. The smoothed position *is* the position -- the model, the
    /// hitbox, the shadow and the shot all use it.
    ///
    /// <b>What it costs.</b> The delay, added to the rewind depth the
    /// authority is asked for. That is the trade: a few frames more rewind, in
    /// exchange for opponents who move. The rewind ceiling was raised with
    /// room for it (<see cref="NetUnlagged.DefaultMaxRewindFrames"/>).
    /// </summary>
    public static class NetSmoothing
    {
        private const int Slots = PlayerEntity.SlotCapacity;

        /// <summary>
        /// Whether puppets are interpolated at all. Off restores exactly what
        /// protocol 6 did -- every snapshot written straight onto the puppet,
        /// and an integer ack naming it -- which is the control arm.
        /// <c>-nointerp</c>.
        /// </summary>
        public static bool Enabled { get; set; } = true;

        /// <summary>
        /// How many frames of positions are kept per slot. Deep enough to
        /// cover the largest delay plus a run of losses, and a power of two so
        /// the index is a mask.
        /// </summary>
        private const int HistoryFrames = 64;

        /// <summary>
        /// The shortest the read point is ever held behind the newest
        /// snapshot.
        ///
        /// Two frames, not one. One is the smallest number that has anything
        /// on both sides of the read point when every snapshot arrives -- and
        /// on a line where every snapshot arrives there was nothing to smooth
        /// in the first place. Two survives a single dropped datagram without
        /// the read point running off the end of what has arrived, which is
        /// the case this is actually for.
        /// </summary>
        public const int MinDelayFrames = 2;

        /// <summary>
        /// The longest.
        ///
        /// Every frame of buffer is a frame of rewind the authority is asked
        /// for on top of the round trip, and a rewind is a shot resolved
        /// against a world that much older -- which is fair to everybody and
        /// still not free, because it is how far back somebody can be shot
        /// after breaking line of sight. Eight frames is 133 ms, which covers
        /// a burst of four or five lost datagrams; a line that wants more than
        /// that is one where a player is going to notice something whatever is
        /// done here, and paying for it in rewind depth is the wrong place.
        /// </summary>
        public const int MaxDelayFrames = 8;

        /// <summary>
        /// How far behind the newest snapshot the read point currently sits.
        /// Grown when the buffer runs dry and allowed to shrink again once it
        /// has not for a while, so a line that goes bad and then recovers does
        /// not keep paying for it.
        /// </summary>
        public static int Delay { get; private set; } = MinDelayFrames;

        /// <summary>
        /// How long the line must behave before the delay is allowed to come
        /// back down by one frame. Four seconds: long enough that a single
        /// good patch does not undo a delay a bad line just earned, short
        /// enough that a player whose connection recovers gets their latency
        /// back inside the same fight rather than at the end of the match.
        ///
        /// It was ten seconds, and that was measured as too long in the wrong
        /// direction: with a growth of one frame per starved *frame*, a single
        /// burst of loss took the delay to its ceiling and it sat there for
        /// two minutes, adding 200 ms to every rewind in the match for a
        /// hiccup that lasted a tenth of a second. See
        /// <see cref="GrowCooldownFrames"/>, which is the other half of that
        /// fix and the more important one.
        /// </summary>
        private const int ShrinkAfterFrames = 240;

        /// <summary>
        /// The least time between two increases of the delay.
        ///
        /// A run of starved frames is <i>one</i> event -- a datagram that did
        /// not arrive, or four -- and growing once per frame of it read a
        /// tenth of a second of loss as ten separate reasons to buffer more.
        /// Half a second between rises means the buffer answers the shape of
        /// the line rather than the length of one gap in it.
        /// </summary>
        private const int GrowCooldownFrames = 30;
        private static int _sinceGrew;

        /// <summary>
        /// Where the read point is, in the authority's frame numbers, as a
        /// fraction. It advances one per simulation frame and is steered --
        /// gently, or snapped when it is hopeless -- toward
        /// <c>newest - Delay</c>.
        /// </summary>
        private static double _readFrame;
        private static bool _running;
        private static int _sinceStarved;

        /// <summary>
        /// How hard the read point is pulled back onto its target each frame.
        ///
        /// This is the one number that decides whether the cure is visible. A
        /// playout clock that corrects instantly is not a clock, it is the
        /// snapping this file exists to remove; one that corrects too slowly
        /// drifts and then has to snap anyway. A twentieth of the error per
        /// frame closes half of any gap in fourteen frames and all of it in
        /// under a second, at a speed-up of at most 5% -- which is under what
        /// an eye reads as motion being wrong.
        /// </summary>
        private const double Correction = 0.05;

        /// <summary>
        /// Past this the read point is put where it belongs rather than walked
        /// there. Ten frames of drift is a stall, a rejoin or a rotation, not
        /// jitter, and gliding across it would take four seconds of everybody
        /// moving at the wrong speed.
        /// </summary>
        private const double SnapError = 10.0;

        /// <summary>Frames the buffer had nothing to interpolate between.</summary>
        public static long Starved { get; private set; }
        /// <summary>Samples served by interpolating, and by holding a position.</summary>
        public static long Interpolated { get; private set; }
        public static long Held { get; private set; }
        /// <summary>Read points put back rather than walked back.</summary>
        public static long Snaps { get; private set; }
        /// <summary>
        /// How far the puppets are moved per frame, summed, and the worst
        /// single step. The stutter measured rather than described: a stream
        /// played back at the rate it arrived has most of its steps at zero
        /// and a few at three times the mean, and one played back on a clock
        /// does not.
        /// </summary>
        public static long Steps { get; private set; }
        public static double StepSum { get; private set; }
        public static float WorstStep { get; private set; }
        /// <summary>Frames in which a puppet did not move at all, and the run of them.</summary>
        public static long StalledFrames { get; private set; }
        public static int WorstStall { get; private set; }
        private static readonly int[] _stallRun = new int[Slots];

        // The ring: one stamp per frame covers every slot, because a snapshot
        // carries all of them at once. The same shape NetUnlagged's history
        // has, and for the same reason.
        private static readonly ushort[,] _life = new ushort[Slots, HistoryFrames];
        private static readonly ushort[,] _generation = new ushort[Slots, HistoryFrames];

        public static void ResetSlot(int slot)
        {
            if (slot < 0 || slot >= Slots) return;
            for (int i = 0; i < HistoryFrames; i++)
            {
                _live[slot, i] = false;
                _life[slot, i] = 0;
                _generation[slot, i] = 0;
            }
            _sampledSeen[slot] = false;
            _stallRun[slot] = 0;
        }

        private static readonly Vector3[,] _position = new Vector3[Slots, HistoryFrames];
        private static readonly bool[,] _altForm = new bool[Slots, HistoryFrames];
        private static readonly bool[,] _live = new bool[Slots, HistoryFrames];
        private static readonly uint[] _stamp = new uint[HistoryFrames];
        private static uint _newest;
        private static readonly Vector3[] _lastSampled = new Vector3[Slots];
        private static readonly bool[] _sampledSeen = new bool[Slots];

        /// <summary>
        /// Whether this machine both wants and can interpolate: it is reading
        /// somebody else's snapshots, and it has some.
        /// </summary>
        public static bool Active => Enabled && NetSession.Active
            && !NetSession.IsAuthority && !NetSession.IsHost && _running;

        /// <summary>
        /// File a snapshot's positions under the frame it names.
        ///
        /// Called once per applied snapshot, from the same place the states
        /// are handed to the players -- so what is buffered is exactly what
        /// the authority said, and exactly what its own rewind history holds
        /// under that number. A snapshot older than one already filed is
        /// dropped: the ordering guard upstream normally catches those, and a
        /// stale one written into the ring is a puppet interpolating
        /// backwards.
        /// </summary>
        public static void Record(uint frame, ReadOnlySpan<PlayerState> states)
        {
            if (!Enabled || frame == 0)
            {
                return;
            }
            // Only an explicit stream change resets this clock.
            if (_running && !NetLifecycleTracker.Newer(frame, _newest)) return;
            int index = (int)(frame % HistoryFrames);
            _stamp[index] = frame;
            for (int i = 0; i < Slots; i++)
            {
                _live[i, index] = false;
            }
            for (int i = 0; i < states.Length; i++)
            {
                int slot = states[i].SlotIndex;
                if (slot < 0 || slot >= Slots)
                {
                    continue;
                }
                bool inPlay = (states[i].Flags & PlayerState.FlagActive) != 0
                    && (states[i].Flags & PlayerState.FlagSpawned) != 0
                    && states[i].Health > 0;
                _life[slot, index] = states[i].LifeId;
                _generation[slot, index] = states[i].SlotGeneration;
                _live[slot, index] = inPlay;
                _position[slot, index] = states[i].Position;
                _altForm[slot, index] = (states[i].Flags & PlayerState.FlagAltForm) != 0;
            }
            if (!_running || frame > _newest)
            {
                _newest = frame;
            }
            if (!_running)
            {
                _running = true;
                _readFrame = frame > (uint)Delay ? frame - Delay : frame;
            }
        }

        /// <summary>
        /// Throw the buffer away and re-base on <paramref name="frame"/>. The
        /// cell for that frame is written by the caller immediately after.
        /// </summary>
        private static void Restart(uint frame)
        {
            Array.Clear(_stamp);
            Array.Clear(_live);
            Array.Clear(_sampledSeen);
            Array.Clear(_stallRun);
            _newest = 0;
            _running = false;
            _sinceStarved = 0;
            _sinceGrew = GrowCooldownFrames;
            NetLog.Event($"playout clock re-based on frame {frame}");
        }

        /// <summary>
        /// Advance the read point one simulation frame and steer it onto its
        /// target. One call a frame, beside the other network hooks.
        /// </summary>
        public static void Tick()
        {
            if (!Enabled || !NetSession.Active || !_running)
            {
                return;
            }
            _readFrame += 1.0;
            double target = (double)_newest - Delay;
            double error = target - _readFrame;
            if (Math.Abs(error) > SnapError)
            {
                // A stall, a rejoin, a rotation, a counter that restarted.
                // Walking across ten frames of error would be four seconds of
                // everybody moving at the wrong speed, which is a worse thing
                // to look at than one jump.
                _readFrame = target;
                Snaps++;
                NetTimingDiagnostics.Correction();
            }
            else
            {
                _readFrame += error * Correction;
            }
            if (_readFrame > _newest)
            {
                // Past everything that has arrived. The read point is pinned
                // rather than allowed to run on, because the alternative is
                // extrapolating -- and a guessed position puts a player
                // through a wall and then snaps them out of it.
                _readFrame = _newest;
                Starved++;
                _sinceStarved = 0;
                if (Delay < MaxDelayFrames && _sinceGrew >= GrowCooldownFrames)
                {
                    Delay++;
                    _sinceGrew = 0;
                }
            }
            else if (++_sinceStarved > ShrinkAfterFrames && Delay > MinDelayFrames)
            {
                Delay--;
                _sinceStarved = 0;
            }
            _sinceGrew++;
        }

        /// <summary>
        /// Where <paramref name="slot"/> should be drawn and shot at this
        /// frame, or false when the buffer cannot say -- in which case the
        /// caller does what it always did and uses the snapshot raw.
        ///
        /// The two frames either side of the read point must both hold that
        /// player alive, because interpolating between a position and a death
        /// -- or between two lives either side of a respawn -- draws a body
        /// sliding across the room to its spawn point.
        /// </summary>
        public static bool Sample(int slot, out Vector3 position, out bool altForm)
        {
            position = Vector3.Zero;
            altForm = false;
            if (!Active || slot < 0 || slot >= Slots)
            {
                return false;
            }
            uint lower = (uint)Math.Floor(_readFrame);
            float fraction = (float)(_readFrame - lower);
            if (!Lookup(slot, lower, out Vector3 a, out bool altA))
            {
                return false;
            }
            altForm = altA;
            if (fraction <= 0.0001f || !Lookup(slot, lower + 1, out Vector3 b, out bool altB)
                || altA != altB)
            {
                // Nothing on the far side, or the player changed form between
                // the two -- a biped's position and a morph ball's are
                // measured from different centres, so blending them slides the
                // model half a body. Hold the near one.
                position = a;
                Held++;
                NoteStep(slot, position);
                return true;
            }
            Vector3 travel = b - a;
            if (travel.LengthSquared > SnapDistance * SnapDistance)
            {
                // A teleporter, a respawn, a jump pad's launch frame -- not
                // something to slide across.
                //
                // **The near side, not the far one**, and that is the whole of
                // why this branch is written out rather than folded into the
                // one above. `NetUnlagged.Reconcile` makes the same three
                // refusals at the other end and falls back to the near side in
                // all three; a client drawing the far side here would be
                // aiming at a body the authority rewinds to the *other* end of
                // a teleport. One frame of the jump shown late costs nothing
                // and is what keeps the two worlds the same world.
                position = a;
                Held++;
                NoteStep(slot, position);
                return true;
            }
            position = a + travel * fraction;
            Interpolated++;
            NoteStep(slot, position);
            return true;
        }

        /// <summary>
        /// How far two consecutive snapshots may put a player apart and still
        /// be the same movement. Boost is the fastest anything travels, at 0.6
        /// units a frame; four units is several frames of it and well short of
        /// any teleport in the game.
        /// </summary>
        private const float SnapDistance = 4.0f;

        private static bool Lookup(int slot, uint frame, out Vector3 position, out bool altForm)
        {
            position = Vector3.Zero;
            altForm = false;
            if (frame == 0 || frame > _newest)
            {
                return false;
            }
            int index = (int)(frame % HistoryFrames);
            if (_stamp[index] != frame || !_live[slot, index]
                || !NetPlayerLifecycle.Matches(slot, _generation[slot, index], _life[slot, index]))
            {
                return false;
            }
            position = _position[slot, index];
            altForm = _altForm[slot, index];
            return Single.IsFinite(position.X) && Single.IsFinite(position.Y)
                && Single.IsFinite(position.Z);
        }

        /// <summary>
        /// How far this puppet moved since the last frame it was sampled.
        ///
        /// The measurement the whole feature is judged on, and the one a
        /// screenshot cannot give: a stream written straight from its
        /// arrivals has most of its steps at zero and the rest at two or three
        /// times the mean, while one read off a clock has them all near the
        /// mean. <c>WorstStall</c> is the longest run of frames a player did
        /// not move at all, which is the stutter a player actually sees.
        /// </summary>
        private static void NoteStep(int slot, Vector3 position)
        {
            if (_sampledSeen[slot])
            {
                float step = (position - _lastSampled[slot]).Length;
                if (Single.IsFinite(step))
                {
                    Steps++;
                    StepSum += step;
                    if (step > WorstStep)
                    {
                        WorstStep = step;
                    }
                    if (step < 0.0005f)
                    {
                        StalledFrames++;
                        _stallRun[slot]++;
                        if (_stallRun[slot] > WorstStall)
                        {
                            WorstStall = _stallRun[slot];
                        }
                    }
                    else
                    {
                        _stallRun[slot] = 0;
                    }
                }
            }
            _lastSampled[slot] = position;
            _sampledSeen[slot] = true;
        }

        /// <summary>
        /// The frame the shooter's world is at, for
        /// <see cref="IntentPacket.AckFrame"/>, and the fraction past it for
        /// <see cref="IntentPacket.AckSubFrame"/>.
        ///
        /// This is the whole reason the smoothing is safe: the read point is a
        /// number, so it can be sent, and the authority rewinds to exactly it.
        /// </summary>
        public static bool AckPoint(out uint frame, out byte subFrame)
        {
            frame = 0;
            subFrame = 0;
            if (!Active)
            {
                return false;
            }
            uint lower = (uint)Math.Floor(_readFrame);
            if (lower == 0)
            {
                return false;
            }
            frame = lower;
            subFrame = (byte)Math.Clamp((int)((_readFrame - lower) * 256.0), 0, 255);
            return true;
        }

        public static void Reset()
        {
            Array.Clear(_stamp);
            Array.Clear(_live);
            Array.Clear(_sampledSeen);
            Array.Clear(_stallRun);
            _newest = 0;
            _readFrame = 0;
            _running = false;
            _sinceStarved = 0;
            _sinceGrew = GrowCooldownFrames;
            Delay = MinDelayFrames;
            Starved = 0;
            Interpolated = 0;
            Held = 0;
            Snaps = 0;
            Steps = 0;
            StepSum = 0;
            WorstStep = 0;
            StalledFrames = 0;
            WorstStall = 0;
        }

        /// <summary>
        /// Everything the buffer holds describes a room that is going away.
        /// The read point goes with it: a rotation restarts the frame counter
        /// on some paths and keeps it on others, and a read point left
        /// pointing into the old match interpolates one room's positions into
        /// another's.
        /// </summary>
        public static void NoteRoomChanged()
        {
            Array.Clear(_stamp);
            Array.Clear(_live);
            Array.Clear(_sampledSeen);
            Array.Clear(_stallRun);
            _newest = 0;
            _readFrame = 0;
            _running = false;
            _sinceGrew = GrowCooldownFrames;
        }

        public static string? Describe()
        {
            if (Steps == 0)
            {
                return null;
            }
            double mean = StepSum / Steps;
            double stalled = 100.0 * StalledFrames / Steps;
            return $"puppet smoothing: {(Enabled ? $"on, {Delay} frames of buffer" : "off")}, "
                + $"{Interpolated} interpolated / {Held} held, {Starved} starved, "
                + $"{Snaps} clock snaps; steps mean {mean:F4} units, worst {WorstStep:F3}, "
                + $"{stalled:F1}% of frames still (longest run {WorstStall})";
        }
    }
}
