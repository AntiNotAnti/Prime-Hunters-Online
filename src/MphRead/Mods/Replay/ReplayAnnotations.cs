using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MphRead.Mods.Network;

namespace MphRead.Mods.Replay
{
    internal sealed record ReplayBookmark(Guid Id, uint Frame, string Name, DateTime CreatedUtc);

    internal sealed record ReplayNamedHighlight(
        Guid Id, uint StartFrame, uint EndFrame, string Name, DateTime CreatedUtc);

    internal sealed class ReplayAnnotationDocument
    {
        public int Version { get; set; } = 1;
        public List<ReplayBookmark> Bookmarks { get; set; } = new();
        public List<ReplayNamedHighlight> Highlights { get; set; } = new();
    }

    /// <summary>
    /// User-authored Replay Studio metadata. It deliberately lives beside the
    /// recording instead of inside it, so naming a moment or highlight never
    /// rewrites the deterministic packet stream.
    /// </summary>
    internal static class ReplayAnnotations
    {
        public const string Extension = ".studio.json";

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };

        public static IReadOnlyList<ReplayBookmark> Bookmarks(string? replay = null)
        {
            ReplayAnnotationDocument document = Load(replay ?? DemoPlayback.CurrentPath);
            return document.Bookmarks
                .OrderBy(bookmark => bookmark.Frame)
                .ToArray();
        }

        public static IReadOnlyList<ReplayNamedHighlight> Highlights(string? replay = null)
        {
            ReplayAnnotationDocument document = Load(replay ?? DemoPlayback.CurrentPath);
            return document.Highlights
                .OrderBy(highlight => highlight.StartFrame)
                .ToArray();
        }

        public static ReplayBookmark AddBookmark(string replay, uint frame, string? name)
        {
            string path = RequireReplay(replay);
            ReplayAnnotationDocument document = Load(path);
            string title = CleanName(name, $"Bookmark {ReplayHud.Time(frame)}");
            var bookmark = new ReplayBookmark(Guid.NewGuid(),
                Math.Min(frame, ReplayController.DurationFrames), title, DateTime.UtcNow);
            document.Bookmarks.Add(bookmark);
            Save(path, document);
            return bookmark;
        }

        public static ReplayNamedHighlight AddHighlight(string replay,
            uint startFrame, uint endFrame, string? name)
        {
            string path = RequireReplay(replay);
            uint start = Math.Min(startFrame, endFrame);
            uint end = Math.Max(startFrame, endFrame);
            if (start == end) throw new ArgumentOutOfRangeException(nameof(endFrame));
            string title = CleanName(name,
                $"Highlight {ReplayHud.Time(start)}-{ReplayHud.Time(end)}");
            ReplayAnnotationDocument document = Load(path);
            var highlight = new ReplayNamedHighlight(Guid.NewGuid(), start, end,
                title, DateTime.UtcNow);
            document.Highlights.Add(highlight);
            Save(path, document);
            return highlight;
        }

        public static void RemoveBookmark(string replay, Guid id)
        {
            string path = RequireReplay(replay);
            ReplayAnnotationDocument document = Load(path);
            if (document.Bookmarks.RemoveAll(bookmark => bookmark.Id == id) > 0)
                Save(path, document);
        }

        public static void RemoveHighlight(string replay, Guid id)
        {
            string path = RequireReplay(replay);
            ReplayAnnotationDocument document = Load(path);
            if (document.Highlights.RemoveAll(highlight => highlight.Id == id) > 0)
                Save(path, document);
        }

        public static void DeleteFor(string replay)
        {
            if (String.IsNullOrWhiteSpace(replay)) return;
            try { File.Delete(Sidecar(replay)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        private static ReplayAnnotationDocument Load(string? replay)
        {
            if (String.IsNullOrWhiteSpace(replay)) return new ReplayAnnotationDocument();
            try
            {
                string sidecar = Sidecar(replay);
                if (!File.Exists(sidecar)) return new ReplayAnnotationDocument();
                ReplayAnnotationDocument? document =
                    JsonSerializer.Deserialize<ReplayAnnotationDocument>(
                        File.ReadAllText(sidecar), JsonOptions);
                if (document is not { Version: 1 }) return new ReplayAnnotationDocument();
                document.Bookmarks ??= new List<ReplayBookmark>();
                document.Highlights ??= new List<ReplayNamedHighlight>();
                document.Bookmarks.RemoveAll(bookmark =>
                    String.IsNullOrWhiteSpace(bookmark.Name)
                    || bookmark.Frame > ReplayFormatV3.MaxFrame);
                document.Highlights.RemoveAll(highlight =>
                    String.IsNullOrWhiteSpace(highlight.Name)
                    || highlight.StartFrame >= highlight.EndFrame
                    || highlight.EndFrame > ReplayFormatV3.MaxFrame);
                return document;
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException or JsonException)
            {
                return new ReplayAnnotationDocument();
            }
        }

        private static void Save(string replay, ReplayAnnotationDocument document)
        {
            string sidecar = Sidecar(replay);
            Directory.CreateDirectory(Path.GetDirectoryName(sidecar)!);
            File.WriteAllText(sidecar, JsonSerializer.Serialize(document, JsonOptions));
        }

        private static string RequireReplay(string replay)
        {
            if (String.IsNullOrWhiteSpace(replay))
                throw new ArgumentException("No replay is open.", nameof(replay));
            string path = Path.GetFullPath(replay);
            if (!File.Exists(path))
                throw new FileNotFoundException("The replay no longer exists.", path);
            return path;
        }

        private static string Sidecar(string replay)
            => Path.GetFullPath(replay) + Extension;

        private static string CleanName(string? name, string fallback)
        {
            string value = String.IsNullOrWhiteSpace(name) ? fallback : name.Trim();
            if (value.Length > 80) value = value[..80];
            return value;
        }
    }
}
