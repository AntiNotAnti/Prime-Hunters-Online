using System;
using MphRead.Entities;
using OpenTK.Mathematics;

namespace MphRead.Mods.Network
{
    /// <summary>
    /// A duel built to measure headshots, in place of the feature tour.
    ///
    /// <see cref="NetTestScript"/> answers "does everything still cross the
    /// wire", and it is the wrong instrument for this question in three ways
    /// at once: it spends fourteen of its fifteen phases doing something other
    /// than shooting, it closes to four units before it fires, and it aims at
    /// <see cref="PlayerEntity.ModAimTarget"/>, which is the centre of the
    /// body sphere -- the bottom third of a hunter. A run of it lands seven
    /// overlaps in ninety seconds and not one of them anywhere near a head.
    ///
    /// What a headshot needs measuring against is the opposite of that: one
    /// weapon, held for the whole run, aimed at the top eighth of a body that
    /// is moving vertically as fast as the game can make it move, from a
    /// distance that does not change while the shot is in the air. So this
    /// drives two roles and nothing else.
    ///
    /// <list type="bullet">
    /// <item><b>The runner</b> keeps moving in the axis the headshot band is
    /// measured along. The band is 0.3 units tall on a body 1.6 units tall
    /// (<c>BeamProjectileEntity</c>: the impact must be
    /// <c>MaxPickupHeight - 0.3</c> above the victim's position), so a rewind
    /// that puts the victim one frame out vertically moves the whole band off
    /// the shot. A jump is the cheapest way to produce that error every second
    /// and a jump pad is the largest.</item>
    /// <item><b>The sniper</b> holds the Imperialist, zoomed, aims at the
    /// middle of that band, and fires on the weapon's own cadence. The
    /// Imperialist is the one beam in the table that scores a headshot at any
    /// range (every other weapon is limited to fifteen units), which is why it
    /// is the weapon the complaint is about and the only one that can measure
    /// the long-range case at all.</item>
    /// </list>
    ///
    /// Both arms of a comparison run this, so a difference between them is the
    /// thing being changed and not the scenario.
    /// </summary>
    public static class HitRig
    {
        public enum RigMode
        {
            /// <summary>Not running; the feature tour drives instead.</summary>
            Off,
            /// <summary>
            /// Close quarters, maximum vertical motion. The runner jumps on a
            /// cadence and rides whatever pads the room has; the sniper holds
            /// a fixed short range so that the flight time is near zero and
            /// everything left is the rewind.
            /// </summary>
            Jump,
            /// <summary>
            /// The long shot. The sniper backs off as far as the room lets it
            /// and the runner strafes across, so the error being measured is
            /// the rewind multiplied by a flight time rather than by nothing.
            /// </summary>
            Sniper,
            /// <summary>
            /// Everybody snipes everybody, at a range where the round arrives
            /// in about a frame. Nobody runs.
            ///
            /// The only scenario that produces the thing the kill arbitration
            /// exists for: two players killing each other inside a round trip,
            /// so that on the machine keeping score one of them is already
            /// dead when the other's shot arrives. Neither of the other two
            /// modes can, because in both of them one of the two clients is
            /// holding a weapon it never fires at anybody.
            ///
            /// The Imperialist kills a full-health hunter with one headshot,
            /// and both ends hold the trigger from the same distance at the
            /// same cadence, so at 250-400 ms the two shots regularly cross in
            /// flight. What the run is read for is
            /// <c>NetHitClaims.VoidedDeadShooter</c> -- shots the authority
            /// refused because somebody had been put down in a strictly
            /// earlier world -- against the trades that were allowed to stand.
            /// </summary>
            Duel,
            /// <summary>
            /// One weapon, emptied into one target that is not moving much,
            /// from a range that keeps the round in the air for a while.
            ///
            /// The scenario a player can describe in one sentence -- "fire
            /// missiles quickly at the same enemy" -- and the one that isolates
            /// a damage-accounting fault from everything else: exactly one
            /// client is shooting, so the authority's health drop for the
            /// victim is *this* client's damage and nobody else's, and the two
            /// totals can simply be subtracted. <c>-hitrig missile</c>,
            /// <c>-hitrig magmaul</c>, <c>-hitrig judicator</c>.
            /// </summary>
            Volley
        }

        /// <summary>The weapon a <see cref="RigMode.Volley"/> run empties.</summary>
        public static BeamType VolleyWeapon { get; private set; } = BeamType.Missile;

        public static RigMode Mode { get; private set; } = RigMode.Off;
        public static bool Active => Mode != RigMode.Off;

        /// <summary>
        /// <c>-hitrig jump</c> or <c>-hitrig sniper</c>. Refused rather than
        /// defaulted, so a typo does not quietly run the other scenario and
        /// report it under this one's name.
        /// </summary>
        public static bool Configure(string? value)
        {
            switch (value?.Trim().ToLowerInvariant())
            {
                case "jump":
                case "jumppad":
                    Mode = RigMode.Jump;
                    return true;
                case "sniper":
                case "long":
                    Mode = RigMode.Sniper;
                    return true;
                case "duel":
                case "trade":
                    Mode = RigMode.Duel;
                    return true;
                case "missile":
                    Mode = RigMode.Volley;
                    VolleyWeapon = BeamType.Missile;
                    return true;
                case "magmaul":
                    Mode = RigMode.Volley;
                    VolleyWeapon = BeamType.Magmaul;
                    return true;
                case "judicator":
                    Mode = RigMode.Volley;
                    VolleyWeapon = BeamType.Judicator;
                    return true;
                case "battlehammer":
                    Mode = RigMode.Volley;
                    VolleyWeapon = BeamType.Battlehammer;
                    return true;
                case "shockcoil":
                    Mode = RigMode.Volley;
                    VolleyWeapon = BeamType.ShockCoil;
                    return true;
                case "voltdriver":
                    Mode = RigMode.Volley;
                    VolleyWeapon = BeamType.VoltDriver;
                    return true;
                case "powerbeam":
                    Mode = RigMode.Volley;
                    VolleyWeapon = BeamType.PowerBeam;
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// How far the sniper tries to stand from the runner, per mode.
        ///
        /// Close is close enough that an Imperialist round arrives in about a
        /// frame, so a miss is the rewind and nothing else. Far is past the
        /// fifteen units at which every other weapon stops scoring headshots
        /// at all, and far enough that the round is in the air for several
        /// frames -- which is the case the catch-up loop exists for.
        /// </summary>
        private const float CloseRange = 9f;
        private const float LongRange = 34f;
        /// <summary>
        /// Far enough that a Missile is in the air for a good fraction of a
        /// second -- which is the difference between this and every other
        /// weapon, and the thing a damage ledger has to be read against.
        /// </summary>
        private const float VolleyRange = 16f;

        /// <summary>
        /// The band the shot is aimed at, as a height above the victim's
        /// position.
        ///
        /// The headshot test is <c>impact.Y - victim.Position.Y &gt;=
        /// MaxPickupHeight - 0.3</c>, which is 0.7999 for every hunter in the
        /// table, and the capsule's top is <c>MaxPickupHeight</c> = 1.0998. So
        /// the band is [0.80, 1.10] and this is the middle of it: aiming at
        /// the top would miss over the head on the first frame of error in
        /// either direction, which would measure the aim rather than the
        /// rewind.
        /// </summary>
        private const float HeadAimHeight = 0.95f;

        /// <summary>Degrees per frame the aim may move. The tour's number, for the same reason.</summary>
        private const float TurnRate = 6f;
        /// <summary>How close the aim must be before the trigger is pulled.</summary>
        private const float FiringCone = 2.5f;

        private static int _frame;
        private static int _stuckFrames;
        private static bool _stuckDirection;
        private static Vector3 _lastPosition;

        /// <summary>Aim asked for this frame, read by the same hook the tour's is.</summary>
        public static float AimDeltaX { get; private set; }
        public static float AimDeltaY { get; private set; }

        /// <summary>
        /// What the run actually did, so a report can say whether the scenario
        /// happened rather than only what came of it.
        ///
        /// A run whose sniper never got a clear shot and a run whose sniper
        /// missed every one look identical in the hit numbers, and only these
        /// tell them apart.
        /// </summary>
        public static long Triggers { get; private set; }
        public static long FramesOnTarget { get; private set; }
        public static long FramesAirborne { get; private set; }
        public static double RangeSum { get; private set; }
        public static long RangeSamples { get; private set; }
        public static float WorstVerticalSpeed { get; private set; }
        public static double VerticalSpeedSum { get; private set; }
        public static long VerticalSpeedSamples { get; private set; }

        public static void Reset()
        {
            _frame = 0;
            _stuckFrames = 0;
            _lastPosition = Vector3.Zero;
            AimDeltaX = 0;
            AimDeltaY = 0;
            Triggers = 0;
            FramesOnTarget = 0;
            FramesAirborne = 0;
            RangeSum = 0;
            RangeSamples = 0;
            WorstVerticalSpeed = 0;
            VerticalSpeedSum = 0;
            VerticalSpeedSamples = 0;
        }

        /// <summary>
        /// Which role this machine plays, from its slot.
        ///
        /// Slot parity rather than join order or a flag, for the reason
        /// <see cref="NetTestScript.FindTarget"/> picks its target by slot:
        /// every machine has to agree, and it has to agree without asking
        /// anybody. Even slots shoot, odd slots run, so a two-client run is
        /// always one of each whichever order they arrive in.
        /// </summary>
        public static bool IsSniper => Mode == RigMode.Duel
            || Math.Max(NetSession.LocalSlot, 0) % 2 == 0;

        public static void Drive(PlayerEntity player)
        {
            _frame++;
            PlayerControls c = player.Controls;
            ClearControls(c);
            if (player.Health == 0)
            {
                // Holding fire is what asks for an early respawn -- on a
                // client it is the only thing that does, since ForceSpawn
                // defers to the authority there. A rig that waited out every
                // death timer would spend a third of a short run with nobody
                // on the map, and the Imperialist kills in one headshot.
                //
                // The respawn happens inside PlayerProcess on a frame when the
                // trigger is still held, so the player comes back alive with
                // its finger down and fires once before this method next runs.
                // Measured at 75 stray shots in a four-minute run, and they
                // are not harmless: they are shots at the *sniper*, who holds
                // a range and never jumps, so they quietly filled the clamp's
                // error measurement with a target that was standing still --
                // which is how that number came out with a vertical component
                // of exactly zero on a run whose runner was airborne 60% of
                // the time.
                //
                // So the gun is pointed at the floor for as long as it is
                // held. The respawn still happens and the stray shot goes into
                // the ground a foot away.
                c.Shoot.IsDown = true;
                AimDeltaX = 0;
                AimDeltaY = -TurnRate;
                FinishControls(player, c);
                return;
            }
            PlayerEntity? other = Opponent(player);
            if (IsSniper)
            {
                DriveSniper(player, c, other);
            }
            else
            {
                DriveRunner(player, c, other);
            }
            FinishControls(player, c);
        }

        /// <summary>
        /// The runner: stay alive, stay in the air, stay in front of the gun.
        ///
        /// It deliberately does not evade. The question is not whether a
        /// sniper can track somebody, it is whether a shot that was on the
        /// head when it left is still on the head when the authority judges
        /// it, and a runner that breaks line of sight only turns that into a
        /// smaller sample.
        /// </summary>
        private static void DriveRunner(PlayerEntity player, PlayerControls c, PlayerEntity? other)
        {
            // Face the sniper, so the runner is a target rather than a back.
            AimAt(player, other, headHeight: 0);
            bool airborne = !player.Flags1.TestFlag(PlayerFlags1.Standing);
            if (airborne)
            {
                FramesAirborne++;
            }
            float rise = MathF.Abs(player.Speed.Y);
            VerticalSpeedSum += rise;
            VerticalSpeedSamples++;
            if (rise > WorstVerticalSpeed)
            {
                WorstVerticalSpeed = rise;
            }
            // Jump as often as the engine will take one. A jump is worth about
            // a fifth of a unit a frame at the top of its arc and three times
            // that on the way down, and a jump pad is worth several -- either
            // way, more than the 0.3-unit band in a frame or two, which is the
            // whole point of the mode.
            c.Jump.IsDown = Mode == RigMode.Jump
                ? _frame % 24 < 3
                : _frame % 90 < 3;
            // And keep walking, so a room with pads in it gets ridden and a
            // room without one still moves laterally. A square rather than a
            // line: a runner walking one way leaves the room.
            Square(c, Mode == RigMode.Jump ? 50 : 80);
        }

        /// <summary>
        /// The sniper: one weapon, one range, one aim point.
        /// </summary>
        private static void DriveSniper(PlayerEntity player, PlayerControls c, PlayerEntity? other)
        {
            // The Imperialist, every frame, because a weapon is a pickup in a
            // match and nobody is picking anything up here. Idempotent once it
            // is held.
            if (Mode == RigMode.Volley)
            {
                if (player.CurrentWeapon != VolleyWeapon)
                {
                    player.ModArmWeapon(VolleyWeapon);
                }
            }
            else if (player.CurrentWeapon != BeamType.Imperialist)
            {
                player.ModArmZoomWeapon();
            }
            // And keep it loaded. The Imperialist costs 20 universal ammo a
            // shot against a cap of 400, so a sniper standing still to hold a
            // range runs dry after twenty shots and the rest of the run
            // measures an empty gun -- which reads in every report as a sniper
            // who stopped hitting anything. Ammo comes from pickups in a
            // match; a rig is not playing a match.
            // Int32.MaxValue rather than a cap read back from the player:
            // ModSetAmmo clamps to whatever this hunter's cap actually is, so
            // asking for more than any cap is the one request that cannot be
            // wrong. Reading the cap and handing it back was a way to top the
            // gun up with zero if the cap had not been set yet, which reads in
            // every report as a sniper who stopped hitting.
            player.ModSetAmmo(Int32.MaxValue, Int32.MaxValue);
            // Zoomed, which is not cosmetic: the Imperialist deals half damage
            // unzoomed, so an unzoomed run measures a different weapon. The
            // press is a toggle on the rising edge, so press towards the state
            // wanted rather than on a timer -- a fixed cadence zooms straight
            // back out again.
            if (Mode != RigMode.Volley && player.ModCanZoom
                && !player.EquipInfo.Zoomed && _frame % 8 == 0)
            {
                c.Zoom.IsDown = true;
            }
            bool onTarget = AimAt(player, other, HeadAimHeight);
            if (onTarget)
            {
                FramesOnTarget++;
            }
            if (other == null)
            {
                Square(c, 60);
                return;
            }
            float range = (other.Position - player.Position).Length;
            RangeSum += range;
            RangeSamples++;
            HoldRange(player, c, range, Mode == RigMode.Sniper ? LongRange
                : Mode == RigMode.Volley && VolleyWeapon != BeamType.ShockCoil ? VolleyRange : CloseRange);
            // Tapped on the weapon's own cadence, and the tap is what makes
            // it fire. Holding looked right -- the Imperialist MP carries
            // WeaponFlags.RepeatFire, which repeats at `shotCooldown` 60 --
            // and measured wrong: a four-minute arm with the trigger held for
            // 5145 frames on target produced 23 beams, against the 85 the
            // cooldown allows. Whatever the repeat path needs, a fresh press
            // does not need it.
            //
            // 63 frames rather than 60, so a tap never lands inside the
            // cooldown of the one before it and get thrown away.
            // The weapon's own cadence plus a frame, so a tap never lands
            // inside the cooldown of the one before it. A Missile's is 20
            // (doubled by the engine's frame convention), which is three times
            // faster than the Imperialist's -- and firing fast at one target
            // is the whole of the scenario.
            int tap = Mode == RigMode.Volley
                ? Weapons.Current[(int)VolleyWeapon].ShotCooldown * 2 + 3
                : 63;
            // In a duel the cadence is the *server's* clock, not this client's.
            //
            // The point of that scenario is two shots crossing in flight, and
            // clients join seconds apart -- so a local frame counter has them
            // firing at unrelated moments and the trade the arbitration exists
            // for simply never happens. Every client sees roughly the same
            // snapshot number at the same time, so keying the tap to it puts
            // the two triggers within a latency of each other, which is
            // exactly the case worth measuring. Same reason NetTestScript
            // keys its phases to the server's clock.
            int clock = Mode == RigMode.Duel && NetSession.LastSnapshotFrame != 0
                ? (int)NetSession.LastSnapshotFrame
                : _frame;
            c.Shoot.IsDown = onTarget && clock % tap < 3;
            if (c.Shoot.IsDown && clock % tap == 0)
            {
                Triggers++;
            }
        }

        /// <summary>
        /// Walk towards or away until the range is the one this mode wants,
        /// and strafe once it is.
        ///
        /// The strafe is what keeps the sniper's own position moving, so the
        /// run is not measuring a rewind against a stationary shooter -- which
        /// is the one case the authority already gets right, since a shooter
        /// is never rewound.
        /// </summary>
        private static void HoldRange(PlayerEntity player, PlayerControls c, float range, float want)
        {
            float moved = (player.Position - _lastPosition).Length;
            _lastPosition = player.Position;
            _stuckFrames = moved < 0.02f ? _stuckFrames + 1 : 0;
            bool stuck = _stuckFrames > 20;
            if (stuck && _stuckFrames > 90)
            {
                _stuckFrames = 0;
                _stuckDirection = !_stuckDirection;
            }
            if (stuck)
            {
                c.MoveLeft.IsDown = _stuckDirection;
                c.MoveRight.IsDown = !_stuckDirection;
                c.Jump.IsDown = _stuckFrames % 30 < 3;
                return;
            }
            // A dead band, not a target: walking to an exact distance means
            // walking back and forth across it forever, and a sniper stepping
            // in and out every frame is a sniper whose own position is never
            // the one the shot was aimed from.
            const float slack = 2.5f;
            c.MoveUp.IsDown = range > want + slack;
            c.MoveDown.IsDown = range < want - slack;
            if (!c.MoveUp.IsDown && !c.MoveDown.IsDown)
            {
                c.MoveLeft.IsDown = _frame / 70 % 2 == 0;
                c.MoveRight.IsDown = _frame / 70 % 2 == 1;
            }
        }

        /// <summary>
        /// Point the gun at a height above a target, and say whether it is
        /// close enough to fire.
        ///
        /// Through <see cref="PlayerEntity.ModAimDeltaTowards"/> rather than
        /// by assigning the aim, because that helper solves for the
        /// convergence point: a shot travels from the muzzle towards a point a
        /// fixed distance down the aim ray, so aiming straight at something
        /// further away than that lands low. On a 0.3-unit band, low is a body
        /// shot.
        /// </summary>
        private static bool AimAt(PlayerEntity player, PlayerEntity? target, float headHeight)
        {
            AimDeltaX = 0;
            AimDeltaY = 0;
            if (target == null)
            {
                return false;
            }
            Vector3 at = headHeight > 0
                ? target.Position.AddY(headHeight)
                : target.ModAimTarget;
            (float turnX, float turnY) = player.ModAimDeltaTowards(at);
            if (!Single.IsFinite(turnX) || !Single.IsFinite(turnY))
            {
                return false;
            }
            AimDeltaX = Math.Clamp(turnX, -TurnRate, TurnRate);
            AimDeltaY = Math.Clamp(turnY, -TurnRate, TurnRate);
            return MathF.Abs(turnX) < FiringCone && MathF.Abs(turnY) < FiringCone;
        }

        /// <summary>The nearest living opponent. Two clients, so there is one.</summary>
        private static PlayerEntity? Opponent(PlayerEntity self)
        {
            PlayerEntity? best = null;
            float bestDistance = Single.MaxValue;
            for (int i = 0; i < PlayerEntity.Players.Count; i++)
            {
                PlayerEntity other = PlayerEntity.Players[i];
                if (other == self || !other.LoadFlags.TestFlag(LoadFlags.Active)
                    || !other.LoadFlags.TestFlag(LoadFlags.Spawned) || other.Health == 0)
                {
                    continue;
                }
                float distance = (other.Position - self.Position).LengthSquared;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = other;
                }
            }
            return best;
        }

        private static void Square(PlayerControls c, int framesPerSide)
        {
            int side = _frame / framesPerSide % 4;
            c.MoveUp.IsDown = side == 0;
            c.MoveRight.IsDown = side == 1;
            c.MoveDown.IsDown = side == 2;
            c.MoveLeft.IsDown = side == 3;
        }

        // The edge bookkeeping is the tour's, and has to be: a press is
        // computed once at the end of the frame, because a phase that writes a
        // button twice would otherwise wipe the edge the first write produced.
        // See NetTestScript.Clear.
        private static bool[] _wasDown = Array.Empty<bool>();

        private static void ClearControls(PlayerControls c)
        {
            if (_wasDown.Length < c.All.Length)
            {
                _wasDown = new bool[c.All.Length];
            }
            for (int i = 0; i < c.All.Length; i++)
            {
                Keybind bind = c.All[i];
                _wasDown[i] = bind.IsDown;
                bind.IsDown = false;
                bind.IsPressed = false;
                bind.IsReleased = false;
            }
        }

        private static void FinishControls(PlayerEntity player, PlayerControls c)
        {
            bool any = false;
            for (int i = 0; i < c.All.Length && i < _wasDown.Length; i++)
            {
                Keybind bind = c.All[i];
                bind.IsPressed = bind.IsDown && !_wasDown[i];
                bind.IsReleased = !bind.IsDown && _wasDown[i];
                any |= bind.IsDown || bind.IsReleased;
            }
            if (any)
            {
                // Say that somebody is playing this player. The rig writes
                // binds after the pass that answers "has anyone touched
                // anything lately", so without this its own shooter went idle,
                // the engine lowered the gun, and `TryFireWeapon` refused at
                // the `GunAnimation.UpDown` check -- *before* NetDamage.NoteFired,
                // so the shot did not even count as attempted.
                //
                // That is the whole of the "a player's own gun fires a third
                // as often as the authority fires it" gap: 19 beams here
                // against 57 on the authority for the same slot, while the
                // *puppet's* count agreed (52/54) because NetPlayerBridge
                // already notes input for puppets. The asymmetry was the
                // instrument's, not the netcode's, and every hit-registration
                // percentage measured before this compared two different
                // volleys. NetTestScript.FinishControls carries the same call
                // for the same reason; this driver is newer and missed it.
                player.ModNoteInput();
            }
            player.ModApplyScriptAim(AimDeltaX, AimDeltaY);
        }

        /// <summary>One line for the report: what the scenario actually did.</summary>
        public static string Describe()
        {
            if (!Active)
            {
                return "hit rig: off";
            }
            string role = IsSniper ? "sniper" : "runner";
            if (IsSniper)
            {
                double range = RangeSamples > 0 ? RangeSum / RangeSamples : 0;
                return $"hit rig: {Mode} as {role}, {Triggers} triggers, "
                    + $"{FramesOnTarget} frames on target, mean range {range:F1} units";
            }
            double rise = VerticalSpeedSamples > 0
                ? VerticalSpeedSum / VerticalSpeedSamples
                : 0;
            return $"hit rig: {Mode} as {role}, {FramesAirborne} frames airborne, "
                + $"mean |vertical speed| {rise:F3} units/frame, worst {WorstVerticalSpeed:F3}";
        }
    }
}
