using System;
using MphRead.Entities;
using MphRead.Mods.Network;

namespace MphRead.Mods
{
    internal enum KillCamKind
    {
        None,
        Personal,
        Final
    }

    /// <summary>
    /// Presentation-only death cameras. Authoritative replay events choose the
    /// killer/victim pair, but this never rewinds or mutates simulation state.
    /// </summary>
    internal static class KillCam
    {
        private const uint PersonalDurationFrames = 150;
        private const uint FinalKillWindowFrames = 8 * 60;

        private static KillCamKind _kind;
        private static int _killerSlot = -1;
        private static int _victimSlot = -1;
        private static uint _startFrame;

        private static int _lastKillerSlot = -1;
        private static int _lastVictimSlot = -1;
        private static uint _lastKillFrame;

        public static bool IsPersonal => _kind == KillCamKind.Personal;
        public static bool IsFinal => _kind == KillCamKind.Final;

        internal static bool IsRecentFinalKill(uint killFrame, uint endFrame)
            => endFrame >= killFrame
                && endFrame - killFrame <= FinalKillWindowFrames;

        internal static void NoteDeath(int victimSlot, int attackerSlot, uint frame)
        {
            if ((uint)victimSlot >= (uint)PlayerEntity.SlotCapacity
                || (uint)attackerSlot >= (uint)PlayerEntity.SlotCapacity
                || attackerSlot == victimSlot)
            {
                return;
            }

            _lastKillerSlot = attackerSlot;
            _lastVictimSlot = victimSlot;
            _lastKillFrame = frame;

            if (Headless.Active || DemoPlayback.IsActive
                || !GameState.Multiplayer
                || GameState.MatchState != MatchState.InProgress
                || SpectatorMode.IsSpectating
                || victimSlot != NetHooks.LocalSlot)
            {
                return;
            }

            _kind = KillCamKind.Personal;
            _killerSlot = attackerSlot;
            _victimSlot = victimSlot;
            _startFrame = frame;
        }

        internal static bool BeginFinal(uint endFrame)
        {
            ClearCurrent();
            if (Headless.Active || DemoPlayback.IsActive
                || !GameState.Multiplayer || SpectatorMode.IsSpectating
                || _lastKillerSlot < 0 || _lastVictimSlot < 0
                || !IsRecentFinalKill(_lastKillFrame, endFrame))
            {
                return false;
            }

            _kind = KillCamKind.Final;
            _killerSlot = _lastKillerSlot;
            _victimSlot = _lastVictimSlot;
            _startFrame = endFrame;
            return true;
        }

        internal static void EndFinal()
        {
            if (_kind == KillCamKind.Final)
                ClearCurrent();
        }

        internal static void AfterSimulation(Scene scene)
        {
            if (_kind != KillCamKind.Personal)
                return;

            int localSlot = NetHooks.LocalSlot;
            if (Headless.Active || DemoPlayback.IsActive
                || GameState.MatchState != MatchState.InProgress
                || SpectatorMode.IsSpectating
                || (uint)localSlot >= (uint)PlayerEntity.Players.Count
                || PlayerEntity.MainPlayerIndex != localSlot
                || NetSession.NetFrame < _startFrame)
            {
                ClearCurrent();
                return;
            }

            PlayerEntity victim = PlayerEntity.Players[localSlot];
            uint elapsed = NetSession.NetFrame - _startFrame;
            if (victim.Health > 0 || elapsed >= PersonalDurationFrames
                || !TryGetPlayer(_killerSlot, requireAlive: true,
                    out PlayerEntity killer))
            {
                ClearCurrent();
                return;
            }

            PlayerEntity.Main.UpdateKillCamera(killer, elapsed / 60f,
                cinematic: false);
        }

        internal static bool TryGetFinalTarget(out PlayerEntity target)
        {
            target = PlayerEntity.Main;
            if (_kind != KillCamKind.Final)
                return false;
            if (!TryGetPlayer(_killerSlot, requireAlive: false, out target))
            {
                ClearCurrent();
                return false;
            }
            return true;
        }

        internal static bool TryGetBanner(out bool final,
            out string killer, out string victim)
        {
            final = _kind == KillCamKind.Final;
            killer = "";
            victim = "";
            if (_kind == KillCamKind.None)
                return false;

            killer = PlayerName(_killerSlot);
            victim = PlayerName(_victimSlot);
            return true;
        }

        internal static void Reset()
        {
            ClearCurrent();
            _lastKillerSlot = -1;
            _lastVictimSlot = -1;
            _lastKillFrame = 0;
        }

        private static bool TryGetPlayer(int slot, bool requireAlive,
            out PlayerEntity player)
        {
            player = PlayerEntity.Main;
            if ((uint)slot >= (uint)PlayerEntity.Players.Count)
                return false;

            PlayerEntity candidate = PlayerEntity.Players[slot];
            if (!candidate.LoadFlags.TestFlag(LoadFlags.Active)
                || !candidate.LoadFlags.TestFlag(LoadFlags.Spawned)
                || requireAlive && candidate.Health <= 0)
            {
                return false;
            }

            player = candidate;
            return true;
        }

        private static string PlayerName(int slot)
        {
            if ((uint)slot < (uint)GameState.Nicknames.Length
                && !String.IsNullOrWhiteSpace(GameState.Nicknames[slot]))
            {
                return GameState.Nicknames[slot].ToUpperInvariant();
            }
            return $"PLAYER {slot + 1}";
        }

        private static void ClearCurrent()
        {
            _kind = KillCamKind.None;
            _killerSlot = -1;
            _victimSlot = -1;
            _startFrame = 0;
        }
    }
}
