using System;
using System.Collections.Generic;
using MphRead.Entities;
using MphRead.Mods.Network;

namespace MphRead.Mods.Replay
{
    /// <summary>
    /// Presentation-only automatic camera selection. The score is deliberately
    /// derived from replay annotations and already-simulated world state; it never
    /// feeds back into packets or simulation.
    /// </summary>
    internal static class ReplayDirector
    {
        private const uint MinimumHoldFrames = 3 * 60;
        private const uint EventLookbackFrames = 4 * 60;
        private const float SwitchMargin = 12;

        private static uint _lastSwitchFrame;
        private static uint _lastFrame;
        private static int _eventStart;
        private static int _eventEnd;
        private static int _currentSlot = -1;
        private static float _currentScore;
        private static readonly float[] EventScores = new float[RosterPacket.MaxSlots];
        private static readonly string?[] EventReasons = new string?[RosterPacket.MaxSlots];

        public static int CurrentSlot => _currentSlot;
        public static float CurrentScore => _currentScore;
        public static string Reason { get; private set; } = "idle";

        public static void Reset()
        {
            _lastSwitchFrame = 0;
            _lastFrame = 0;
            _eventStart = 0;
            _eventEnd = 0;
            _currentSlot = -1;
            _currentScore = 0;
            Array.Clear(EventScores);
            Array.Clear(EventReasons);
            Reason = "idle";
        }

        public static void Tick(Scene scene)
        {
            if (!ReplayCamera.Director || !DemoPlayback.IsActive)
                return;

            uint frame = ReplayController.CurrentFrame;
            IReadOnlyList<ReplayEvent> events = DemoPlayback.Events;
            if (frame < _lastFrame || _eventEnd > events.Count)
            {
                _eventStart = 0;
                _eventEnd = 0;
            }
            _lastFrame = frame;
            while (_eventEnd < events.Count && events[_eventEnd].Frame <= frame)
                _eventEnd++;
            uint cutoff = frame > EventLookbackFrames ? frame - EventLookbackFrames : 0;
            while (_eventStart < _eventEnd && events[_eventStart].Frame < cutoff)
                _eventStart++;

            Array.Clear(EventScores);
            Array.Clear(EventReasons);
            for (int i = _eventStart; i < _eventEnd; i++)
            {
                ReplayEvent e = events[i];
                float age = 1 - (frame - e.Frame) / (float)Math.Max(1u, EventLookbackFrames);
                void Add(byte slot, float value, string reason)
                {
                    if (slot >= EventScores.Length || value <= 0) return;
                    if (value >= EventScores[slot])
                        EventReasons[slot] = reason;
                    EventScores[slot] += value;
                }

                switch (e.Type)
                {
                    case ReplayEventType.Kill:
                        Add(e.ActorSlot, 70 * age, "recent kill");
                        Add(e.TargetSlot, 34 * age, "kill aftermath");
                        break;
                    case ReplayEventType.Objective:
                        Add(e.ActorSlot, 78 * age, "objective pressure");
                        break;
                    case ReplayEventType.Damage:
                        float damage = Math.Min(26, Math.Max(0, e.Value) * 0.22f) * age;
                        Add(e.ActorSlot, damage, "damage exchange");
                        Add(e.TargetSlot, damage * 0.5f, "under pressure");
                        break;
                    case ReplayEventType.ScoreChanged:
                        Add(e.ActorSlot, 22 * age, "score change");
                        break;
                }
            }

            int bestSlot = -1;
            float bestScore = float.MinValue;
            string bestReason = "quiet";

            foreach (PlayerEntity player in scene.GetPlayerEntities())
            {
                if (!player.LoadFlags.TestFlag(LoadFlags.Active)
                    || !player.LoadFlags.TestFlag(LoadFlags.Spawned)
                    || player.Health <= 0)
                    continue;

                float score = 4;
                string reason = "active";
                if (player.Health <= 25)
                {
                    score += 18;
                    reason = "low health";
                }

                int nearbyEnemies = 0;
                float closest = float.MaxValue;
                foreach (PlayerEntity other in scene.GetPlayerEntities())
                {
                    if (other == player || other.Health <= 0
                        || !other.LoadFlags.TestFlag(LoadFlags.Spawned)
                        || (GameState.Teams && other.TeamIndex == player.TeamIndex))
                        continue;
                    float distance = (other.Position - player.Position).Length;
                    closest = Math.Min(closest, distance);
                    if (distance <= 12) nearbyEnemies++;
                }
                if (nearbyEnemies > 0)
                {
                    score += Math.Min(30, nearbyEnemies * 9);
                    reason = nearbyEnemies > 1 ? "multi-player fight" : "duel";
                    if (closest <= 5) score += 8;
                }

                if (player.SlotIndex >= 0 && player.SlotIndex < EventScores.Length)
                {
                    score += EventScores[player.SlotIndex];
                    if (EventReasons[player.SlotIndex] is string eventReason)
                        reason = eventReason;
                }

                if (DemoPlayback.LastFrame > frame && DemoPlayback.LastFrame - frame <= 60 * 60)
                    score *= 1.12f;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestSlot = player.SlotIndex;
                    bestReason = reason;
                }
            }

            if (bestSlot < 0)
                return;

            bool currentValid = _currentSlot >= 0 && _currentSlot < PlayerEntity.Players.Count
                && PlayerEntity.Players[_currentSlot].LoadFlags.TestFlag(LoadFlags.Spawned)
                && PlayerEntity.Players[_currentSlot].Health > 0;
            bool holdExpired = frame < _lastSwitchFrame || frame - _lastSwitchFrame >= MinimumHoldFrames;
            bool clearlyBetter = bestSlot != _currentSlot && bestScore >= _currentScore + SwitchMargin;

            if (_currentSlot < 0 || !currentValid || (holdExpired && clearlyBetter))
            {
                _currentSlot = bestSlot;
                _currentScore = bestScore;
                _lastSwitchFrame = frame;
                Reason = bestReason;
                SpectatorMode.Watch(bestSlot);

                // Pick a readable shot while leaving authored tracks alone.
                if (!ReplayCamera.PlayTrack)
                {
                    if (bestReason.Contains("objective", StringComparison.Ordinal))
                        ReplayCamera.SetDirectorMode(ReplayCameraMode.Orbit);
                    else if (bestReason is "duel" or "multi-player fight" or "damage exchange")
                        ReplayCamera.SetDirectorMode(ReplayCameraMode.Chase);
                    else
                        ReplayCamera.SetDirectorMode(ReplayCameraMode.FirstPerson);
                }
            }
            else if (bestSlot == _currentSlot)
            {
                _currentScore = bestScore;
                Reason = bestReason;
            }
        }
    }
}
