using System;
using MphRead.Entities;
using MphRead.Formats;
using MphRead.Mods.Network;
using OpenTK.Mathematics;

namespace MphRead.Mods.Replay
{
    public enum ReplayPresentationProfile { Faithful, Presentation }
    public enum ReplayCameraMode { FirstPerson, Chase, Free, Orbit }
    public static class ReplayCamera
    {
        public static ReplayCameraMode Mode { get; private set; }
        public static float Distance { get; set; } = 4;
        public static float Height { get; set; } = 1.5f;
        public static float FieldOfView { get; set; } = 80;
        public static float Roll { get; set; }
        public static bool Director { get; set; }
        public static bool TrackCollisionAvoidance { get; set; } = true;
        public static bool TrackConstantSpeed { get; set; } = true;
        internal static ReplayCameraInterpolation TrackInterpolation { get; set; } = ReplayCameraInterpolation.Spline;
        internal static ReplayCameraEase TrackEase { get; set; } = ReplayCameraEase.InOut;
        internal static readonly ReplayCameraTrack Track = new();
        private static string? _trackPath;
        private static int _bookmarkIndex;
        public static ReplayPresentationProfile Profile { get; private set; } = ReplayPresentationProfile.Faithful;
        private static bool _playTrack;
        public static bool PlayTrack { get => _playTrack; set { _playTrack = value; Changed = true; } }
        public static int LookAtSlot { get; set; } = -1;
        public static string? TrackError => Track.LastError;
        public static int KeyframeCount => Track.Keys.Count;
        public static void SetProfile(ReplayPresentationProfile profile)
        {
            Profile = profile;
            if (profile == ReplayPresentationProfile.Faithful) PlayTrack = false;
            Changed = true;
            ReplayController.NoteInput();
        }
        internal static void ClearBookmarks()
        {
            Track.Clear(); _trackPath = null; _bookmarkIndex = 0;
            Profile = ReplayPresentationProfile.Faithful; PlayTrack = false; LookAtSlot = -1;
        }
        internal static void EnsureTrack()
        {
            string? path = DemoPlayback.CurrentPath;
            if (path == null || path == _trackPath) return;
            _trackPath = path;
            Track.Load(path);
            _bookmarkIndex = 0;
        }
        internal static void SaveKeyframe(Vector3 position, Vector3 facing, float fov)
        {
            EnsureTrack();
            if (Track.Put(new ReplayCameraKeyframe(ReplayController.CurrentFrame, position,
                ReplayCameraTrack.FacingRotation(facing), fov, (sbyte)Math.Clamp(LookAtSlot, -1, 7),
                MathHelper.DegreesToRadians(Math.Clamp(Roll, -360, 360)), TrackInterpolation, TrackEase))
                && _trackPath != null)
                Track.Save(_trackPath);
            Chat.ChatBox.System(Track.LastError == null ? "Camera keyframe saved" : "Camera track: " + Track.LastError);
        }
        public static void RemoveKeyframe()
        {
            EnsureTrack();
            if (!Track.Remove(ReplayController.CurrentFrame)) { Chat.ChatBox.System("No camera keyframe at this frame"); return; }
            if (_trackPath != null) Track.Save(_trackPath);
            Chat.ChatBox.System(Track.LastError == null ? "Camera keyframe removed" : "Camera track: " + Track.LastError);
        }
        internal static bool NextKeyframe(out ReplayCameraKeyframe key)
        {
            EnsureTrack(); key = default;
            if (Track.Keys.Count == 0) return false;
            key = Track.Keys[_bookmarkIndex++ % Track.Keys.Count];
            return true;
        }
        internal static bool Changed;
        internal static bool BookmarkRequested;
        internal static bool RestoreRequested;
        public static void SetMode(ReplayCameraMode mode)
        {
            PlayTrack = false;
            Mode = mode;
            Changed = true;
            ReplayController.NoteInput();
        }
        internal static void SetDirectorMode(ReplayCameraMode mode)
        {
            if (PlayTrack || Mode == mode) return;
            Mode = mode;
            Changed = true;
        }
        public static void ToggleFree() => SetMode(Mode == ReplayCameraMode.Free ? ReplayCameraMode.FirstPerson : ReplayCameraMode.Free);
        public static void Bookmark() { BookmarkRequested = true; ReplayController.NoteInput(); }
        public static void RestoreBookmark() { RestoreRequested = true; ReplayController.NoteInput(); }
        internal static void Reset()
        {
            Mode = ReplayCameraMode.FirstPerson;
            Changed = true;
            BookmarkRequested = false;
            RestoreRequested = false;
            ReplayDirector.Reset();
        }
        internal static void TickDirector(Scene scene)
        {
            ReplayDirector.Tick(scene);
        }
        public static void WatchEvent(bool victim)
        {
            ReplayEvent? nearest = null;
            foreach (ReplayEvent e in DemoPlayback.Events)
                if (e.Type == ReplayEventType.Kill && e.Frame <= ReplayController.CurrentFrame) nearest = e;
            if (nearest is ReplayEvent selected) SpectatorMode.Watch(victim ? selected.TargetSlot : selected.ActorSlot);
        }
    }
}

namespace MphRead
{
    public partial class Scene
    {
        private long _replayCameraTime;
        private float _replayOrbit;
        private Vector3? _replayFollowPosition;

        private double ReplayPresentationTime => Math.Max(0, Mods.Network.ReplayController.CurrentFrame - 1d + ReplayRenderAlpha);

        private void ModReplayCamera()
        {
            if (!Mods.Network.DemoPlayback.IsActive) return;
            Mods.Replay.ReplayCamera.EnsureTrack();
            Mods.Replay.ReplayCamera.TickDirector(this);
            var mode = Mods.Replay.ReplayCamera.Mode;
            if (Mods.Replay.ReplayCamera.Changed)
            {
                SetFreeCamera(mode != Mods.Replay.ReplayCameraMode.FirstPerson);
                Mods.Replay.ReplayCamera.Changed = false;
                _replayFollowPosition = null;
            }
            long now = Environment.TickCount64;
            float delta = _replayCameraTime == 0 ? 0 : Math.Clamp((now - _replayCameraTime) / 1000f, 0, 0.1f);
            _replayCameraTime = now;
            if (Mods.Replay.ReplayCamera.BookmarkRequested)
            {
                Mods.Replay.ReplayCamera.BookmarkRequested = false;
                // In first-person the scene's auxiliary facing may be stale; capture
                // the camera actually shown, without writing back into the hunter.
                Mods.Replay.ReplayCamera.SaveKeyframe(_freeCam ? _cameraPosition : this.Players.Main.CameraInfo.Position,
                    _freeCam ? _cameraFacing : this.Players.Main.CameraInfo.Facing,
                    _freeCam ? _cameraFov : MathHelper.DegreesToRadians(
                        Mods.RenderOptions.ScaleCameraFov(
                            this.Players.Main.CameraInfo.Fov > 0
                                ? this.Players.Main.CameraInfo.Fov
                                : Mods.RenderOptions.DefaultFov)));
            }
            if (Mods.Replay.ReplayCamera.RestoreRequested)
            {
                Mods.Replay.ReplayCamera.RestoreRequested = false;
                if (Mods.Replay.ReplayCamera.NextKeyframe(out var saved))
                {
                    Mods.Replay.ReplayCamera.PlayTrack = false;
                    Mods.Replay.ReplayCamera.SetMode(Mods.Replay.ReplayCameraMode.Free);
                    ApplyReplayKeyframe(saved);
                    return;
                }
            }
            if (Mods.Replay.ReplayCamera.Profile == Mods.Replay.ReplayPresentationProfile.Presentation
                && Mods.Replay.ReplayCamera.PlayTrack
                && Mods.Replay.ReplayCamera.Track.Sample(
                    ReplayPresentationTime,
                    out var trackFrame,
                    Mods.Replay.ReplayCamera.TrackConstantSpeed))
            {
                ApplyReplayKeyframe(trackFrame);
                return;
            }
            if (mode == Mods.Replay.ReplayCameraMode.Free
                && Mods.Replay.ReplayCamera.Profile == Mods.Replay.ReplayPresentationProfile.Presentation)
            {
                _cameraFov = MathHelper.DegreesToRadians(Math.Clamp(
                    Mods.Replay.ReplayCamera.FieldOfView, 20, 140));
            }
            if (mode is not (Mods.Replay.ReplayCameraMode.Chase or Mods.Replay.ReplayCameraMode.Orbit)) return;
            var player = this.Players.Main;
            if (!player.LoadFlags.TestFlag(LoadFlags.Spawned)) return;
            SetFreeCamera(true);

            Vector3 playerPosition = Services.IsReplica ? player.ReplayDrawTransform.Row3.Xyz : player.Position;
            Vector3 facing = player.CameraInfo.Facing;
            if (ReplayPoses?.Sample(player.SlotIndex, ReplayRenderAlpha, out _, out var replicaFacing) == true)
                facing = replicaFacing;

            Vector3 target = playerPosition
                + Vector3.UnitY * Math.Clamp(Mods.Replay.ReplayCamera.Height, 0.5f, 8);
            facing.Y = 0;
            if (facing.LengthSquared < 0.001f) facing = -Vector3.UnitZ;
            facing.Normalize();
            if (mode == Mods.Replay.ReplayCameraMode.Orbit)
            {
                _replayOrbit = (float)(ReplayPresentationTime / 60 * 0.4);
                facing = new Vector3(MathF.Sin(_replayOrbit), 0, MathF.Cos(_replayOrbit));
            }
            Vector3 desired = target - facing * Math.Clamp(Mods.Replay.ReplayCamera.Distance, 1, 20);
            Vector3 candidate = !Mods.Replay.ReplayVideoExporter.Active && Mods.Replay.ReplayCamera.Profile == Mods.Replay.ReplayPresentationProfile.Presentation && _replayFollowPosition.HasValue ? Vector3.Lerp(_replayFollowPosition.Value, desired, 1 - MathF.Exp(-delta * 10)) : desired;
            CollisionResult collision = default;
            if (CollisionDetection.CheckBetweenPoints(target, candidate, TestFlags.Players, this, ref collision))
                candidate = target + (candidate - target) * Math.Max(0, collision.Distance - 0.05f);
            _cameraPosition = candidate;
            _replayFollowPosition = candidate;
            Vector3 view = target - candidate;
            if (view.LengthSquared > 0.0001f) _cameraFacing = view.Normalized();
            _cameraRight = Vector3.Cross(_cameraFacing, Vector3.UnitY).Normalized();
            _cameraUp = Vector3.Cross(_cameraRight, _cameraFacing).Normalized();
            _cameraFov = MathHelper.DegreesToRadians(Math.Clamp(Mods.Replay.ReplayCamera.FieldOfView, 40, 120));
        }
        private void ApplyReplayKeyframe(Mods.Replay.ReplayCameraKeyframe key)
        {
            SetFreeCamera(true);
            Vector3 desired = key.Position;
            if (Mods.Replay.ReplayCamera.TrackCollisionAvoidance
                && Mods.Replay.ReplayCamera.Profile == Mods.Replay.ReplayPresentationProfile.Presentation)
            {
                CollisionResult pathCollision = default;
                Vector3 from = _cameraPosition;
                // A track's collision anchor is recorded, so the same target
                // frame cannot depend on the previously displayed/seeked frame.
                foreach (var anchor in Mods.Replay.ReplayCamera.Track.Keys)
                {
                    if (anchor.Frame > ReplayPresentationTime) break;
                    from = anchor.Position;
                }
                if ((desired - from).LengthSquared < 10000
                    && CollisionDetection.CheckBetweenPoints(from, desired, TestFlags.Players, this, ref pathCollision))
                {
                    desired = from + (desired - from) * Math.Max(0, pathCollision.Distance - 0.05f);
                }
            }
            _cameraPosition = desired;
            _cameraFacing = Vector3.Transform(-Vector3.UnitZ, key.Rotation).Normalized();
            _cameraUp = Vector3.Transform(Vector3.UnitY, key.Rotation).Normalized();
            if (Math.Abs(key.Roll) > 0.00001f)
                _cameraUp = Vector3.Transform(_cameraUp,
                    Quaternion.FromAxisAngle(_cameraFacing, key.Roll)).Normalized();
            if (key.LookAtSlot >= 0 && key.LookAtSlot < this.Players.Items.Count)
            {
                var target = this.Players.Items[key.LookAtSlot];
                if (target.LoadFlags.TestFlag(LoadFlags.Active) && target.LoadFlags.TestFlag(LoadFlags.Spawned))
                {
                    Vector3 direction = (Services.IsReplica ? target.ReplayDrawTransform.Row3.Xyz : target.Position) + Vector3.UnitY - _cameraPosition;
                    if (direction.LengthSquared > 0.0001f) _cameraFacing = direction.Normalized();
                }
            }
            Vector3 right = Vector3.Cross(_cameraFacing, _cameraUp);
            if (right.LengthSquared < 0.0001f)
                right = Vector3.Cross(_cameraFacing, Math.Abs(_cameraFacing.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitZ);
            _cameraRight = right.Normalized();
            _cameraUp = Vector3.Cross(_cameraRight, _cameraFacing).Normalized();
            _cameraFov = key.Fov;
        }
    }
}
