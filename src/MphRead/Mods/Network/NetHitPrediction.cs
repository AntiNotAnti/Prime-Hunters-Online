using System;
using MphRead.Entities;

namespace MphRead.Mods.Network
{
    /// <summary>
    /// Hits that land the instant they are fired, on the machine that fired
    /// them.
    ///
    /// Lag compensation put the *authority's* answer where the shooter aimed;
    /// it did nothing about when that answer arrives. A client shoots, the
    /// intent goes upstream, the authority resolves it, the snapshot comes
    /// back: the hit is correct and a full round trip late, so at 150 ms a
    /// player empties a clip into somebody who does not flinch until a fifth
    /// of a second after each shot. The authority itself never had this --
    /// its own gun resolves in the frame it is fired -- which is exactly the
    /// asymmetry this removes. Everyone who is not the authority resolves
    /// their own shots locally, now, and the authority's answer arrives later
    /// to confirm or overrule it.
    ///
    /// <b>Lag compensation is what makes this sound.</b> The authority rewinds
    /// every other player to the snapshot frame the shooter had applied --
    /// which is the world the shooter's own machine is holding when it fires.
    /// The local resolution and the authority's are therefore the same test
    /// against the same positions, and they agree except for a frame of skew
    /// and for the things a client cannot know (invulnerability windows the
    /// authority has already opened, a victim killed by somebody else in the
    /// meantime). Without <see cref="NetUnlagged"/> underneath it this would
    /// mispredict exactly as often as the shots used to miss.
    ///
    /// Two rules keep a prediction from becoming a lie:
    ///
    /// <list type="number">
    /// <item><b>A prediction never scores and never ends a match.</b> The
    /// scoreboard is assigned from the snapshot for every slot, so a point
    /// awarded by a predicted kill is overwritten by the authority's answer
    /// within a snapshot either way; and <c>EndIfPointGoalReached</c> is
    /// already refused on a machine that is not keeping the score, by
    /// <c>NetMatchEnd.MayEndOnScore</c>. What the death path is allowed to do
    /// here is the part a player is waiting for -- the body drops, the banner
    /// says who it was, the mark lands.</item>
    /// <item><b>A prediction is only ever your own shot on somebody else.</b>
    /// Incoming damage is not predicted. Whether you were hit is a question
    /// about a shot fired on another machine, aimed at a copy of you that
    /// machine is holding, and this one has no better guess at it than the
    /// authority's -- it has a worse one. The one thing predicted *onto* this
    /// machine's own player is the health its own Shock Coil drains out of
    /// somebody else, which is not a guess about anybody else's input.</item>
    /// </list>
    ///
    /// <para>
    /// <b>What is held, and for how long.</b> Predicting a hit and then
    /// letting the next snapshot assign the authority's health straight over
    /// it is a prediction that lasts one frame: the flinch is instant and the
    /// bar springs back up, which is the thing "it is not registering" is
    /// actually describing. So a prediction is held -- the victim's health is
    /// the authority's number minus whatever this machine has predicted and
    /// not yet had confirmed, and a victim predicted dead stays down rather
    /// than being respawned by a snapshot that has not heard about it yet.
    /// The hold lasts one measured round trip and a margin
    /// (<see cref="HoldFrames"/>), never the two seconds a prediction is kept
    /// for the statistics: a mispredicted hit is a wrong health bar, and a
    /// wrong health bar has to expire in the time it takes the authority to
    /// answer rather than in the time it takes to be sure it never will.
    /// </para>
    ///
    /// What the prediction is reconciled against is <see cref="NetDamage"/>'s
    /// existing replay. A hit the authority confirms for a victim this
    /// machine already predicted is consumed and *not* shown a second time;
    /// one it never confirms expires quietly, and the health the snapshot
    /// carries -- which is applied every frame anyway -- puts the victim back
    /// where the authority says. Nothing has to be rolled back, because
    /// nothing durable is ever written: health is assigned from the snapshot,
    /// the score is assigned from the snapshot, and a wrongly killed puppet is
    /// put back on the map by the same branch that spawns everybody else.
    /// </summary>
    public static class NetHitPrediction
    {
        private const int Slots = PlayerEntity.SlotCapacity;

        /// <summary>
        /// Off with <c>-nohitprediction</c>, which is the control: the same
        /// match measured with hits resolving locally and with them waiting
        /// for the authority is the only way to say what this is worth. On by
        /// default, as <see cref="NetUnlagged.Enabled"/> is.
        /// </summary>
        public static bool Enabled { get; set; } = true;

        /// <summary>
        /// The mark over the crosshair. Separate from the prediction because
        /// it is not part of it: the authority draws the same mark for the
        /// same hits, and so does an offline match, where there is nothing to
        /// predict and the confirmation is simply true. Off with
        /// <c>-nohitmarker</c>.
        /// </summary>
        public static bool MarkerEnabled { get; set; } = true;

        /// <summary>
        /// True on a machine whose own damage resolution is a guess rather
        /// than the answer -- a client that is not the authority, outside a
        /// replay of the authority's own hit.
        ///
        /// This is the same condition <see cref="NetDamage.Suppress"/> used to
        /// throw the hit away under, and it is still what decides whether a
        /// hit counts: <see cref="NetDamage.Note"/> records nothing here, so
        /// the diagnostics still describe one machine's resolution and not
        /// eight machines' opinions of it.
        /// </summary>
        public static bool Predicting => NetSession.Active
            && !NetSession.IsHost && !NetSession.IsAuthority && !NetDamage.Replaying;

        /// <summary>
        /// How long a prediction waits for the authority to agree with it.
        ///
        /// Generous on purpose: it has to cover the round trip, the gap
        /// between snapshots, and the rewind depth the authority may have
        /// applied on top. Anything still outstanding after two seconds was
        /// not a slow confirmation, it was a miss.
        /// </summary>
        private const int PendingFrames = 120;

        /// <summary>
        /// Predictions outstanding for one victim at once. A burst of Judicator
        /// shots or a Battlehammer stream can put several in the air before
        /// the first is answered; beyond this the oldest is dropped, since a
        /// prediction nobody has confirmed in two dozen hits is not going to
        /// be.
        ///
        /// Two dozen rather than the eight this started at, because the Shock
        /// Coil resolves a hit every other frame for as long as the trigger is
        /// held: at 300 ms of round trip that is nine outstanding before the
        /// first answer arrives, and an overflow does not merely lose a
        /// statistic any more -- it drops that hit's damage out of the health
        /// the victim is being held at.
        /// </summary>
        private const int PendingCapacity = 24;

        private static readonly uint[,] _pendingFrame = new uint[Slots, PendingCapacity];

        /// <summary>
        /// What each outstanding prediction took off that victim, so the bar
        /// can be drawn at the authority's health minus what this machine has
        /// already landed on them. See <see cref="HealthFor"/>.
        /// </summary>
        private static readonly int[,] _pendingDamage = new int[Slots, PendingCapacity];

        /// <summary>Whether that prediction was the one that killed them here.</summary>
        private static readonly bool[,] _pendingLethal = new bool[Slots, PendingCapacity];

        /// <summary>
        /// Whether this machine resolved that hit as a headshot.
        ///
        /// The question <see cref="Confirmed"/> cannot answer and the one the
        /// Imperialist is played on. A headshot is decided by a single
        /// comparison in <c>BeamProjectileEntity</c> -- the impact point must
        /// be at least 0.8 units above the victim's position, on a body 1.6
        /// units tall -- so the band is 0.3 units, the top eighth of a hunter.
        /// A rewind that lands the victim a frame out vertically, which is
        /// what a jump pad does in a frame, turns a headshot into a body shot
        /// while leaving the *hit* perfectly confirmed. So a client whose
        /// every prediction is confirmed can still be a client whose every
        /// instant kill was refused, and nothing here would have said so.
        /// </summary>
        private static readonly bool[,] _pendingHeadshot = new bool[Slots, PendingCapacity];

        /// <summary>
        /// The weapon behind each outstanding prediction, so the tally can be
        /// read a weapon at a time. <see cref="BeamType.None"/> -- an alt
        /// form's ram, a scythe, a bomb, the void -- is kept as
        /// <see cref="AltBeam"/> rather than dropped: half the complaints
        /// about prediction are about alt form, and a bucket that silently
        /// discarded them could not answer any of them.
        /// </summary>
        private static readonly byte[,] _pendingBeam = new byte[Slots, PendingCapacity];

        /// <summary>
        /// The hit claim this prediction was declared under, or zero when
        /// there is none (claims off, or a hit on this machine's own player).
        ///
        /// <b>This is what retires a prediction exactly.</b> The snapshot
        /// cannot: it carries a count of hits on a victim and the slot of only
        /// the *last* attacker, so a hit of this machine's own that was
        /// followed, inside one snapshot window, by somebody else's is never
        /// matched -- <see cref="Confirm"/> is not even called. The debit then
        /// stays on the books while the authority's own number already has it,
        /// and the victim is drawn at the authority's health minus a hit the
        /// authority has already taken off. Two or three of those and a victim
        /// who is comfortably alive is drawn on one point of health, which is
        /// where the next shot predicts a kill that nobody else sees. Reported
        /// as "my client thinks three missiles killed him": three uncharged
        /// missiles are 96 of a hunter's 99, so three points of stale debit
        /// are the whole of the error.
        ///
        /// A verdict names the claim, and the claim names the hit. See
        /// <see cref="Settle"/>.
        /// </summary>
        private static readonly ushort[,] _pendingClaim = new ushort[Slots, PendingCapacity];

        /// <summary>
        /// Whether that entry has already been answered by name -- a verdict
        /// naming its claim. It stays in the ring until the head reaches it,
        /// because the entries in front of it are still outstanding, but it
        /// owes nothing and must not be counted again.
        /// </summary>
        private static readonly bool[,] _pendingSpent = new bool[Slots, PendingCapacity];

        // A travelling projectile is often resolved by the authority before
        // the shooter's local copy lands. Its authoritative health already
        // includes the hit, so charging the local prediction debit again
        // would double-subtract the same damage from the displayed bar.
        private static readonly bool[,] _pendingTravelled = new bool[Slots, PendingCapacity];

        /// <summary>
        /// Whether that entry is a hit on this machine's own player -- its own
        /// splash, a fall. Kept out of the per-weapon tally for the same
        /// reason <see cref="SelfPredicted"/> is kept out of
        /// <see cref="Predicted"/>: it is not the same claim. There is no
        /// other machine's opinion to be wrong about, so counting it beside
        /// the shots that crossed a wire would flatter every weapon that can
        /// splash.
        /// </summary>
        private static readonly bool[,] _pendingSelf = new bool[Slots, PendingCapacity];

        /// <summary>
        /// Predictions already retired by a verdict, waiting for the snapshot
        /// that reports the same hits to arrive and be absorbed.
        ///
        /// The verdict and the snapshot describe the same hit and race each
        /// other. Whichever loses must not be counted as a second event:
        /// without this a claim settled by its verdict left nothing pending,
        /// and the snapshot a frame later read as "the authority credited a
        /// hit this machine never predicted" -- <see cref="Unpredicted"/>
        /// climbing to roughly the size of <see cref="Confirmed"/> and the
        /// report becoming unreadable. Small, because the two are never more
        /// than a snapshot apart.
        /// </summary>
        private static readonly int[] _settledCredit = new int[Slots];
        private static readonly uint[] _settledFrame = new uint[Slots];
        private const int SettledCreditMax = 8;

        private static readonly int[] _pendingCount = new int[Slots];
        private static readonly int[] _pendingHead = new int[Slots];

        /// <summary>
        /// The health this machine last drew for a slot, and the frame it last
        /// predicted a hit on them: together, the promise that a bar this
        /// machine has already taken down does not go back up while it is
        /// still shooting.
        ///
        /// The bar going up is what the Shock Coil made impossible to miss.
        /// Releasing the trigger put a chunk of the victim's health back,
        /// visibly, on the shooter's screen -- and it was not a mispredicted
        /// hit. <see cref="Confirm"/> retires as many predictions as the
        /// snapshot says landed, and the authority resolves several hits of a
        /// continuous beam for every one this machine resolves (the damage is
        /// divided by 32 and dithered off the frame counter, so the parity
        /// that produces a damaging hit is not the same parity on two
        /// machines). One snapshot therefore retires *everything* outstanding,
        /// the debit falls to nothing, and what is drawn is the authority's
        /// number -- which is correct, and half a round trip behind what this
        /// machine has already shown. While the trigger is held the next hit
        /// covers it; the moment it is released nothing does, and the bar
        /// climbs back by exactly the drain of the last half round trip.
        ///
        /// So the number is floored by what was last drawn, for as long as
        /// this machine is still predicting hits on that slot. It cannot hide
        /// damage -- the floor only ever refuses a *rise* -- and it costs at
        /// most one hold window of lag on a victim who picks up health while
        /// being shot at.
        /// </summary>
        private static readonly int[] _shownHealth = new int[Slots];
        private static readonly uint[] _predictedFrame = new uint[Slots];

        /// <summary>
        /// The authority's health for each slot as of the last snapshot, so a
        /// <b>rise</b> in it can be recognised.
        ///
        /// This is what the floor above must never refuse. A victim who picks
        /// up health, or respawns, is a victim whose bar has gone up for a
        /// reason that has nothing to do with this machine's predictions --
        /// and the floor, which is armed again by every hit predicted on that
        /// slot, will hold the old low number for as long as the shooting
        /// lasts. Measured against the Japan server at 257 ms: the drawn bar
        /// sat a mean 26 points and a worst **61** below the authority's, and
        /// every one of those points was the floor. On a continuous weapon it
        /// never lifts at all, because a Shock Coil predicts a hit every other
        /// frame.
        /// </summary>
        private static readonly int[] _lastAuthorityHealth = new int[Slots];

        /// <summary>How often a rise in the authority's number lifted the floor.</summary>
        public static long FloorLifted { get; private set; }

        /// <summary>
        /// Points of damage this machine predicted onto each slot, against the
        /// points the authority's own number actually came down by.
        ///
        /// <b>The direct answer to "is it counted twice".</b> Every other line
        /// here counts events -- hits predicted, hits confirmed -- and a hit
        /// counted once but worth twice as much looks perfect in all of them.
        /// These two are points, and with one client shooting (which is what
        /// <c>-hitrig missile</c> arranges) the authority's drop is this
        /// client's damage and nobody else's, so the ratio is the whole
        /// measurement.
        ///
        /// The authority's drop counts everything that hurt that player,
        /// including their own splash and the void, so in an ordinary match
        /// it is an upper bound rather than a comparison. Read it from a rig
        /// run.
        /// </summary>
        private static readonly long[] _predictedPoints = new long[Slots];
        private static readonly long[] _authorityDrop = new long[Slots];

        /// <summary>
        /// How long a prediction is allowed to hold the picture: one measured
        /// round trip, a snapshot's gap, and a margin.
        ///
        /// Not <see cref="PendingFrames"/>, which is how long a prediction is
        /// kept for the *statistics* -- two seconds, deliberately generous,
        /// because a confirmation that arrives late is still a confirmation
        /// and counting it as a miss would flatter nothing. Holding a health
        /// bar for two seconds is a different proposition: a hit that was
        /// wrong is a health bar that is wrong, and it has to right itself in
        /// about the time the authority takes to answer rather than in the
        /// time it takes to be certain it never will.
        ///
        /// The ping is the server's own measurement of this client's round
        /// trip (see <see cref="NetSession.SlotPing"/>), which is zero until
        /// it has one -- and the clamp is what makes that read as "assume a
        /// quarter of a second" rather than as "hold nothing".
        /// </summary>
        private static int HoldFrames
        {
            get
            {
                int slot = NetHooks.LocalSlot;
                int ping = slot >= 0 && slot < NetSession.SlotPing.Length
                    ? NetSession.SlotPing[slot]
                    : 0;
                // 60 Hz of simulation, plus twelve frames for the gap between
                // snapshots and the rewind the authority may have applied.
                //
                // Plus the grace a hit claim waits out on the authority
                // (NetHitClaims.GraceFrames) whenever claims are live at all.
                // A rescued hit is applied at the end of that window rather
                // than on arrival, so its confirmation is one grace later than
                // an ordinary one -- and a hold that expires first is the
                // resurrection the claim exists to stop, reintroduced by the
                // clock rather than by the authority disagreeing.
                int grace = NetHitClaims.Claiming ? NetHitClaims.GraceFrames : 0;
                return Math.Clamp((int)(ping * 0.06f) + 12 + grace, 15, 120);
            }
        }

        /// <summary>
        /// Health this machine's own player has drained out of somebody else
        /// and not yet been told about, as (frame, amount).
        ///
        /// The Shock Coil takes what it deals and gives it to the shooter, and
        /// that is the one heal a client can work out for itself: it is the
        /// arithmetic of a hit this machine has already resolved, not a guess
        /// about anybody's input. Without it the beam was the one weapon in
        /// the game whose whole point arrived a round trip late -- the victim
        /// flinched instantly, courtesy of the prediction, and the health it
        /// bought did not turn up until the authority said so.
        ///
        /// Kept as a credit on top of the authority's number rather than as an
        /// absolute health, so damage taken while draining still shows the
        /// moment the authority reports it. A stale credit is worth a point or
        /// two for a fraction of a second; a stale absolute would hide a
        /// rocket.
        /// </summary>
        private const int HealCapacity = 48;
        private static readonly uint[] _healFrame = new uint[HealCapacity];
        private static readonly int[] _healAmount = new int[HealCapacity];
        private static int _healCount;
        private static int _healHead;

        /// <summary>
        /// The bucket everything without a beam behind it is counted in: an
        /// alt form's ram, Weavel's scythe, Spire's spin, a bomb, the void.
        /// One past the last real <see cref="BeamType"/>.
        /// </summary>
        public const int AltBeam = 10;
        private const int BeamBuckets = AltBeam + 1;

        /// <summary>
        /// The same tally as <see cref="Predicted"/> and the rest, a weapon at
        /// a time -- and the reason it exists is that the aggregate cannot
        /// answer the question anybody actually asks. "The prediction is
        /// wrong" is never a statement about prediction; it is a statement
        /// about one weapon, or about alt form, and a 95% confirmed rate
        /// across a match is perfectly compatible with every charged Power
        /// Beam shot in it being resolved at a different number on the
        /// authority. <see cref="DescribeByWeapon"/> prints it.
        /// </summary>
        private static readonly long[] _beamPredicted = new long[BeamBuckets];
        private static readonly long[] _beamConfirmed = new long[BeamBuckets];
        private static readonly long[] _beamDenied = new long[BeamBuckets];
        private static readonly long[] _beamLethal = new long[BeamBuckets];
        private static readonly long[] _beamUndone = new long[BeamBuckets];
        private static readonly long[] _beamDamage = new long[BeamBuckets];

        private static int Bucket(BeamType beam)
        {
            int index = (int)beam;
            return index < 0 || index >= AltBeam ? AltBeam : index;
        }

        private static string BucketName(int bucket)
        {
            return bucket == AltBeam ? "alt/bomb" : ((BeamType)bucket).ToString();
        }

        /// <summary>Hits this machine resolved for itself, before being told.</summary>
        public static long Predicted { get; private set; }

        /// <summary>Predictions the authority went on to agree with.</summary>
        public static long Confirmed { get; private set; }

        /// <summary>
        /// Predictions the authority never confirmed: a shot that connected
        /// here and nowhere else. The number that says whether this is worth
        /// having -- a few per cent is the frame of skew, a large share means
        /// the local world and the rewound one are not the same world.
        /// </summary>
        public static long Denied { get; private set; }

        /// <summary>
        /// Hits the authority credited this machine with that it had not
        /// predicted -- the opposite error, and the one that costs nothing:
        /// it is shown when it arrives, which is what every hit used to do.
        /// </summary>
        public static long Unpredicted { get; private set; }

        /// <summary>
        /// Authority-confirmed hits this client did not predict, split by
        /// whether the local shooter was moving. This preserves upstream's
        /// diagnostic for spotting divergence between local projectile flight
        /// and the authority's rewound simulation.
        /// </summary>
        public static long UnpredictedMoving { get; private set; }
        public static long UnpredictedStill { get; private set; }

        private static bool MovingNow()
        {
            int local = NetHooks.LocalSlot;
            return local >= 0 && local < PlayerEntity.Players.Count
                && PlayerEntity.Players[local].Speed.LengthSquared > 0.0004f;
        }

        /// <summary>
        /// Remote lethal hits held at one health so the authority owns the
        /// actual death. This is the current behavior; the old death-prediction
        /// switches are compatibility no-ops.
        /// </summary>
        public static long LethalHeld { get; private set; }

        /// <summary>Legacy diagnostic retained for report compatibility; remote deaths are not predicted.</summary>
        public static long DeathsPredicted { get; private set; }

        /// <summary>
        /// Deaths this machine's own player died on the frame it died them: a
        /// rocket jump at low health, a fall into the void, a crusher. Counted
        /// apart from <see cref="DeathsPredicted"/> because it is not the same
        /// claim -- there is no other machine's opinion to be wrong about.
        /// </summary>
        public static long SelfDeathsPredicted { get; private set; }

        /// <summary>
        /// Legacy diagnostic retained for old report readers. Remote-player
        /// deaths are authority-owned now, so normal current runs keep this at zero.
        /// </summary>
        public static long DeathsUndone { get; private set; }

        /// <summary>Health drained by this machine's own beam, ahead of the authority.</summary>
        public static long DrainPredicted { get; private set; }

        /// <summary>
        /// Headshots this machine resolved on somebody else, and what the
        /// authority said about the same hits when it answered.
        ///
        /// <see cref="HeadshotsDowngraded"/> is the number this whole exercise
        /// is about: the shooter saw a head hit, the authority agreed a hit
        /// landed and called it a body shot. On the Imperialist that is the
        /// difference between an instant kill and half a health bar, and it is
        /// invisible to <see cref="Confirmed"/>, which counts the hit and asks
        /// nothing about where it landed.
        ///
        /// <see cref="HeadshotsUpgraded"/> is the opposite error and is worth
        /// counting for the same reason a mispredicted hit is: it says the two
        /// machines disagree about the victim's height, not which way.
        /// </summary>
        public static long HeadshotsPredicted { get; private set; }
        public static long HeadshotsAgreed { get; private set; }
        public static long HeadshotsDowngraded { get; private set; }
        public static long HeadshotsUpgraded { get; private set; }

        /// <summary>
        /// Your own splash, on you, resolved the frame it went off -- the
        /// rocket jump. Counted apart from <see cref="Predicted"/> because it
        /// is not the same claim: source, target and input are all on this
        /// machine, so it is arithmetic rather than a bet on a rewind, and
        /// mixing the two would flatter the percentage that measures the bet.
        /// </summary>
        public static long SelfPredicted { get; private set; }

        /// <summary>Those of them the authority went on to agree with.</summary>
        public static long SelfConfirmed { get; private set; }

        /// <summary>Compatibility option: remote death always waits for authority.</summary>
        public static bool DeathEnabled { get => false; set { } }
        private static readonly bool[,] _pendingHeld = new bool[Slots, PendingCapacity];
        public static long LethalConfirmed { get; private set; }
        public static long LethalDenied { get; private set; }

        private static void ResolveHeld(int slot, int at, bool confirmed)
        {
            if (!_pendingHeld[slot, at]) return;
            _pendingHeld[slot, at] = false;
            if (confirmed) LethalConfirmed++; else LethalDenied++;
            if (NetLog.Enabled) NetLog.Event($"[predict] lethal {(confirmed ? "confirmed" : "denied")} "
                + $"slot={slot} generation={NetPlayerLifecycle.Generation(slot)} life={NetPlayerLifecycle.Get(slot)} "
                + $"frame={NetSession.NetFrame} {LifecycleDetails(slot)}");
        }

        private static readonly ushort[] _life = new ushort[Slots];
        private static readonly ushort[] _generation = new ushort[Slots];
        private static readonly ushort[,] _pendingLife = new ushort[Slots, PendingCapacity];
        private static readonly ushort[,] _pendingGeneration = new ushort[Slots, PendingCapacity];

        private static void EnsureLife(int slot)
        {
            if (slot < 0 || slot >= Slots) return;
            if (_life[slot] != NetPlayerLifecycle.Get(slot) || _generation[slot] != NetPlayerLifecycle.Generation(slot))
            {
                ForgetSlot(slot);
                _life[slot] = NetPlayerLifecycle.Get(slot);
                _generation[slot] = NetPlayerLifecycle.Generation(slot);
            }
        }

        public static string LifecycleDetails(int slot) => $"pending={_pendingCount[slot]} "
            + $"debit={Debit(slot)} authorityHealth={_lastAuthorityHealth[slot]} heldDead={HeldDead(slot)}";

        public static string HealthDetails(int slot)
        {
            if (slot < 0 || slot >= Slots) return "invalid slot";
            if (!NetPlayerLifecycle.Matches(slot, _generation[slot], _life[slot]))
                return "predictedDebit=0 shownFloor=0 pending=0 lastAuthorityHP=0";
            return $"predictedDebit={Debit(slot)} shownFloor={_shownHealth[slot]} pending={_pendingCount[slot]} lastAuthorityHP={_lastAuthorityHealth[slot]}";
        }

        // Three frames: beyond this the authority's catch-up commonly
        // resolves the projectile before the local copy reaches its target.
        private const float TravelFlight = 3 / 60f;

        private static int _markerTimer;

        /// <summary>
        /// Frames the mark stays up. Two tenths of a second: long enough to
        /// read at a glance, short enough that a stream of hits reads as a
        /// stream rather than as one long flash.
        /// </summary>
        private const int MarkerFrames = 12;

        /// <summary>
        /// How solid the mark is drawn, 0 when there is nothing to draw. It
        /// fades over its last few frames rather than blinking out.
        /// </summary>
        public static float MarkerAlpha
        {
            get
            {
                if (!MarkerEnabled || _markerTimer <= 0)
                {
                    return 0;
                }
                const int fade = 6;
                return _markerTimer >= fade ? 1f : _markerTimer / (float)fade;
            }
        }

        public static void Reset()
        {
            Array.Clear(_life);
            Array.Clear(_generation);
            Array.Clear(_pendingLife);
            Array.Clear(_pendingGeneration);
            Array.Clear(_pendingFrame);
            Array.Clear(_pendingDamage);
            Array.Clear(_pendingLethal);
            Array.Clear(_pendingHeadshot);
            Array.Clear(_pendingBeam);
            Array.Clear(_pendingClaim);
            Array.Clear(_pendingSpent);
            Array.Clear(_pendingTravelled);
            Array.Clear(_pendingSelf);
            Array.Clear(_settledCredit);
            Array.Clear(_settledFrame);
            Array.Clear(_beamPredicted);
            Array.Clear(_beamConfirmed);
            Array.Clear(_beamDenied);
            Array.Clear(_beamLethal);
            Array.Clear(_beamUndone);
            Array.Clear(_beamDamage);
            Array.Clear(_pendingCount);
            Array.Clear(_pendingHead);
            Array.Clear(_healFrame);
            Array.Clear(_healAmount);
            _healCount = 0;
            _healHead = 0;
            Predicted = 0;
            Confirmed = 0;
            SelfPredicted = 0;
            SelfConfirmed = 0;
            Denied = 0;
            Unpredicted = 0;
            UnpredictedMoving = 0;
            UnpredictedStill = 0;
            LethalHeld = LethalConfirmed = LethalDenied = 0;
            Array.Clear(_pendingHeld);
            DeathsPredicted = 0;
            SelfDeathsPredicted = 0;
            DeathsUndone = 0;
            Array.Clear(_shownHealth);
            Array.Clear(_predictedFrame);
            HealthSamples = 0;
            HealthDisagreed = 0;
            HealthUnderPoints = 0;
            HealthUnderWorst = 0;
            HealthOverPoints = 0;
            HealthOverWorst = 0;
            FloorLifted = 0;
            Array.Clear(_lastAuthorityHealth);
            Array.Clear(_predictedPoints);
            Array.Clear(_authorityDrop);
            FloorHeld = 0;
            FloorHeldPoints = 0;
            FloorWorst = 0;
            DebitPoints = 0;
            DebitWorst = 0;
            DrainPredicted = 0;
            HeadshotsPredicted = 0;
            HeadshotsAgreed = 0;
            HeadshotsDowngraded = 0;
            HeadshotsUpgraded = 0;
            _markerTimer = 0;
        }

        /// <summary>
        /// Whether damage this machine has just resolved should be allowed to
        /// land as a prediction rather than thrown away.
        ///
        /// Asked by <see cref="NetDamage.Suppress"/>, which is the top of
        /// <c>TakeDamage</c> -- before any of the engine's feedback has run,
        /// so a "no" here costs exactly what it always cost.
        /// </summary>
        public static bool Predicts(PlayerEntity victim, EntityBase? source, DamageFlags flags)
        {
            if (!Enabled || victim.Health <= 0)
            {
                return false;
            }
            int local = NetHooks.LocalSlot;
            if (local < 0)
            {
                return false;
            }
            if (source == null)
            {
                // Nothing fired this: the void under the map, a kill plane, a
                // crusher, a room telling a player to die. It is dealt to
                // whoever is standing there, on whatever machine is standing
                // them there, so the only copy of it worth resolving early is
                // this machine's own player -- a remote player's fall is the
                // authority's to report, exactly as it always was.
                //
                // Only the lethal ones. DamageFlags.Death kills whatever the
                // number is, which is what a fall into the void is, and it is
                // the one case a player is actually waiting on: falling for a
                // quarter of a second after you have already left the map is
                // the same complaint as a rocket jump that starts late. The
                // chip damage from standing in lava carries no such flag and
                // stays with the authority, where a rate that depends on frame
                // parity cannot make two machines disagree about a health bar.
                return victim.SlotIndex == local && flags.TestFlag(DamageFlags.Death);
            }
            // Whose shot it is, and nothing about who it lands on.
            //
            // This used to refuse a hit whose victim was this machine's own
            // player -- rule two, incoming damage is not predicted -- and that
            // refusal is already made by the line below: damage arriving from
            // somebody else has an owner who is not this slot. What the extra
            // clause actually excluded was the one hit that is *entirely*
            // local: your own splash, on you. Source, target and input are all
            // on this machine, there is nothing to guess about anybody, and it
            // is the hit whose feedback matters most on the frame it happens,
            // because a rocket jump is not damage that arrives late -- it is a
            // jump that does not happen. See the self-damage section in
            // .claude/multiplayer/NETWORK-PREDICTION.md.
            PlayerEntity? owner = OwnerOf(source);
            if (owner == null || owner.SlotIndex != local)
            {
                return false;
            }
            return true;
        }

        /// <summary>
        /// Whose shot this is. A halfturret's beams are Weavel's, and a bomb
        /// belongs to whoever laid it -- both are hits a player aimed and
        /// both are answered by the authority the same way.
        /// </summary>
        private static PlayerEntity? OwnerOf(EntityBase? source)
        {
            if (source is BeamProjectileEntity beam)
            {
                if (beam.Owner is PlayerEntity player)
                {
                    return player;
                }
                if (beam.Owner is HalfturretEntity turret)
                {
                    return turret.Owner;
                }
                return null;
            }
            if (source is BombEntity bomb)
            {
                return bomb.Owner;
            }
            if (source is PlayerEntity attacker)
            {
                // An alt form's attack -- Weavel's scythe, Spire's spin,
                // Sylux's trail -- is dealt by the player rather than by
                // anything it spawned. These are the hits that feel worst
                // when they arrive late, because they land at arm's length:
                // the whole of the attack is over before the authority's
                // answer to it comes back.
                return attacker;
            }
            return null;
        }

        /// <summary>
        /// A hit that has survived every one of <c>TakeDamage</c>'s refusals
        /// and is about to be applied: the damage is final, the attacker is
        /// known, and the death has not been decided yet. The last moment a
        /// prediction can be recorded, and the only one at which the clamp
        /// that keeps it from killing still works.
        /// </summary>
        /// <param name="beam">
        /// The weapon behind the hit, or <see cref="BeamType.None"/> for
        /// everything that is not a beam -- an alt form's attack, a bomb, the
        /// void. Only a hit claim reads it, and only so that the authority can
        /// bound the damage against what that weapon can actually deal and the
        /// victim's own machine can replay the right hit rather than a
        /// nameless one. <see cref="NetHitClaims"/>.
        /// </param>
        /// <param name="flight">
        /// How long the projectile behind this hit had been in the air, in
        /// seconds. Zero for everything that is not a beam.
        ///
        /// Retained for caller compatibility; all remote lethal hits are held.
        /// </param>
        public static void NoteHit(PlayerEntity victim, PlayerEntity? attacker,
            ref DamageFlags flags, ref uint damage, BeamType beam = BeamType.None,
            uint launchFrame = 0, float flight = 0)
        {
            int local = NetHooks.LocalSlot;
            if (local < 0)
            {
                return;
            }
            // No attacker at all is the environment, and Predicts only ever
            // lets one of those through for this machine's own player: the
            // void, a kill plane, a crusher. It is a self-kill with nothing
            // holding the trigger.
            bool self;
            if (attacker == null)
            {
                if (victim.SlotIndex != local)
                {
                    return;
                }
                self = true;
            }
            else
            {
                if (attacker.SlotIndex != local)
                {
                    return;
                }
                self = attacker == victim;
            }
            if (Predicting && Enabled)
            {
                // Lethal even at zero damage when the flag says so: a fall
                // into the void is TakeDamage(0, DamageFlags.Death), and
                // reading the number alone would file the one death that is
                // certainly right as a scratch.
                bool lethal = victim.Health > 0
                    && (damage >= (uint)victim.Health || flags.TestFlag(DamageFlags.Death));
                // Keep feedback immediate while reserving remote death for authority.
                uint claimedDamage = damage;
                int weapon = NetShotDiagnostics.Bucket(beam);
                NetShotDiagnostics.LocalHits[weapon]++;
                NetShotDiagnostics.Predictions[weapon]++;
                NetShotDiagnostics.PredictedDamage[weapon] += damage;
                if (flags.TestFlag(DamageFlags.Headshot)) NetShotDiagnostics.LocalHeadshots[weapon]++;
                if (attacker != null && NetLog.Enabled) NetShotDiagnostics.Trace("prediction",
                    ShotKey.For(attacker.SlotIndex, launchFrame), beam, $"victim={victim.SlotIndex} damage={damage}");
                bool claimedLethal = lethal;
                if (lethal && !self)
                {
                    damage = (uint)Math.Max(0, victim.Health - 1);
                    flags &= ~DamageFlags.Death;
                    LethalHeld++;
                    lethal = false;
                    if (NetLog.Enabled) NetLog.Event($"[predict] lethal held slot={victim.SlotIndex} "
                        + $"life={NetPlayerLifecycle.Get(victim.SlotIndex)} frame={NetSession.NetFrame}");
                }
                bool headshot = flags.TestFlag(DamageFlags.Headshot);
                if (!self)
                {
                    // Capped at what is actually there to take, because that
                    // is all the authority's own number can come down by: a
                    // 32-damage Missile into a victim on 3 health removes 3.
                    // Counting the 32 made every kill look like a hit worth a
                    // third more than it was, which is how this ledger first
                    // read x1.21 on a run whose damage was exact.
                    _predictedPoints[victim.SlotIndex] +=
                        Math.Min((int)damage, Math.Max(0, victim.Health));
                }
                int at = Push(victim.SlotIndex, NetSession.NetFrame, (int)damage,
                    lethal, headshot, beam, self);
                if (at >= 0)
                {
                    _pendingHeld[victim.SlotIndex, at] = claimedLethal && !self;
                    _pendingTravelled[victim.SlotIndex, at] = !self && flight > TravelFlight;
                }
                // And tell the authority, which may not find this hit itself:
                // its rewind has a ceiling, its copy of the trigger pull may
                // be stale, and if this machine's player is killed during the
                // round trip it will not run the shot at all. The position
                // handed over is *this machine's* copy of the victim, which is
                // the one thing about the claim the authority can check --
                // against its own history, at the frame this machine was
                // looking at. NetHitClaims.
                //
                // The id comes back and is stamped onto the prediction just
                // filed, because the verdict for that claim is the only exact
                // answer this machine ever gets about this particular hit.
                // See _pendingClaim.
                if (!self && attacker != null)
                {
                    ushort claimId = NetHitClaims.Declare(victim, attacker, beam, claimedDamage,
                        flags, claimedLethal, victim.Position, launchFrame);
                    StampClaim(victim.SlotIndex, at, claimId);
                }
                if (headshot && !self)
                {
                    HeadshotsPredicted++;
                }
                // Counted apart from the rest. The confirmed percentage is a
                // claim about shots aimed at other people over a wire; a hit
                // on yourself, resolved on the machine that fired it, would
                // only flatter it.
                if (self)
                {
                    SelfPredicted++;
                }
                else
                {
                    Predicted++;
                }
                if (lethal)
                {
                    if (self)
                    {
                        SelfDeathsPredicted++;
                    }
                    else
                    {
                        DeathsPredicted++;
                    }
                }
            }
            // Every machine, every mode: on the authority and offline this is
            // a hit that has actually happened, and there is no reason the
            // confirmation a player gets should depend on which machine is
            // running the match. Not in the story, which is the DS's game and
            // has no such mark.
            //
            // Never for your own splash landing on you: the mark answers "did
            // that land on somebody", and a rocket jump is not a hit anybody
            // wants confirming.
            if (!GameState.SinglePlayer && !self)
            {
                _markerTimer = MarkerFrames;
            }
        }

        /// <summary>
        /// The authority has confirmed a hit on <paramref name="slot"/> by
        /// this machine's player. Returns whether it was one this machine had
        /// already shown, in which case the replay is skipped -- the flinch,
        /// the sound and the knockback all happened when the trigger was
        /// pulled.
        /// </summary>
        /// <param name="authorityHeadshot">
        /// Whether the authority's own resolution of the hit it is reporting
        /// carried <see cref="DamageFlags.Headshot"/>. The snapshot names only
        /// the last attacker and the flags of the last hit, so this can only
        /// be asked of the newest prediction being retired -- which is the one
        /// it describes.
        /// </param>
        public static bool Confirm(int slot, int landed = 1, bool authorityHeadshot = false)
        {
            EnsureLife(slot);
            if (slot < 0 || slot >= Slots)
            {
                return false;
            }
            if (_pendingCount[slot] == 0)
            {
                // Nothing outstanding, so this snapshot's hits are split two
                // ways and both halves matter.
                //
                // The first is a hit whose own verdict already retired it --
                // the verdict and the snapshot race, and this absorbs the
                // loser so it is not counted twice. The credit **expires**:
                // the two are never more than a snapshot apart, and a stale
                // one is worse than useless, because it makes a hit the
                // authority resolved on its own look like one this machine had
                // already predicted, and the guard below never fires for it.
                // That was two of every five predicted kills surviving the
                // first version of this.
                //
                // The second is a hit of this machine's own that it never
                // resolved at all -- its copy of the shot missed, or the
                // authority's catch-up got there first. Counted, and nothing
                // else: a heuristic that tried to cancel the local copy when
                // it landed suppressed 46 good predictions out of 49 on
                // loopback, because at a low ping the authority beats a client
                // by a frame on almost everything. Telling the two apart needs
                // the shot's identity, and the snapshot carries a count.
                int owed = Math.Max(1, landed);
                if (NetSession.NetFrame - _settledFrame[slot] >= (uint)HoldFrames)
                {
                    _settledCredit[slot] = 0;
                }
                int fromSettled = Math.Min(owed, _settledCredit[slot]);
                _settledCredit[slot] -= fromSettled;
                int rest = owed - fromSettled;
                if (rest > 0)
                {
                    Unpredicted += rest;
                    if (MovingNow()) UnpredictedMoving += rest;
                    else UnpredictedStill += rest;
                }
                return fromSettled > 0;
            }
            // As many as the snapshot says landed, not one.
            //
            // A snapshot carries a *count* of hits since the last one and the
            // slot of only the last attacker, and this used to retire a single
            // prediction however many it was reporting. Two of this machine's
            // own hits inside one snapshot window therefore left one of them
            // outstanding, to time out later looking like a miss -- which is
            // most of what the "denied" number was measuring, and, now that a
            // prediction holds the victim's health, a hold that outlived its
            // confirmation by the whole of the window. Capped at what is
            // actually outstanding, so a burst that included somebody else's
            // hits cannot retire more than this machine predicted.
            bool self = slot == NetHooks.LocalSlot;
            int take = Math.Clamp(landed, 1, _pendingCount[slot]);
            // Anything the snapshot is reporting beyond what is outstanding
            // here is either somebody else's hit -- which this machine never
            // predicted and does not account for -- or one of its own that a
            // verdict has already retired. Take those off the credit so they
            // are not counted twice.
            if (landed > take)
            {
                int spare = landed - take;
                _settledCredit[slot] -= Math.Min(spare, _settledCredit[slot]);
            }
            for (int i = 0; i < take; i++)
            {
                int head = _pendingHead[slot];
                bool predictedHeadshot = _pendingHeadshot[slot, head];
                _pendingHeadshot[slot, head] = false;
                // Already answered by name, by the verdict for its own claim.
                // The snapshot is counting the same hit, so it consumes one of
                // `take` -- it just must not be counted a second time.
                if (RetireHead(slot, confirmed: true) < 0)
                {
                    continue;
                }
                if (self)
                {
                    SelfConfirmed++;
                    continue;
                }
                Confirmed++;
                // Only the last of a batch is described by the flags this
                // snapshot carries; the earlier ones in the same window are
                // hits whose flags were overwritten before they were sent, and
                // scoring them against this one would invent disagreements
                // that the wire never reported either way.
                if (i != take - 1)
                {
                    continue;
                }
                if (predictedHeadshot && authorityHeadshot)
                {
                    HeadshotsAgreed++;
                }
                else if (predictedHeadshot)
                {
                    HeadshotsDowngraded++;
                }
                else if (authorityHeadshot)
                {
                    HeadshotsUpgraded++;
                }
            }
            return true;
        }

        /// <summary>
        /// Forget what is outstanding for one slot, because the slot has
        /// changed hands or the room has.
        ///
        /// A prediction describes a hit on a particular player in a particular
        /// room. Kept across either, the worst of it is a lethal one holding
        /// the new occupant of that slot dead on this screen for the length of
        /// the hold -- a player who has just spawned into a fresh map, lying
        /// down because somebody else was shot before the rotation. The
        /// statistics go with it: they are per-match, like
        /// <c>NetDamage.ResetForRoomChange</c>'s tallies.
        /// </summary>
        public static void ForgetSlot(int slot)
        {
            if (slot < 0 || slot >= Slots)
            {
                return;
            }
            for (int i = 0; i < PendingCapacity; i++)
            {
                _pendingFrame[slot, i] = 0;
                _pendingDamage[slot, i] = 0;
                _pendingLethal[slot, i] = false;
                _pendingHeld[slot, i] = false;
                _pendingHeadshot[slot, i] = false;
                _pendingBeam[slot, i] = AltBeam;
                _pendingClaim[slot, i] = 0;
                _pendingSpent[slot, i] = false;
                _pendingTravelled[slot, i] = false;
                _pendingSelf[slot, i] = false;
            }
            if (slot == NetHooks.LocalSlot) { _healCount = 0; _healHead = 0; }
            _pendingCount[slot] = 0;
            _pendingHead[slot] = 0;
            _settledCredit[slot] = 0;
            _shownHealth[slot] = 0;
            _predictedFrame[slot] = 0;
            _lastAuthorityHealth[slot] = 0;
            _settledCredit[slot] = 0;
            _settledFrame[slot] = 0;
        }

        /// <summary>
        /// The authority has put <paramref name="slot"/> back on the map, so
        /// everything this machine predicted about their last life is spent.
        ///
        /// Two jobs at one moment. It counts a kill this machine showed that
        /// the authority never confirmed -- the hold expired, the next
        /// snapshot stood them up, and that is exactly what a mispredicted
        /// kill looks like from here. And it drops the outstanding debit,
        /// because a prediction about the life that just ended must not come
        /// off the health of the one that just started: without this, a
        /// player killed and respawned inside the hold window would come back
        /// with the last twenty points this machine had landed on them
        /// already taken off.
        /// </summary>
        public static void NoteRespawn(int slot)
        {
            if (slot < 0 || slot >= Slots)
            {
                return;
            }
            if (slot == NetSession.LocalSlot) ForgetPending();
            else ForgetSlot(slot);
        }

        /// <summary>
        /// The authority has reported <paramref name="slot"/> dead, so
        /// everything this machine predicted about that life is answered and
        /// the hold is over.
        ///
        /// Without it a predicted self-kill outlived its own confirmation: the
        /// authority's report of the death carries no attacker this machine
        /// can match (a fall names nobody), so nothing retired the pending
        /// lethal entry, and <see cref="HeldDead"/> went on refusing the
        /// respawn that came a moment later -- a player who died in the void
        /// and then lay there for the rest of the hold window.
        ///
        /// <b>It releases the hold and nothing else.</b> Clearing the slot
        /// outright would drop predictions that have not been answered yet,
        /// and they would leave the tally neither confirmed nor denied --
        /// <see cref="Predicted"/> would stop equalling
        /// <see cref="Confirmed"/> plus <see cref="Denied"/>, which is the one
        /// arithmetic that makes those numbers readable. They stay, to be
        /// confirmed or to age out; what they must not do is go on holding a
        /// body down that the authority has already agreed is down.
        /// <see cref="NoteRespawn"/> is what clears the slot, at the respawn,
        /// where the life really has ended.
        /// </summary>
        public static void NoteDeath(int slot)
        {
            if (slot < 0 || slot >= Slots)
            {
                return;
            }
            for (int i = 0; i < PendingCapacity; i++)
            {
                _pendingLethal[slot, i] = false;
                _pendingHeld[slot, i] = false;
            }
        }

        /// <summary>Everything outstanding, for a rotation into a new room.</summary>
        public static void ForgetPending()
        {
            for (int slot = 0; slot < Slots; slot++)
            {
                ForgetSlot(slot);
            }
            _healCount = 0;
            _healHead = 0;
        }

        /// <summary>
        /// Health this machine's own Shock Coil has just drained, before the
        /// authority has said so.
        ///
        /// Called from the beam's life-drain branch, beside the
        /// <c>GainHealth</c> it is reporting. Only the credit is recorded here
        /// -- the engine has already applied the heal locally, exactly as it
        /// does offline; this is what stops the next snapshot from assigning
        /// it straight back off again.
        /// </summary>
        public static void NoteDrain(PlayerEntity healer, int amount)
        {
            if (!Enabled || !Predicting || amount <= 0)
            {
                return;
            }
            int local = NetHooks.LocalSlot;
            if (local < 0 || healer.SlotIndex != local)
            {
                return;
            }
            if (_healCount == HealCapacity)
            {
                _healHead = (_healHead + 1) % HealCapacity;
                _healCount--;
            }
            int tail = (_healHead + _healCount) % HealCapacity;
            _healFrame[tail] = NetSession.NetFrame;
            _healAmount[tail] = amount;
            _healCount++;
            DrainPredicted += amount;
            NetShotDiagnostics.DrainCredit[NetShotDiagnostics.Bucket(BeamType.ShockCoil)] += amount;
        }

        /// <summary>
        /// What this machine has taken off <paramref name="slot"/> and not yet
        /// been told about, in points of health.
        /// </summary>
        private static int Debit(int slot)
        {
            EnsureLife(slot);
            if (!Enabled || slot < 0 || slot >= Slots || _pendingCount[slot] == 0)
            {
                return 0;
            }
            uint now = NetSession.NetFrame;
            int hold = HoldFrames;
            int debit = 0;
            for (int i = 0; i < _pendingCount[slot]; i++)
            {
                int at = (_pendingHead[slot] + i) % PendingCapacity;
                if (NetPlayerLifecycle.Matches(slot, _pendingGeneration[slot, at], _pendingLife[slot, at])
                    && now - _pendingFrame[slot, at] < (uint)hold)
                {
                    if (_pendingTravelled[slot, at])
                    {
                        // Authority usually paid this projectile before the local
                        // copy arrived; do not show the same damage twice.
                        continue;
                    }
                    debit += _pendingDamage[slot, at];
                }
            }
            return debit;
        }

        /// <summary>
        /// The health to show for a puppet: the authority's number, less what
        /// this machine has already landed on them and not yet had confirmed.
        ///
        /// Never zero on its own account. Assigning zero health is not a
        /// death -- it skips the whole death path -- so a hold that ran the
        /// bar to the bottom would produce a player who is neither alive nor
        /// dead. A predicted kill goes through <c>TakeDamage</c> like any
        /// other and is held by <see cref="HeldDead"/> instead.
        /// </summary>
        public static int HealthFor(int slot, int authorityHealth)
        {
            EnsureLife(slot);
            if (!Enabled || slot < 0 || slot >= Slots)
            {
                return authorityHealth;
            }
            // A rise in the authority's own number is a heal or a respawn,
            // and the floor may not refuse it: it exists to stop a bar
            // climbing back because this machine's *own* predictions were
            // retired faster than it could make new ones, not to hide what the
            // authority is reporting. See _lastAuthorityHealth.
            if (authorityHealth > _lastAuthorityHealth[slot] && _shownHealth[slot] > 0)
            {
                _shownHealth[slot] = 0;
                FloorLifted++;
            }
            if (_lastAuthorityHealth[slot] > authorityHealth && authorityHealth > 0)
            {
                _authorityDrop[slot] += _lastAuthorityHealth[slot] - authorityHealth;
            }
            _lastAuthorityHealth[slot] = authorityHealth;
            int debit = Debit(slot);
            int health = debit > 0 && authorityHealth > 1
                ? Math.Max(1, authorityHealth - debit)
                : authorityHealth;
            int owed = health;
            // Nothing this machine has taken down goes back up while it is
            // still shooting. See _shownHealth: with a continuous beam the
            // debit is retired by the authority faster than it is built, so
            // the honest number is the authority's -- and the authority's is
            // half a round trip stale, which is a bar that visibly climbs the
            // moment the trigger is released.
            if (authorityHealth > 0 && _shownHealth[slot] > 0
                && NetSession.NetFrame - _predictedFrame[slot] < (uint)HoldFrames)
            {
                health = Math.Min(health, _shownHealth[slot]);
                health = Math.Max(1, health);
            }
            // What the two bars actually say, sampled where both numbers are
            // in the same hand. "The client kills him and the server does not"
            // is a statement about this difference and nothing else, and
            // before this there was no line anywhere that printed it.
            //
            // Three quantities, because they answer different questions:
            // the debit is the prediction doing its job, the floor is the
            // guard against a bar climbing back, and a gap with *neither*
            // outstanding is a straight disagreement -- a hit counted twice
            // here, or one the authority never had.
            if (authorityHealth > 0)
            {
                HealthSamples++;
                if (health < owed)
                {
                    FloorHeld++;
                    FloorHeldPoints += owed - health;
                    FloorWorst = Math.Max(FloorWorst, owed - health);
                }
                if (debit == 0 && health != authorityHealth)
                {
                    HealthDisagreed++;
                    int gap = authorityHealth - health;
                    if (gap > 0)
                    {
                        HealthUnderPoints += gap;
                        HealthUnderWorst = Math.Max(HealthUnderWorst, gap);
                    }
                    else
                    {
                        HealthOverPoints += -gap;
                        HealthOverWorst = Math.Max(HealthOverWorst, -gap);
                    }
                }
                DebitPoints += debit;
                DebitWorst = Math.Max(DebitWorst, debit);
            }
            _shownHealth[slot] = health;
            return health;
        }

        /// <summary>
        /// The client's health bar against the authority's, sampled on every
        /// snapshot applied to a remote player.
        ///
        /// <see cref="HealthDisagreed"/> is the number that matters: nothing
        /// outstanding and the two still differ. Everything else here is the
        /// mechanism working -- a debit is a hit this machine has landed and
        /// not had answered, and the floor is what stops a bar climbing back
        /// while the trigger is still down.
        /// </summary>
        public static long HealthSamples { get; private set; }
        public static long HealthDisagreed { get; private set; }
        /// <summary>
        /// Points the drawn bar sat <b>below</b> the authority's with nothing
        /// outstanding -- the direction that predicts kills the authority
        /// refuses -- and above it, which is the harmless one.
        /// </summary>
        public static long HealthUnderPoints { get; private set; }
        public static int HealthUnderWorst { get; private set; }
        public static long HealthOverPoints { get; private set; }
        public static int HealthOverWorst { get; private set; }
        public static long FloorHeld { get; private set; }
        public static long FloorHeldPoints { get; private set; }
        public static int FloorWorst { get; private set; }
        public static long DebitPoints { get; private set; }
        public static int DebitWorst { get; private set; }

        /// <summary>
        /// Points predicted against points the authority actually removed, per
        /// slot. The line that says whether a hit is being counted twice --
        /// see <see cref="_predictedPoints"/> for how to read it.
        /// </summary>
        public static string DescribeDamageLedger()
        {
            var text = new System.Text.StringBuilder();
            text.Append("damage ledger (predicted / authority removed):");
            bool any = false;
            for (int slot = 0; slot < Slots; slot++)
            {
                if (_predictedPoints[slot] == 0 && _authorityDrop[slot] == 0)
                {
                    continue;
                }
                any = true;
                string ratio = _authorityDrop[slot] > 0
                    ? $" x{_predictedPoints[slot] / (double)_authorityDrop[slot]:F2}"
                    : "";
                text.Append($" slot {slot} {_predictedPoints[slot]}/{_authorityDrop[slot]}{ratio};");
            }
            return any ? text.ToString() : "damage ledger: nothing predicted onto anybody";
        }

        /// <summary>One line for the report: the two bars, side by side.</summary>
        public static string DescribeHealth()
        {
            if (HealthSamples == 0)
            {
                return "health bars: nothing drawn for anybody else";
            }
            return $"health bars: {HealthSamples} sample(s); debit mean "
                + $"{DebitPoints / (double)HealthSamples:F2} worst {DebitWorst}; "
                + $"floor held {FloorHeld} ({FloorHeldPoints} point(s), worst {FloorWorst}), "
                + $"lifted {FloorLifted}; disagreed with nothing outstanding "
                + $"{HealthDisagreed} -- drawn low by {HealthUnderPoints} point(s) "
                + $"(worst {HealthUnderWorst}), high by {HealthOverPoints} "
                + $"(worst {HealthOverWorst})";
        }

        /// <summary>
        /// This machine's own health: the authority's number plus whatever its
        /// beam has drained since the authority last spoke.
        /// </summary>
        public static int LocalHealthFor(PlayerEntity player, int authorityHealth)
        {
            EnsureLife(player.SlotIndex);
            if (!Enabled || authorityHealth <= 0)
            {
                return authorityHealth;
            }
            uint now = NetSession.NetFrame;
            int hold = HoldFrames;
            int credit = 0;
            for (int i = 0; i < _healCount; i++)
            {
                int at = (_healHead + i) % HealCapacity;
                if (now - _healFrame[at] < (uint)hold)
                {
                    credit += _healAmount[at];
                }
            }
            // And less your own splash, held the same way a victim's is. The
            // debit is the mirror of the credit and has to be here, not in
            // HealthFor: nothing calls HealthFor for the local slot, and
            // without this a rocket jump would take the health off for one
            // frame and the next snapshot would hand it straight back until
            // the authority caught up -- the same one-frame prediction the
            // hold exists to stop, on the one player who is looking at the
            // number.
            credit -= Debit(NetHooks.LocalSlot);
            // A self-kill this machine has already died is not undone by a
            // snapshot that has not heard about it. The branch in ApplyState
            // that spawns a player normally returns before this line while
            // HeldDead is true, so this is the belt to that brace: handing
            // back the authority's health here would stand a corpse up with
            // the death camera still running.
            if (player.Health <= 0 && HeldDead(NetHooks.LocalSlot))
            {
                return 0;
            }
            if (credit == 0)
            {
                return authorityHealth;
            }
            int max = player.HealthMax > 0 ? player.HealthMax : authorityHealth;
            // Floored at 1 for HealthFor's reason: an assignment is not a
            // death. A self-inflicted prediction *can* be lethal now, and the
            // line above is what keeps that one from coming through here.
            return Math.Clamp(authorityHealth + credit, 1, max);
        }

        /// <summary>
        /// Whether this machine has killed <paramref name="slot"/> and is
        /// still waiting to hear whether it was right.
        ///
        /// While this is true the snapshot is not allowed to put that player
        /// back on the map: the authority's copy of them is a round trip
        /// behind and still walking around, and respawning the corpse every
        /// snapshot until the kill is confirmed is worse than either answer.
        /// It stops being true the moment the kill is confirmed -- or, if it
        /// never is, when the hold expires and the next snapshot spawns them
        /// as it always did.
        /// </summary>
        public static bool HeldDead(int slot)
        {
            EnsureLife(slot);
            if (!Enabled || slot < 0 || slot >= Slots)
            {
                return false;
            }
            // Somebody else is only held down when death is predicted for
            // somebody else, which it is not by default. This machine's own
            // player always is: a self-kill is predicted whatever DeathEnabled
            // says, and a snapshot that has not heard about it yet would stand
            // the body straight back up -- the resurrection this whole switch
            // exists to stop, on the one player who is looking at it.
            if (slot != NetHooks.LocalSlot)
            {
                return false;
            }
            uint now = NetSession.NetFrame;
            int hold = HoldFrames;
            for (int i = 0; i < _pendingCount[slot]; i++)
            {
                int at = (_pendingHead[slot] + i) % PendingCapacity;
                if (_pendingLethal[slot, at] && now - _pendingFrame[slot, at] < (uint)hold)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Once a simulation step: age the outstanding predictions out and
        /// count the mark down. In the step and not in the draw, because both
        /// are measured in frames and a picture with no step behind it must
        /// not advance either.
        /// </summary>
        public static void Tick()
        {
            if (_markerTimer > 0)
            {
                _markerTimer--;
            }
            if (!NetSession.Active)
            {
                return;
            }
            uint now = NetSession.NetFrame;
            for (int slot = 0; slot < Slots; slot++)
            {
                while (_pendingCount[slot] > 0)
                {
                    uint frame = _pendingFrame[slot, _pendingHead[slot]];
                    // Unsigned, so a counter that has been reset underneath us
                    // reads as an enormous age rather than a negative one --
                    // which is the right answer either way: nothing pending
                    // from before a reset can still be confirmed.
                    if (now - frame < PendingFrames)
                    {
                        break;
                    }
                    RetireHead(slot, confirmed: false);
                }
            }
            // The drain credit is aged on the same clock. It is only ever read
            // through the hold window, so this is housekeeping rather than
            // policy -- it keeps the ring from filling with entries nothing
            // will ever count again.
            while (_healCount > 0 && now - _healFrame[_healHead] >= PendingFrames)
            {
                _healHead = (_healHead + 1) % HealCapacity;
                _healCount--;
            }
        }

        /// <summary>
        /// File a prediction. Returns where it landed in the ring, so the
        /// caller can stamp the claim id onto it once the claim has been
        /// declared -- the claim is what retires it exactly later.
        /// </summary>
        private static int Push(int slot, uint frame, int damage, bool lethal,
            bool headshot, BeamType beam, bool self)
        {
            EnsureLife(slot);
            if (slot < 0 || slot >= Slots)
            {
                return -1;
            }
            if (_pendingCount[slot] == PendingCapacity)
            {
                RetireHead(slot, confirmed: false);
            }
            _predictedFrame[slot] = frame;
            int tail = (_pendingHead[slot] + _pendingCount[slot]) % PendingCapacity;
            _pendingLife[slot, tail] = NetPlayerLifecycle.Get(slot);
            _pendingGeneration[slot, tail] = NetPlayerLifecycle.Generation(slot);
            _pendingFrame[slot, tail] = frame;
            _pendingDamage[slot, tail] = Math.Max(0, damage);
            _pendingLethal[slot, tail] = lethal;
            _pendingHeadshot[slot, tail] = headshot;
            _pendingBeam[slot, tail] = (byte)Bucket(beam);
            _pendingClaim[slot, tail] = 0;
            _pendingSpent[slot, tail] = false;
            _pendingTravelled[slot, tail] = false;
            _pendingSelf[slot, tail] = self;
            _pendingCount[slot]++;
            if (!self)
            {
                int bucket = Bucket(beam);
                _beamPredicted[bucket]++;
                _beamDamage[bucket] += Math.Max(0, damage);
                if (lethal)
                {
                    _beamLethal[bucket]++;
                }
            }
            return tail;
        }

        /// <summary>
        /// Take the oldest outstanding prediction for a slot off the books and
        /// file it under the weapon it was made with. One place, so the
        /// per-weapon tally cannot drift from the aggregate one.
        /// </summary>
        private static int RetireHead(int slot, bool confirmed)
        {
            int head = _pendingHead[slot];
            ResolveHeld(slot, head, confirmed);
            bool spent = _pendingSpent[slot, head] || _pendingSelf[slot, head];
            int bucket = _pendingBeam[slot, head];
            if (!spent && bucket >= 0 && bucket < BeamBuckets)
            {
                if (confirmed)
                {
                    _beamConfirmed[bucket]++;
                }
                else
                {
                    _beamDenied[bucket]++;
                }
            }
            bool answered = _pendingSpent[slot, head];
            // Not a self-hit: SelfPredicted is counted apart from Predicted,
            // so counting its timeouts in Denied breaks the one arithmetic
            // that makes these numbers readable -- Predicted == Confirmed +
            // Denied. A run with three unanswered rocket-jump splashes read
            // "13 predicted, 12 confirmed, 2 denied".
            if (!answered && !confirmed && !_pendingSelf[slot, head])
            {
                Denied++;
            }
            _pendingClaim[slot, head] = 0;
            _pendingSpent[slot, head] = false;
            _pendingSelf[slot, head] = false;
            _pendingHead[slot] = (head + 1) % PendingCapacity;
            _pendingCount[slot]--;
            return answered ? -1 : head;
        }

        /// <summary>
        /// Retire the one prediction a hit claim was declared under, because
        /// the authority has answered that claim by name.
        ///
        /// <b>The exact retirement, and the only one there is.</b> The
        /// snapshot's is approximate by construction -- see
        /// <see cref="_pendingClaim"/> -- and its failure mode is a debit that
        /// stays on the books after the authority's own health already has the
        /// hit in it, which draws a victim lower than they are and eventually
        /// predicts a kill on somebody standing on most of a health bar. A
        /// verdict names the claim; the claim names the hit; nothing is
        /// guessed.
        ///
        /// <paramref name="confirmed"/> is true for the two verdicts that mean
        /// the damage happened on the authority as well -- it resolved the
        /// same shot itself (<c>Duplicate</c>) or it applied this claim
        /// (<c>Applied</c>) -- and false for every refusal, which is a
        /// prediction that was wrong and should stop holding the picture.
        /// </summary>
        public static void Settle(int slot, ushort claimId, bool confirmed)
        {
            EnsureLife(slot);
            if (!Enabled || slot < 0 || slot >= Slots || claimId == 0
                || _pendingCount[slot] == 0)
            {
                return;
            }
            for (int i = 0; i < _pendingCount[slot]; i++)
            {
                int at = (_pendingHead[slot] + i) % PendingCapacity;
                if (_pendingClaim[slot, at] != claimId)
                {
                    continue;
                }
                ResolveHeld(slot, at, confirmed);
                int bucket = _pendingSelf[slot, at] ? -1 : _pendingBeam[slot, at];
                if (bucket >= 0 && bucket < BeamBuckets)
                {
                    if (confirmed)
                    {
                        _beamConfirmed[bucket]++;
                    }
                    else
                    {
                        _beamDenied[bucket]++;
                    }
                }
                if (confirmed)
                {
                    Confirmed++;
                    if (_settledCredit[slot] < SettledCreditMax)
                    {
                        _settledCredit[slot]++;
                    }
                    _settledFrame[slot] = NetSession.NetFrame;
                }
                else
                {
                    Denied++;
                    if (_pendingLethal[slot, at])
                    {
                        DeathsUndone++;
                        if (bucket >= 0 && bucket < BeamBuckets)
                        {
                            _beamUndone[bucket]++;
                        }
                    }
                    // A refused prediction is one whose bar should spring back
                    // up, so the floor under the drawn health goes with it.
                    _shownHealth[slot] = 0;
                }
                // Answered, so nothing may count it again -- but marked, not
                // removed: the entries in front of it are still outstanding
                // and the ones behind it are still owed answers.
                _pendingClaim[slot, at] = 0;
                _pendingSpent[slot, at] = true;
                if (confirmed)
                {
                    // <b>The verdict settles the books, not the picture.</b>
                    // "The authority has this hit" and "the authority has told
                    // this machine what the victim's health is now" are half a
                    // round trip apart: the verdict is flushed the frame the
                    // claim is matched, the health rides the next snapshot.
                    // Dropping the debit here left the bar standing on the
                    // floor alone for that window -- measured at 250 ms as the
                    // drawn bar sitting a charged missile's 48 points above
                    // where it belonged, with nothing outstanding to explain
                    // it. So the damage stays in the debit and the entry is
                    // retired by the snapshot that actually carries the health
                    // (Confirm, walking the head) or by ageing out of the hold
                    // window, exactly as it was before claims existed.
                    return;
                }
                // A refusal is different: the prediction is wrong, and the
                // sooner its damage leaves the picture the shorter the wrong
                // bar lasts. That is the whole point of a verdict.
                _pendingDamage[slot, at] = 0;
                _pendingLethal[slot, at] = false;
                _pendingHeadshot[slot, at] = false;
                while (_pendingCount[slot] > 0 && _pendingSpent[slot, _pendingHead[slot]]
                    && _pendingDamage[slot, _pendingHead[slot]] == 0)
                {
                    _pendingSpent[slot, _pendingHead[slot]] = false;
                    _pendingHead[slot] = (_pendingHead[slot] + 1) % PendingCapacity;
                    _pendingCount[slot]--;
                }
                return;
            }
        }

        /// <summary>
        /// Stamp the claim a prediction was declared under onto it.
        /// </summary>
        private static void StampClaim(int slot, int at, ushort claimId)
        {
            if (claimId != 0 && slot >= 0 && slot < Slots && at >= 0 && at < PendingCapacity)
            {
                _pendingClaim[slot, at] = claimId;
            }
        }

        /// <summary>One line for the harness reports and the diagnostics screen.</summary>
        public static string Describe()
        {
            if (!Enabled)
            {
                return "hit prediction: off";
            }
            if (Predicted == 0)
            {
                // Self-hits still say so: a run that landed nothing on
                // anybody else can still have rocket-jumped and fallen into
                // the void, and those are predictions too. Every self-kill is
                // a self-hit, so this one test covers both.
                string own = SelfPredicted > 0
                    ? $", {SelfPredicted} self-hits predicted "
                        + $"({SelfConfirmed} confirmed, {SelfDeathsPredicted} of them lethal)"
                    : "";
                return "hit prediction: on, nothing predicted here "
                    + $"({Unpredicted} hits arrived from the authority)" + own;
            }
            double agreed = Confirmed * 100.0 / Predicted;
            string deaths = DeathEnabled
                ? $"{DeathsPredicted} kills predicted, {DeathsUndone} undone"
                : $"{LethalHeld} lethal hits held ({LethalConfirmed} confirmed, {LethalDenied} denied)";
            if (SelfDeathsPredicted > 0)
            {
                deaths += $", {SelfDeathsPredicted} self-kills predicted";
            }
            string drain = DrainPredicted > 0 ? $", {DrainPredicted} health drained ahead" : "";
            string self = SelfPredicted > 0
                ? $", {SelfPredicted} self-hits predicted ({SelfConfirmed} confirmed)"
                : "";
            return $"hit prediction: {Predicted} predicted, {Confirmed} confirmed "
                + $"({agreed:F1}%), {Denied} denied, {Unpredicted} unpredicted, "
                + deaths + drain + self;
        }

        /// <summary>
        /// The tally a weapon at a time, which is the only form of it that can
        /// answer "the prediction is wrong with X".
        ///
        /// One line per weapon that predicted anything, with the damage this
        /// machine took off with it. A weapon whose denied share stands out
        /// from the rest is a weapon whose damage this machine and the
        /// authority compute differently -- the charge tier, a powerup one of
        /// them has and the other does not, a splash one of them resolved as a
        /// direct hit -- and that is invisible in the aggregate line, which is
        /// dominated by whatever was fired most.
        /// </summary>
        public static string DescribeByWeapon()
        {
            var text = new System.Text.StringBuilder();
            text.Append("hit prediction by weapon:");
            bool any = false;
            for (int i = 0; i < BeamBuckets; i++)
            {
                if (_beamPredicted[i] == 0)
                {
                    continue;
                }
                any = true;
                long answered = _beamConfirmed[i] + _beamDenied[i];
                string rate = answered > 0
                    ? $"{_beamConfirmed[i] * 100.0 / answered:F0}%"
                    : "unanswered";
                text.Append($"\n  {BucketName(i),-13} {_beamPredicted[i],5} predicted, "
                    + $"{_beamConfirmed[i],5} confirmed ({rate}), {_beamDenied[i],5} denied, "
                    + $"{_beamDamage[i],6} damage");
                if (_beamLethal[i] > 0 || _beamUndone[i] > 0)
                {
                    text.Append($", {_beamLethal[i]} kills ({_beamUndone[i]} undone)");
                }
            }
            if (!any)
            {
                return "hit prediction by weapon: nothing predicted here";
            }
            return text.ToString();
        }

        /// <summary>
        /// The headshot line, which is a different claim from the one above
        /// and reads as a pass while that one does.
        ///
        /// A hit whose flags disagree is `confirmed` to everything else here:
        /// the authority says a hit landed, the prediction is retired, the
        /// percentage is unmoved. What the shooter saw was an instant kill and
        /// what they got was a body shot, and this is the only line that says
        /// so.
        /// </summary>
        public static string DescribeHeadshots()
        {
            long answered = HeadshotsAgreed + HeadshotsDowngraded;
            if (HeadshotsPredicted == 0 && HeadshotsUpgraded == 0)
            {
                return "headshots: none predicted here";
            }
            string text = $"headshots: {HeadshotsPredicted} predicted";
            if (answered > 0)
            {
                text += $", {HeadshotsAgreed} agreed by the authority "
                    + $"({HeadshotsAgreed * 100.0 / answered:F1}%), "
                    + $"{HeadshotsDowngraded} downgraded to body shots";
            }
            else
            {
                text += ", none answered yet";
            }
            if (HeadshotsUpgraded > 0)
            {
                text += $", {HeadshotsUpgraded} the authority called a headshot and this machine did not";
            }
            return text;
        }
    }
}
