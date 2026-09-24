using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MphRead.Mods.Network;

namespace MphRead.Mods.Replay
{
    internal enum ReplaySegmentCamera
    {
        Current,
        FirstPerson,
        Chase,
        Orbit,
        Director
    }

    internal sealed record ReplayReelSegment(
        Guid Id,
        uint StartFrame,
        uint EndFrame,
        string Name,
        ReplaySegmentCamera Camera);

    internal sealed class ReplayReelDocument
    {
        public int Version { get; set; } = 1;
        public string SourceReplay { get; set; } = "";
        public List<ReplayReelSegment> Segments { get; set; } = new();
    }

    /// <summary>
    /// Persistent non-destructive highlight-reel assembly. A reel is only a
    /// list of source ranges and presentation choices, so reordering/trimming
    /// never rewrites packet data.
    /// </summary>
    internal static class ReplayReels
    {
        public const string Extension = ".reel.json";
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };

        public static IReadOnlyList<ReplayReelSegment> Segments(string replay)
            => Load(replay).Segments.ToArray();

        public static ReplayReelSegment Add(string replay, uint start, uint end,
            string? name = null, ReplaySegmentCamera camera = ReplaySegmentCamera.Current)
        {
            string source = RequireReplay(replay);
            NormalizeRange(ref start, ref end);
            ReplayReelDocument doc = Load(source);
            var segment = new ReplayReelSegment(Guid.NewGuid(), start, end,
                Clean(name, $"Segment {ReplayHud.Time(start)}-{ReplayHud.Time(end)}"),
                camera);
            doc.Segments.Add(segment);
            Save(source, doc);
            return segment;
        }

        public static int AddHighlights(string replay,
            IEnumerable<ReplayHighlight> highlights, int limit = 8)
        {
            string source = RequireReplay(replay);
            ReplayReelDocument doc = Load(source);
            int added = 0;
            foreach (ReplayHighlight highlight in highlights
                .OrderByDescending(h => h.Score)
                .Take(Math.Clamp(limit, 1, 32)))
            {
                if (highlight.StartFrame >= highlight.EndFrame)
                    continue;
                doc.Segments.Add(new ReplayReelSegment(Guid.NewGuid(),
                    highlight.StartFrame, highlight.EndFrame, highlight.Label,
                    ReplaySegmentCamera.Director));
                added++;
            }
            if (added > 0)
                Save(source, doc);
            return added;
        }

        public static void Move(string replay, Guid id, int delta)
        {
            string source = RequireReplay(replay);
            ReplayReelDocument doc = Load(source);
            int from = doc.Segments.FindIndex(segment => segment.Id == id);
            if (from < 0)
                return;
            int to = Math.Clamp(from + Math.Sign(delta), 0, doc.Segments.Count - 1);
            if (from == to)
                return;
            ReplayReelSegment value = doc.Segments[from];
            doc.Segments.RemoveAt(from);
            doc.Segments.Insert(to, value);
            Save(source, doc);
        }

        public static void Trim(string replay, Guid id, uint start, uint end)
        {
            string source = RequireReplay(replay);
            NormalizeRange(ref start, ref end);
            ReplayReelDocument doc = Load(source);
            int index = doc.Segments.FindIndex(segment => segment.Id == id);
            if (index < 0)
                return;
            doc.Segments[index] = doc.Segments[index] with
            {
                StartFrame = start,
                EndFrame = end
            };
            Save(source, doc);
        }

        public static void CycleCamera(string replay, Guid id)
        {
            string source = RequireReplay(replay);
            ReplayReelDocument doc = Load(source);
            int index = doc.Segments.FindIndex(segment => segment.Id == id);
            if (index < 0)
                return;
            int count = Enum.GetValues<ReplaySegmentCamera>().Length;
            ReplaySegmentCamera next = (ReplaySegmentCamera)
                (((int)doc.Segments[index].Camera + 1) % count);
            doc.Segments[index] = doc.Segments[index] with { Camera = next };
            Save(source, doc);
        }

        public static void Remove(string replay, Guid id)
        {
            string source = RequireReplay(replay);
            ReplayReelDocument doc = Load(source);
            if (doc.Segments.RemoveAll(segment => segment.Id == id) > 0)
                Save(source, doc);
        }

        public static void Clear(string replay)
        {
            string source = RequireReplay(replay);
            ReplayReelDocument doc = Load(source);
            doc.Segments.Clear();
            Save(source, doc);
        }

        public static void DeleteFor(string replay)
        {
            if (String.IsNullOrWhiteSpace(replay))
                return;
            try { File.Delete(Sidecar(replay)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        private static ReplayReelDocument Load(string replay)
        {
            string source = ReplayVirtualClips.LogicalPath(replay);
            try
            {
                string sidecar = Sidecar(source);
                if (!File.Exists(sidecar))
                    return New(source);
                ReplayReelDocument? doc = JsonSerializer.Deserialize<ReplayReelDocument>(
                    File.ReadAllText(sidecar), JsonOptions);
                if (doc is not { Version: 1 })
                    return New(source);
                doc.SourceReplay = source;
                doc.Segments ??= new List<ReplayReelSegment>();
                doc.Segments.RemoveAll(segment => segment.StartFrame >= segment.EndFrame
                    || segment.EndFrame > ReplayFormatV3.MaxFrame
                    || String.IsNullOrWhiteSpace(segment.Name));
                return doc;
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException or JsonException)
            {
                return New(source);
            }
        }

        private static ReplayReelDocument New(string source)
            => new() { SourceReplay = source };

        private static void Save(string replay, ReplayReelDocument doc)
        {
            string sidecar = Sidecar(replay);
            Directory.CreateDirectory(Path.GetDirectoryName(sidecar)!);
            File.WriteAllText(sidecar, JsonSerializer.Serialize(doc, JsonOptions));
        }

        private static string RequireReplay(string replay)
        {
            if (String.IsNullOrWhiteSpace(replay))
                throw new ArgumentException("No replay is open.", nameof(replay));
            string source = ReplayVirtualClips.LogicalPath(replay);
            if (!File.Exists(source))
                throw new FileNotFoundException("The replay no longer exists.", source);
            return source;
        }

        private static string Sidecar(string replay)
            => ReplayVirtualClips.LogicalPath(replay) + Extension;

        private static void NormalizeRange(ref uint start, ref uint end)
        {
            if (start > end)
                (start, end) = (end, start);
            if (start >= end)
                throw new ArgumentOutOfRangeException(nameof(end));
        }

        private static string Clean(string? name, string fallback)
        {
            string value = String.IsNullOrWhiteSpace(name) ? fallback : name.Trim();
            return value.Length <= 80 ? value : value[..80];
        }
    }

    internal readonly record struct ReplayExportPreset(
        string Name,
        ReplayVideoResolution Resolution,
        int Fps,
        bool CleanHud);

    internal static class ReplayExportPresets
    {
        public static readonly ReplayExportPreset[] All =
        {
            new("Balanced 1080p60", ReplayVideoResolution.P1080, 60, true),
            new("Smooth 1080p120", ReplayVideoResolution.P1080, 120, true),
            new("Broadcast 1440p60", ReplayVideoResolution.P1440, 60, true),
            new("Archive 4K60", ReplayVideoResolution.P2160, 60, true),
            new("HUD 1080p60", ReplayVideoResolution.P1080, 60, false)
        };
    }

    internal static class ReplayExportQueue
    {
        private static readonly Queue<ReplayVideoExportManifest> Pending = new();
        private static ReplayVideoExportManifest? _last, _failed;
        private static readonly List<string> Failures = new();
        internal static IReadOnlyList<string> RecentFailures => Failures;
        internal static void NoteFailure(ReplayVideoExportManifest? job, string error)
        {
            if (job != null) _failed = job;
            Failures.Add(DateTime.Now.ToString("HH:mm") + " · " + (error.Length > 180 ? error[..180] + "…" : error));
            if (Failures.Count > 5) Failures.RemoveAt(0);
        }

        public static int PendingCount => Pending.Count;
        public static string Status { get; private set; } = "Export queue idle.";

        public static void Enqueue(ReplayVideoExportManifest manifest)
        {
            Pending.Enqueue(manifest);
            _last = manifest;
            Status = $"Queued {Pending.Count} export{(Pending.Count == 1 ? "" : "s")}.";
        }

        public static bool Pump()
        {
            if (ReplayVideoExporter.Active || Pending.Count == 0)
                return ReplayVideoExporter.Active;

            ReplayVideoExportManifest next = Pending.Dequeue();
            _last = next;
            if (!ReplayVideoExporter.Start(next))
            {
                Status = ReplayVideoExporter.Status;
                return false;
            }
            Status = $"Rendering {(next.PresetName.Length == 0 ? "export" : next.PresetName)}"
                + $" · {Pending.Count} queued after this.";
            return true;
        }

        public static void CancelActive()
        {
            if (ReplayVideoExporter.Active)
                ReplayVideoExporter.Cancel();
            Status = $"Export cancelled · {Pending.Count} still queued.";
        }

        public static bool RetryLast()
        {
            var retry = _failed ?? _last;
            if (retry == null)
            {
                Status = "Nothing to retry.";
                return false;
            }
            Pending.Enqueue(retry);
            Status = "Last export queued again.";
            return true;
        }

        internal static void NoteFinished(string status)
        {
            Status = Pending.Count == 0
                ? status
                : $"{status} {Pending.Count} export{(Pending.Count == 1 ? "" : "s")} queued.";
        }

        public static void ClearPending()
        {
            Pending.Clear();
            Status = ReplayVideoExporter.Active
                ? "Pending exports cleared; current render continues."
                : "Export queue cleared.";
        }
    }

    internal sealed record ReplayVideoSegment(
        uint StartFrame,
        uint EndFrame,
        string Name,
        ReplaySegmentCamera Camera);

    internal static partial class ReplayVideoExport
    {
        public static ReplayVideoExportManifest CreatePresetManifest(
            string replay, uint startFrame, uint endFrame,
            ReplayExportPreset preset, bool? cleanHud = null,
            bool director = false, bool cameraTrack = true)
        {
            ReplayVideoExportManifest manifest = CreateManifest(replay,
                startFrame, endFrame, preset.Resolution, preset.Fps,
                cleanHud ?? preset.CleanHud, director, cameraTrack);
            return manifest with { PresetName = preset.Name };
        }

        public static ReplayVideoExportManifest CreateReelManifest(
            string replay, IReadOnlyList<ReplayReelSegment> segments,
            ReplayExportPreset preset, bool? cleanHud = null)
        {
            if (segments.Count == 0)
                throw new ArgumentException("The highlight reel is empty.", nameof(segments));

            ReplayReelSegment first = segments[0];
            uint maxEnd = segments.Max(segment => segment.EndFrame);
            ReplayVideoExportManifest manifest = CreateManifest(replay,
                first.StartFrame, maxEnd, preset.Resolution, preset.Fps,
                cleanHud ?? preset.CleanHud, director: false, cameraTrack: false);
            ReplayVideoSegment[] ranges = segments.Select(segment =>
                new ReplayVideoSegment(segment.StartFrame, segment.EndFrame,
                    segment.Name, segment.Camera)).ToArray();
            return manifest with
            {
                Segments = ranges,
                PresetName = preset.Name + " reel"
            };
        }
    }
}
