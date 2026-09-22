using System;
using MphRead.Entities;
using OpenTK.Mathematics;

namespace MphRead.Mods.Network
{
    // Asset-backed regression: compare a real live tick with restoring its
    // complete starting state and replaying the exact command through physics.
    internal sealed class MovementSimulationCheck
    {
        private readonly MovementState[] _before;
        private readonly bool[] _alive;
        private readonly ushort[] _lives;
        private readonly MovementState[,] _history;
        private readonly IntentPacket[,] _inputs;
        private int _tick;
        private int _checks, _failures;
        private int _replayChecks;
        private int _externalStateTick;
        private int _altTicks, _boostTicks, _padTicks, _frozenTicks;

        private float _worstPosition, _worstSpeed;
        internal MovementSimulationCheck(int players)
        {
            _before = new MovementState[players]; _alive = new bool[players]; _lives = new ushort[players];
            _history = new MovementState[players, 64]; _inputs = new IntentPacket[players, 64];
        }
        internal void BeforeStep()
        {
            for (int slot = 0; slot < _before.Length; slot++)
            {
                var player = PlayerEntity.Players[slot];
                _alive[slot] = player.ModIsInPlay;
                _lives[slot] = NetPlayerLifecycle.Get(slot);
                if (_alive[slot])
                {
                    var state = player.ModCaptureMovement();
                    if (_tick == 200 || _tick == 500)
                    {
                        state.FrozenTimer = 20;
                        player.ModRestoreMovement(state);
                        _externalStateTick = _tick + 1;
                    }
                    if (_tick == 400 || _tick == 900)
                    {
                        state.Speed += new Vector3(.3f, .4f, -.2f);
                        state.Acceleration = new Vector3(.03f, .01f, -.02f);
                        state.AccelerationTimer = 12;
                        player.ModRestoreMovement(state);
                        _externalStateTick = _tick + 1;
                    }
                    _before[slot] = player.ModCaptureMovement();
                }
            }
        }
        internal void AfterStep()
        {
            _tick++;
            for (int slot = 0; slot < _before.Length; slot++)
            {
                var player = PlayerEntity.Players[slot];
                if (!_alive[slot] || !player.ModIsInPlay || _lives[slot] != NetPlayerLifecycle.Get(slot)) continue;
                var expected = player.ModCaptureMovement();
                _history[slot, _tick % 64] = expected;
                _inputs[slot, _tick % 64] = NetSession.RemoteIntents[slot];
                if (player.IsAltForm) _altTicks++;
                if (expected.BoostCharge > 0 || (expected.Flags1 & (uint)PlayerFlags1.Boosting) != 0) _boostTicks++;
                if (expected.JumpPadControlLock > 0) _padTicks++;
                if (expected.FrozenTimer > 0) _frozenTicks++;

                player.ModRestoreMovement(_before[slot]);
                player.ModReplayMovement(NetSession.RemoteIntents[slot]);
                var actual = player.ModCaptureMovement();
                player.ModRestoreMovement(expected);
                if (_tick > Math.Max(64, _externalStateTick + 27) && _before.Length == 1 && _tick % 7 == 0)
                {
                    int delay = 3 + (_tick * 17 % 24);
                    player.ModRestoreMovement(_history[slot, (_tick - delay) % 64]);
                    for (int frame = _tick - delay + 1; frame <= _tick; frame++)
                    {
                        player.ModReplayMovement(_inputs[slot, frame % 64]);
                    }
                    var replayed = player.ModCaptureMovement();
                    player.ModRestoreMovement(expected);
                    _replayChecks++;
                    if (Vector3.Distance(replayed.Position, expected.Position) > 0.001f
                        || Vector3.Distance(replayed.Speed, expected.Speed) > 0.001f)
                    {
                        if (_failures++ < 12) Console.WriteLine($"MOVEMENT replay divergence tick={_tick} delay={delay} position={Vector3.Distance(replayed.Position, expected.Position):F6} speed={Vector3.Distance(replayed.Speed, expected.Speed):F6}");
                    }
                }
                float position = Vector3.Distance(actual.Position, expected.Position);
                float speed = Vector3.Distance(actual.Speed, expected.Speed);
                _worstPosition = Math.Max(_worstPosition, position); _worstSpeed = Math.Max(_worstSpeed, speed);
                _checks++;
                if (!Single.IsFinite(position) || !Single.IsFinite(speed) || position > 0.001f || speed > 0.001f)
                {
                    if (_failures++ < 12) Console.WriteLine($"MOVEMENT divergence tick={NetSession.NetFrame} slot={slot} position={position:F6} speed={speed:F6} flags={actual.Flags1:X}/{expected.Flags1:X} facing={actual.FacingVector}/{expected.FacingVector}");
                }
            }
        }
        internal bool Report()
        {
            Console.WriteLine($"MOVEMENTCHECK {(_checks > 0 && _failures == 0 ? "PASS" : "FAIL")}: {_checks} ticks, {_replayChecks} delayed replays, {_failures} divergences; max position={_worstPosition:F6}, speed={_worstSpeed:F6}");
            Console.WriteLine($"MOVEMENT coverage: alt={_altTicks}, boost={_boostTicks}, jump-pad={_padTicks}, frozen={_frozenTicks}");
            return _checks > 0 && _failures == 0;
        }
    }
}
