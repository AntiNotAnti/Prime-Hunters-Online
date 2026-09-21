using System;
using MphRead.Entities;
using MphRead.Formats;
using MphRead.Formats.Culling;
using MphRead.Mods.Launcher;
using MphRead.Mods.Network;
using OpenTK.Mathematics;

namespace MphRead.Mods
{
    internal enum KillCamKind
    {
        None,
        Personal,
        Final
    }

    internal readonly record struct KillCamPlayerPose(
        Matrix4 Transform,
        Vector3 Position,
        Vector3 Facing,
        Vector3 Up,
        int Health,
        bool Active,
        bool Spawned,
        bool AltForm,
        int Weapon);

    internal readonly record struct KillCamCameraPose(
        Vector3 Position,
        Vector3 Target,
        Vector3 Up,
        float Fov,
        NodeRef NodeRef);

    /// <summary>
    /// A bounded presentation history used by death and final-kill cameras.
    ///
    /// The live network simulation never rewinds. Every 60 Hz step copies the
    /// player poses and cameras that were actually presented for the applied
    /// authoritative snapshot into a small ring. A kill cam walks that ring
    /// forward from before the death to the confirmed death frame while the
    /// real match continues behind it.
    /// </summary>
    internal static class KillCam
    {
        private const int HistoryFrames = 6 * 60;
        private const uint PersonalPreRollFrames = 2 * 60;
        private const uint FinalPreRollFrames = 150;
        private const int PersonalEndHoldFrames = 24;
        private const uint FinalKillWindowFrames = 8 * 60;

        private sealed class FrameSnapshot
        {
            public bool Valid;
            public uint Frame;
            public readonly KillCamPlayerPose[] Players =
                new KillCamPlayerPose[PlayerEntity.SlotCapacity];
            public readonly KillCamCameraPose[] Cameras =
                new KillCamCameraPose[PlayerEntity.SlotCapacity];
        }

        private static readonly FrameSnapshot[] History = CreateHistory();
        private static int _writeIndex;
        private static uint _lastCapturedFrame = UInt32.MaxValue;

        private static KillCamKind _kind;
        private static int _killerSlot = -1;
        private static int _victimSlot = -1;
        private static uint _playbackStart;
        private static uint _playbackFrame;
        private static uint _playbackEnd;
        private static uint _activationLiveFrame;
        private static int _endHold;
        private static bool _skipArmed;
        private static FrameSnapshot? _current;

        private static int _lastKillerSlot = -1;
        private static int _lastVictimSlot = -1;
        private static uint _lastKillFrame;

        public static bool Active => _kind != KillCamKind.None && _current != null;
        public static bool IsPersonal => _kind == KillCamKind.Personal && _current != null;
        public static bool IsFinal => _kind == KillCamKind.Final && _current != null;
        public static uint PlaybackFrame => _playbackFrame;

        private static FrameSnapshot[] CreateHistory()
        {
            var frames = new FrameSnapshot[HistoryFrames];
            for (int i = 0; i < frames.Length; i++)
                frames[i] = new FrameSnapshot();
            return frames;
        }

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

            if (!LauncherPrefs.KillCamEnabled || Headless.Active
                || DemoPlayback.IsActive || !GameState.Multiplayer
                || GameState.MatchState != MatchState.InProgress
                || SpectatorMode.IsSpectating
                || victimSlot != NetHooks.LocalSlot)
            {
                return;
            }

            StartHistorical(KillCamKind.Personal, attackerSlot, victimSlot,
                frame, PersonalPreRollFrames);
        }

        internal static bool BeginFinal(uint matchEndFrame)
        {
            ClearCurrent();
            if (!LauncherPrefs.FinalKillCamEnabled || Headless.Active
                || DemoPlayback.IsActive || !GameState.Multiplayer
                || SpectatorMode.IsSpectating || _lastKillerSlot < 0
                || _lastVictimSlot < 0
                || !IsRecentFinalKill(_lastKillFrame, matchEndFrame))
            {
                return false;
            }

            return StartHistorical(KillCamKind.Final, _lastKillerSlot,
                _lastVictimSlot, _lastKillFrame, FinalPreRollFrames);
        }

        internal static void EndFinal()
        {
            if (_kind == KillCamKind.Final)
                ClearCurrent();
        }

        internal static void AfterSimulation(Scene scene)
        {
            Capture();

            if (_kind == KillCamKind.None)
                return;

            if (_kind == KillCamKind.Personal)
            {
                if (!LauncherPrefs.KillCamEnabled)
                {
                    ClearCurrent();
                    return;
                }

                int local = NetHooks.LocalSlot;
                if ((uint)local >= (uint)PlayerEntity.Players.Count
                    || PlayerEntity.MainPlayerIndex != local
                    || SpectatorMode.IsSpectating
                    || GameState.MatchState != MatchState.InProgress)
                {
                    ClearCurrent();
                    return;
                }

                var shoot = PlayerEntity.Players[local].Controls.Shoot;
                if (!_skipArmed)
                {
                    if (!shoot.IsDown)
                        _skipArmed = true;
                }
                else if (shoot.IsPressed)
                {
                    // Fire is a media control while the player is already dead.
                    // Consume the edge so the same press cannot leak into a
                    // respawn/weapon action on the frame the camera closes.
                    shoot.IsPressed = false;
                    ClearCurrent();
                    return;
                }
            }

            // The activation step already selected the first historical frame.
            // Advancing here on that same step would make the first picture
            // vanish before it was ever drawn.
            if (NetSession.NetFrame == _activationLiveFrame)
                return;

            if (_playbackFrame < _playbackEnd)
            {
                _playbackFrame++;
                _current = FindAtOrBefore(_playbackFrame);
                if (_current == null)
                    ClearCurrent();
                return;
            }

            if (_kind == KillCamKind.Personal)
            {
                if (++_endHold >= PersonalEndHoldFrames)
                    ClearCurrent();
            }
            // Final kill holds its confirmed last frame until GameState moves
            // from the three-second GameOver camera into the results screen.
        }

        private static void Capture()
        {
            if (Headless.Active || DemoPlayback.IsActive || !NetSession.Active)
                return;

            uint frame = NetSession.IsClient && NetSession.AppliedSnapshotFrame != 0
                ? NetSession.AppliedSnapshotFrame
                : NetSession.NetFrame;

            int index;
            if (_lastCapturedFrame == frame)
            {
                index = (_writeIndex + History.Length - 1) % History.Length;
            }
            else
            {
                index = _writeIndex;
                _writeIndex = (_writeIndex + 1) % History.Length;
                _lastCapturedFrame = frame;
            }

            FrameSnapshot snapshot = History[index];
            snapshot.Valid = true;
            snapshot.Frame = frame;
            for (int slot = 0; slot < PlayerEntity.SlotCapacity; slot++)
            {
                PlayerEntity player = PlayerEntity.Players[slot];
                bool active = player.LoadFlags.TestFlag(LoadFlags.Active);
                bool spawned = player.LoadFlags.TestFlag(LoadFlags.Spawned);
                snapshot.Players[slot] = new KillCamPlayerPose(
                    player.Transform,
                    player.Position,
                    player.FacingVector,
                    player.UpVector,
                    player.Health,
                    active,
                    spawned,
                    player.IsAltForm || player.IsMorphing,
                    (int)player.CurrentWeapon);

                CameraInfo camera = player.CameraInfo;
                snapshot.Cameras[slot] = new KillCamCameraPose(
                    camera.Position,
                    camera.Target,
                    camera.UpVector,
                    camera.Fov,
                    camera.NodeRef);
            }
        }

        private static bool StartHistorical(KillCamKind kind, int killerSlot,
            int victimSlot, uint deathFrame, uint preRoll)
        {
            uint wantedStart = deathFrame > preRoll ? deathFrame - preRoll : 0;
            FrameSnapshot? first = FindAtOrBefore(wantedStart)
                ?? FindOldestAtOrBefore(deathFrame);
            if (first == null || first.Frame > deathFrame)
                return false;

            _kind = kind;
            _killerSlot = killerSlot;
            _victimSlot = victimSlot;
            _playbackStart = first.Frame;
            _playbackFrame = first.Frame;
            _playbackEnd = deathFrame;
            _current = first;
            _activationLiveFrame = NetSession.NetFrame;
            _endHold = 0;
            _skipArmed = false;
            return true;
        }

        private static FrameSnapshot? FindAtOrBefore(uint frame)
        {
            FrameSnapshot? best = null;
            uint bestFrame = 0;
            for (int i = 0; i < History.Length; i++)
            {
                FrameSnapshot candidate = History[i];
                if (!candidate.Valid || candidate.Frame > frame)
                    continue;
                if (best == null || candidate.Frame >= bestFrame)
                {
                    best = candidate;
                    bestFrame = candidate.Frame;
                }
            }
            return best;
        }

        private static FrameSnapshot? FindOldestAtOrBefore(uint frame)
        {
            FrameSnapshot? oldest = null;
            uint oldestFrame = UInt32.MaxValue;
            for (int i = 0; i < History.Length; i++)
            {
                FrameSnapshot candidate = History[i];
                if (!candidate.Valid || candidate.Frame > frame)
                    continue;
                if (candidate.Frame <= oldestFrame)
                {
                    oldest = candidate;
                    oldestFrame = candidate.Frame;
                }
            }
            return oldest;
        }

        internal static bool IsHistoricalCameraOwner(int slot)
            => IsPersonal && slot == _killerSlot;

        internal static bool TryGetHistoricalPose(int slot,
            out KillCamPlayerPose pose)
        {
            pose = default;
            if (_current == null || (uint)slot >= (uint)_current.Players.Length)
                return false;
            pose = _current.Players[slot];
            return pose.Active && pose.Spawned;
        }

        internal static bool TryGetHistoricalCamera(out KillCamCameraPose camera)
        {
            camera = default;
            if (_current == null || (uint)_killerSlot >= (uint)PlayerEntity.SlotCapacity)
                return false;

            if (_kind == KillCamKind.Personal)
            {
                camera = _current.Cameras[_killerSlot];
                return true;
            }

            KillCamPlayerPose killer = _current.Players[_killerSlot];
            if (!killer.Active)
                return false;

            Vector3 forward = killer.Facing;
            forward.Y = 0;
            forward = forward.LengthSquared < 0.0001f
                ? Vector3.UnitZ : forward.Normalized();
            Vector3 right = Vector3.Cross(Vector3.UnitY, forward);
            right = right.LengthSquared < 0.0001f
                ? Vector3.UnitX : right.Normalized();

            float phase = Progress * MathF.PI;
            Vector3 focus = killer.Position.AddY(killer.AltForm ? 0.65f : 1.2f);
            Vector3 target = focus + forward;
            if ((uint)_victimSlot < (uint)PlayerEntity.SlotCapacity)
            {
                KillCamPlayerPose victim = _current.Players[_victimSlot];
                if (victim.Active)
                    target = Vector3.Lerp(focus + forward, victim.Position.AddY(0.8f), 0.35f);
            }
            Vector3 position = focus - forward * (killer.AltForm ? 4.6f : 4.0f)
                + right * (1.1f + MathF.Sin(phase) * 0.75f)
                + Vector3.UnitY * (killer.AltForm ? 1.0f : 1.35f);
            camera = new KillCamCameraPose(position, target, Vector3.UnitY,
                80, killer.Active ? _current.Cameras[_killerSlot].NodeRef : NodeRef.None);
            return true;
        }

        internal static bool TryGetBanner(out bool final,
            out string killer, out string victim, out string weapon)
        {
            final = IsFinal;
            killer = "";
            victim = "";
            weapon = "";
            if (!Active)
                return false;
            killer = PlayerName(_killerSlot);
            victim = PlayerName(_victimSlot);
            if (_current != null && (uint)_killerSlot < (uint)PlayerEntity.SlotCapacity)
            {
                weapon = WeaponName(_current.Players[_killerSlot].Weapon);
            }
            return true;
        }

        internal static string WeaponName(int value)
        {
            // BeamType is backed by sbyte. Enum.IsDefined(Type, object)
            // requires the boxed value to be the enum's exact underlying type;
            // passing our stored Int32 throws instead of returning false.
            if (value < SByte.MinValue || value > SByte.MaxValue)
                return "";
            sbyte raw = (sbyte)value;
            return Enum.IsDefined(typeof(BeamType), raw)
                ? ((BeamType)raw).ToString().ToUpperInvariant()
                : "";
        }

        internal static float Progress
        {
            get
            {
                if (!Active || _playbackEnd <= _playbackStart)
                    return 1;
                return Math.Clamp((_playbackFrame - _playbackStart)
                    / (float)Math.Max(1u, _playbackEnd - _playbackStart), 0, 1);
            }
        }

        internal static void Reset()
        {
            ClearCurrent();
            _lastKillerSlot = -1;
            _lastVictimSlot = -1;
            _lastKillFrame = 0;
            _writeIndex = 0;
            _lastCapturedFrame = UInt32.MaxValue;
            for (int i = 0; i < History.Length; i++)
                History[i].Valid = false;
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
            _playbackStart = 0;
            _playbackFrame = 0;
            _playbackEnd = 0;
            _activationLiveFrame = 0;
            _endHold = 0;
            _skipArmed = false;
            _current = null;
        }
    }
}
