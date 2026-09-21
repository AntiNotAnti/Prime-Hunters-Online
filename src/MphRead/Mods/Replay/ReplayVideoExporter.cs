using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using MphRead.Mods.Network;

namespace MphRead.Mods.Replay
{
    /// <summary>
    /// Deterministic replay-frame export driven by the normal renderer. Jobs may
    /// contain one range or an ordered highlight reel. The exporter steps the
    /// replay exactly once per captured source frame and the queue starts the
    /// next job only after the current renderer has released ownership.
    /// </summary>
    internal static class ReplayVideoExporter
    {
        private static ReplayVideoExportManifest? _job;
        private static ReplayVideoSegment[] _segments = Array.Empty<ReplayVideoSegment>();
        private static int _segmentIndex;
        private static uint _lastFrame = UInt32.MaxValue;
        private static int _written;
        private static int _totalFrames;
        private static string? _directory;
        private static ReplayCameraMode _previousMode;
        private static ReplayPresentationProfile _previousProfile;
        private static bool _previousDirector;
        private static bool _previousTrack;
        private static bool _cameraStateSaved;

        public static bool Active => _job != null;
        public static int FramesWritten => _written;
        public static float Progress => _totalFrames <= 0
            ? 0 : Math.Clamp(_written / (float)_totalFrames, 0, 1);
        public static string Status { get; private set; } = "";
        public static string? LastOutput { get; private set; }

        public static bool Start(ReplayVideoExportManifest job)
        {
            if (Active)
            {
                Status = "A video export is already active.";
                return false;
            }
            if (!DemoPlayback.IsActive || DemoPlayback.CurrentPath == null
                || !Path.GetFullPath(DemoPlayback.CurrentPath).Equals(
                    Path.GetFullPath(job.Replay), StringComparison.OrdinalIgnoreCase))
            {
                Status = "Open the replay that belongs to this render job first.";
                return false;
            }

            _segments = job.Segments is { Count: > 0 }
                ? job.Segments.ToArray()
                : new[]
                {
                    new ReplayVideoSegment(job.StartFrame, job.EndFrame,
                        "Selection", ReplaySegmentCamera.Current)
                };
            foreach (ReplayVideoSegment segment in _segments)
            {
                if (segment.StartFrame >= segment.EndFrame
                    || segment.EndFrame > ReplayController.DurationFrames)
                {
                    Status = "A render segment is outside this replay.";
                    _segments = Array.Empty<ReplayVideoSegment>();
                    return false;
                }
            }

            _previousMode = ReplayCamera.Mode;
            _previousProfile = ReplayCamera.Profile;
            _previousDirector = ReplayCamera.Director;
            _previousTrack = ReplayCamera.PlayTrack;
            _cameraStateSaved = true;

            _job = job;
            _directory = Path.GetDirectoryName(job.FramePattern)
                ?? Path.GetDirectoryName(job.SuggestedOutput);
            if (String.IsNullOrWhiteSpace(_directory))
            {
                _job = null;
                _segments = Array.Empty<ReplayVideoSegment>();
                Status = "The render output directory is invalid.";
                return false;
            }

            Directory.CreateDirectory(_directory);
            _written = 0;
            _segmentIndex = 0;
            _lastFrame = UInt32.MaxValue;
            _totalFrames = EstimateFrames(job.Fps, _segments);
            LastOutput = null;
            Status = "Seeking to render start...";
            ApplySegmentCamera(_segments[0], job);
            ReplayController.Seek(_segments[0].StartFrame, resume: false);
            return true;
        }

        public static void Cancel()
        {
            _job = null;
            _segments = Array.Empty<ReplayVideoSegment>();
            _segmentIndex = 0;
            RestoreCameraState();
            Status = "Video export cancelled.";
        }

        public static void AfterSceneDraw(Scene scene)
        {
            CaptureReplayThumbnails(scene);

            if (_job == null)
                ReplayExportQueue.Pump();

            if (_job != null && !DemoPlayback.IsActive)
            {
                Cancel();
                return;
            }

            ReplayVideoExportManifest? job = _job;
            if (job == null || ReplayController.IsSeeking
                || _segmentIndex >= _segments.Length)
            {
                return;
            }

            ReplayVideoSegment segment = _segments[_segmentIndex];
            uint frame = ReplayController.CurrentFrame;
            if (frame < segment.StartFrame)
            {
                ReplayController.Seek(segment.StartFrame, resume: false);
                return;
            }
            if (frame > segment.EndFrame)
            {
                AdvanceSegment();
                return;
            }
            if (frame == _lastFrame)
                return;

            // A v3 replay is a 60 Hz simulation. For 30 fps exports retain every
            // second simulation frame. 60/120 capture every source frame and
            // FFmpeg handles presentation duplication for 120.
            int stride = job.Fps <= 30 ? 2 : 1;
            if ((frame - segment.StartFrame) % stride == 0)
            {
                string path = Path.Combine(_directory!,
                    $"frame_{_written:D8}.png");
                bool saved = job.CleanHud
                    ? MphRead.Mods.ScreenCapture.Save(scene, path)
                    : MphRead.Mods.ScreenCapture.SaveWindow(scene, path);
                if (!saved)
                {
                    Status = $"Frame {frame} could not be captured; export stopped.";
                    _job = null;
                    _segments = Array.Empty<ReplayVideoSegment>();
                    RestoreCameraState();
                    ReplayExportQueue.NoteFinished(Status);
                    return;
                }
                _written++;
                Status = $"Rendering {_segmentIndex + 1}/{_segments.Length} "
                    + $"{segment.Name} · {ReplayHud.Time(frame)} / "
                    + $"{ReplayHud.Time(segment.EndFrame)} · {_written}/{_totalFrames} frames";
            }
            _lastFrame = frame;

            if (frame >= segment.EndFrame)
            {
                AdvanceSegment();
                return;
            }

            ReplayController.StepForward();
        }

        private static void AdvanceSegment()
        {
            ReplayVideoExportManifest? job = _job;
            if (job == null)
                return;

            _segmentIndex++;
            _lastFrame = UInt32.MaxValue;
            if (_segmentIndex >= _segments.Length)
            {
                Finish();
                return;
            }

            ReplayVideoSegment next = _segments[_segmentIndex];
            ApplySegmentCamera(next, job);
            Status = $"Seeking reel segment {_segmentIndex + 1}/{_segments.Length}...";
            ReplayController.Seek(next.StartFrame, resume: false);
        }

        private static void ApplySegmentCamera(ReplayVideoSegment segment,
            ReplayVideoExportManifest job)
        {
            ReplayCamera.Director = false;
            ReplayCamera.PlayTrack = false;

            switch (segment.Camera)
            {
                case ReplaySegmentCamera.FirstPerson:
                    ReplayCamera.SetProfile(ReplayPresentationProfile.Presentation);
                    ReplayCamera.SetMode(ReplayCameraMode.FirstPerson);
                    break;
                case ReplaySegmentCamera.Chase:
                    ReplayCamera.SetProfile(ReplayPresentationProfile.Presentation);
                    ReplayCamera.SetMode(ReplayCameraMode.Chase);
                    break;
                case ReplaySegmentCamera.Orbit:
                    ReplayCamera.SetProfile(ReplayPresentationProfile.Presentation);
                    ReplayCamera.SetMode(ReplayCameraMode.Orbit);
                    break;
                case ReplaySegmentCamera.Director:
                    ReplayCamera.SetProfile(ReplayPresentationProfile.Presentation);
                    ReplayCamera.Director = true;
                    break;
                default:
                    ReplayCamera.Director = job.Director;
                    if (job.CameraTrack)
                    {
                        ReplayCamera.SetProfile(ReplayPresentationProfile.Presentation);
                        ReplayCamera.PlayTrack = true;
                    }
                    break;
            }
        }

        private static int EstimateFrames(int fps,
            IReadOnlyList<ReplayVideoSegment> segments)
        {
            int stride = fps <= 30 ? 2 : 1;
            long total = 0;
            foreach (ReplayVideoSegment segment in segments)
                total += (segment.EndFrame - segment.StartFrame) / (uint)stride + 1;
            return (int)Math.Min(Int32.MaxValue, total);
        }

        private static void Finish()
        {
            ReplayVideoExportManifest? job = _job;
            _job = null;
            _segments = Array.Empty<ReplayVideoSegment>();
            _segmentIndex = 0;
            if (job == null)
                return;

            RestoreCameraState();
            LastOutput = job.SuggestedOutput;
            Status = $"Rendered {_written} frames.";
#if !ANDROID
            try
            {
                var start = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = job.FfmpegArguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = _directory ?? ""
                };
                Process? process = Process.Start(start);
                if (process != null)
                    Status += " FFmpeg encoding started.";
            }
            catch (Exception ex) when (ex is InvalidOperationException
                or System.ComponentModel.Win32Exception)
            {
                Status += " FFmpeg was not found; PNG sequence and encode.txt were kept.";
            }
#else
            Status += " PNG sequence is ready for desktop encoding.";
#endif
            ReplayExportQueue.NoteFinished(Status);
        }

        private static void RestoreCameraState()
        {
            if (!_cameraStateSaved)
                return;
            ReplayCamera.SetProfile(_previousProfile);
            ReplayCamera.SetMode(_previousMode);
            ReplayCamera.Director = _previousDirector;
            ReplayCamera.PlayTrack = _previousTrack;
            _cameraStateSaved = false;
        }

        // Three scene stills turn the replay library into a visual browser. They are
        // opportunistic: watching/exporting a replay populates them for free, while a
        // replay never opened still gets the map thumbnail as its immediate fallback.
        private static string? _thumbReplay;
        private static readonly bool[] _thumbDone = new bool[3];

        private static void CaptureReplayThumbnails(Scene scene)
        {
            string? replay = DemoPlayback.CurrentPath;
            if (replay == null || ReplayController.IsSeeking)
                return;
            if (!String.Equals(_thumbReplay, replay, StringComparison.OrdinalIgnoreCase))
            {
                _thumbReplay = replay;
                for (int i = 0; i < _thumbDone.Length; i++)
                    _thumbDone[i] = File.Exists(ThumbnailPath(replay, i));
            }

            uint duration = Math.Max(1u, ReplayController.DurationFrames);
            uint[] targets =
            {
                Math.Min(duration, Math.Max(60u, duration / 4)),
                Math.Min(duration, Math.Max(120u, duration / 2)),
                Math.Min(duration, Math.Max(180u, duration * 3 / 4))
            };
            uint frame = ReplayController.CurrentFrame;
            for (int i = 0; i < targets.Length; i++)
            {
                if (_thumbDone[i] || frame < targets[i])
                    continue;
                _thumbDone[i] = MphRead.Mods.ScreenCapture.Save(scene,
                    ThumbnailPath(replay, i));
            }
        }

        public static string ThumbnailPath(string replay, int index)
            => replay + $".thumb{Math.Clamp(index, 0, 2)}.png";

        public static string? BestThumbnail(string replay)
        {
            for (int i = 0; i < 3; i++)
            {
                string path = ThumbnailPath(replay, i);
                if (File.Exists(path))
                    return path;
            }
            return null;
        }

        public static string[] Thumbnails(string replay)
            => Enumerable.Range(0, 3).Select(i => ThumbnailPath(replay, i))
                .Where(File.Exists).ToArray();
    }
}
