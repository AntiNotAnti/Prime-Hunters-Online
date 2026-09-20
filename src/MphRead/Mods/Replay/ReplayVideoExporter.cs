using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using MphRead.Mods.Network;

namespace MphRead.Mods.Replay
{
    /// <summary>
    /// Deterministic replay-frame export driven by the normal renderer. The replay is
    /// paused at the requested start, one simulation frame is stepped per captured
    /// picture, and the scene's own offscreen target is read before any launcher
    /// overlay is composited over it.
    /// </summary>
    internal static class ReplayVideoExporter
    {
        private static ReplayVideoExportManifest? _job;
        private static uint _lastFrame = UInt32.MaxValue;
        private static int _written;
        private static string? _directory;

        public static bool Active => _job != null;
        public static int FramesWritten => _written;
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
            if (job.StartFrame >= job.EndFrame || job.EndFrame > ReplayController.DurationFrames)
            {
                Status = "The render range is outside this replay.";
                return false;
            }

            _job = job;
            _directory = Path.GetDirectoryName(job.FramePattern)
                ?? Path.GetDirectoryName(job.SuggestedOutput);
            if (String.IsNullOrWhiteSpace(_directory))
            {
                _job = null;
                Status = "The render output directory is invalid.";
                return false;
            }

            Directory.CreateDirectory(_directory);
            _written = 0;
            _lastFrame = UInt32.MaxValue;
            LastOutput = null;
            Status = "Seeking to render start...";
            ReplayCamera.Director = job.Director;
            if (job.CameraTrack)
            {
                ReplayCamera.SetProfile(ReplayPresentationProfile.Presentation);
                ReplayCamera.PlayTrack = true;
            }
            ReplayController.Seek(job.StartFrame, resume: false);
            return true;
        }

        public static void Cancel()
        {
            _job = null;
            Status = "Video export cancelled.";
        }

        public static void AfterSceneDraw(Scene scene)
        {
            CaptureReplayThumbnails(scene);
            if (_job != null && !DemoPlayback.IsActive)
            {
                Cancel();
                return;
            }

            ReplayVideoExportManifest? job = _job;
            if (job == null || ReplayController.IsSeeking)
                return;

            uint frame = ReplayController.CurrentFrame;
            if (frame < job.StartFrame)
            {
                ReplayController.Seek(job.StartFrame, resume: false);
                return;
            }
            if (frame > job.EndFrame)
            {
                Finish();
                return;
            }
            if (frame == _lastFrame)
                return;

            // A v3 replay is a 60 Hz simulation. For lower requested output rates,
            // retain evenly spaced simulation frames. 60/120 produce every source
            // frame; FFmpeg performs presentation-rate duplication for 120.
            int stride = job.Fps <= 30 ? 2 : 1;
            if ((frame - job.StartFrame) % stride == 0)
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
                    return;
                }
                _written++;
                Status = $"Rendering {ReplayHud.Time(frame)} / {ReplayHud.Time(job.EndFrame)}"
                    + $" · {_written} frames";
            }
            _lastFrame = frame;

            if (frame >= job.EndFrame)
            {
                Finish();
                return;
            }

            // One simulation step becomes one rendered picture. This deliberately
            // ignores wall-clock frame pacing, making output independent of display Hz.
            ReplayController.StepForward();
        }

        private static void Finish()
        {
            ReplayVideoExportManifest? job = _job;
            _job = null;
            if (job == null) return;
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
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                Status += " FFmpeg was not found; PNG sequence and encode.txt were kept.";
            }
#else
            Status += " PNG sequence is ready for desktop encoding.";
#endif
        }

        // Three scene stills turn the replay library into a visual browser. They are
        // opportunistic: watching/exporting a replay populates them for free, while a
        // replay never opened still gets the map thumbnail as its immediate fallback.
        private static string? _thumbReplay;
        private static readonly bool[] _thumbDone = new bool[3];

        private static void CaptureReplayThumbnails(Scene scene)
        {
            string? replay = DemoPlayback.CurrentPath;
            if (replay == null || ReplayController.IsSeeking) return;
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
                if (_thumbDone[i] || frame < targets[i]) continue;
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
                if (File.Exists(path)) return path;
            }
            return null;
        }

        public static string[] Thumbnails(string replay)
            => Enumerable.Range(0, 3).Select(i => ThumbnailPath(replay, i))
                .Where(File.Exists).ToArray();
    }
}
