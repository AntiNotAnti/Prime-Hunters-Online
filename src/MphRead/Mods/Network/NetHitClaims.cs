using System;
using MphRead.Entities;
using OpenTK.Mathematics;

namespace MphRead.Mods.Network
{
    /// <summary>
    /// A client tells the authority which of its own shots landed, and the
    /// authority decides what to do about it.
    ///
    /// <b>The fault, stated as a player would.</b> "I shot him, it hit, and
    /// nothing happened." Lag compensation (<see cref="NetUnlagged"/>) and
    /// instant hit registration (<see cref="NetHitPrediction"/>) between them
    /// cover the case where the authority and the shooter can run the same
    /// test on the same positions -- which is most of the time, and which is
    /// why those two are worth having. This file is about the cases where they
    /// cannot:
    ///
    /// <list type="number">
    /// <item>The rewind hit its ceiling. Measured on this box at a 320 ms
    /// round trip with 80 ms of jitter and 2% loss, over two scenarios:
    /// <b>80.0% and 88.9% of shots clamped</b>, 2.8 and 3.2 frames of rewind
    /// refused each, the victims a mean 0.21 units from where their shooter
    /// saw them and the worst 0.90 -- against a headshot band 0.30 units tall
    /// and a body about 0.90 across. The requested-depth histogram's *mode*
    /// was two frames past the ceiling in both: the distribution folded onto
    /// it rather than a tail touching it. In the first of those runs the
    /// shooter's own machine resolved 17 of the 23 hits the authority
    /// credited it with, and 2 of its 9 headshots came back as body
    /// shots.</item>
    /// <item>The trigger pull arrived out of a press history, so the packet
    /// carrying it acks a newer world than the one it happened in.</item>
    /// <item><b>The shooter was killed during the round trip.</b> The
    /// authority never runs the shot at all: its copy of that player was
    /// already dead when the intent arrived, so a dead player's presses do
    /// nothing. This is the one a player calls unfair rather than laggy --
    /// they watched the shot land, watched the body drop, and then watched it
    /// stand back up because the authority had killed them first.</item>
    /// </list>
    ///
    /// <b>What a claim is not.</b> It is not the client being trusted. Five
    /// things are checked before one is believed -- see <see cref="Judge"/> --
    /// and the geometric one is the whole point: the authority looks the
    /// victim up in its own rewind history, at the frame the claim names, and
    /// refuses a claim whose hit point is nowhere near the body it finds
    /// there. A claim can only ever rescue a hit that the authority's own
    /// history says was there to be had.
    ///
    /// <b>And it is exactly reciprocal.</b> Every client's claims go through
    /// the same code with the same tolerances, so a player with a bad line is
    /// not given an advantage by it, they are given back what the line took.
    /// The authority's own player claims nothing, because it has nothing to
    /// claim: it already resolves its shots against the puppets it holds.
    ///
    /// <b>The arbitration.</b> A claim carries the world-frame its shooter
    /// was looking at, and that is what two people who killed each other are
    /// separated by. A shot is void if its shooter had already been killed
    /// <i>in that clock</i> -- by a shot aimed at a strictly earlier world --
    /// and counts otherwise, including when the shooter is dead on the
    /// authority by the time the claim arrives. Two shots aimed at the same
    /// world both count: a trade, which is the honest answer to a trade.
    /// Because the ordering is by the shooters' own stamps and not by which
    /// datagram won the race, the outcome does not depend on arrival order --
    /// which is why claims are resolved in fire-frame order at the end of
    /// their grace window rather than one at a time as they land.
    /// </summary>
    public static class NetHitClaims
    {
        private const int Slots = PlayerEntity.SlotCapacity;

        /// <summary>
        /// Whether a client declares its hits at all. On restores exactly what
        /// protocol 6 did: the shooter predicts, the authority resolves, and
        /// where the two disagree the authority wins in silence.
        /// </summary>
        public static bool Enabled { get; set; } = true;

        /// <summary>
        /// How far apart the two machines' copies of the victim may be, in
        /// units, before a claim about them is refused.
        ///
        /// <b>What is actually compared.</b> The claim carries where the
        /// <i>shooter's</i> copy of the victim stood when the hit landed, and
        /// the authority compares it against where its own history says that
        /// player stood in the frame the shooter was looking at. So this is
        /// not a tolerance on aim -- it is the one question worth asking of a
        /// claim, which is whether the two machines were looking at the same
        /// player in the same place. Anything else the shooter might claim --
        /// the trajectory, the range, the geometry of the room -- it computed
        /// itself and the authority cannot check without re-running the shot,
        /// which it cannot do: that shot happened on another machine, at
        /// another charge level, with another set of pickups.
        ///
        /// 2.0 units against a hunter about 0.90 across and 1.60 tall: it
        /// covers a client whose puppet is a frame or two of physics away from
        /// the authority's (measured at up to 0.377 units a frame for a player
        /// in the air) and refuses a claim about somebody two body-lengths
        /// from where they were.
        /// </summary>
        public const float ClaimRadius = 2.0f;

        /// <summary>
        /// The same, for a hit with no beam behind it: an alt form's scythe,
        /// spin or trail, and a bomb. These land at arm's length or in a
        /// blast, so the two copies of the victim have further to be apart
        /// before the claim stops being about the same event.
        /// </summary>
        public const float MeleeRadius = 4.0f;

        /// <summary>
        /// How long a validated claim is held before it is applied, in frames.
        ///
        /// It exists to stop the same hit landing twice. The authority is
        /// simulating the same shot from the same intent, and its own answer
        /// arrives around the same time as the claim -- earlier for a hitscan
        /// weapon, later for anything that travels. Applying a claim the
        /// moment it arrives would therefore double the damage on every shot
        /// the authority was going to resolve anyway, which is almost all of
        /// them.
        ///
        /// So a claim waits, and is dropped the moment the authority resolves
        /// a hit from that shooter on that victim -- in the window before it
        /// arrived as well as the window after, since a hitscan weapon's own
        /// answer comes first. What is left at the end of the window is a hit
        /// the authority was never going to find, which is the only kind worth
        /// applying.
        ///
        /// <b>It has to be one round trip, and eighteen frames was not.</b> The
        /// gap between the authority resolving a shot itself and the claim for
        /// that same shot arriving is <i>not</i> the same for every weapon,
        /// and the difference is the whole of the "missiles hit twice" report:
        ///
        /// <list type="bullet">
        /// <item><b>Hitscan</b> -- the Imperialist at 200 units a frame -- is
        /// resolved by the catch-up loop on the frame it is spawned, which is
        /// one *upstream* trip after the shooter fired. The claim arrives a
        /// full round trip after that same moment, so the two are separated by
        /// the <i>downstream</i> half alone: 7-8 frames at 250 ms.</item>
        /// <item><b>Anything that travels</b> -- the Missile at 0.25 units a
        /// frame, the Magmaul at 1.7 -- is only advanced by the rewind depth
        /// at spawn and then flies in real time, so the authority resolves it
        /// at the same world-frame the shooter did. The claim then arrives a
        /// <i>full</i> round trip later: 15 frames at 250 ms, 24 at 400.</item>
        /// </list>
        ///
        /// The flight time cancels in both cases; what differs is that the
        /// hitscan weapon gets half a trip for free from the catch-up. A fixed
        /// 18-frame window therefore matched every Imperialist hit and started
        /// missing missiles somewhere past 300 ms -- and a missed match is the
        /// authority's own hit <i>and</i> the rescued claim, both applied: the
        /// victim takes the damage, then takes it again when the shot they can
        /// see finally arrives.
        ///
        /// So it is a round trip, measured, plus a margin -- not a constant.
        /// The shooter waits no longer either way, because their own screen
        /// showed the hit when they fired it; what waits is the rescue, and a
        /// rescue is a hit that would otherwise never land at all.
        /// </summary>
        public static int GraceFor(int slot)
        {
            int ping = slot >= 0 && slot < NetSession.SlotPing.Length
                ? NetSession.SlotPing[slot]
                : 0;
            return Math.Clamp((int)(ping * 0.06f) + 20, MinGraceFrames, MaxGraceFrames);
        }

        /// <summary>
        /// The floor, for a shooter whose round trip is not measured yet, and
        /// the ceiling, past which waiting costs the victim more than the
        /// double did.
        /// </summary>
        /// <summary>
        /// The floor, for a shooter whose round trip is not measured yet, and
        /// the ceiling, past which waiting costs the victim more than the
        /// double did.
        ///
        /// The margin of 20 frames on top of the round trip is measured
        /// rather than guessed: at 400 ms the gap between the authority
        /// resolving a shot and the claim for it arriving ran from **-41 to
        /// +34 frames** over sixteen samples -- negative meaning the
        /// authority answered *after* the claim landed, which happens when
        /// its own copy of a slow projectile is behind the shooter's. A round
        /// trip is 24 frames there, so 24 + 20 covers both tails.
        /// </summary>
        public const int MinGraceFrames = 24;
        public const int MaxGraceFrames = 72;

        /// <summary>
        /// What the client assumes the authority is waiting, so that the hold
        /// it keeps on the victim's health outlives the rescue. Its own round
        /// trip, by the same formula -- the authority is computing this from
        /// the same ping.
        /// </summary>
        public static int GraceFrames => GraceFor(NetSession.LocalSlot);

        /// <summary>
        /// How old a claim may be when it arrives. Past this the history
        /// cannot serve the frame it names, so there is nothing to check it
        /// against -- and a claim that cannot be checked is refused rather
        /// than believed.
        /// </summary>
        private const int MaxClaimAge = NetUnlagged.HistoryFrames - 4;

        // ---------------------------------------------------------------
        // The shooter's side
        // ---------------------------------------------------------------

        private const int OutboxCapacity = 32;

        private struct Outgoing
        {
            public ushort MatchId;
            public ulong AuthorityEpoch;
            public ushort ShooterGeneration;
            public ushort ShooterLifeId;
            public ushort VictimGeneration;
            public ushort VictimLifeId;

            public ushort Id;
            public uint Frame;
            public uint AckFrame;
            public uint LaunchFrame;
            public byte VictimSlot;
            public byte Beam;
            public ushort Damage;
            public byte Flags;
            public Vector3 HitPoint;
            /// <summary>Frames since it was first sent, for the retry budget.</summary>
            public int Age;
            public int Sends;
            public bool Live;
        }

        private static readonly Outgoing[] _outbox = new Outgoing[OutboxCapacity];
        private static ushort _nextId = 1;

        /// <summary>
        /// How many times an unanswered claim is repeated, and how long it
        /// keeps trying. UDP loses packets and a lost claim is a kill that did
        /// not happen, so this is the same reasoning as
        /// <see cref="IntentPacket.PressHistory"/> -- repeat until told to
        /// stop. Six sends over 90 frames survives three consecutive drops on
        /// a line that is losing one datagram in twenty, and 90 frames is past
        /// any round trip the game is worth playing on.
        /// </summary>
        private const int MaxSends = 6;
        /// <summary>
        /// How long an unanswered claim is kept before it is given up on.
        ///
        /// Six sends at up to forty frames apart would run past any hold, so
        /// this is the real bound on how long a claim is asked about: one
        /// second, whichever of the two runs out first. It is deliberately
        /// **shorter than the longest
        /// <c>NetHitPrediction.HoldFrames</c>**, because giving up is what
        /// releases the prediction the claim was holding: a lethal prediction
        /// nobody ever answers holds a body down, and the sooner that decision
        /// goes back to the authority the shorter the wrong picture lasts.
        /// </summary>
        private const int MaxAge = 60;
        /// <summary>
        /// The least time between repeats of the same claim, in frames, when
        /// the round trip is not known yet.
        /// </summary>
        private const int MinResendInterval = 8;

        /// <summary>
        /// How long to wait before asking again.
        ///
        /// **A round trip, not a fixed eight frames.** Eight is 133 ms, and at
        /// the 320 ms line this work is about the first repeat always goes out
        /// before the first answer can possibly arrive -- measured at five
        /// repeats against ten claims received, which is half the traffic
        /// spent on questions that were already being answered. The measured
        /// ping plus a snapshot's gap is the earliest a resend can mean
        /// anything; before a ping exists the floor stands in for it.
        /// </summary>
        private static int ResendInterval
        {
            get
            {
                int slot = NetSession.LocalSlot;
                int ping = slot >= 0 && slot < NetSession.SlotPing.Length
                    ? NetSession.SlotPing[slot]
                    : 0;
                return Math.Clamp((int)(ping * 0.06f) + 6, MinResendInterval, 40);
            }
        }

        /// <summary>Claims made, answered, and how the answers came back.</summary>
        public static long Declared { get; private set; }
        public static long Applied { get; private set; }
        public static long Duplicate { get; private set; }
        public static long RefusedDeadShooter { get; private set; }
        public static long RefusedDeadVictim { get; private set; }
        public static long RefusedOther { get; private set; }
        public static long Unanswered { get; private set; }
        public static long Resends { get; private set; }

        /// <summary>
        /// Whether this machine has somewhere to send a claim. The authority
        /// has not: it resolves its own shots against the puppets it holds, so
        /// there is nothing for anybody to arbitrate.
        /// </summary>
        public static bool Claiming => Enabled && NetSession.Active
            && !NetSession.IsAuthority && !NetSession.IsHost
            && NetSession.LocalSlot >= 0;

        /// <summary>
        /// Record a hit this machine has just resolved for its own player, so
        /// that the authority is told about it.
        ///
        /// Called from <see cref="NetHitPrediction.NoteHit"/>, which is the
        /// one point at which the damage is final and the victim is known. A
        /// hit on this machine's own player is not claimed: it is either the
        /// player's own splash, which the authority computes identically from
        /// the same inputs, or somebody else's shot, which is theirs to claim.
        /// </summary>
        /// <returns>
        /// The id this claim was filed under, or zero when nothing was
        /// claimed. The caller stamps it onto the prediction it has just
        /// filed, so the verdict can retire that exact one --
        /// <see cref="NetHitPrediction.Settle"/>.
        /// </returns>
        public static ushort Declare(PlayerEntity victim, PlayerEntity attacker,
            BeamType beam, uint damage, DamageFlags flags, bool lethal, Vector3 hitPoint,
            uint launchFrame)
        {
            if (!Claiming || victim == attacker || damage == 0
                || NetPlayerLifecycle.Get(victim.SlotIndex) == 0 || NetPlayerLifecycle.Get(attacker.SlotIndex) == 0)
            {
                return 0;
            }
            int slot = victim.SlotIndex;
            if (slot < 0 || slot >= Slots)
            {
                return 0;
            }
            byte claimFlags = 0;
            if (flags.TestFlag(DamageFlags.Headshot))
            {
                claimFlags |= HitClaimPacket.FlagHeadshot;
            }
            if (lethal)
            {
                claimFlags |= HitClaimPacket.FlagLethal;
            }
            // The three afflictions travel with the claim because they are
            // produced inside TakeDamage from the beam entity, and a claim
            // applied on the authority has no beam to produce them from. The
            // snapshot's flag byte then carries them to everybody else, as it
            // already does for a hit the authority resolved itself.
            if (victim.ModFrozen)
            {
                claimFlags |= HitClaimPacket.FlagFrozen;
            }
            int index = -1;
            for (int i = 0; i < OutboxCapacity; i++)
            {
                if (!_outbox[i].Live)
                {
                    index = i;
                    break;
                }
            }
            if (index < 0)
            {
                // Full. The oldest is the one least likely to still be worth
                // answering, and dropping it is better than dropping the hit
                // that just happened.
                int oldest = 0;
                for (int i = 1; i < OutboxCapacity; i++)
                {
                    if (_outbox[i].Age > _outbox[oldest].Age)
                    {
                        oldest = i;
                    }
                }
                index = oldest;
                Unanswered++;
            }
            // The same world the intent is acking, and it has to be: the
            // authority checks this claim by looking the victim up in its own
            // history at this number, so a claim naming one frame while the
            // shooter's screen was showing another is a claim checked against
            // the wrong picture. When the playout clock is running, the read
            // point is what the screen was showing -- NetSmoothing -- and the
            // applied snapshot is a frame or more newer than it.
            uint ack = NetSmoothing.AckPoint(out uint readFrame, out _)
                ? readFrame
                : NetSession.AppliedSnapshotFrame;
            _outbox[index] = new Outgoing
            {
                MatchId = NetSession.CurrentMatchId,
                AuthorityEpoch = NetSession.AuthorityEpoch,
                ShooterGeneration = NetPlayerLifecycle.Generation(attacker.SlotIndex),
                ShooterLifeId = NetPlayerLifecycle.Get(attacker.SlotIndex),
                VictimGeneration = NetPlayerLifecycle.Generation(slot),
                VictimLifeId = NetPlayerLifecycle.Get(slot),
                Id = _nextId,
                Frame = NetSession.NetFrame,
                AckFrame = ack,
                LaunchFrame = launchFrame,
                VictimSlot = (byte)slot,
                Beam = beam == BeamType.None ? HitClaimPacket.NoBeam : (byte)beam,
                Damage = (ushort)Math.Min(damage, UInt16.MaxValue),
                Flags = claimFlags,
                HitPoint = hitPoint,
                Age = 0,
                Sends = 0,
                Live = true
            };
            ushort id = _nextId;
            _nextId++;
            if (_nextId == 0)
            {
                _nextId = 1;
            }
            Declared++;
            NetShotDiagnostics.Claims[NetShotDiagnostics.Bucket(beam)]++;
            if (NetLog.Enabled) NetShotDiagnostics.Trace("claim", ShotKey.For(attacker.SlotIndex, launchFrame), beam,
                $"id={id} victim={slot} ack={ack} damage={damage}");
            return id;
        }

        /// <summary>
        /// Pack the claims that still want an answer into a datagram. Returns
        /// the bytes written, or zero when there is nothing to say -- which is
        /// every frame of a match where the authority is agreeing with
        /// everything, so the cost of this feature on a healthy line is one
        /// comparison a frame.
        /// </summary>
        public static int Compose(Span<byte> dest)
        {
            if (!Claiming)
            {
                return 0;
            }
            int count = 0;
            int offset = 1;
            // Read once: it is a property over the ping table and this loop
            // runs sixty times a second.
            int interval = ResendInterval;
            for (int i = 0; i < OutboxCapacity && count < HitClaimPacket.MaxPerPacket; i++)
            {
                ref Outgoing entry = ref _outbox[i];
                if (!entry.Live)
                {
                    continue;
                }
                if (entry.Sends > 0
                    && (entry.Sends >= MaxSends || entry.Age % interval != 0))
                {
                    continue;
                }
                var packet = new HitClaimPacket
                {
                    MatchId = entry.MatchId,
                    AuthorityEpoch = entry.AuthorityEpoch,
                    ShooterGeneration = entry.ShooterGeneration,
                    ShooterLifeId = entry.ShooterLifeId,
                    VictimGeneration = entry.VictimGeneration,
                    VictimLifeId = entry.VictimLifeId,
                    ClaimId = entry.Id,
                    Frame = entry.Frame,
                    AckFrame = entry.AckFrame,
                    LaunchFrame = entry.LaunchFrame,
                    VictimSlot = entry.VictimSlot,
                    Beam = entry.Beam,
                    Damage = entry.Damage,
                    Flags = entry.Flags,
                    HitPoint = entry.HitPoint
                };
                packet.Write(dest[offset..]);
                offset += HitClaimPacket.Size;
                if (entry.Sends > 0)
                {
                    Resends++;
                }
                entry.Sends++;
                count++;
            }
            if (count == 0)
            {
                return 0;
            }
            dest[0] = (byte)count;
            return offset;
        }

        /// <summary>Age the outbox. One call a frame from the client.</summary>
        private static void TickOutbox()
        {
            for (int i = 0; i < OutboxCapacity; i++)
            {
                ref Outgoing entry = ref _outbox[i];
                if (!entry.Live)
                {
                    continue;
                }
                entry.Age++;
                if (entry.Age > MaxAge)
                {
                    entry.Live = false;
                    Unanswered++;
                    if (NetLog.Enabled) NetShotDiagnostics.Trace("unanswered",
                        new ShotKey(entry.AuthorityEpoch, entry.MatchId, NetSession.LocalSlot, entry.ShooterGeneration, entry.ShooterLifeId, entry.LaunchFrame),
                        (BeamType)entry.Beam, $"id={entry.Id} reason=verdict-timeout sends={entry.Sends}");
                    // Six sends and no answer. The claim may still have landed
                    // -- it is the verdict that went missing, not necessarily
                    // the claim -- so this is not "the hit did not happen", it
                    // is "stop holding the picture on the strength of it and
                    // let the authority's snapshots decide", which is what
                    // every build before claims did. A lethal prediction left
                    // holding is a body on the floor for the whole of the hold
                    // window.
                    NetHitPrediction.Settle(entry.VictimSlot, entry.Id, confirmed: false);
                }
            }
        }

        /// <summary>
        /// One or more verdicts from the authority.
        ///
        /// A verdict does not tell the shooter whether the hit landed -- the
        /// snapshot has always carried that. It tells the shooter it may stop
        /// asking, and on a refusal it says so within a round trip instead of
        /// leaving the prediction to time out over two seconds with the
        /// victim's health held wrong for the whole of it.
        /// </summary>
        public static void ApplyVerdicts(ReadOnlySpan<byte> payload)
        {
            if (payload.Length < 1)
            {
                return;
            }
            if (payload.Length < HitVerdictPacket.HeaderSize) return;
            if (!NetSession.MatchesStream(System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(payload[1..]),
                System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(payload[3..]))
                || !NetPlayerLifecycle.Matches(NetSession.LocalSlot,
                    System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(payload[11..]),
                    System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(payload[13..]))) return;
            int count = Math.Min((int)payload[0], HitVerdictPacket.MaxPerPacket);
            for (int i = 0; i < count; i++)
            {
                int at = HitVerdictPacket.HeaderSize + i * HitVerdictPacket.EntrySize;
                if (at + HitVerdictPacket.EntrySize > payload.Length)
                {
                    break;
                }
                ushort id = (ushort)(payload[at] | (payload[at + 1] << 8));
                byte result = payload[at + 2];
                for (int j = 0; j < OutboxCapacity; j++)
                {
                    ref Outgoing entry = ref _outbox[j];
                    if (!entry.Live || entry.Id != id
                        || !NetPlayerLifecycle.Matches(entry.VictimSlot, entry.VictimGeneration, entry.VictimLifeId))
                    {
                        continue;
                    }
                    entry.Live = false;
                    int weapon = NetShotDiagnostics.Bucket((BeamType)entry.Beam);
                    if (result == HitVerdictPacket.ResultApplied) NetShotDiagnostics.Rescues[weapon]++;
                    else if (result != HitVerdictPacket.ResultDuplicate) NetShotDiagnostics.Refusals[weapon]++;
                    if (NetLog.Enabled) NetShotDiagnostics.Trace("verdict",
                        new ShotKey(entry.AuthorityEpoch, entry.MatchId, NetSession.LocalSlot, entry.ShooterGeneration, entry.ShooterLifeId, entry.LaunchFrame),
                        (BeamType)entry.Beam, $"id={id} result={HitVerdictPacket.Describe(result)}");
                    switch (result)
                    {
                    case HitVerdictPacket.ResultApplied:
                        Applied++;
                        // The authority has the damage. Retire the prediction
                        // this claim was declared under, by name: the snapshot
                        // cannot do it -- it reports only the *last* attacker
                        // on a victim, so a hit of this machine's own followed
                        // by somebody else's inside one snapshot window is
                        // never matched, and its debit goes on being taken off
                        // a health the authority has already taken it off.
                        // NetHitPrediction._pendingClaim.
                        NetHitPrediction.Settle(entry.VictimSlot, id, confirmed: true);
                        break;
                    case HitVerdictPacket.ResultDuplicate:
                        Duplicate++;
                        NetHitPrediction.Settle(entry.VictimSlot, id, confirmed: true);
                        break;
                    case HitVerdictPacket.ResultDeadShooter:
                        RefusedDeadShooter++;
                        // The arbitration said somebody got there first. The
                        // prediction this machine is holding is wrong and the
                        // sooner it is let go the shorter the wrong health bar
                        // lasts -- which is the whole reason a verdict exists.
                        NetHitPrediction.Settle(entry.VictimSlot, id, confirmed: false);
                        break;
                    case HitVerdictPacket.ResultDeadVictim:
                        RefusedDeadVictim++;
                        NetHitPrediction.Settle(entry.VictimSlot, id, confirmed: false);
                        break;
                    default:
                        RefusedOther++;
                        NetHitPrediction.Settle(entry.VictimSlot, id, confirmed: false);
                        NetLog.Event($"claim {id} on slot {entry.VictimSlot} "
                            + $"{HitVerdictPacket.Describe(result)}");
                        break;
                    }
                    break;
                }
            }
        }

        // ---------------------------------------------------------------
        // The authority's side
        // ---------------------------------------------------------------

        private const int PendingCapacity = 64;

        private struct Pending
        {
            public ushort MatchId;
            public ulong AuthorityEpoch;
            public ushort ShooterGeneration;
            public ushort ShooterLifeId;
            public ushort VictimGeneration;
            public ushort VictimLifeId;

            public ushort Id;
            public byte ShooterSlot;
            public byte VictimSlot;
            public byte Beam;
            public ushort Damage;
            public byte Flags;
            public uint AckFrame;
            public uint LaunchFrame;
            public Vector3 HitPoint;
            /// <summary>The authority frame it arrived on.</summary>
            public uint Arrived;
            /// <summary>
            /// How long this claim waits before it is applied, in frames --
            /// one measured round trip for *this* shooter plus a margin. A
            /// number per claim rather than a constant, because the gap
            /// between the authority's own answer and the claim for the same
            /// shot is that shooter's round trip. <see cref="GraceFor"/>.
            /// </summary>
            public int Grace;
            public bool Live;
        }

        private static readonly Pending[] _pending = new Pending[PendingCapacity];

        /// <summary>
        /// The last claim id accepted from each slot, so that a repeat of a
        /// claim already dealt with is answered again rather than applied
        /// again. Ids rise, so "not newer than the newest" is the whole test
        /// -- with the same restart allowance every other ordering guard on
        /// this wire has, since a client that rejoins starts counting from one
        /// and would otherwise have every claim of its new session refused.
        /// </summary>
        private static readonly ushort[] _newestId = new ushort[Slots];
        private static readonly byte[] _lastResult = new byte[Slots];
        private const int SeenCapacity = 128;
        private const byte ResultPending = 255;
        private static readonly ushort[,] _seenIds = new ushort[Slots, SeenCapacity];
        private static readonly byte[,] _seenResults = new byte[Slots, SeenCapacity];
        private static readonly BeamType[,] _seenBeams = new BeamType[Slots, SeenCapacity];
        private static readonly ShotKey[,] _seenKeys = new ShotKey[Slots, SeenCapacity];

        /// <summary>
        /// Hits the authority resolved itself, for the duplicate test: the
        /// last few frames on which each shooter landed something on each
        /// victim, and whether a claim has already been matched against each.
        ///
        /// <b>A ring rather than one number, and that is not tidiness.</b> The
        /// newest hit alone is enough for a weapon that fires once a second
        /// and wrong for every other kind: a Battlehammer burst puts four
        /// claims into one grace window, and matching all four against the one
        /// hit the authority happens to have resolved throws away three hits
        /// that really did go missing. Each authority hit retires at most one
        /// claim, which is the arithmetic that makes "already resolved" mean
        /// what the report says it means.
        ///
        /// Eight deep per pair: the fastest weapon in the game resolves a hit
        /// every other frame, so eight covers the 18-frame grace window at the
        /// rate anything can actually be fired, and the whole table is 8x8x8
        /// frames plus a flag each.
        /// </summary>
        private const int LedgerDepth = 8;
        private static readonly uint[,,] _authorityHit = new uint[Slots, Slots, LedgerDepth];
        /// <summary>
        /// The world-frame the shooter was looking at when the authority
        /// resolved each of its own hits -- the same quantity a claim carries
        /// in <see cref="HitClaimPacket.AckFrame"/>.
        ///
        /// <b>This is what a hit is matched by, and the clock is not.</b> Two
        /// copies of one shot are resolved at the same world-frame by
        /// construction -- that is what the rewind and the catch-up are for --
        /// however far apart the two machines answer in wall time and however
        /// long the projectile was in the air. Matching on arrival time
        /// instead meant a window that had to be guessed, and a window that is
        /// too small applies the authority's hit and the claim both.
        /// </summary>
        private static readonly uint[,,] _authorityHitAck = new uint[Slots, Slots, LedgerDepth];
        /// <summary>
        /// The launch frame of the shot behind each of the authority's own
        /// hits -- <c>BeamProjectileEntity.ModLaunchFrame</c>. Zero for a hit
        /// with no beam behind it.
        /// </summary>
        private static readonly uint[,,] _authorityHitLaunch = new uint[Slots, Slots, LedgerDepth];
        private static readonly bool[,,] _authorityHitUsed = new bool[Slots, Slots, LedgerDepth];
        /// <summary>
        /// What the authority's own simulation put on the victim for each of
        /// those hits.
        ///
        /// Kept for one purpose: a claim carries the number the *shooter*
        /// computed for the same shot, so when the two are paired the pair is
        /// a direct measurement of whether the shooter's prediction and the
        /// authority's resolution agree about the damage -- per weapon, in a
        /// real match, with no instrument in the way. They should agree
        /// exactly, because both machines run the same table over the same
        /// state; everywhere they do not, one of them is reading a quantity
        /// the other has not been told (the charge tier, a powerup, the damage
        /// level, a halfturret's health). See <see cref="DescribeAgreement"/>.
        /// </summary>
        private static readonly int[,,] _authorityHitDamage = new int[Slots, Slots, LedgerDepth];
        private static readonly int[,] _authorityHitHead = new int[Slots, Slots];

        /// <summary>
        /// How the shooter's number compared with the authority's, per weapon,
        /// for every pair the ledger matched. The bucket is the beam, with
        /// <see cref="NetHitPrediction.AltBeam"/> for everything with no beam
        /// behind it.
        /// </summary>
        private static readonly long[] _agreeByBeam = new long[NetHitPrediction.AltBeam + 1];
        private static readonly long[] _differByBeam = new long[NetHitPrediction.AltBeam + 1];
        private static readonly long[] _claimedByBeam = new long[NetHitPrediction.AltBeam + 1];
        private static readonly long[] _resolvedByBeam = new long[NetHitPrediction.AltBeam + 1];
        private static int _disagreementsLogged;

        /// <summary>
        /// How far two acks may differ and still possibly be the same shot.
        ///
        /// <b>A loose sanity bound, not the match.</b> It was tried as the
        /// match -- eight frames -- on the reasoning that two copies of one
        /// shot resolve at the same world-frame, and that is true of the shot
        /// but not of what either machine can stamp it with. The authority's
        /// stamp is <see cref="FireFrameOf"/>, the shooter's ack as the
        /// authority currently knows it, which for anything that travels was
        /// sampled long after the trigger and is a further downstream trip
        /// behind the shooter's own. Measured at 400 ms: the two differ by
        /// more than eight frames on nearly every shot, the match failed
        /// almost always, and applications went *up* from 7 to 13 -- the
        /// opposite of the fix. Identifying the shot properly means carrying
        /// the rewind target from <c>BeginShot</c> through to the beam's
        /// impact, which nothing does yet.
        ///
        /// So the match is back on arrival time, sized from the round trip,
        /// and this only stops a claim pairing with a hit from a completely
        /// different exchange.
        /// </summary>
        private const int AckMatchFrames = 120;

        /// <summary>
        /// How far two launch frames may differ and still be one shot. Both
        /// machines name the same instant, so this is slack for the frame the
        /// stamp was taken on and nothing more.
        /// </summary>
        private const int LaunchMatchFrames = 4;

        /// <summary>
        /// File a hit the authority resolved for itself, so that a claim about
        /// the same one is recognised as a repeat rather than applied twice.
        /// </summary>
        private static void NoteLedger(int attacker, int victim, uint ack, uint launch,
            int damage, bool used = false)
        {
            int head = _authorityHitHead[attacker, victim];
            _authorityHit[attacker, victim, head] = NetSession.NetFrame;
            _authorityHitAck[attacker, victim, head] = ack;
            _authorityHitLaunch[attacker, victim, head] = launch;
            _authorityHitDamage[attacker, victim, head] = damage;
            _authorityHitUsed[attacker, victim, head] = used;
            _authorityHitHead[attacker, victim] = (head + 1) % LedgerDepth;
        }

        /// <summary>
        /// Whether the authority has an unclaimed hit of its own from
        /// <paramref name="attacker"/> on <paramref name="victim"/> inside this
        /// claim's window, and take it if so.
        ///
        /// The window runs from <see cref="GraceFrames"/> <i>before</i> the
        /// claim arrived to the present, because a hitscan weapon's own answer
        /// reaches the authority first and anything that travels reaches it
        /// after.
        /// </summary>
        /// <summary>
        /// How far the nearest hit this authority resolved itself, for this
        /// pair, is from the claim's arrival -- searched over the whole ring
        /// rather than the match window, and counting used entries too.
        /// Positive means the authority resolved it *before* the claim landed.
        /// Int32.MinValue when there is nothing at all.
        /// </summary>
        private static int NearestLedgerOffset(int attacker, int victim, uint arrived)
        {
            int best = Int32.MinValue;
            for (int i = 0; i < LedgerDepth; i++)
            {
                uint at = _authorityHit[attacker, victim, i];
                if (at == 0)
                {
                    continue;
                }
                int offset = (int)arrived - (int)at;
                if (best == Int32.MinValue || Math.Abs(offset) < Math.Abs(best))
                {
                    best = offset;
                }
            }
            return best;
        }

        private static bool TakeLedger(int attacker, int victim, uint claimAck,
            uint claimLaunch, uint arrived, int window, out int authorityDamage)
        {
            authorityDamage = 0;
            // Pass one: the shot's own stamp.
            //
            // Two copies of one shot carry the same launch frame by
            // construction -- the authority stamps the world it rewound to,
            // the shooter the point its playout clock was reading -- so this
            // is exact, and it does not care how long the projectile was in
            // the air or how far apart the two players were. Everything the
            // time window could not do is done here.
            //
            // A direct hit and its splash are two hits from one beam and
            // therefore share a launch frame: two claims, two ledger entries,
            // one consumed each. That is why an entry is taken rather than
            // merely matched.
            if (claimLaunch != 0)
            {
                for (int i = 0; i < LedgerDepth; i++)
                {
                    if (_authorityHitUsed[attacker, victim, i]
                        || _authorityHit[attacker, victim, i] == 0)
                    {
                        continue;
                    }
                    uint launch = _authorityHitLaunch[attacker, victim, i];
                    if (launch != 0 && Math.Abs((long)launch - claimLaunch) <= LaunchMatchFrames)
                    {
                        _authorityHitUsed[attacker, victim, i] = true;
                        authorityDamage = _authorityHitDamage[attacker, victim, i];
                        MatchedByLaunch++;
                        return true;
                    }
                }
            }
            // Pass two: the time window, for the hits that carry no stamp --
            // an alt form's attack, a bomb -- and for a beam whose stamp did
            // not survive. Inexact by construction; see LaunchFrame.
            uint floor = arrived > (uint)window ? arrived - (uint)window : 0;
            int best = -1;
            long bestGap = Int64.MaxValue;
            for (int i = 0; i < LedgerDepth; i++)
            {
                if (_authorityHitUsed[attacker, victim, i])
                {
                    continue;
                }
                uint at = _authorityHit[attacker, victim, i];
                // Inside the window either side of the claim's arrival: the
                // authority answers *before* it for a hitscan weapon and can
                // answer after it for a slow one.
                if (at == 0 || at < floor || at > arrived + (uint)window)
                {
                    continue;
                }
                // Never pair two shots that both carry a stamp and disagree:
                // pass one already had its chance, and matching them here
                // would be the window overruling the exact answer.
                uint launch = _authorityHitLaunch[attacker, victim, i];
                if (launch != 0 && claimLaunch != 0)
                {
                    continue;
                }
                long gap = Math.Abs((long)_authorityHitAck[attacker, victim, i] - claimAck);
                if (gap <= AckMatchFrames && gap < bestGap)
                {
                    bestGap = gap;
                    best = i;
                }
            }
            if (best < 0)
            {
                return false;
            }
            _authorityHitUsed[attacker, victim, best] = true;
            authorityDamage = _authorityHitDamage[attacker, victim, best];
            MatchedByWindow++;
            return true;
        }

        private static void ClearLedger(int attacker, int victim)
        {
            for (int i = 0; i < LedgerDepth; i++)
            {
                _authorityHit[attacker, victim, i] = 0;
                _authorityHitAck[attacker, victim, i] = 0;
                _authorityHitLaunch[attacker, victim, i] = 0;
                _authorityHitDamage[attacker, victim, i] = 0;
                _authorityHitUsed[attacker, victim, i] = false;
            }
            _authorityHitHead[attacker, victim] = 0;
        }

        /// <summary>
        /// The world-frame of the shot that killed each slot, and whether the
        /// slot is currently dead. This is the arbitration's whole state: a
        /// shot is void if its shooter is down and was put down by a shot
        /// aimed at a strictly earlier world than the one this shot was aimed
        /// at.
        /// </summary>
        private static readonly uint[] _deathFire = new uint[Slots];
        private static readonly bool[] _dead = new bool[Slots];
        /// <summary>
        /// The world-frame of the newest hit on each slot, stamped as it is
        /// resolved. Whatever is standing here when a slot goes down is the
        /// stamp of the shot that killed it.
        /// </summary>
        private static readonly uint[] _lastHitFire = new uint[Slots];
        private static readonly bool[] _wasInPlay = new bool[Slots];

        /// <summary>
        /// True while <see cref="ApplyOne"/> is inside the one
        /// <c>TakeDamage</c> that makes a claim real, with the world-frame it
        /// is applying. <see cref="NoteAuthorityHit"/> reads both so that the
        /// ledger entry this produces is filed under the claim's own ack and
        /// marked used -- it is the authority's hit now, and nothing else may
        /// match against it.
        /// </summary>
        private static bool ApplyingClaim;
        private static uint ApplyingClaimAck;
        private static uint ApplyingClaimLaunch;
        internal static uint CurrentClaimLaunch => ApplyingClaim ? ApplyingClaimLaunch : 0;

        /// <summary>What the authority did with the claims it was sent.</summary>
        public static long Received { get; private set; }
        public static long AppliedHere { get; private set; }
        public static long DuplicateHere { get; private set; }
        public static long VoidedDeadShooter { get; private set; }
        public static long VoidedDeadVictim { get; private set; }
        public static long RefusedHere { get; private set; }
        public static long TooOldHere { get; private set; }
        /// <summary>
        /// Claims that arrived again because their answer did not. Counted so
        /// that the report's outcomes add up to what was received: without it
        /// a line reading "8 received, 1 applied, 2 already resolved" leaves
        /// five unaccounted for and looks like lost claims rather than
        /// repeated ones.
        /// </summary>
        public static long RepeatsHere { get; private set; }
        /// <summary>
        /// How each duplicate was recognised. The pair is the health check on
        /// this whole mechanism: <see cref="MatchedByLaunch"/> should be
        /// almost all of it, and <see cref="MatchedByWindow"/> should be the
        /// alt-form attacks and bombs alone. Window matches climbing for beam
        /// weapons means the stamp is not surviving the trip.
        /// </summary>
        public static long MatchedByLaunch { get; private set; }
        public static long MatchedByWindow { get; private set; }
        public static long RescuedDamage { get; private set; }
        public static long RescuedKills { get; private set; }
        public static long RescuedHeadshots { get; private set; }
        /// <summary>
        /// Imperialist headshots the shooter resolved as lethal but the
        /// authority paired with a lower-damage body hit for the same shot.
        /// The claim is already geometry/lifecycle validated before it reaches
        /// this counter; only the missing damage is applied.
        /// </summary>
        public static long HeadshotCorrections { get; private set; }

        /// <summary>
        /// Whether this machine is the one that arbitrates. The same test
        /// <see cref="NetUnlagged"/> makes, and for the same reason: only the
        /// machine composing the snapshots has a history to check a claim
        /// against.
        /// </summary>
        public static bool Arbitrating => Enabled && NetSession.Active
            && (NetSession.Role == NetRole.Host || NetSession.IsAuthority);

        /// <summary>
        /// Stamp the world-frame a hit was aimed at, for the arbitration.
        /// Called from <see cref="NetDamage.Note"/>, which the authority runs
        /// for every hit it resolves, and directly by <see cref="ApplyOne"/>
        /// for a hit it is rescuing.
        /// </summary>
        public static void NoteAuthorityHit(int attackerSlot, int victimSlot,
            uint launchFrame = 0, int damage = 0)
        {
            if (!Arbitrating || victimSlot < 0 || victimSlot >= Slots)
            {
                return;
            }
            // The world this hit was aimed in. For a beam that is the frame it
            // was launched in, not the frame its shooter's screen was showing
            // when it arrived -- see the arbitration in Judge for why the
            // difference is the whole of the Missile and Magmaul complaint.
            // FireFrameOf is the fallback for a hit with no beam behind it.
            uint fire = ApplyingClaim ? ApplyingClaimAck : FireFrameOf(attackerSlot);
            if (ApplyingClaim) launchFrame = ApplyingClaimLaunch;
            _lastHitFire[victimSlot] = launchFrame != 0 ? launchFrame : fire;
            if (attackerSlot >= 0 && attackerSlot < Slots)
            {
                // Filed under the world-frame the shooter was looking at, which
                // is what a claim for this same shot will name -- and already
                // used when this hit *is* a claim being applied, so nothing
                // else can match against it.
                NoteLedger(attackerSlot, victimSlot,
                    ApplyingClaim ? ApplyingClaimAck : fire,
                    ApplyingClaim ? ApplyingClaimLaunch : launchFrame,
                    damage, used: ApplyingClaim);
            }
        }

        /// <summary>
        /// The world-frame a player of this slot is currently shooting in.
        ///
        /// For anybody the authority is holding as a puppet that is the frame
        /// their own screen is showing, which is exactly what their intent
        /// acks. For the authority's own player, and for a bot, it is the
        /// present: they aim at what they hold.
        /// </summary>
        private static uint FireFrameOf(int slot)
        {
            if (slot < 0 || slot >= Slots || slot == NetSession.LocalSlot
                || !NetSession.RemoteIntentValid[slot])
            {
                return NetSession.NetFrame;
            }
            uint ack = NetSession.RemoteIntents[slot].AckFrame;
            return ack == 0 || ack > NetSession.NetFrame ? NetSession.NetFrame : ack;
        }

        /// <summary>
        /// A datagram of claims from one slot. Validation that can be done on
        /// the spot is done here; everything that has to wait for the
        /// authority's own answer to the same shot goes into
        /// <see cref="_pending"/> and is settled by <see cref="Tick"/>.
        /// </summary>
        public static void Receive(int shooterSlot, ReadOnlySpan<byte> payload)
        {
            if (!Arbitrating || shooterSlot < 0 || shooterSlot >= Slots || payload.Length < 1)
            {
                return;
            }
            int count = Math.Min((int)payload[0], HitClaimPacket.MaxPerPacket);
            for (int i = 0; i < count; i++)
            {
                int at = 1 + i * HitClaimPacket.Size;
                if (at + HitClaimPacket.Size > payload.Length)
                {
                    break;
                }
                HitClaimPacket claim = HitClaimPacket.Read(payload[at..]);
                Received++;
                if (claim.ShooterLifeId == 0 || claim.VictimLifeId == 0
                    || !NetSession.MatchesStream(claim.MatchId, claim.AuthorityEpoch)
                    || !NetPlayerLifecycle.Matches(shooterSlot, claim.ShooterGeneration, claim.ShooterLifeId))
                {
                    NetPlayerLifecycle.OldLifeClaims++;
                    continue;
                }
                if (!NetPlayerLifecycle.Matches(claim.VictimSlot, claim.VictimGeneration, claim.VictimLifeId))
                {
                    NetPlayerLifecycle.OldLifeClaims++;
                    Answer(shooterSlot, claim.ClaimId, HitVerdictPacket.ResultWrongLife, remember: false);
                    continue;
                }
                // A repeat of something already answered. The answer is
                // repeated too: the shooter is asking because the first one
                // did not arrive.
                if (Seen(shooterSlot, claim.ClaimId))
                {
                    RepeatsHere++;
                    int seenAt = claim.ClaimId % SeenCapacity;
                    byte result = _seenIds[shooterSlot, seenAt] == claim.ClaimId
                        ? _seenResults[shooterSlot, seenAt] : HitVerdictPacket.ResultTooOld;
                    if (result != ResultPending) Answer(shooterSlot, claim.ClaimId, result);
                    continue;
                }
                int claimAt = claim.ClaimId % SeenCapacity;
                _seenBeams[shooterSlot, claimAt] = (BeamType)claim.Beam;
                _seenKeys[shooterSlot, claimAt] = ShotKey.For(shooterSlot, claim.LaunchFrame);
                NetShotDiagnostics.Claims[NetShotDiagnostics.Bucket((BeamType)claim.Beam)]++;
                Remember(shooterSlot, claim.ClaimId, ResultPending);
                byte immediate = Judge(shooterSlot, claim);
                if (immediate != HitVerdictPacket.ResultApplied)
                {
                    Answer(shooterSlot, claim.ClaimId, immediate);
                    continue;
                }
                Park(shooterSlot, claim);
            }
        }

        private static bool Seen(int slot, ushort id) => id == 0
            || _seenIds[slot, id % SeenCapacity] == id
            || (_newestId[slot] != 0 && !NetLifecycleTracker.Newer(id, _newestId[slot])
                && unchecked((ushort)(_newestId[slot] - id)) >= SeenCapacity);

        private static void Remember(int slot, ushort id, byte result)
        {
            if (_newestId[slot] == 0 || NetLifecycleTracker.Newer(id, _newestId[slot])) _newestId[slot] = id;
            int at = id % SeenCapacity;
            _seenIds[slot, at] = id;
            _seenResults[slot, at] = result;
        }

        /// <summary>
        /// Everything about a claim that can be decided the moment it lands.
        /// Returns <see cref="HitVerdictPacket.ResultApplied"/> when it
        /// survives -- which means "worth parking", not "applied yet".
        ///
        /// Five tests, in the order that costs least:
        ///
        /// <list type="number">
        /// <item>the slots are real, and a shooter does not claim on itself --
        /// a hit on your own player is either your own splash, which the
        /// authority computes identically from the same inputs, or somebody
        /// else's shot, which is theirs to claim;</item>
        /// <item>the frame it names is inside the history, so there is
        /// something to check it against;</item>
        /// <item>the damage is no more than that weapon can possibly deal,
        /// with every multiplier in the game applied at once;</item>
        /// <item>the victim was in play at that frame, and is still somebody
        /// worth damaging now;</item>
        /// <item>the victim's body, <i>as the authority's own history holds
        /// it at that frame</i>, is within <see cref="ClaimRadius"/> of where
        /// the claim says the hit landed.</item>
        /// </list>
        ///
        /// The last is the one that makes the whole thing safe. A claim can
        /// only ever rescue a shot the authority's own record agrees was
        /// there to be taken.
        /// </summary>
        /// <summary>
        /// Pure geometry predicate used by the authority and the asset-free
        /// regression suite. Keeping the exact radius/finite-value rule in one
        /// place prevents a test helper from quietly becoming a second policy.
        /// </summary>
        private static bool WithinClaimRadius(Vector3 authorityPosition, Vector3 hitPoint, byte beam)
        {
            Vector3 offset = hitPoint - authorityPosition;
            if (!Single.IsFinite(offset.X) || !Single.IsFinite(offset.Y)
                || !Single.IsFinite(offset.Z))
            {
                return false;
            }
            float reach = beam == HitClaimPacket.NoBeam ? MeleeRadius : ClaimRadius;
            return offset.LengthSquared <= reach * reach;
        }

        /// <summary>
        /// Whether the shooter had already been killed in a strictly earlier
        /// world than the shot was fired in. Equal-world shots trade. A zero
        /// launch stamp falls back to the ack, matching the claim protocol.
        /// </summary>
        private static bool ShooterDiedBeforeShot(bool dead, uint deathFire,
            uint launchFrame, uint ackFrame)
        {
            uint fired = launchFrame != 0 ? launchFrame : ackFrame;
            return dead && deathFire < fired;
        }

        private static byte Judge(int shooterSlot, in HitClaimPacket claim)
        {
            int victimSlot = claim.VictimSlot;
            if (victimSlot < 0 || victimSlot >= Slots || victimSlot == shooterSlot
                || victimSlot >= PlayerEntity.Players.Count)
            {
                RefusedHere++;
                return HitVerdictPacket.ResultRefused;
            }
            uint now = NetSession.NetFrame;
            if (claim.AckFrame == 0 || claim.AckFrame > now || now - claim.AckFrame > MaxClaimAge)
            {
                TooOldHere++;
                return HitVerdictPacket.ResultTooOld;
            }
            if (claim.LaunchFrame > claim.AckFrame)
            {
                RefusedHere++;
                return HitVerdictPacket.ResultInvalidLaunch;
            }
            if (claim.Damage > MaxDamageFor(claim.Beam))
            {
                RefusedHere++;
                NetLog.Event($"slot {shooterSlot} claimed {claim.Damage} damage with beam "
                    + $"{claim.Beam}, which cannot deal more than {MaxDamageFor(claim.Beam)}");
                return HitVerdictPacket.ResultDamageLimit;
            }
            // Where the authority itself had the victim, at the frame the
            // shooter was looking at. This is the claim's only evidence and
            // the authority's own record of it.
            if (!NetUnlagged.PositionAt(victimSlot, claim.AckFrame, claim.VictimGeneration, claim.VictimLifeId, out Vector3 was))
            {
                // Either the victim was not in play in that world, or the ring
                // no longer holds it. Both mean there is nothing to check.
                TooOldHere++;
                return HitVerdictPacket.ResultTooOld;
            }
            if (!WithinClaimRadius(was, claim.HitPoint, claim.Beam))
            {
                Vector3 offset = claim.HitPoint - was;
                RefusedHere++;
                NetLog.Event($"slot {shooterSlot} claimed a hit on slot {victimSlot} at "
                    + $"{claim.HitPoint}, {offset.Length:F2} units from where frame "
                    + $"{claim.AckFrame} put them");
                return HitVerdictPacket.ResultGeometry;
            }
            PlayerEntity victim = PlayerEntity.Players[victimSlot];
            if (!victim.LoadFlags.TestFlag(LoadFlags.Active) || !victim.ModIsInPlay)
            {
                VoidedDeadVictim++;
                return HitVerdictPacket.ResultDeadVictim;
            }
            // The arbitration. Dead now is not the test -- a player killed
            // during the round trip still gets the shot they took before it
            // happened, and that is the whole point. The test is whether
            // somebody put them down in a world strictly earlier than the one
            // they fired in.
            //
            // <b>Fired in, which is the launch frame and not the ack.</b> A
            // claim's AckFrame is the world its shooter was reading when the
            // hit *resolved*, and for anything that travels that is a long way
            // after the trigger: a Missile is in the air for the better part
            // of a second. Judging it by the ack asks "were you already dead
            // when your rocket landed", which voids a shot that left the gun
            // before the shot that killed you was even aimed -- and it is
            // invisible on a Power Beam or an Imperialist, whose rounds arrive
            // in about a frame, which is exactly the shape the complaint came
            // in: kills undone with the Missile and the Magmaul, none with the
            // other two. LaunchFrame is the same quantity as _deathFire on
            // both machines (NetUnlagged.LaunchFrameFor), so this is a
            // comparison of like with like; the ack is the fallback for the
            // hits that carry no stamp, where the two coincide anyway.
            if (ShooterDiedBeforeShot(_dead[shooterSlot], _deathFire[shooterSlot],
                claim.LaunchFrame, claim.AckFrame))
            {
                VoidedDeadShooter++;
                return HitVerdictPacket.ResultDeadShooter;
            }
            return HitVerdictPacket.ResultApplied;
        }

        /// <summary>
        /// The measurement this whole ledger makes possible for nothing: the
        /// shooter's own number for a shot, beside the authority's number for
        /// the same shot.
        ///
        /// Both machines run the same damage table over the same weapon, so a
        /// difference is never rounding -- it is one of them reading a
        /// quantity the other was never told. The four that exist are the
        /// charge tier (the authority rebuilds it by counting frames the
        /// trigger was held, and a partial-charge weapon's damage is a
        /// continuous function of that count), a powerup one copy has and the
        /// other does not, the damage level -- a per-machine *setting* until
        /// the server started publishing it -- and a halfturret's health,
        /// which decides how a hit on Weavel is split and lives nowhere but
        /// the authority.
        ///
        /// A zero on the authority's side is not a disagreement: it means the
        /// ledger entry predates the damage being recorded, which is every
        /// entry filed by an older build and every hit applied out of a claim.
        /// </summary>
        private static void NoteAgreement(int shooter, int victim, byte beam,
            int claimed, int resolved)
        {
            if (resolved <= 0)
            {
                return;
            }
            int bucket = beam == HitClaimPacket.NoBeam
                || beam >= NetHitPrediction.AltBeam
                ? NetHitPrediction.AltBeam
                : beam;
            _claimedByBeam[bucket] += claimed;
            _resolvedByBeam[bucket] += resolved;
            if (claimed == resolved)
            {
                _agreeByBeam[bucket]++;
                return;
            }
            _differByBeam[bucket]++;
            // Logged a few times and then not again: a mismatch that happens
            // at all happens on every shot of that weapon, so the first few
            // say everything and the rest is noise in a server log.
            if (_disagreementsLogged < 20)
            {
                _disagreementsLogged++;
                NetLog.Event($"slot {shooter} predicted {claimed} damage on slot {victim} "
                    + $"with beam {beam} and this machine resolved {resolved}");
            }
        }

        /// <summary>
        /// The per-weapon agreement, for the authority's report. Empty when
        /// nothing was ever paired, which is a match in which no claim and no
        /// hit of the authority's own ever described the same shot.
        /// </summary>
        public static string DescribeAgreement()
        {
            var text = new System.Text.StringBuilder();
            text.Append("predicted vs resolved damage:");
            bool any = false;
            for (int i = 0; i < _agreeByBeam.Length; i++)
            {
                long paired = _agreeByBeam[i] + _differByBeam[i];
                if (paired == 0)
                {
                    continue;
                }
                any = true;
                string name = i == NetHitPrediction.AltBeam
                    ? "alt/bomb"
                    : ((BeamType)i).ToString();
                text.Append($"\n  {name,-13} {paired,5} paired, {_agreeByBeam[i],5} agreed "
                    + $"({_agreeByBeam[i] * 100.0 / paired:F0}%), {_differByBeam[i],5} differed"
                    + $" -- {_claimedByBeam[i]} claimed against {_resolvedByBeam[i]} resolved");
            }
            return any ? text.ToString() : "predicted vs resolved damage: nothing paired";
        }


        /// <summary>
        /// Reconcile the one duplicate that must not be treated as "already
        /// paid": an Imperialist headshot the shooter resolved for full
        /// headshot damage while the authority resolved the same launch as a
        /// body hit.
        ///
        /// The claim has already passed Judge, including rewind-age,
        /// lifecycle, damage-ceiling and geometry checks. TakeLedger has also
        /// paired it to exactly one authority hit. Applying the whole claim
        /// would double the body damage; applying only the difference makes
        /// the final authoritative total equal the validated shooter result
        /// exactly once.
        /// </summary>
        private static void ReconcileImperialistHeadshot(ref Pending entry, int resolved)
        {
            if (entry.Beam != (byte)BeamType.Imperialist
                || (entry.Flags & HitClaimPacket.FlagHeadshot) == 0
                || entry.Damage <= resolved)
            {
                return;
            }
            int victimSlot = entry.VictimSlot;
            int shooterSlot = entry.ShooterSlot;
            if (victimSlot >= PlayerEntity.Players.Count
                || shooterSlot >= PlayerEntity.Players.Count)
            {
                return;
            }
            PlayerEntity victim = PlayerEntity.Players[victimSlot];
            PlayerEntity shooter = PlayerEntity.Players[shooterSlot];
            if (!victim.LoadFlags.TestFlag(LoadFlags.Active) || !victim.ModIsInPlay
                || victim.Health <= 0)
            {
                return;
            }

            uint correction = (uint)(entry.Damage - resolved);
            uint before = (uint)victim.Health;
            ApplyingClaim = true;
            ApplyingClaimAck = entry.AckFrame;
            ApplyingClaimLaunch = entry.LaunchFrame;
            try
            {
                using (new NetDamage.ClaimScope(BeamType.Imperialist))
                {
                    victim.TakeDamage(correction,
                        DamageFlags.Headshot | DamageFlags.NoDmgInvuln, null, shooter);
                }
            }
            finally
            {
                ApplyingClaim = false;
            }
            uint applied = before - (uint)Math.Max(0, victim.Health);
            if (applied == 0)
            {
                return;
            }
            HeadshotCorrections++;
            NetLog.Event($"claim {entry.Id}: reconciled Imperialist headshot on slot "
                + $"{victimSlot}, authority body={resolved}, shooter headshot={entry.Damage}, "
                + $"added={applied}");
        }

        private static void Park(int shooterSlot, in HitClaimPacket claim)
        {
            int index = -1;
            for (int i = 0; i < PendingCapacity; i++)
            {
                if (!_pending[i].Live)
                {
                    index = i;
                    break;
                }
            }
            if (index < 0)
            {
                return;
            }
            _pending[index] = new Pending
            {
                MatchId = claim.MatchId,
                AuthorityEpoch = claim.AuthorityEpoch,
                ShooterGeneration = claim.ShooterGeneration,
                ShooterLifeId = claim.ShooterLifeId,
                VictimGeneration = claim.VictimGeneration,
                VictimLifeId = claim.VictimLifeId,
                Id = claim.ClaimId,
                ShooterSlot = (byte)shooterSlot,
                VictimSlot = claim.VictimSlot,
                Beam = claim.Beam,
                Damage = claim.Damage,
                Flags = claim.Flags,
                AckFrame = claim.AckFrame,
                LaunchFrame = claim.LaunchFrame,
                HitPoint = claim.HitPoint,
                Arrived = NetSession.NetFrame,
                Grace = GraceFor(shooterSlot),
                Live = true
            };
        }

        /// <summary>
        /// The largest number a weapon can put on somebody, with every
        /// multiplier in the game stacked: the charged headshot, doubled by
        /// the powerup, doubled again by a Double effectiveness, and the
        /// high damage level on top.
        ///
        /// A ceiling rather than a check. The point is that a claim cannot
        /// invent damage, not that the authority recomputes the shot -- which
        /// it cannot, because the shot happened on another machine with
        /// another charge level and another set of pickups.
        /// </summary>
        private static int MaxDamageFor(byte beam)
        {
            int raw = 200;
            if (beam != HitClaimPacket.NoBeam && Weapons.Current != null
                && beam < Weapons.Current.Count)
            {
                WeaponInfo info = Weapons.Current[beam];
                raw = Math.Max(info.ChargedHeadshotDamage,
                    Math.Max(info.HeadshotDamage,
                    Math.Max(info.MinChargeHeadshotDamage,
                    Math.Max(info.ChargedDamage,
                    Math.Max(info.UnchargedDamage,
                    Math.Max(info.MinChargeDamage, info.ChargedSplashDamage))))));
            }
            // x2 double damage, x2 the largest effectiveness multiplier,
            // x1.25 the highest damage level.
            return (int)(raw * 5.0f) + 1;
        }

        /// <summary>
        /// One call a frame, from <see cref="NetHooks.AfterSimulation"/>.
        ///
        /// On the authority it ages the parked claims, drops the ones the
        /// authority has since answered for itself, and applies what is left
        /// -- <b>in fire-frame order</b>, which is what makes a mutual kill
        /// come out the same way whichever datagram won the race. On a client
        /// it ages the outbox.
        /// </summary>
        public static void Tick()
        {
            if (!NetRoomChange.GameplayReady) return;
            if (Claiming)
            {
                TickOutbox();
            }
            if (!Arbitrating)
            {
                return;
            }
            TrackDeaths();
            uint now = NetSession.NetFrame;
            // Oldest fire-frame first. Selection over a 64-entry array with
            // almost nothing live in it: the loop below normally finds nothing
            // at all, and the sort only ever orders claims that came due on
            // the same frame, which is two in the case this exists for.
            while (true)
            {
                int next = -1;
                int overdue = -1;
                for (int i = 0; i < PendingCapacity; i++)
                {
                    ref Pending entry = ref _pending[i];
                    if (!entry.Live)
                    {
                        continue;
                    }
                    // The authority answered this itself. Nothing to rescue,
                    // and applying it would be the same hit twice. One of its
                    // hits retires one claim, so a burst that really did lose
                    // three of four still gets three back.
                    if (!NetSession.MatchesStream(entry.MatchId, entry.AuthorityEpoch)
                        || !NetPlayerLifecycle.Matches(entry.ShooterSlot, entry.ShooterGeneration, entry.ShooterLifeId)
                        || !NetPlayerLifecycle.Matches(entry.VictimSlot, entry.VictimGeneration, entry.VictimLifeId))
                    {
                        entry.Live = false;
                        NetPlayerLifecycle.OldLifeClaims++;
                        continue;
                    }
                    if (TakeLedger(entry.ShooterSlot, entry.VictimSlot, entry.AckFrame,
                        entry.LaunchFrame, entry.Arrived, entry.Grace, out int resolved))
                    {
                        // A validated Imperialist headshot can pair with the
                        // authority's body hit when the two rewinds differ by
                        // only the narrow head band. "Duplicate" used to throw
                        // away the missing 128 damage and leave the target
                        // alive after the shooter had been told HEADSHOT.
                        ReconcileImperialistHeadshot(ref entry, resolved);
                        entry.Live = false;
                        DuplicateHere++;
                        NoteAgreement(entry.ShooterSlot, entry.VictimSlot, entry.Beam,
                            entry.Damage, resolved);
                        Answer(entry.ShooterSlot, entry.Id, HitVerdictPacket.ResultDuplicate);
                        continue;
                    }
                    if (next < 0 || (entry.LaunchFrame != 0 ? entry.LaunchFrame : entry.AckFrame)
                        < (_pending[next].LaunchFrame != 0 ? _pending[next].LaunchFrame : _pending[next].AckFrame))
                    {
                        next = i;
                    }
                    if (now - entry.Arrived >= 2 * MaxGraceFrames
                        && (overdue < 0 || entry.Arrived < _pending[overdue].Arrived)) overdue = i;
                }
                if (next < 0)
                {
                    break;
                }
                // A known earlier shot still inside its grace must be decided first.
                // Packet arrival order must not turn a one-frame winner into a trade.
                if (now - _pending[next].Arrived < (uint)_pending[next].Grace)
                {
                    // A stream of newly arriving older claims must not starve a
                    // completed grace window indefinitely. Late evidence cannot
                    // reorder a verdict beyond this bounded arbitration window.
                    if (overdue < 0) break;
                    next = overdue;
                }
                ApplyOne(ref _pending[next]);
                _pending[next].Live = false;
                TrackDeaths();
            }
            FlushVerdicts();
        }

        /// <summary>
        /// Watch each slot cross from in play to down, and stamp the world it
        /// was killed in. <see cref="_lastHitFire"/> is whatever the newest
        /// hit on that slot was aimed at, which for a kill is the killing
        /// shot -- and a death with no hit behind it (the void, a crusher, the
        /// clock) is stamped with the present, which is the truth for
        /// something nobody aimed.
        /// </summary>
        private static void TrackDeaths()
        {
            for (int i = 0; i < Slots; i++)
            {
                bool inPlay = i < PlayerEntity.Players.Count
                    && PlayerEntity.Players[i].LoadFlags.TestFlag(LoadFlags.Active)
                    && PlayerEntity.Players[i].ModIsInPlay;
                if (_wasInPlay[i] && !inPlay)
                {
                    _dead[i] = true;
                    _deathFire[i] = _lastHitFire[i] != 0 ? _lastHitFire[i] : NetSession.NetFrame;
                }
                else if (!_wasInPlay[i] && inPlay)
                {
                    _dead[i] = false;
                    _deathFire[i] = 0;
                    _lastHitFire[i] = 0;
                    for (int j = 0; j < Slots; j++)
                    {
                        ClearLedger(j, i);
                    }
                }
                _wasInPlay[i] = inPlay;
            }
        }

        /// <summary>
        /// Shots this authority has already made real out of a claim, and how
        /// many hits of each are still owed a refusal.
        ///
        /// <b>The other half of not paying twice.</b> Matching a claim against
        /// hits the authority has already resolved covers the case where its
        /// answer came first. It cannot cover the opposite one -- the claim is
        /// applied at the end of its window and the authority's own copy of
        /// that shot lands *after* -- and measured at 400 ms that is most of
        /// what was left: rescues with the authority's matching hit 48 frames
        /// the wrong side of them. So the shot is remembered, and when the
        /// authority's own copy finally arrives it is refused at the top of
        /// <c>TakeDamage</c>.
        ///
        /// A count rather than a flag, because one shot can land more than
        /// once on one victim: a Missile's direct hit and its splash are two,
        /// and both machines produce both. Two claims applied means the next
        /// two of the authority's own hits for that shot are the same two.
        /// </summary>
        private const int RescuedCapacity = 32;
        private static readonly byte[] _rescuedAttacker = new byte[RescuedCapacity];
        private static readonly byte[] _rescuedVictim = new byte[RescuedCapacity];
        private static readonly ShotKey[] _rescuedKeys = new ShotKey[RescuedCapacity];
        private static readonly ushort[] _rescuedVictimGeneration = new ushort[RescuedCapacity];
        private static readonly ushort[] _rescuedVictimLife = new ushort[RescuedCapacity];
        private static readonly uint[] _rescuedAt = new uint[RescuedCapacity];
        private static readonly int[] _rescuedOwed = new int[RescuedCapacity];
        private static int _rescuedHead;

        /// <summary>
        /// How long a rescued shot is remembered. A projectile cannot outlive
        /// its own lifespan (255 ticks at 30 Hz, or 510 simulation frames); this
        /// is past that with room for the trip that delivered the claim.
        /// </summary>
        private const int RescuedFrames = 720;

        /// <summary>Hits the authority was refused because a claim had them.</summary>
        public static long SuppressedHere { get; private set; }

        private static void NoteRescued(int attacker, int victim, uint launch)
        {
            if (launch == 0)
            {
                // Nothing to recognise it by later. The time window is all
                // there is for these, and it is why they are not zero.
                return;
            }
            for (int i = 0; i < RescuedCapacity; i++)
            {
                if (_rescuedOwed[i] > 0 && _rescuedKeys[i] == ShotKey.For(attacker, launch)
                    && _rescuedVictim[i] == victim
                    && NetPlayerLifecycle.Matches(victim, _rescuedVictimGeneration[i], _rescuedVictimLife[i]))
                {
                    _rescuedOwed[i]++;
                    _rescuedAt[i] = NetSession.NetFrame;
                    return;
                }
            }
            int at = _rescuedHead;
            _rescuedHead = (_rescuedHead + 1) % RescuedCapacity;
            _rescuedAttacker[at] = (byte)attacker;
            _rescuedVictim[at] = (byte)victim;
            _rescuedKeys[at] = ShotKey.For(attacker, launch);
            _rescuedVictimGeneration[at] = NetPlayerLifecycle.Generation(victim);
            _rescuedVictimLife[at] = NetPlayerLifecycle.Get(victim);
            _rescuedAt[at] = NetSession.NetFrame;
            _rescuedOwed[at] = 1;
        }

        /// <summary>
        /// Whether this hit is the authority's own copy of a shot a claim has
        /// already made real, in which case it must not land a second time.
        /// Consumes one of the hits owed. Called from
        /// <see cref="NetDamage.Suppress"/>, at the top of <c>TakeDamage</c>,
        /// which is the only point early enough to refuse it.
        /// </summary>
        public static bool AlreadyRescued(int attacker, int victim, uint launch, ShotKey? launchKey = null)
        {
            if (!Arbitrating || launch == 0 || ApplyingClaim
                || attacker < 0 || attacker >= Slots || victim < 0 || victim >= Slots)
            {
                return false;
            }
            uint now = NetSession.NetFrame;
            for (int i = 0; i < RescuedCapacity; i++)
            {
                if (_rescuedOwed[i] <= 0 || _rescuedKeys[i] != (launchKey ?? ShotKey.For(attacker, launch))
                    || _rescuedVictim[i] != victim
                    || !NetPlayerLifecycle.Matches(victim, _rescuedVictimGeneration[i], _rescuedVictimLife[i]))
                {
                    continue;
                }
                if (now - _rescuedAt[i] > RescuedFrames)
                {
                    _rescuedOwed[i] = 0;
                    continue;
                }
                _rescuedOwed[i]--;
                SuppressedHere++;
                NetLog.Event($"refused the authority's own copy of slot {attacker}'s shot "
                    + $"(launch {launch}) on slot {victim}: a claim already made it real");
                return true;
            }
            return false;
        }

        /// <summary>
        /// Make a claim real: the damage the authority's own simulation never
        /// found, applied through the same <c>TakeDamage</c> every other hit
        /// goes through, so the damage sequence, the scoreboard, the death and
        /// the snapshot that carries all three are the ones that already work.
        /// </summary>
        private static void ApplyOne(ref Pending entry)
        {
            int victimSlot = entry.VictimSlot;
            int shooterSlot = entry.ShooterSlot;
            if (!NetSession.MatchesStream(entry.MatchId, entry.AuthorityEpoch)
                || !NetPlayerLifecycle.Matches(shooterSlot, entry.ShooterGeneration, entry.ShooterLifeId)
                || !NetPlayerLifecycle.Matches(victimSlot, entry.VictimGeneration, entry.VictimLifeId))
            {
                NetPlayerLifecycle.OldLifeClaims++;
                return;
            }
            if (victimSlot >= PlayerEntity.Players.Count
                || shooterSlot >= PlayerEntity.Players.Count)
            {
                Answer(shooterSlot, entry.Id, HitVerdictPacket.ResultRefused);
                return;
            }
            PlayerEntity victim = PlayerEntity.Players[victimSlot];
            PlayerEntity shooter = PlayerEntity.Players[shooterSlot];
            if (!victim.LoadFlags.TestFlag(LoadFlags.Active) || !victim.ModIsInPlay)
            {
                VoidedDeadVictim++;
                Answer(shooterSlot, entry.Id, HitVerdictPacket.ResultDeadVictim);
                return;
            }
            // Re-asked at the moment of application, not only on arrival: the
            // grace window is long enough for somebody to have killed this
            // shooter in a world earlier than the one it fired in, and the
            // arbitration is about that ordering rather than about which
            // packet arrived first.
            if (ShooterDiedBeforeShot(_dead[shooterSlot], _deathFire[shooterSlot],
                entry.LaunchFrame, entry.AckFrame))
            {
                VoidedDeadShooter++;
                Answer(shooterSlot, entry.Id, HitVerdictPacket.ResultDeadShooter);
                return;
            }
            DamageFlags flags = DamageFlags.NoDmgInvuln;
            if ((entry.Flags & HitClaimPacket.FlagHeadshot) != 0)
            {
                flags |= DamageFlags.Headshot;
            }
            // The killing-world stamp and ledger entry are written by NetDamage.Note on the
            // way through TakeDamage, filed under this ack and already used.
            //
            // It used to be written here *as well*, which put two entries in an
            // eight-deep ring for every rescue: one used, one free for a later
            // claim to match against by mistake, and the pair evicting genuine
            // authority hits twice as fast as they arrived.
            ApplyingClaim = true;
            ApplyingClaimAck = entry.AckFrame;
            ApplyingClaimLaunch = entry.LaunchFrame;
            bool lethal = victim.Health <= entry.Damage;
            uint before = (uint)victim.Health;
            // The scope carries two things into TakeDamage: the beam the claim
            // names, so the victim's own machine replays the right hit rather
            // than a nameless one, and the fact that this is a claim, so the
            // damage is not multiplied a second time by a powerup and a damage
            // level the shooter has already applied.
            //
            // Source is the shooter rather than a beam, because there is no
            // beam: this is a hit that happened on another machine. The
            // direction is left null, which is what makes the damage indicator
            // point at the attacker -- exactly as it does for a local hit with
            // no knockback, and every beam that knocks anybody back carries
            // its impulse through the authority's own resolution, which this
            // one did not have.
            try
            {
                using (new NetDamage.ClaimScope(entry.Beam == HitClaimPacket.NoBeam
                    ? BeamType.None : (BeamType)entry.Beam))
                {
                    victim.TakeDamage(entry.Damage, flags, null, shooter);
                }
            }
            finally
            {
                ApplyingClaim = false;
            }
            if (victim.Health >= before)
            {
                // Spawn protection, teams and other damage rules can refuse a
                // geometrically valid hit. Never freeze or prepay that flight.
                RefusedHere++;
                Answer(shooterSlot, entry.Id, HitVerdictPacket.ResultNoDamage);
                return;
            }
            if ((entry.Flags & HitClaimPacket.FlagFrozen) != 0 && victim.Health > 0)
            {
                // The affliction does not survive the trip on its own: it is
                // produced inside TakeDamage from the beam entity, and there
                // is no beam here. The snapshot's flag byte carries it onward
                // to everybody exactly as it does for a hit the authority
                // resolved itself.
                victim.ModSetFrozen(true);
            }
            AppliedHere++;
            // Remember the shot, so the authority's own copy of it -- which
            // for a slow projectile can still be in the air -- is refused when
            // it lands rather than paid a second time.
            NoteRescued(shooterSlot, victimSlot, entry.LaunchFrame);
            RescuedDamage += before - (uint)Math.Max(0, victim.Health);
            if ((entry.Flags & HitClaimPacket.FlagHeadshot) != 0)
            {
                RescuedHeadshots++;
            }
            if (lethal)
            {
                RescuedKills++;
            }
            // Every rescue, not only the lethal ones. A rescued hit is a shot
            // the authority's own simulation never found, so each line here is
            // an answer to "it went through him" -- and the frame gap is what
            // says whether it was the rewind's ceiling or the shooter's death
            // that lost it.
            // The line that says whether a rescue was really a rescue.
            //
            // `nearest` is how far the closest hit this authority resolved
            // itself, for the same pair, sits from this claim's arrival --
            // used or not. A rescue with nothing near it is a shot the
            // authority genuinely never found. A rescue with a hit sitting
            // right on top of it is either a second claim for one shot (a
            // direct hit and its splash are two) or the match window being
            // wrong again, and that is the double-damage report. Keep it:
            // without it the fault is invisible from either end.
            NetLog.Event($"rescue {entry.Id} beam="
                + (entry.Beam == HitClaimPacket.NoBeam ? "none" : ((BeamType)entry.Beam).ToString())
                + $" nearest={NearestLedgerOffset(shooterSlot, victimSlot, entry.Arrived)}f"
                + $" window={entry.Grace}f ack={entry.AckFrame} arrived={entry.Arrived}");
            NetLog.Event($"claim {entry.Id}: slot {shooterSlot} hit slot {victimSlot} for "
                + $"{before - (uint)Math.Max(0, victim.Health)}"
                + ((entry.Flags & HitClaimPacket.FlagHeadshot) != 0 ? " (headshot)" : "")
                + (lethal ? " and killed them" : "")
                + $", aimed at frame {entry.AckFrame}, "
                + $"{NetSession.NetFrame - entry.AckFrame} frames ago"
                + (_dead[shooterSlot] ? " -- and was dead by the time it arrived" : ""));
            Answer(shooterSlot, entry.Id, HitVerdictPacket.ResultApplied);
        }

        /// <summary>
        /// Where a verdict goes. Filled in by whoever owns the socket -- the
        /// dedicated server, or a peer host -- because this file has none of
        /// its own and those two transports do not look alike.
        /// </summary>
        public delegate void VerdictWriter(int slot,
            ReadOnlySpan<(ushort Id, byte Result)> verdicts);

        public static VerdictWriter? VerdictSink { get; set; }

        /// <summary>
        /// Verdicts waiting to go out, per shooter.
        ///
        /// Buffered for a frame rather than sent one datagram each, because
        /// they arrive in two different places -- <see cref="Receive"/>
        /// answers what it can refuse on sight, <see cref="Tick"/> answers
        /// what had to wait out its grace -- and a client repeating six claims
        /// into one packet would otherwise be answered with six. One frame is
        /// 16 ms against the round trip this exists to save, which is
        /// hundreds.
        /// </summary>
        private const int VerdictCapacity = HitVerdictPacket.MaxPerPacket;
        private static readonly (ushort Id, byte Result)[,] _verdicts =
            new (ushort, byte)[Slots, VerdictCapacity];
        private static readonly int[] _verdictCount = new int[Slots];

        private static void Answer(int slot, ushort id, byte result, bool remember = true)
        {
            if (slot < 0 || slot >= Slots)
            {
                return;
            }
            int at = id % SeenCapacity;
            if (remember && _seenIds[slot, at] == id && _seenResults[slot, at] == ResultPending)
            {
                int weapon = NetShotDiagnostics.Bucket(_seenBeams[slot, at]);
                if (result == HitVerdictPacket.ResultApplied) NetShotDiagnostics.Rescues[weapon]++;
                else if (result != HitVerdictPacket.ResultDuplicate) NetShotDiagnostics.Refusals[weapon]++;
                if (NetLog.Enabled) NetShotDiagnostics.Trace("authority-verdict", _seenKeys[slot, at],
                    _seenBeams[slot, at], $"claim={id} result={HitVerdictPacket.Describe(result)}");
            }
            // Remember even when this datagram's answer queue is full: a retry
            // must repeat the verdict, never park the already applied hit again.
            if (remember) Remember(slot, id, result);
            if (_verdictCount[slot] >= VerdictCapacity) return;
            _verdicts[slot, _verdictCount[slot]++] = (id, result);
        }

        private static void FlushVerdicts()
        {
            if (VerdictSink == null)
            {
                Array.Clear(_verdictCount);
                return;
            }
            Span<(ushort Id, byte Result)> scratch = stackalloc (ushort, byte)[VerdictCapacity];
            for (int slot = 0; slot < Slots; slot++)
            {
                int count = _verdictCount[slot];
                if (count == 0)
                {
                    continue;
                }
                for (int i = 0; i < count; i++)
                {
                    scratch[i] = _verdicts[slot, i];
                }
                _verdictCount[slot] = 0;
                VerdictSink(slot, scratch[..count]);
            }
        }

        public static void Reset()
        {
            Array.Clear(_outbox);
            Array.Clear(_pending);
            Array.Clear(_seenIds);
            Array.Clear(_seenResults);
            Array.Clear(_newestId);
            Array.Clear(_lastResult);
            Array.Clear(_authorityHit);
            Array.Clear(_authorityHitAck);
            Array.Clear(_authorityHitLaunch);
            Array.Clear(_authorityHitUsed);
            Array.Clear(_authorityHitHead);
            Array.Clear(_deathFire);
            Array.Clear(_dead);
            Array.Clear(_lastHitFire);
            Array.Clear(_wasInPlay);
            Array.Clear(_verdictCount);
            Array.Clear(_rescuedOwed);
            _rescuedHead = 0;
            _nextId = 1;
            Declared = 0;
            Applied = 0;
            Duplicate = 0;
            RefusedDeadShooter = 0;
            RefusedDeadVictim = 0;
            RefusedOther = 0;
            Unanswered = 0;
            Resends = 0;
            Received = 0;
            AppliedHere = 0;
            DuplicateHere = 0;
            VoidedDeadShooter = 0;
            VoidedDeadVictim = 0;
            RefusedHere = 0;
            TooOldHere = 0;
            RepeatsHere = 0;
            SuppressedHere = 0;
            MatchedByLaunch = 0;
            MatchedByWindow = 0;
            RescuedDamage = 0;
            Array.Clear(_agreeByBeam);
            Array.Clear(_differByBeam);
            Array.Clear(_claimedByBeam);
            Array.Clear(_resolvedByBeam);
            _disagreementsLogged = 0;
            RescuedKills = 0;
            RescuedHeadshots = 0;
            HeadshotCorrections = 0;
        }

        /// <summary>
        /// Forget everything about one slot. A claim describes a hit on a
        /// particular player in a particular room; kept across either, it
        /// would be applied to whoever arrives next.
        /// </summary>
        public static void ForgetSlot(int slot, bool preserveFlights = false)
        {
            if (slot < 0 || slot >= Slots)
            {
                return;
            }
            for (int i = 0; i < OutboxCapacity; i++)
            {
                if (_outbox[i].VictimSlot == slot || slot == NetSession.LocalSlot)
                {
                    _outbox[i].Live = false;
                }
            }
            for (int i = 0; i < PendingCapacity; i++)
            {
                if (_pending[i].VictimSlot == slot || _pending[i].ShooterSlot == slot)
                {
                    _pending[i].Live = false;
                }
            }
            _verdictCount[slot] = 0;
            for (int i = 0; i < SeenCapacity; i++) { _seenIds[slot, i] = 0; _seenResults[slot, i] = 0; }
            for (int i = 0; i < RescuedCapacity; i++)
                if ((!preserveFlights && _rescuedAttacker[i] == slot) || _rescuedVictim[i] == slot) _rescuedOwed[i] = 0;
            _newestId[slot] = 0;
            _lastResult[slot] = 0;
            _deathFire[slot] = 0;
            _dead[slot] = false;
            _lastHitFire[slot] = 0;
            _wasInPlay[slot] = false;
            for (int i = 0; i < Slots; i++)
            {
                ClearLedger(slot, i);
                ClearLedger(i, slot);
            }
        }

        /// <summary>Every claim in flight is about a room that is going away.</summary>
        public static void ForgetPending()
        {
            Array.Clear(_seenIds);
            Array.Clear(_seenResults);
            Array.Clear(_newestId);
            Array.Clear(_verdictCount);
            Array.Clear(_rescuedOwed);
            Array.Clear(_outbox);
            Array.Clear(_pending);
            Array.Clear(_authorityHit);
            Array.Clear(_authorityHitAck);
            Array.Clear(_authorityHitUsed);
            Array.Clear(_authorityHitHead);
            Array.Clear(_deathFire);
            Array.Clear(_dead);
            Array.Clear(_lastHitFire);
            Array.Clear(_wasInPlay);
        }

        /// <summary>
        /// The shooter's line in a report. Absent from a run that claimed
        /// nothing, which is what the authority's own report says.
        /// </summary>
        public static string? Describe()
        {
            if (Declared == 0 && Received == 0)
            {
                return null;
            }
            if (Received > 0)
            {
                return $"hit claims (as authority): {Received} received, {AppliedHere} applied "
                    + $"({RescuedDamage} damage, {RescuedKills} kills, {RescuedHeadshots} "
                    + $"headshots rescued, {HeadshotCorrections} duplicate headshots corrected), "
                    + $"{DuplicateHere} already resolved, "
                    + $"{VoidedDeadShooter} from a shooter already dead, "
                    + $"{VoidedDeadVictim} on a victim already down, "
                    + $"{RefusedHere} refused, {TooOldHere} too old, "
                    + $"{RepeatsHere} repeats "
                    + $"(matched {MatchedByLaunch} by shot, {MatchedByWindow} by window, "
                    + $"{SuppressedHere} of its own refused as already rescued)";
            }
            long answered = Applied + Duplicate + RefusedDeadShooter
                + RefusedDeadVictim + RefusedOther;
            double pct = answered > 0 ? 100.0 * (Applied + Duplicate) / answered : 0;
            return $"hit claims: {Declared} declared, {Applied} applied, "
                + $"{Duplicate} already resolved ({pct:F1}% stood), "
                + $"{RefusedDeadShooter} void (dead shooter), "
                + $"{RefusedDeadVictim} void (victim down), {RefusedOther} refused, "
                + $"{Unanswered} unanswered, {Resends} repeats";
        }
    }
}
