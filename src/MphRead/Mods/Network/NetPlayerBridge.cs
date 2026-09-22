using System;
using MphRead.Entities;
using OpenTK.Mathematics;

namespace MphRead.Mods.Network
{
    /// <summary>
    /// Translates between MphRead's player state and the wire format.
    ///
    /// The injection design leans on something the project already does:
    /// PlayerAi.ProcessInput() drives bots by writing into player.Controls,
    /// the exact surface the keyboard writes into. A remote player is
    /// therefore just a third writer of that same surface -- no new input
    /// path, no engine change.
    /// </summary>
    public static class NetPlayerBridge
    {
        private static readonly FormReconciliation[] _formReconciliation =
            new FormReconciliation[PlayerEntity.SlotCapacity];

        // Retained for older diagnostic consumers. Lifecycle drop counters now
        // live in NetPlayerLifecycle; clients no longer choose spawn points.
        public static int PlacementsRefused;
        public static int SpawnFacingsTurned;
        public static float WorstSpawnFacing;
        public static int StaleDeathsIgnored;

        /// <summary>
        /// What the last snapshot said each slot's form was, so the netdbg
        /// line can print it beside what this machine actually has. 0 not
        /// said, 1 biped, 2 alt.
        /// </summary>
        private static readonly byte[] _formSaid = new byte[PlayerEntity.SlotCapacity];

        public static string FormSaidByAuthority()
        {
            var text = new System.Text.StringBuilder(PlayerEntity.SlotCapacity);
            for (int i = 0; i < PlayerEntity.MaxPlayers && i < _formSaid.Length; i++)
            {
                text.Append(_formSaid[i] == 0 ? '-' : _formSaid[i] == 2 ? 'A' : 'b');
            }
            return text.ToString();
        }

        /// <summary>
        /// Beyond this a remote player is placed outright, not eased. Well
        /// past anything a lost burst of updates can account for, so what is
        /// left is a respawn or a teleporter -- where a jump is correct.
        /// </summary>
        private const float SnapDistance = 15f;
        /// <summary>How much of the remaining gap a remote player closes each frame.</summary>
        private const float CatchUpRate = 0.35f;
        /// <summary>Closed faster when the gap is wide, so catching up is not slow motion.</summary>
        private const float FastCatchUpRate = 0.6f;
        private const float FastCatchUpAbove = 3f;

        /// <summary>
        /// How many updates were thrown away for holding a value that is not
        /// a number, or one no room could contain.
        ///
        /// One of these is enough to ruin a match for everybody: a NaN
        /// position is written into a player, spreads to whoever aims at it,
        /// and is then published as authoritative. The player stops moving,
        /// dies repeatedly, and every measurement of it reads NaN. Dropping
        /// the update keeps the last good value instead, which is wrong for
        /// one frame rather than permanently.
        /// </summary>
        public static long RejectedUpdates { get; private set; }

        /// <summary>
        /// Times a remote player had to be placed rather than eased, and the
        /// worst of them. This is the teleport a player actually sees: the
        /// smoothed catch-up is invisible, a snap is not.
        /// </summary>
        public static long Snaps { get; private set; }
        public static float WorstSnap { get; private set; }

        /// <summary>
        /// Frames on which a player's room node could not be worked out from
        /// its position at all, even after the body and half a unit either
        /// side of it were tried.
        ///
        /// The measurement behind "players go invisible up there": the node is
        /// what the renderer culls against, so a lookup that fails leaves a
        /// puppet holding a stale one. Non-zero says the map has places the
        /// portal volumes do not cover, and which map and how often is the
        /// difference between a room to look at and a fluke.
        /// </summary>
        public static long NodeLookupsUnresolved;

        // Local movement is predicted immediately and reconciled when a
        // snapshot names the exact owner-input frame the authority processed.
        // Do not micro-teleport a live local body after its collision pass:
        // even a small downward correction can place the capsule partly
        // through a floor with no sweep protecting that move.
        private const float PredictionSnapDistance = 2.0f;
        private const float PredictionVelocityDeadzone = 0.08f;

        public static long PredictionCorrections { get; private set; }
        public static long PredictionSnaps { get; private set; }
        public static long PredictionHistoryMisses { get; private set; }
        public static long PredictionRecoveries { get; private set; }
        public static double PredictionErrorSum { get; private set; }
        public static float PredictionWorstError { get; private set; }

        /// <summary>
        /// The fastest a puppet may be said to be travelling, in units per
        /// frame. Boost -- the quickest a hunter moves under its own power --
        /// caps at 0.6, so this is eight times anything legitimate and exists
        /// only to stop a derived velocity from becoming a launch.
        /// </summary>
        private const float MaxReportedSpeed = 5f;

        /// <summary>Positions beyond this are not a level, they are corruption.</summary>
        private const float PositionLimit = 100000f;

        /// <summary>
        /// A position measured while its owner was in one form, expressed in
        /// the form this copy of the player is actually in.
        ///
        /// UpdateForm moves Position by the difference between the biped and
        /// alt collision volumes' centres each way, so `P_alt = P_biped +
        /// (bipedCentre - altCentre)`. The two are the same standing spot
        /// written in two reference frames, and nothing in the packet said
        /// which -- so for as long as a puppet's form lagged its owner's, it
        /// was placed in the wrong one and its hitbox sat that far off the
        /// body. Vertically, on a biped cylinder 1.6 units tall, which is
        /// enough for a shot aimed at the chest to pass under it.
        ///
        /// A no-op whenever the two agree, which is almost always.
        /// </summary>
        /// <summary>
        /// <see cref="InForm"/>, for the reconciliation path. Same
        /// conversion, same reason: a position recorded while its owner was a
        /// morph ball and applied to a biped is out by the difference between
        /// the two collision centres, which is most of a chest.
        /// </summary>
        public static Vector3 InFormFor(PlayerEntity player, Vector3 position, bool measuredInAlt)
        {
            return InForm(player, position, measuredInAlt);
        }

        private static Vector3 InForm(PlayerEntity player, Vector3 position, bool measuredInAlt)
        {
            if (measuredInAlt == player.IsAltForm)
            {
                return position;
            }
            int hunter = (int)player.Hunter;
            if (hunter < 0 || hunter >= 8)
            {
                return position;
            }
            Vector3 delta = PlayerEntity.PlayerVolumes[hunter, 0].SpherePosition
                - PlayerEntity.PlayerVolumes[hunter, 2].SpherePosition;
            return measuredInAlt ? position - delta : position + delta;
        }

        private static bool Sane(Vector3 value)
        {
            return Single.IsFinite(value.X) && Single.IsFinite(value.Y) && Single.IsFinite(value.Z)
                && MathF.Abs(value.X) < PositionLimit && MathF.Abs(value.Y) < PositionLimit
                && MathF.Abs(value.Z) < PositionLimit;
        }

        /// <summary>
        /// Rising edges from the last few frames, newest first, so a
        /// one-frame press survives a lost packet. See IntentPacket.Presses.
        /// </summary>
        private static readonly uint[] _pressHistory = new uint[IntentPacket.PressHistory];

        /// <summary>
        /// Record this frame's rising edges, whether or not a packet goes out
        /// this frame.
        ///
        /// Separate from building the packet because the history is also the
        /// loss-recovery lane: a one-frame press exists only on the frame it
        /// happens, while each 60 Hz packet repeats the last few rising edges.
        /// The dedicated server then carries those edges in the bundle's
        /// separate redundant event section rather than relaying raw intents.
        /// </summary>
        public static void RecordPresses(PlayerEntity player)
        {
            if (!player.ModIsInPlay)
            {
                Array.Clear(_pressHistory);
                _hasLatch = false;
                return;
            }
            PlayerControls c = player.Controls;
            IntentButtons pressed = IntentButtons.None;
            if (c.MoveLeft.IsPressed) pressed |= IntentButtons.MoveLeft;
            if (c.MoveRight.IsPressed) pressed |= IntentButtons.MoveRight;
            if (c.MoveUp.IsPressed) pressed |= IntentButtons.MoveUp;
            if (c.MoveDown.IsPressed) pressed |= IntentButtons.MoveDown;
            if (c.Shoot.IsPressed) pressed |= IntentButtons.Shoot;
            if (c.Zoom.IsPressed) pressed |= IntentButtons.Zoom;
            if (c.Jump.IsPressed) pressed |= IntentButtons.Jump;
            if (c.Morph.IsPressed) pressed |= IntentButtons.Morph;
            if (c.Boost.IsPressed) pressed |= IntentButtons.Boost;
            if (c.AltAttack.IsPressed) pressed |= IntentButtons.AltAttack;
            if (c.ScanVisor.IsPressed) pressed |= IntentButtons.ScanVisor;
            if (c.NextWeapon.IsPressed) pressed |= IntentButtons.NextWeapon;
            if (c.PrevWeapon.IsPressed) pressed |= IntentButtons.PrevWeapon;
            if (c.RolltLeft.IsPressed) pressed |= IntentButtons.RollLeft;
            if (c.RollRight.IsPressed) pressed |= IntentButtons.RollRight;
            if (c.RollUp.IsPressed) pressed |= IntentButtons.RollUp;
            if (c.RollDown.IsPressed) pressed |= IntentButtons.RollDown;
            for (int i = _pressHistory.Length - 1; i > 0; i--)
            {
                _pressHistory[i] = _pressHistory[i - 1];
            }
            _pressHistory[0] = (uint)pressed;
            // The charge that will be spent by the shot this frame fires, and
            // the ram that will be spent by the boost it releases.
            //
            // Sampled here before capture so a release observes the charge as
            // it stood on the firing frame, before gameplay spends it. What the authority
            // needs is the value the trigger was let go on, so it is latched
            // on the frame of the release and held until a packet carries it.
            // Nothing is latched on a frame with no release, and the current
            // value is sent then, which is what keeps a puppet's charge
            // tracking its owner's while the trigger is still held.
            if (c.Shoot.IsReleased || c.Boost.IsReleased || c.AltAttack.IsPressed)
            {
                _latchedCharge = player.ModChargeLevel;
                _latchedBoostDamage = player.ModBoostDamage;
                _latchedHomingTarget = c.Shoot.IsReleased
                    ? player.ModPickNetworkHomingTarget()
                    : (byte)0;
                if (c.Shoot.IsReleased)
                {
                    // The owner's visual projectile consumes the same decision
                    // that is put on the wire for the authority/observers.
                    player.ModSetPendingHomingTarget(_latchedHomingTarget);
                }
                _hasLatch = true;
            }
        }

        /// <summary>
        /// The charge and ram strength of the newest release, waiting for a
        /// packet to carry it. See <see cref="IntentPacket.StateSize"/>.
        /// </summary>
        private static int _latchedCharge;
        private static int _latchedBoostDamage;
        private static byte _latchedHomingTarget;
        private static bool _hasLatch;

        /// <summary>Local player's controls and aim -> wire intent (client side).</summary>
        public static IntentPacket CaptureIntent(PlayerEntity player)
        {
            PlayerControls c = player.Controls;
            IntentButtons buttons = IntentButtons.None;
            if (c.MoveLeft.IsDown) buttons |= IntentButtons.MoveLeft;
            if (c.MoveRight.IsDown) buttons |= IntentButtons.MoveRight;
            if (c.MoveUp.IsDown) buttons |= IntentButtons.MoveUp;
            if (c.MoveDown.IsDown) buttons |= IntentButtons.MoveDown;
            if (c.Shoot.IsDown) buttons |= IntentButtons.Shoot;
            if (c.Zoom.IsDown) buttons |= IntentButtons.Zoom;
            if (c.Jump.IsDown) buttons |= IntentButtons.Jump;
            if (c.Morph.IsDown) buttons |= IntentButtons.Morph;
            if (c.Boost.IsDown) buttons |= IntentButtons.Boost;
            if (c.AltAttack.IsDown) buttons |= IntentButtons.AltAttack;
            if (c.ScanVisor.IsDown) buttons |= IntentButtons.ScanVisor;
            if (c.NextWeapon.IsDown) buttons |= IntentButtons.NextWeapon;
            if (c.PrevWeapon.IsDown) buttons |= IntentButtons.PrevWeapon;
            if (c.RolltLeft.IsDown) buttons |= IntentButtons.RollLeft;
            if (c.RollRight.IsDown) buttons |= IntentButtons.RollRight;
            if (c.RollUp.IsDown) buttons |= IntentButtons.RollUp;
            if (c.RollDown.IsDown) buttons |= IntentButtons.RollDown;
            // The owner's own answer, not an edge for the receiver to rebuild.
            if (player.EquipInfo.Zoomed) buttons |= IntentButtons.ZoomedState;
            // Which frame Position below is measured in. See
            // IntentButtons.AltFormState.
            if (player.IsAltForm) buttons |= IntentButtons.AltFormState;
            // Whether Position below is where this player is, or where its
            // body is lying. See IntentButtons.InPlayState.
            if (player.LoadFlags.TestFlag(LoadFlags.Spawned) && player.Health > 0)
            {
                buttons |= IntentButtons.InPlayState;
            }
            // Watching rather than playing. The only route this has to the
            // rest of the match: see IntentButtons.SpectatingState.
            if (player.Flags2.TestFlag(PlayerFlags2.Spectating))
            {
                buttons |= IntentButtons.SpectatingState;
            }
            // Ready for the next match. Only the server reads it, and only
            // while the results screen is up -- see DedicatedServer's end
            // sequence.
            if (Mods.EndScreen.Ready)
            {
                buttons |= IntentButtons.ReadyState;
            }
            var intent = new IntentPacket
            {
                Buttons = buttons,
                Aim = player.ModGunVector,
                Position = player.Position,
                // The owner's own weapon, every frame. The authority never
                // receives snapshots, so without this it showed a remote
                // player holding whatever a relayed NextWeapon press happened
                // to select from the weapons *it* believed that player had --
                // and availability comes from pickups, which are not shared.
                WeaponSelect = (byte)player.CurrentWeapon,
                // The owner's own count. Everyone simulates this player's
                // shots and spends the ammo; only the owner walks over the
                // pickups that refill it, so every other machine's copy runs
                // down and eventually refuses to spawn a beam at all.
                AmmoUa = (ushort)Math.Clamp(player.ModAmmo.Ua, 0, UInt16.MaxValue),
                AmmoMissiles = (ushort)Math.Clamp(player.ModAmmo.Missiles, 0, UInt16.MaxValue),
                // Press history is copied below into the inline value buffer.
                // What this player's next shot is worth, from the machine that
                // knows. Everything here was re-derived on the authority from
                // the buttons above until now, and re-deriving a shooter is a
                // second simulation of them: the charge count drifts by the
                // send interval and the jitter, and the two powerups are
                // collected by each machine's own copy of the pickups and so
                // can simply be absent on the authority's. Both put a
                // different number on the same shot, which is a client
                // predicting damage the authority will not deal.
                // IntentPacket.StateSize.
                ChargeLevel = (byte)Math.Clamp(
                    _hasLatch ? _latchedCharge : player.ModChargeLevel, 0, 255),
                BoostDamage = (byte)Math.Clamp(
                    _hasLatch ? _latchedBoostDamage : player.ModBoostDamage, 0, 255),
                ShotFlags = (byte)((player.DoubleDamage ? IntentPacket.FlagDoubleDamage : 0)
                    | (player.IsPrimeHunter ? IntentPacket.FlagPrimeHunter : 0)),
                HomingTarget = _hasLatch ? _latchedHomingTarget : (byte)0,
                HasState = true,
                // Which frame of the authority's simulation this player was
                // looking at while they aimed and fired. The authority rewinds
                // everybody else to it before resolving the shot -- see
                // NetUnlagged. Zero on the authority itself, which is never
                // behind, and on a client that has not been sent a snapshot
                // yet; both are read as "no rewind".
                // The snapshot this client is holding, which under
                // -snapshotpuppets is also the one its own shot was resolved
                // against; otherwise the newest one received, which is what
                // every build before this one sent. The two differ by one
                // frame -- a snapshot arrives at the top of the frame and is
                // applied at the bottom -- and the newer of them asks the
                // authority to rewind one frame less far than the shooter was
                // looking. See NetSession.AppliedSnapshotFrame.
                AckFrame = NetHooks.SnapshotOwnsPuppets && NetSession.AppliedSnapshotFrame != 0
                    ? NetSession.AppliedSnapshotFrame
                    : NetSession.LastSnapshotFrame
            };
            for (int i = 0; i < IntentPacket.PressHistory; i++)
            {
                intent.Presses[i] = _pressHistory[i];
            }
            // And the read point itself, if the puppets are being drawn on a
            // playout clock: that is a point *between* two snapshots, and an
            // integer ack cannot name it. Overwrites the choice above rather
            // than competing with it -- when the clock is running it is the
            // only honest answer to "what was I looking at". NetSmoothing.
            if (NetSmoothing.AckPoint(out uint readFrame, out byte readSub))
            {
                intent.AckFrame = readFrame;
                intent.AckSubFrame = readSub;
            }
            // The latch has been spent. From here the live value is sent again,
            // which is what lets a puppet's charge climb with its owner's while
            // the trigger is held.
            _hasLatch = false;
            if (NetLog.Enabled && (intent.Buttons.HasFlag(IntentButtons.Shoot) || player.Controls.Shoot.IsReleased))
                NetShotDiagnostics.Trace("input", ShotKey.For(player.SlotIndex, intent.AckFrame), player.CurrentWeapon,
                    $"intentFrame={intent.Frame} intentLife={intent.LifeId} inPlay={intent.Buttons.HasFlag(IntentButtons.InPlayState)} shoot={player.Controls.Shoot.IsDown} press={player.Controls.Shoot.IsPressed}");
            return intent;
        }

        /// <summary>
        /// Wire intent -> a remote player's controls (authority side). Mirrors
        /// how the keyboard path derives IsPressed/IsReleased from the
        /// previous frame, so gameplay code that tests those edges behaves
        /// the same for a remote player as for a local one.
        /// </summary>
        /// <summary>
        /// The bits of an intent that mean somebody pressed something, as
        /// opposed to the four that describe what state the sender is in.
        ///
        /// The difference matters for <see cref="PlayerEntity.ModNoteInput"/>:
        /// `InPlayState` is set on every packet a living player sends, so
        /// counting the whole mask would make a puppet look busy while its
        /// owner stood perfectly still -- and the engine lowers an idle
        /// player's gun, which their own screen would then be doing and
        /// nobody else's. Replicating the idle means replicating the idle.
        /// </summary>
        private const IntentButtons PressedButtons = ~(IntentButtons.ZoomedState
            | IntentButtons.AltFormState | IntentButtons.InPlayState
            | IntentButtons.SpectatingState | IntentButtons.ReadyState);

        /// <summary>Newest press frame already applied, per slot.</summary>
        private static readonly uint[] _lastPressFrame = new uint[PlayerEntity.SlotCapacity];
        private static readonly bool[] _pressSeen = new bool[PlayerEntity.SlotCapacity];

        /// <summary>
        /// How many frames old the trigger pull being applied this frame is.
        ///
        /// Zero on the ordinary path, where the packet that carries a press is
        /// the packet composed on the frame it happened. It is not zero when
        /// that packet was lost or arrived out of order: the edge is then
        /// recovered from the press history of a *later* packet
        /// (<see cref="MissedPresses"/>), and applied with that later packet's
        /// ack, aim and position -- so the authority rewinds by the newer
        /// packet's round trip and resolves an older shot against a world
        /// several frames too new. That is the mechanism, and this is the
        /// number that corrects it: <see cref="Mods.Network.NetUnlagged"/>
        /// adds it back on to the rewind depth.
        ///
        /// A reordered intent is thrown away outright
        /// (<see cref="NetSession.AcceptSlotIntent"/>), so a straggler's shot
        /// reaches the simulation by this same route and carries the same
        /// error.
        /// </summary>
        private static readonly bool[] _respawnRequested = new bool[PlayerEntity.SlotCapacity];
        public static bool RespawnRequested(int slot) => NetSession.Active && slot != NetSession.LocalSlot
            && slot >= 0 && slot < _respawnRequested.Length && _respawnRequested[slot];

        public static readonly int[] ShootPressAge = new int[PlayerEntity.SlotCapacity];

        public static void ApplyIntent(PlayerEntity player, in IntentPacket intent)
        {
            if (intent.LifeId == 0 || !NetPlayerLifecycle.Matches(player.SlotIndex, intent.SlotGeneration, intent.LifeId)) return;
            if (!Sane(intent.Aim))
            {
                RejectedUpdates++;
                NetLog.Event($"slot {player.SlotIndex} intent rejected: aim={intent.Aim}");
                return;
            }
            PlayerControls c = player.Controls;
            int aimSlot = player.SlotIndex;
            if (aimSlot >= 0 && aimSlot < _aimHeld.Length && _aimHeld[aimSlot]
                && (intent.AckFrame >= SpawnFrame[aimSlot]
                    || NetSession.NetFrame - SpawnFrame[aimSlot] > AimHoldCeiling))
            {
                _aimHeld[aimSlot] = false;
            }
            IntentButtons missed = MissedPresses(player.SlotIndex, intent, out int shootAge);
            if (player.SlotIndex >= 0 && player.SlotIndex < ShootPressAge.Length)
            {
                ShootPressAge[player.SlotIndex] = shootAge;
            }
            _respawnRequested[player.SlotIndex] = !intent.Buttons.HasFlag(IntentButtons.InPlayState)
                && intent.Buttons.HasFlag(IntentButtons.Shoot);
            if (!intent.Buttons.HasFlag(IntentButtons.InPlayState))
            {
                // Consume history, but never turn a dead player's respawn button into
                // a weapon press (or a charged-shot release) on an ahead-of-owner puppet.
                c.ClearAll();
                ShootPressAge[player.SlotIndex] = 0;
                player.ModSetSpectating(intent.Buttons.HasFlag(IntentButtons.SpectatingState));
                return;
            }
            Set(c.MoveLeft, intent.Buttons.HasFlag(IntentButtons.MoveLeft), missed.HasFlag(IntentButtons.MoveLeft));
            Set(c.MoveRight, intent.Buttons.HasFlag(IntentButtons.MoveRight), missed.HasFlag(IntentButtons.MoveRight));
            Set(c.MoveUp, intent.Buttons.HasFlag(IntentButtons.MoveUp), missed.HasFlag(IntentButtons.MoveUp));
            Set(c.MoveDown, intent.Buttons.HasFlag(IntentButtons.MoveDown), missed.HasFlag(IntentButtons.MoveDown));
            Set(c.Shoot, intent.Buttons.HasFlag(IntentButtons.Shoot), missed.HasFlag(IntentButtons.Shoot));
            Set(c.Zoom, intent.Buttons.HasFlag(IntentButtons.Zoom), missed.HasFlag(IntentButtons.Zoom));
            Set(c.Jump, intent.Buttons.HasFlag(IntentButtons.Jump), missed.HasFlag(IntentButtons.Jump));
            Set(c.Morph, intent.Buttons.HasFlag(IntentButtons.Morph), missed.HasFlag(IntentButtons.Morph));
            if (c.Morph.IsPressed)
            {
                NetLog.Event($"slot {player.SlotIndex} morph press received, now {player.ModFormState()}");
            }
            Set(c.Boost, intent.Buttons.HasFlag(IntentButtons.Boost), missed.HasFlag(IntentButtons.Boost));
            Set(c.AltAttack, intent.Buttons.HasFlag(IntentButtons.AltAttack), missed.HasFlag(IntentButtons.AltAttack));
            Set(c.ScanVisor, intent.Buttons.HasFlag(IntentButtons.ScanVisor), missed.HasFlag(IntentButtons.ScanVisor));
            Set(c.NextWeapon, intent.Buttons.HasFlag(IntentButtons.NextWeapon), missed.HasFlag(IntentButtons.NextWeapon));
            Set(c.PrevWeapon, intent.Buttons.HasFlag(IntentButtons.PrevWeapon), missed.HasFlag(IntentButtons.PrevWeapon));
            Set(c.RolltLeft, intent.Buttons.HasFlag(IntentButtons.RollLeft), missed.HasFlag(IntentButtons.RollLeft));
            Set(c.RollRight, intent.Buttons.HasFlag(IntentButtons.RollRight), missed.HasFlag(IntentButtons.RollRight));
            Set(c.RollUp, intent.Buttons.HasFlag(IntentButtons.RollUp), missed.HasFlag(IntentButtons.RollUp));
            Set(c.RollDown, intent.Buttons.HasFlag(IntentButtons.RollDown), missed.HasFlag(IntentButtons.RollDown));
            if (intent.WeaponSelect != 0xFF)
            {
                player.ModSetWeapon((BeamType)intent.WeaponSelect);
            }
            player.ModSetAmmo(intent.AmmoUa, intent.AmmoMissiles);
            // Somebody is playing this hunter, even though it is not this
            // machine's keyboard doing it.
            //
            // Without this a puppet looked idle from the moment its owner
            // stopped respawning or changing weapon, and the engine lowers an
            // idle player's gun -- which `CanShoot` refuses to fire through.
            // So a player holding still and firing, which is what a sniper
            // does, had their shots fail to spawn on every other machine
            // including the authority, whose shots are the only ones that
            // count. See PlayerEntity.ModNoteInput.
            if ((intent.Buttons & PressedButtons) != IntentButtons.None)
            {
                player.ModNoteInput();
            }
            // After the weapon, because zoom belongs to one and the engine
            // refuses it on a weapon that cannot. Taken as state rather than
            // rebuilt from the press: see IntentButtons.ZoomedState.
            player.ModSetZoom(intent.Buttons.HasFlag(IntentButtons.ZoomedState));
            // The owner's own answer about whether it is still in the match.
            // On the authority this is what makes a spectator stop being a
            // target; from there the snapshot's FlagSpectating carries it to
            // everybody else.
            player.ModSetSpectating(intent.Buttons.HasFlag(IntentButtons.SpectatingState));
            // Form changes come from the recovered Morph control above. The
            // authority must retain its collision/freeze decision; forcing an
            // owner's reported form could unmorph through a low ceiling.
            // And what this player's next shot is worth, from the one machine
            // that knows -- charge, ram, double damage, the Prime Hunter
            // bonus. Only here, and only from a sender that actually said so:
            // a client built before IntentPacket.StateSize sends none of it,
            // and writing zeros for it would take a puppet's charge and
            // powerups away rather than leave them where the old build's
            // re-derivation put them.
            //
            // Only on the authority: it is the machine
            // whose copy of this shot decides what it hit, and a client that
            // also acted on it would be correcting a puppet from two sources.
            if (intent.HasState && intent.HomingTarget != 0
                && player.SlotIndex != NetHooks.LocalSlot)
            {
                // Unlike charge/damage state, this is a one-shot visual/physics
                // decision that every machine simulating the projectile needs.
                player.ModSetPendingHomingTarget(intent.HomingTarget);
            }
            if (intent.HasState && (NetSession.IsAuthority || NetSession.IsHost))
            {
                player.ModSetShotState(intent.ChargeLevel,
                    (intent.ShotFlags & IntentPacket.FlagDoubleDamage) != 0);
            }
        }

        /// <summary>
        /// Rising edges this packet carries that this slot has not applied
        /// yet, taken from the packet's short history of them.
        ///
        /// Without this, an edge existed only in the single packet whose
        /// frame it fell on, and losing that packet lost the action outright.
        /// The frame each entry belongs to is what stops a press being
        /// applied twice when the redundant copies arrive.
        /// </summary>
        /// <summary>
        /// A new life starts here: forget the trigger pulls the last one left
        /// behind.
        ///
        /// The press history exists so a one-frame pull survives a lost packet,
        /// and it is keyed by frame number rather than by life. Holding fire
        /// while dead is how a player respawns early, so the history is full
        /// of pulls at the exact moment the authority puts them back on the
        /// map -- and <see cref="MissedPresses"/> then replays the backlog:
        /// three Power Beam rounds on three consecutive frames against a
        /// five-frame cooldown, aimed wherever the last life was looking.
        /// Clearing the flag re-runs the baseline the first packet from a peer
        /// already takes, which replays nothing and loses at most one packet's
        /// worth of real pulls.
        ///
        /// Called from <c>PlayerEntity.Spawn</c>, so it runs on the authority
        /// and on every client alike.
        /// </summary>
        public static void NoteSpawn(int slot)
        {
            if (slot < 0 || slot >= _pressSeen.Length)
            {
                return;
            }
            _pressSeen[slot] = false;
            ShootPressAge[slot] = 0;
            SpawnFrame[slot] = NetSession.NetFrame;
            _aimHeld[slot] = true;
        }

        /// <summary>The frame each slot last spawned on. Diagnostics only.</summary>
        public static readonly uint[] SpawnFrame = new uint[PlayerEntity.SlotCapacity];

        /// <summary>
        /// Whether this slot's relayed aim still describes the life that ended.
        ///
        /// The intents already in flight when the authority respawns somebody
        /// were composed before their owner could know, so they carry the aim
        /// the dead player was holding -- and the authority fires along it from
        /// the spawn point. Measured against Japan: 20 frames of shots leaving
        /// on (1.00,0.03,0.06) before the direction snapped to the spawn
        /// facing (0,0,1), which is one round trip.
        ///
        /// Frame numbers cannot tell these packets apart: they are newer than
        /// anything seen, only stale in wall-clock terms. What separates them
        /// is <see cref="IntentPacket.AckFrame"/> -- the snapshot its sender
        /// had applied. Once that reaches the frame the spawn was published on,
        /// the client has demonstrably seen it and its aim is its own again.
        /// Until then the spawn facing stands.
        /// </summary>
        private static readonly bool[] _aimHeld = new bool[PlayerEntity.SlotCapacity];

        /// <summary>How long the hold may last if an ack never catches up.</summary>
        private const uint AimHoldCeiling = 90;

        public static bool AimTrusted(int slot)
        {
            return slot < 0 || slot >= _aimHeld.Length || !_aimHeld[slot];
        }

        private static IntentButtons MissedPresses(int slot, in IntentPacket intent,
            out int shootAge)
        {
            shootAge = 0;
            if (slot < 0 || slot >= _lastPressFrame.Length)
            {
                return IntentButtons.None;
            }
            if (!_pressSeen[slot])
            {
                // First packet from this peer: note where their frame counter
                // stands and replay nothing. The history reaches back several
                // frames, and applying all of it would open with a burst of
                // presses from before this client was listening.
                _pressSeen[slot] = true;
                _lastPressFrame[slot] = intent.Frame;
                return NetCommandStream.Enabled(slot) ? (IntentButtons)intent.Presses[0] : IntentButtons.None;
            }
            IntentButtons missed = IntentButtons.None;
            for (int i = intent.Presses.Length - 1; i >= 0; i--)
            {
                uint frame = unchecked(intent.Frame - (uint)i);
                if (!NetLifecycleTracker.Newer(frame, _lastPressFrame[slot]))
                {
                    continue;
                }
                missed |= (IntentButtons)intent.Presses[i];
                // The oldest trigger pull in this packet, because that is the
                // one whose world is furthest from the one the packet's ack
                // names. The loop runs oldest-first, so the first Shoot it
                // finds is it, and `i` is its age in frames.
                if (shootAge == 0
                    && ((IntentButtons)intent.Presses[i]).HasFlag(IntentButtons.Shoot))
                {
                    shootAge = i;
                }
            }
            // Every frame up to this packet is now accounted for, whether or
            // not it carried a press. Leaving gaps here let the same frame be
            // consumed again by a later packet.
            if (NetLifecycleTracker.Newer(intent.Frame, _lastPressFrame[slot])) _lastPressFrame[slot] = intent.Frame;
            return missed;
        }

        /// <summary>
        /// Drive one control from a relayed intent.
        ///
        /// The held state comes from the packet's button levels, but the
        /// rising edge comes only from the press history -- never from the
        /// level as well. Deriving it from both applied the same press twice:
        /// once when the level went down, once when the redundant copy
        /// arrived. For a toggle like morph, twice is the same as never, and
        /// the puppet ended up one transition behind its owner for the rest
        /// of the match -- drawn as a biped while morphed, and as a morph
        /// ball while walking.
        /// </summary>
        private static void Set(Keybind bind, bool down, bool pressed = false)
        {
            bool wasDown = bind.IsDown;
            bind.IsDown = down || pressed;
            bind.IsPressed = pressed;
            bind.IsReleased = !down && wasDown && !pressed;
        }

        /// <summary>
        /// Authoritative state -> a player, on a client that is not the
        /// authority.
        ///
        /// Snapping, not interpolating: correctness first. Smoothing belongs
        /// on top of a working baseline, not underneath one -- interpolating
        /// before the plain path is proven only hides where the two sides
        /// disagree.
        ///
        /// The cases are deliberately different. Somebody else's player is a
        /// puppet and takes everything, including the spawn itself, because
        /// Spawn() is what unhides the model. This machine's own player takes
        /// its spawn, its death and its health from the authority too -- those
        /// are the match, and a client that decided them for itself was
        /// playing a different one -- but keeps its facing, because aim has to
        /// answer the mouse now rather than after a round trip, and keeps its
        /// own position immediately. The isLocal branch reconciles that
        /// prediction against the exact owner-input frame echoed by the server.
        /// </summary>
        private static readonly ushort[] _appliedLifeId = new ushort[PlayerEntity.SlotCapacity];
        private static readonly bool[] _lifeApplied = new bool[PlayerEntity.SlotCapacity];

        private static void BeginRemoteLife(PlayerEntity player, in PlayerState state)
        {
            int slot = player.SlotIndex;
            ForgetSlot(slot);
            _appliedLifeId[slot] = state.LifeId;
            _lifeApplied[slot] = true;
            NetHitPrediction.NoteRespawn(slot);
            NetDamage.BeginLife(slot, state);
            NetHitClaims.ForgetSlot(slot);
            NetUnlagged.ResetSlot(slot);
            player.ModResetNetworkHistory();
            _lastPredictionAck[slot] = NetSession.NetFrame;
            player.Controls?.ClearAll();
            player.ModSetFrozen(false);
            player.ModSetBurning(false);
            player.ModSetDisrupted(false);
            if (state.LifeId != 0)
            {
                NetPlayerLifecycle.ApplyingSpawn = true;
                try { player.ModNetSpawn(state.Position, state.Facing); }
                finally { NetPlayerLifecycle.ApplyingSpawn = false; }
                Move(player, InForm(player, state.Position, (state.Flags & PlayerState.FlagAltForm) != 0));
                player.Speed = state.Speed;
                player.ModSetSpawnFacing(state.Facing);
                if (state.Health == 0) player.ModNetDie();
            }
            player.Health = state.Health;
        }

        public static void ApplyState(PlayerEntity player, in PlayerState state, bool isLocal)
        {
            int slot = player.SlotIndex;
            // Validate before scores, damage, position, or presentation can change.
            if (slot != state.SlotIndex || !NetPlayerLifecycle.Matches(slot, state.SlotGeneration, state.LifeId)) return;
            if (!Sane(state.Position) || !Sane(state.Speed) || !Sane(state.Facing))
            {
                RejectedUpdates++;
                return;
            }
            bool fresh = !_lifeApplied[slot] || _appliedLifeId[slot] != state.LifeId;
            if (fresh) BeginRemoteLife(player, state);
            bool spawned = (state.Flags & PlayerState.FlagSpawned) != 0 && state.Health > 0;
            _formSaid[slot] = (byte)((state.Flags & PlayerState.FlagAltForm) != 0 ? 2 : 1);
            // During room-change settling, snapshots from the finished
            // match can still arrive. Never seed the fresh match with the old
            // winning score.
            if (!NetRoomChange.Settling)
            {
                GameState.Points[slot] = state.Points;
                GameState.Kills[slot] = state.Kills;
                GameState.Deaths[slot] = state.Deaths;
            }
            // Preserve the local prediction before damage replay potentially
            // applies authoritative knockback for feedback. Reconciliation
            // rebuilds velocity from this value so the impulse is not doubled.
            Vector3 predictedCurrentSpeed = isLocal ? player.Speed : default;
            NetDamage.Replay(player, state);
            if (!spawned)
            {
                _pendingLocalCorrection[slot] = false;
                if (state.Health == 0 && player.Health > 0) player.ModNetDie();
                player.Health = state.Health;
                if (state.Health == 0) NetHitPrediction.NoteDeath(slot);
                player.ModSetSpectating((state.Flags & PlayerState.FlagSpectating) != 0);
                return;
            }
            // A client may predict a kill-plane/self death before the server
            // agrees. Movement is server authoritative now, so a same-life
            // alive snapshot must be allowed to recover that false local death
            // rather than waiting forever for a new LifeId the server will
            // never allocate.
            bool recoveredLocalLife = false;
            if (!fresh && player.Health <= 0)
            {
                if (!isLocal) return;
                RecoverLocalAuthorityLife(player, state);
                recoveredLocalLife = true;
            }
            if (!isLocal)
            {
                Move(player, InForm(player, state.Position, (state.Flags & PlayerState.FlagAltForm) != 0));
                player.Speed = state.Speed;
                player.Health = NetHitPrediction.HealthFor(slot, state.Health);
                player.ModSetFacing(state.Facing);
                player.ModSetWeapon((BeamType)state.CurrentWeapon);
                player.EquipInfo.Zoomed = (state.Flags & PlayerState.FlagZoomed) != 0;
                ApplyForm(player, (state.Flags & PlayerState.FlagAltForm) != 0);
                player.ModSetSpectating((state.Flags & PlayerState.FlagSpectating) != 0);
            }
            else
            {
                if (!fresh && !recoveredLocalLife && NetRoomChange.GameplayReady && !NetMovementPrediction.Active)
                {
                    ReconcileLocalMovement(player, state, slot, predictedCurrentSpeed);
                    ApplyForm(player, (state.Flags & PlayerState.FlagAltForm) != 0);
                }
                player.Health = NetHitPrediction.LocalHealthFor(player, state.Health);
            }
            if (!isLocal || !NetMovementPrediction.Active) player.ModSetFrozen((state.Flags & PlayerState.FlagFrozen) != 0);
            ApplyAfflictions(player, state);
        }

        private static void RecoverLocalAuthorityLife(PlayerEntity player,
            in PlayerState state)
        {
            int slot = player.SlotIndex;
            NetPlayerLifecycle.ApplyingSpawn = true;
            try
            {
                // Same life, so do not run respawn-choice/hunter-selection
                // policy again. This is only rebuilding presentation/simulation
                // state after a local death prediction the authority rejected.
                player.ModNetSpawn(state.Position, state.Facing, respawn: false);
            }
            finally
            {
                NetPlayerLifecycle.ApplyingSpawn = false;
            }
            Move(player, InForm(player, state.Position, (state.Flags & PlayerState.FlagAltForm) != 0));
            player.Speed = state.Speed;
            player.ModSetSpawnFacing(state.Facing);
            player.Health = state.Health;
            player.ModSetSpectating((state.Flags & PlayerState.FlagSpectating) != 0);
            player.ModResetNetworkHistory();
            if ((uint)slot < _lastPredictionAck.Length)
            {
                _lastPredictionAck[slot] = NetSession.NetFrame;
                _pendingLocalCorrection[slot] = false;
            }
            NetHitPrediction.NoteRespawn(slot);
            PredictionRecoveries++;
            NetLog.Event($"slot {slot} recovered same-life local death from authority");
        }

        private static void ApplyAfflictions(PlayerEntity player, PlayerState state)
        {
            player.ModSetDisrupted((state.Flags & PlayerState.FlagDisrupted) != 0);
            player.ModSetBurning((state.Flags & PlayerState.FlagBurning) != 0);
        }

        /// <summary>
        /// Keep a remote player's form in step with the authority's, without
        /// stepping on the transition.
        ///
        /// The owner's relayed press normally drives the switch. The timed
        /// guard also protects a normal transition while the older authority
        /// snapshot (or owner intent) is still in flight.
        /// </summary>
        private static void ApplyForm(PlayerEntity player, bool altForm)
        {
            int slot = player.SlotIndex;
            if (slot < 0 || slot >= _formReconciliation.Length)
            {
                return;
            }
            FormCorrection correction = ReconcileForm(slot, NetSession.NetFrame,
                altForm, player.IsAltForm, player.IsMorphing, player.IsUnmorphing,
                NetSession.SlotPing[slot]);
            // First the real transition, because that is what creates the
            // parts of a form that are separate entities -- Weavel's
            // halfturret exists only because EnterAltForm adds it, so a
            // client that skipped straight to the flag showed a Weavel in alt
            // form with no turret. Only if that does not take does the flag
            // get forced.
            if (correction == FormCorrection.Start)
            {
                player.ModStartFormSwitch();
            }
            else if (correction == FormCorrection.Force)
            {
                player.ModForceForm(altForm);
            }
        }

        internal static FormCorrection ReconcileForm(int slot, uint frame, bool desiredAlt,
            bool actualAlt, bool morphing, bool unmorphing, int ping)
            => slot < 0 || slot >= _formReconciliation.Length ? FormCorrection.None
                : _formReconciliation[slot].Step(frame, desiredAlt, actualAlt, morphing, unmorphing, ping);

        private static readonly uint[] _lastPredictionAck =
            new uint[PlayerEntity.SlotCapacity];
        private static readonly bool[] _pendingLocalCorrection =
            new bool[PlayerEntity.SlotCapacity];
        private static readonly Vector3[] _pendingLocalPosition =
            new Vector3[PlayerEntity.SlotCapacity];
        private static readonly Vector3[] _pendingLocalSpeed =
            new Vector3[PlayerEntity.SlotCapacity];
        private static readonly bool[] _pendingLocalAltForm =
            new bool[PlayerEntity.SlotCapacity];

        internal static void ApplyPendingLocalCorrection(PlayerEntity player)
        {
            int slot = player.SlotIndex;
            if ((uint)slot >= _pendingLocalCorrection.Length
                || !_pendingLocalCorrection[slot])
            {
                return;
            }
            _pendingLocalCorrection[slot] = false;
            if (player.Health <= 0) return;
            Move(player, InForm(player, _pendingLocalPosition[slot], _pendingLocalAltForm[slot]));
            player.Speed = _pendingLocalSpeed[slot];
            InvalidateLocalPrediction(player, slot);
        }

        private static void InvalidateLocalPrediction(PlayerEntity player, int slot)
        {
            // Outstanding predictions predate the correction. Comparing their
            // unchanged errors again would add the same impulse every snapshot
            // or repeatedly teleport the body back while the ack catches up.
            player.ModResetNetworkHistory();
            _lastPredictionAck[slot] = NetSession.NetFrame;
        }

        private static void QueueLocalCorrection(in PlayerState state, int slot)
        {
            _pendingLocalPosition[slot] = state.Position;
            _pendingLocalSpeed[slot] = state.Speed;
            _pendingLocalAltForm[slot] = (state.Flags & PlayerState.FlagAltForm) != 0;
            _pendingLocalCorrection[slot] = true;
            PredictionCorrections++;
            PredictionSnaps++;
        }

        private static void ReconcileLocalMovement(PlayerEntity player,
            in PlayerState state, int slot, Vector3 predictedCurrentSpeed)
        {
            if ((uint)slot >= _lastPredictionAck.Length)
            {
                return;
            }
            uint ack = NetSession.RemoteInputFrames[slot];
            if (ack == 0 || NetLifecycleTracker.Newer(ack, NetSession.NetFrame)
                || (_lastPredictionAck[slot] != 0
                && !NetLifecycleTracker.Newer(ack, _lastPredictionAck[slot])))
            {
                return;
            }
            // Process each acknowledgement once, including history misses and
            // form transitions. ApplyState may run twice per simulation tick.
            _lastPredictionAck[slot] = ack;

            // The authority result below was captured immediately after first
            // simulating this exact owner input. Compare it only with the
            // client's prediction recorded for the same input frame.
            if (!player.ModGetNetworkPrediction(ack,
                    out Vector3 predictedPosition, out Vector3 predictedSpeed,
                    out bool predictedAlt))
            {
                PredictionHistoryMisses++;
                // A long outage can outlive the bounded prediction history.
                // Recover from the current authority state instead of letting
                // the local body diverge indefinitely without a comparison.
                QueueLocalCorrection(state, slot);
                return;
            }

            bool authorityAlt = NetSession.RemoteInputAltForms[slot];
            if (predictedAlt != authorityAlt)
            {
                return;
            }

            Vector3 authorityPosition = NetSession.RemoteInputPositions[slot];
            Vector3 authoritySpeed = NetSession.RemoteInputSpeeds[slot];
            if (!Sane(authorityPosition) || !Sane(authoritySpeed))
            {
                RejectedUpdates++;
                return;
            }
            Vector3 error = authorityPosition - predictedPosition;
            if (!Sane(error))
            {
                RejectedUpdates++;
                return;
            }

            float distance = error.Length;
            PredictionErrorSum += distance;
            PredictionWorstError = Math.Max(PredictionWorstError, distance);

            if (distance >= PredictionSnapDistance)
            {
                // Large disagreements are real authority corrections. Small
                // disagreements stay in local prediction so every physical move
                // remains collision-swept. Snap to a position the authority
                // actually occupied, not to a synthetic current+historical
                // error point that may lie through nearby geometry.
                QueueLocalCorrection(state, slot);
                return;
            }

            // Reconcile velocity only when the authority genuinely disagreed
            // at the matched input frame. Tiny gravity/floor differences are
            // ignored so normal standing/falling noise cannot be fed back as
            // a fresh downward impulse every snapshot.
            Vector3 speedError = authoritySpeed - predictedSpeed;
            if (Sane(speedError)
                && speedError.LengthSquared >= PredictionVelocityDeadzone * PredictionVelocityDeadzone)
            {
                Vector3 targetSpeed = authoritySpeed
                    + (predictedCurrentSpeed - predictedSpeed);
                if (Sane(targetSpeed))
                {
                    player.Speed = targetSpeed;
                    InvalidateLocalPrediction(player, slot);
                }
            }
        }

        /// <summary>
        /// Forget where the authority had everybody standing, because it was
        /// in a different room. The next snapshot that reports a player
        /// spawned then counts as a placement rather than as a continuation,
        /// which is what re-seats everyone after a rotation.
        /// </summary>
        public static void NoteRoomChanged()
        {
            NetMovementInput.Reset();
            NetCommandStream.Reset();
            NetMovementPrediction.Reset();
            Array.Clear(_formReconciliation);
            Array.Clear(_lifeApplied);
            Array.Clear(_reportSeen);
            Array.Clear(_lastPredictionAck);
            Array.Clear(_pendingLocalCorrection);
            Array.Clear(_pendingLocalPosition);
            Array.Clear(_pendingLocalSpeed);
            Array.Clear(_pendingLocalAltForm);
        }

        public static void Reset()
        {
            NetMovementInput.Reset();
            NetCommandStream.Reset();
            NetMovementPrediction.Reset();
            Array.Clear(_formReconciliation);
            Array.Clear(_appliedLifeId);
            Array.Clear(_lifeApplied);
            Snaps = 0;
            WorstSnap = 0;
            NodeLookupsUnresolved = 0;
            PlacementsRefused = 0;
            SpawnFacingsTurned = 0;
            WorstSpawnFacing = 0;
            StaleDeathsIgnored = 0;
            Array.Clear(_formSaid);
            Array.Clear(_lastPressFrame);
            Array.Clear(_pressSeen);
            Array.Clear(_aimHeld);
            Array.Clear(SpawnFrame);
            Array.Clear(ShootPressAge);
            Array.Clear(_pressHistory);
            _hasLatch = false;
            Array.Clear(_lastPredictionAck);
            Array.Clear(_pendingLocalCorrection);
            Array.Clear(_pendingLocalPosition);
            Array.Clear(_pendingLocalSpeed);
            Array.Clear(_pendingLocalAltForm);
            PredictionCorrections = 0;
            PredictionSnaps = 0;
            PredictionHistoryMisses = 0;
            PredictionRecoveries = 0;
            PredictionErrorSum = 0;
            PredictionWorstError = 0;
            Array.Clear(_lastReportPosition);
            Array.Clear(_lastReportFrame);
            Array.Clear(_reportSeen);
        }

        /// <summary>
        /// Forget everything remembered about one slot, because whoever was in
        /// it has gone and the next occupant is a different person.
        ///
        /// Every array above is indexed by slot and, until this existed, was
        /// cleared only when the whole session started or stopped, or when the
        /// room changed. A slot that changed hands mid-match therefore handed
        /// the newcomer the previous occupant's history -- their last reported
        /// position and frame number, their spawn barrier, their divergence
        /// and staleness counters.
        ///
        /// That is not a theoretical hazard; StaleSinceSpawn names it in so
        /// many words: "a peer that reconnects restarts its counter at zero,
        /// and a slot that changes hands inherits the barrier of whoever held
        /// it... which is a player nobody can hit and who slides without ever
        /// taking a step". It is bounded there by a 120-frame give-up, so it
        /// costs two seconds rather than a session -- but the bound is a
        /// mitigation for a state that should not exist, and two seconds of a
        /// player who cannot be hit is still the thing being reported.
        ///
        /// Cheap and unambiguous: a slot changing hands means the old
        /// occupant's history is meaningless by definition, so there is
        /// nothing to weigh up.
        /// </summary>
        public static void ForgetSlot(int slot)
        {
            if (slot < 0 || slot >= PlayerEntity.SlotCapacity)
            {
                return;
            }
            _formReconciliation[slot].Reset();
            _lifeApplied[slot] = false;
            _appliedLifeId[slot] = 0;
            _lastPressFrame[slot] = 0;
            _pressSeen[slot] = false;
            _aimHeld[slot] = false;
            SpawnFrame[slot] = 0;
            ShootPressAge[slot] = 0;
            _respawnRequested[slot] = false;
            if (slot == NetSession.LocalSlot)
            {
                Array.Clear(_pressHistory);
                _hasLatch = false;
                _latchedCharge = _latchedBoostDamage = 0;
            }
            NetMovementInput.ResetSlot(slot);
            NetCommandStream.ResetSlot(slot);
            if (slot == NetSession.LocalSlot) NetMovementPrediction.Reset();
            _lastPredictionAck[slot] = 0;
            _pendingLocalCorrection[slot] = false;
            _pendingLocalPosition[slot] = Vector3.Zero;
            _lastReportPosition[slot] = Vector3.Zero;
            _lastReportFrame[slot] = 0;
            _reportSeen[slot] = false;
        }

        /// <summary>
        /// Put a remote player where its owner says it is.
        ///
        /// Called for every client, the authority included, so there is
        /// exactly one simulation of each player: the one on the machine
        /// whose keyboard is driving it. Everyone else follows.
        /// </summary>
        public static void ApplyReportedPosition(PlayerEntity player, in IntentPacket intent)
        {
            if (!Sane(intent.Position))
            {
                RejectedUpdates++;
                return;
            }
            if (FrozenInPlace(player))
            {
                return;
            }
            if (intent.Position == Vector3.Zero)
            {
                return; // the owner has not spawned yet
            }
            if (StaleSinceSpawn(player, intent))
            {
                return;
            }
            Vector3 reported = InForm(player, intent.Position,
                intent.Buttons.HasFlag(IntentButtons.AltFormState));
            NoteReportedVelocity(player, reported, intent.Frame);
            Vector3 delta = reported - player.Position;
            float distance = delta.Length;
            if (distance > SnapDistance)
            {
                // Too far to be movement: a respawn, a teleporter, or a long
                // gap in the packets. Snapping is right here -- gliding across
                // half the level would be worse than a jump.
                Snaps++;
                WorstSnap = Math.Max(WorstSnap, distance);
                Move(player, reported);
                return;
            }
            // The owner also sends the aim that was calculated against this
            // position. Smoothing here leaves the authoritative hitbox behind
            // that aim under latency, so moving directly is required for
            // collision and rendering to agree.
            Move(player, reported);
        }

        /// <summary>
        /// The position half of <see cref="ApplyReportedPosition"/>, with none
        /// of its bookkeeping. Called a second time in the same frame, after
        /// the engine's movement step, so the velocity it derives and the
        /// snaps it counts must not be counted twice.
        /// </summary>
        /// <summary>
        /// Put a puppet back where the *authority's snapshot* said, after the
        /// engine's movement step.
        ///
        /// The snapshot twin of <see cref="RestoreReportedPosition"/>, and it
        /// exists for the same reason: a puppet is placed, then simulated one
        /// frame further, and a shot resolved after that step is tested
        /// against the result rather than against the position anybody agreed
        /// on. For a player in the air that frame is vertical and was measured
        /// at up to 0.377 units, against a headshot band 0.3 units tall.
        ///
        /// Which of the two runs is which world the machine is claiming to
        /// hold: the authority pins to what the owner reported, because that
        /// is what its history files; a client under
        /// <see cref="NetHooks.SnapshotOwnsPuppets"/> pins to the snapshot,
        /// because that is what it draws and what its ack names.
        /// </summary>
        /// <summary>
        /// Put a remote player at the exact sub-frame world most recently
        /// presented to this client. Called before local input so collision
        /// tests the same opponent position the shooter actually aimed at.
        /// </summary>
        public static void RestoreSnapshotPresentationPosition(PlayerEntity player,
            in PlayerState state)
        {
            if (FrozenInPlace(player))
            {
                return;
            }
            if (NetSmoothing.SamplePresentation(player.SlotIndex,
                    out Vector3 presented, out bool presentedAlt)
                && Sane(presented) && presented != Vector3.Zero)
            {
                Move(player, InForm(player, presented, presentedAlt));
                return;
            }
            RestoreSnapshotPosition(player, state);
        }

        public static void RestoreSnapshotPosition(PlayerEntity player, in PlayerState state)
        {
            if (FrozenInPlace(player))
            {
                return;
            }
            // The playout clock's answer if it has one: a point between two
            // snapshots rather than whichever one arrived last, which is the
            // difference between an opponent who moves and one who stutters.
            // The intent carries the read point, so the authority rewinds to
            // exactly this world and nothing is given up for it.
            // NetSmoothing.
            if (NetSmoothing.Sample(player.SlotIndex, out Vector3 smoothed, out bool smoothedAlt)
                && Sane(smoothed) && smoothed != Vector3.Zero)
            {
                Move(player, InForm(player, smoothed, smoothedAlt));
                return;
            }
            if (!Sane(state.Position) || state.Position == Vector3.Zero)
            {
                return;
            }
            Move(player, InForm(player, state.Position,
                (state.Flags & PlayerState.FlagAltForm) != 0));
        }

        public static void RestoreReportedPosition(PlayerEntity player, in IntentPacket intent)
        {
            if (!Sane(intent.Position) || intent.Position == Vector3.Zero
                || StaleSinceSpawn(player, intent) || FrozenInPlace(player))
            {
                return;
            }
            Move(player, InForm(player, intent.Position,
                intent.Buttons.HasFlag(IntentButtons.AltFormState)));
        }

        private static bool StaleSinceSpawn(PlayerEntity player, in IntentPacket intent) =>
            !NetPlayerLifecycle.Matches(player.SlotIndex, intent.SlotGeneration, intent.LifeId)
            || !intent.Buttons.HasFlag(IntentButtons.InPlayState);

        private static readonly Vector3[] _lastReportPosition = new Vector3[PlayerEntity.SlotCapacity];
        private static readonly uint[] _lastReportFrame = new uint[PlayerEntity.SlotCapacity];
        private static readonly bool[] _reportSeen = new bool[PlayerEntity.SlotCapacity];

        /// <summary>
        /// How fast a puppet is travelling, worked out from the positions its
        /// owner reported rather than from a simulation of it.
        ///
        /// Nothing else fills this in. The authority skips a remote player's
        /// movement step entirely -- the owner already ran it and sent the
        /// result -- so Speed would keep whatever it last held, and it was
        /// therefore forced to zero. But Speed is in the snapshot, so that
        /// zero became the authoritative velocity of every remote player on
        /// every screen: opponents slid around at a dead stop, and each
        /// client had its own speed cleared sixty times a second.
        ///
        /// The gap between two reports is what it is divided by, so this
        /// stays right when a packet goes missing and the next one covers
        /// four frames instead of two.
        /// </summary>
        private static void NoteReportedVelocity(PlayerEntity player, Vector3 reported, uint frame)
        {
            int slot = player.SlotIndex;
            if (slot < 0 || slot >= _lastReportFrame.Length)
            {
                return;
            }
            if (_reportSeen[slot] && frame > _lastReportFrame[slot])
            {
                // Capped: a report that follows a long silence describes a
                // gap, not a frame of movement, and dividing by two hundred
                // is as wrong as dividing by one.
                uint elapsed = Math.Min(frame - _lastReportFrame[slot], 8);
                Vector3 travelled = reported - _lastReportPosition[slot];
                float step = travelled.Length;
                if (!Sane(travelled) || step > SnapDistance)
                {
                    // Not movement: a respawn, a teleporter, or a gap in the
                    // packets. Dividing a jump across the level by two frames
                    // produces a velocity of a hundred and fifty units a
                    // frame, and that number does not stay here -- it goes
                    // into the snapshot as this player's authoritative speed,
                    // every client applies it to its puppet, and the owner
                    // takes it back at its next respawn and is launched out of
                    // the level. Measured before this guard: the authority
                    // held a player at Y=163 and climbing 35 units a frame.
                    player.Speed = Vector3.Zero;
                }
                else
                {
                    Vector3 speed = travelled / elapsed;
                    float magnitude = speed.Length;
                    // Belt and braces. Boost, the fastest a hunter moves, caps
                    // at 0.6 units a frame; anything near this ceiling is
                    // already not a hunter running.
                    if (magnitude > MaxReportedSpeed)
                    {
                        speed *= MaxReportedSpeed / magnitude;
                    }
                    player.Speed = speed;
                }
            }
            if (!_reportSeen[slot] || frame > _lastReportFrame[slot])
            {
                _reportSeen[slot] = true;
                _lastReportFrame[slot] = frame;
                _lastReportPosition[slot] = reported;
            }
        }

        /// <summary>
        /// Move the player's room node along with it. NodeRef is what the
        /// renderer culls against (PlayerDraw: `IsMainPlayer ||
        /// IsVisible(NodeRef)`), and the engine normally advances it during
        /// simulation. Writing a position straight in skips that, so a remote
        /// player kept the node it spawned in and vanished -- or showed only
        /// a shadow -- as soon as the viewer was elsewhere.
        /// </summary>
        /// <summary>
        /// Whether this puppet is frozen, and so must not be moved by what its
        /// owner is still reporting.
        ///
        /// The other half of "frozen players who keep moving", and the half
        /// the state flag could not reach. A freeze is resolved on the
        /// authority, and its victim does not learn of it for a round trip --
        /// during which they are still walking about on their own machine and
        /// still reporting where they have got to. Every one of those reports
        /// was applied on top of a player the authority was holding perfectly
        /// still, so the host watched a block of ice slide across the room for
        /// as long as the trip took. At 250 ms that is fifteen frames of
        /// movement, which is exactly what it looks like.
        ///
        /// A frozen player cannot move: any position that arrives while the
        /// timer runs describes a moment before the ice, so there is nothing
        /// to lose by ignoring it. The local simulation still runs -- a frozen
        /// player falls -- and whatever the two copies disagree about by the
        /// time it thaws is what <see cref="Diverged"/> is for.
        /// </summary>
        private static bool FrozenInPlace(PlayerEntity player)
        {
            return player.ModFrozen;
        }

        private static void Move(PlayerEntity player, Vector3 position)
        {
            Vector3 previous = player.Position;
            player.Position = position;
            // This runs after PlayerProcess has captured PrevPosition. Keep
            // the next collision sweep anchored to the corrected position;
            // otherwise the engine treats the network correction as player
            // movement and can push the puppet away from the hitbox.
            player.PrevPosition = position;
            player.ModRefreshNodeRef(previous);
            // And the collision volume, which the engine only recomputes
            // inside the movement step this correction comes after. See
            // ModRefreshVolume: the shadow and the burn effect are drawn from
            // it, and shots are tested against it.
            player.ModRefreshVolume();
        }
    }
}
