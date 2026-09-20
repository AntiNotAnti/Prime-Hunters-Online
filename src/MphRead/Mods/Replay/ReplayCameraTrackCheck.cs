using System;
using System.IO;
using System.Linq;
using OpenTK.Mathematics;

namespace MphRead.Mods.Replay
{
    internal static class ReplayCameraTrackCheck
    {
        public static void Run()
        {
            string directory = Path.Combine(Path.GetTempPath(), "replay-camera-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string replay = Path.Combine(directory, "sample.fpdemo");
            try
            {
                byte[] source = { 1, 2, 3, 4 };
                File.WriteAllBytes(replay, source);
                var track = new ReplayCameraTrack();
                var start = new ReplayCameraKeyframe(10, Vector3.Zero, Quaternion.Identity, 1, -1);
                var end = new ReplayCameraKeyframe(30, new Vector3(10, 4, 2), Quaternion.FromAxisAngle(Vector3.UnitY, MathF.PI / 2), 2, 3);
                Require(track.Put(end) && track.Put(start), "insert");
                Require(track.Keys[0] == start, "sorted keys");
                Require(track.Sample(20, out var mid) && (mid.Position - new Vector3(5, 2, 1)).Length < 0.0001f && Math.Abs(mid.Fov - 1.5f) < 0.0001f, "linear position/FOV");
                var facing = Vector3.Transform(-Vector3.UnitZ, mid.Rotation);
                Require((facing - new Vector3(-MathF.Sqrt(0.5f), 0, -MathF.Sqrt(0.5f))).Length < 0.0001f, "spherical orientation");
                Require(mid.LookAtSlot == -1 && track.Sample(30, out var last) && last.LookAtSlot == 3, "look-at boundary");
                Require(track.Sample(20, out var paused) && paused == mid, "paused frame stable");
                Require(track.Sample(20, out var constantMid, constantSpeed: true)
                    && float.IsFinite(constantMid.Position.X)
                    && float.IsFinite(constantMid.Position.Y)
                    && float.IsFinite(constantMid.Position.Z),
                    "constant-speed spline sample finite");
                Require(track.Sample(0, out var first) && first == start
                    && track.Sample(uint.MaxValue, out last) && last == end, "endpoint clamp");
                foreach (Vector3 direction in new[] { Vector3.UnitX, -Vector3.UnitZ, new Vector3(1, 2, 3).Normalized(), Vector3.UnitY })
                    Require((Vector3.Transform(-Vector3.UnitZ, ReplayCameraTrack.FacingRotation(direction)) - direction).Length < 0.0001f, "capture orientation");
                Require(track.Save(replay), "save");
                byte[] good = File.ReadAllBytes(replay + ".camera");
                var loaded = new ReplayCameraTrack();
                Require(loaded.Load(replay) && loaded.Keys.SequenceEqual(track.Keys), "round trip");
                Require(File.ReadAllBytes(replay).SequenceEqual(source), "replay unchanged");
                Require(loaded.Put(start with { Position = Vector3.UnitX }) && loaded.Keys.Count == 2, "replace frame");
                Require(!loaded.Put(start with { Fov = float.NaN }) && !loaded.Put(start with { Rotation = default }), "finite normalized validation");
                loaded.Clear();
                for (uint i = 0; i < ReplayCameraTrack.MaxKeys; i++) Require(loaded.Put(start with { Frame = i }), "capacity insert");
                Require(!loaded.Put(start with { Frame = 100 }) && loaded.Put(start), "capacity and replacement");
                Require(loaded.Remove(10) && !loaded.Remove(100), "remove");
                byte[] damaged = (byte[])good.Clone(); damaged[25] ^= 1;
                File.WriteAllBytes(replay + ".camera", damaged);
                Require(!loaded.Load(replay) && loaded.Keys.Count == 0, "checksum rejects entire track");
                File.WriteAllBytes(replay + ".camera", good[..12]);
                Require(!loaded.Load(replay), "truncation");
                File.WriteAllBytes(replay + ".camera", new byte[10000]);
                Require(!loaded.Load(replay), "bounded size");
                File.WriteAllBytes(replay + ".camera", good);
                File.AppendAllText(replay, "changed");
                Require(!loaded.Load(replay), "source binding");
                Require(!loaded.Save(Path.Combine(directory, "missing.fpdemo")), "missing source");
                Require(Directory.GetFiles(directory, "*.tmp").Length == 0, "temporary cleanup");
                ReplayCamera.SetProfile(ReplayPresentationProfile.Presentation);
                ReplayCamera.PlayTrack = true;
                ReplayCamera.SetProfile(ReplayPresentationProfile.Faithful);
                Require(!ReplayCamera.PlayTrack, "faithful disables track");
                Console.WriteLine("[replaycheck] camera: interpolation, persistence, bounds, corruption and profiles passed");
            }
            finally
            {
                foreach (string file in Directory.GetFiles(directory)) File.Delete(file);
                Directory.Delete(directory);
            }
        }
        private static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("camera: " + message); }
    }
}
