using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using MphRead.Mods.Network;

namespace MphRead.Mods.Replay
{
    internal enum ReplayHighlightKind
    {
        Kill,
        MultiKill,
        Objective,
        CloseFinish,
        Comeback,
        Duel
    }

    internal readonly record struct ReplayHighlight(
        uint StartFrame,
        uint EndFrame,
        uint FocusFrame,
        ReplayHighlightKind Kind,
        byte ActorSlot,
        byte TargetSlot,
        int Score,
        string Label);

    internal readonly record struct ReplayPlayerAnalytics(
        byte Slot,
        int Kills,
        int Deaths,
        int Damage,
        int ScoreEvents,
        int ObjectiveEvents,
        int Spawns,
        int Joins,
        int Leaves);

    internal sealed class ReplayAnalyticsSnapshot
    {
        public IReadOnlyList<ReplayPlayerAnalytics> Players { get; init; } = Array.Empty<ReplayPlayerAnalytics>();
        public int TotalKills { get; init; }
        public int TotalDamage { get; init; }
        public int ObjectiveEvents { get; init; }
        public uint DurationFrames { get; init; }
    }

    /// <summary>
    /// Derived replay information. It only reads annotations and metadata, so it can never
    /// influence packet playback or simulation determinism.
    /// </summary>
    internal static class ReplayStudio
    {
        private const uint HighlightPreRoll = 4 * 60;
        private const uint HighlightPostRoll = 3 * 60;
        private const uint MultiKillWindow = 8 * 60;
        private static string? _cachePath;
        private static int _cacheEventCount = -1;
        private static uint _cacheDuration;
        private static IReadOnlyList<ReplayHighlight>? _cachedHighlights;
        private static ReplayAnalyticsSnapshot? _cachedAnalytics;

        internal static void ResetCache()
        {
            _cachePath = null;
            _cacheEventCount = -1;
            _cacheDuration = 0;
            _cachedHighlights = null;
            _cachedAnalytics = null;
        }

        private static bool DefaultCacheValid(int eventCount, uint duration)
            => String.Equals(_cachePath, DemoPlayback.CurrentPath, StringComparison.OrdinalIgnoreCase)
                && _cacheEventCount == eventCount
                && _cacheDuration == duration;

        private static void NoteDefaultCache(int eventCount, uint duration)
        {
            string? path = DemoPlayback.CurrentPath;
            if (!String.Equals(_cachePath, path, StringComparison.OrdinalIgnoreCase)
                || _cacheEventCount != eventCount || _cacheDuration != duration)
            {
                _cachePath = path;
                _cacheEventCount = eventCount;
                _cacheDuration = duration;
                _cachedHighlights = null;
                _cachedAnalytics = null;
            }
        }

        public static IReadOnlyList<ReplayHighlight> Highlights(
            IReadOnlyList<ReplayEvent>? events = null, uint? duration = null)
        {
            bool useCache = events == null && !duration.HasValue;
            events ??= DemoPlayback.Events;
            uint length = duration ?? DemoPlayback.LastFrame;
            if (useCache)
            {
                NoteDefaultCache(events.Count, length);
                if (DefaultCacheValid(events.Count, length) && _cachedHighlights != null)
                    return _cachedHighlights;
            }

            var result = new List<ReplayHighlight>();
            var recentKills = new Dictionary<byte, Queue<ReplayEvent>>();

            foreach (ReplayEvent e in events.OrderBy(e => e.Frame))
            {
                if (e.Type == ReplayEventType.Kill && e.ActorSlot != byte.MaxValue)
                {
                    if (!recentKills.TryGetValue(e.ActorSlot, out Queue<ReplayEvent>? queue))
                    {
                        queue = new Queue<ReplayEvent>();
                        recentKills[e.ActorSlot] = queue;
                    }
                    while (queue.Count > 0 && e.Frame - queue.Peek().Frame > MultiKillWindow)
                        queue.Dequeue();
                    queue.Enqueue(e);

                    int chain = queue.Count;
                    int score = chain >= 3 ? 90 + chain * 8 : chain == 2 ? 72 : 45;
                    ReplayHighlightKind kind = chain >= 2 ? ReplayHighlightKind.MultiKill : ReplayHighlightKind.Kill;
                    string label = chain switch
                    {
                        >= 4 => $"{chain}x multi-kill",
                        3 => "Triple kill",
                        2 => "Double kill",
                        _ => "Kill"
                    };
                    AddMerged(result, Make(e.Frame, length, kind, e.ActorSlot, e.TargetSlot, score, label));
                }
                else if (e.Type == ReplayEventType.Objective)
                {
                    AddMerged(result, Make(e.Frame, length, ReplayHighlightKind.Objective,
                        e.ActorSlot, e.TargetSlot, 82, "Objective play"));
                }
                else if (e.Type == ReplayEventType.MatchEnded && e.Frame > 0)
                {
                    uint start = e.Frame > 12 * 60 ? e.Frame - 12 * 60 : 0;
                    result.Add(new ReplayHighlight(start, Math.Min(length, e.Frame + 2 * 60), e.Frame,
                        ReplayHighlightKind.CloseFinish, e.ActorSlot, e.TargetSlot, 88, "Match finish"));
                }
            }

            // Dense damage exchanges without a kill are worth surfacing as duels.
            foreach (IGrouping<uint, ReplayEvent> bucket in events
                .Where(e => e.Type == ReplayEventType.Damage)
                .GroupBy(e => e.Frame / (5 * 60)))
            {
                int damage = bucket.Sum(e => Math.Max(0, e.Value));
                if (damage < 120) continue;
                ReplayEvent focus = bucket.OrderByDescending(e => e.Value).First();
                AddMerged(result, Make(focus.Frame, length, ReplayHighlightKind.Duel,
                    focus.ActorSlot, focus.TargetSlot, Math.Min(80, 45 + damage / 6), "Heavy duel"));
            }

            ReplayHighlight[] highlights = result
                .OrderByDescending(h => h.Score)
                .ThenBy(h => h.FocusFrame)
                .ToArray();
            if (useCache) _cachedHighlights = highlights;
            return highlights;
        }

        public static ReplayAnalyticsSnapshot Analytics(
            IReadOnlyList<ReplayEvent>? events = null, uint? duration = null)
        {
            bool useCache = events == null && !duration.HasValue;
            events ??= DemoPlayback.Events;
            uint length = duration ?? DemoPlayback.LastFrame;
            if (useCache)
            {
                NoteDefaultCache(events.Count, length);
                if (DefaultCacheValid(events.Count, length) && _cachedAnalytics != null)
                    return _cachedAnalytics;
            }

            var rows = new Dictionary<byte, MutableAnalytics>();
            MutableAnalytics Row(byte slot)
            {
                if (!rows.TryGetValue(slot, out MutableAnalytics? row))
                {
                    row = new MutableAnalytics();
                    rows[slot] = row;
                }
                return row;
            }

            int totalKills = 0;
            int totalDamage = 0;
            int objectives = 0;
            foreach (ReplayEvent e in events)
            {
                if (e.ActorSlot != byte.MaxValue)
                {
                    MutableAnalytics actor = Row(e.ActorSlot);
                    switch (e.Type)
                    {
                        case ReplayEventType.Kill: actor.Kills++; totalKills++; break;
                        case ReplayEventType.Damage: actor.Damage += Math.Max(0, e.Value); totalDamage += Math.Max(0, e.Value); break;
                        case ReplayEventType.ScoreChanged: actor.ScoreEvents++; break;
                        case ReplayEventType.Objective: actor.Objectives++; objectives++; break;
                        case ReplayEventType.PlayerSpawn: actor.Spawns++; break;
                        case ReplayEventType.PlayerJoined: actor.Joins++; break;
                        case ReplayEventType.PlayerLeft: actor.Leaves++; break;
                    }
                }
                if (e.Type == ReplayEventType.PlayerDeath && e.ActorSlot != byte.MaxValue)
                    Row(e.ActorSlot).Deaths++;
                else if (e.Type == ReplayEventType.Kill && e.TargetSlot != byte.MaxValue)
                    Row(e.TargetSlot).Deaths++;
            }

            var players = rows.OrderBy(p => p.Key)
                .Select(p => new ReplayPlayerAnalytics(p.Key, p.Value.Kills, p.Value.Deaths,
                    p.Value.Damage, p.Value.ScoreEvents, p.Value.Objectives,
                    p.Value.Spawns, p.Value.Joins, p.Value.Leaves))
                .ToArray();
            var snapshot = new ReplayAnalyticsSnapshot
            {
                Players = players,
                TotalKills = totalKills,
                TotalDamage = totalDamage,
                ObjectiveEvents = objectives,
                DurationFrames = length
            };
            if (useCache) _cachedAnalytics = snapshot;
            return snapshot;
        }

        private static ReplayHighlight Make(uint frame, uint duration, ReplayHighlightKind kind,
            byte actor, byte target, int score, string label)
        {
            uint start = frame > HighlightPreRoll ? frame - HighlightPreRoll : 0;
            uint end = Math.Min(duration, frame + HighlightPostRoll);
            return new ReplayHighlight(start, end, frame, kind, actor, target, score, label);
        }

        private static void AddMerged(List<ReplayHighlight> result, ReplayHighlight candidate)
        {
            for (int i = 0; i < result.Count; i++)
            {
                ReplayHighlight current = result[i];
                if (current.ActorSlot == candidate.ActorSlot
                    && candidate.StartFrame <= current.EndFrame + 60
                    && current.StartFrame <= candidate.EndFrame + 60)
                {
                    result[i] = new ReplayHighlight(
                        Math.Min(current.StartFrame, candidate.StartFrame),
                        Math.Max(current.EndFrame, candidate.EndFrame),
                        candidate.Score >= current.Score ? candidate.FocusFrame : current.FocusFrame,
                        candidate.Score >= current.Score ? candidate.Kind : current.Kind,
                        candidate.ActorSlot,
                        candidate.Score >= current.Score ? candidate.TargetSlot : current.TargetSlot,
                        Math.Max(current.Score, candidate.Score),
                        candidate.Score >= current.Score ? candidate.Label : current.Label);
                    return;
                }
            }
            result.Add(candidate);
        }

        private sealed class MutableAnalytics
        {
            public int Kills;
            public int Deaths;
            public int Damage;
            public int ScoreEvents;
            public int Objectives;
            public int Spawns;
            public int Joins;
            public int Leaves;
        }
    }

    internal sealed record ReplayVirtualClipDocument(
        int Version,
        string SourceReplay,
        uint StartFrame,
        uint EndFrame,
        DateTime CreatedUtc,
        string Name);

    internal static class ReplayVirtualClips
    {
        public const string Extension = ".fpclip";

        public static string Save(string sourceReplay, uint startFrame, uint endFrame, string? name = null)
        {
            if (startFrame >= endFrame) throw new ArgumentOutOfRangeException(nameof(endFrame));
            string source = Path.GetFullPath(sourceReplay);
            if (!File.Exists(source)) throw new FileNotFoundException("The source replay does not exist.", source);
            Directory.CreateDirectory(DemoLibrary.Directory);
            string title = string.IsNullOrWhiteSpace(name)
                ? $"Clip {ReplayHud.Time(startFrame)}-{ReplayHud.Time(endFrame)}"
                : name.Trim();
            var document = new ReplayVirtualClipDocument(1, source, startFrame, endFrame, DateTime.UtcNow, title);
            string path = Path.Combine(DemoLibrary.Directory,
                $"virtual_{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}_{Guid.NewGuid():N}{Extension}");
            File.WriteAllText(path, JsonSerializer.Serialize(document, JsonOptions));
            return path;
        }

        public static bool TryLoad(string path, out ReplayVirtualClipDocument? document)
        {
            document = null;
            try
            {
                if (!File.Exists(path)) return false;
                document = JsonSerializer.Deserialize<ReplayVirtualClipDocument>(
                    File.ReadAllText(path), JsonOptions);
                return document is { Version: 1 } value
                    && value.StartFrame < value.EndFrame
                    && !string.IsNullOrWhiteSpace(value.SourceReplay);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return false;
            }
        }

        public static ReplayOpenResult Materialize(string virtualClipPath, string outputPath)
        {
            if (!TryLoad(virtualClipPath, out ReplayVirtualClipDocument? clip) || clip == null)
                return ReplayOpenResult.Corrupt;
            if (!File.Exists(clip.SourceReplay)) return ReplayOpenResult.FileMissing;
            return ReplayArchive.Extract(clip.SourceReplay, clip.StartFrame, clip.EndFrame, outputPath);
        }

        public static IReadOnlyList<string> List()
        {
            if (!Directory.Exists(DemoLibrary.Directory)) return Array.Empty<string>();
            return Directory.EnumerateFiles(DemoLibrary.Directory, "*" + Extension, SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToArray();
        }

        public static bool IsFavorite(string path) => File.Exists(path + ".favorite");

        public static void ToggleFavorite(string path)
        {
            string marker = path + ".favorite";
            if (File.Exists(marker)) File.Delete(marker);
            else File.WriteAllText(marker, "");
        }

        public static bool Rename(string path, string name)
        {
            if (!TryLoad(path, out ReplayVirtualClipDocument? clip) || clip == null)
                return false;
            string title = String.IsNullOrWhiteSpace(name) ? clip.Name : name.Trim();
            File.WriteAllText(path, JsonSerializer.Serialize(
                clip with { Name = title }, JsonOptions));
            return true;
        }

        public static void Delete(string path)
        {
            File.Delete(path);
            File.Delete(path + ".favorite");
            ReplayAnnotations.DeleteFor(path);
            string cache = CachePath(path);
            File.Delete(cache);
        }

        public static string? ResolveForPlayback(string path, out ReplayOpenResult result)
        {
            result = ReplayOpenResult.Corrupt;
            if (!TryLoad(path, out ReplayVirtualClipDocument? clip) || clip == null)
                return null;
            if (!File.Exists(clip.SourceReplay))
            {
                result = ReplayOpenResult.FileMissing;
                return null;
            }

            string output = CachePath(path);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                var source = new FileInfo(clip.SourceReplay);
                var descriptor = new FileInfo(path);
                if (File.Exists(output)
                    && File.GetLastWriteTimeUtc(output) >= descriptor.LastWriteTimeUtc
                    && File.GetLastWriteTimeUtc(output) >= source.LastWriteTimeUtc)
                {
                    result = ReplayOpenResult.Success;
                    return output;
                }

                File.Delete(output);
                result = ReplayArchive.Extract(clip.SourceReplay,
                    clip.StartFrame, clip.EndFrame, output);
                return result == ReplayOpenResult.Success ? output : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or ArgumentException)
            {
                return null;
            }
        }

        private static string CachePath(string path)
        {
            string directory = Path.Combine(DemoLibrary.Directory, ".virtual-cache");
            string file = Path.GetFileNameWithoutExtension(path) + DemoFile.Extension;
            return Path.Combine(directory, file);
        }

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };
    }

    internal sealed record ReplayStoragePolicy(
        long MaxBytes,
        bool DeleteFullMatches = true,
        bool DeleteMaterializedClips = false,
        bool DeleteVirtualClips = false);

    internal readonly record struct ReplayStorageResult(long BeforeBytes, long AfterBytes, int DeletedFiles);

    internal static class ReplayStorageManager
    {
        public static ReplayStorageResult Apply(ReplayStoragePolicy policy)
        {
            if (policy.MaxBytes <= 0) return new ReplayStorageResult(0, 0, 0);
            Directory.CreateDirectory(DemoLibrary.Directory);
            FileInfo[] files = new DirectoryInfo(DemoLibrary.Directory)
                .EnumerateFiles("*" + DemoFile.Extension, SearchOption.TopDirectoryOnly)
                .Where(f => !f.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f.LastWriteTimeUtc)
                .ToArray();

            string cacheDirectory = Path.Combine(DemoLibrary.Directory, ".virtual-cache");
            FileInfo[] cacheFiles = Directory.Exists(cacheDirectory)
                ? new DirectoryInfo(cacheDirectory)
                    .EnumerateFiles("*" + DemoFile.Extension, SearchOption.TopDirectoryOnly)
                    .OrderBy(f => f.LastWriteTimeUtc)
                    .ToArray()
                : Array.Empty<FileInfo>();

            var protectedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string virtualClip in ReplayVirtualClips.List())
            {
                if (ReplayVirtualClips.TryLoad(virtualClip, out ReplayVirtualClipDocument? clip)
                    && clip != null)
                {
                    protectedSources.Add(Path.GetFullPath(clip.SourceReplay));
                }
            }

            long before = files.Sum(f => f.Length) + cacheFiles.Sum(f => f.Length);
            long total = before;
            int deleted = 0;

            // Materialized virtual clips are a cache, never the source of truth.
            // Evict them before deleting a real recording.
            foreach (FileInfo cache in cacheFiles)
            {
                if (total <= policy.MaxBytes) break;
                if (DemoPlayback.IsActive && DemoPlayback.CurrentPath != null
                    && String.Equals(Path.GetFullPath(cache.FullName),
                        Path.GetFullPath(DemoPlayback.CurrentPath),
                        StringComparison.OrdinalIgnoreCase))
                {
                    // A virtual clip plays from this materialized cache file.
                    // Deleting an open file is legal on Unix, but a later
                    // checkpoint fallback/rebuild has to reopen CurrentPath.
                    continue;
                }
                try
                {
                    long bytes = cache.Length;
                    cache.Delete();
                    total = Math.Max(0, total - bytes);
                    deleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Continue with other cache entries or recordings.
                }
            }

            foreach (FileInfo file in files)
            {
                if (total <= policy.MaxBytes) break;
                string path = file.FullName;
                if (File.Exists(path + ".favorite")) continue;
                if (DemoPlayback.IsActive && DemoPlayback.CurrentPath != null
                    && String.Equals(Path.GetFullPath(path),
                        Path.GetFullPath(DemoPlayback.CurrentPath),
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                if (protectedSources.Contains(Path.GetFullPath(path))) continue;
                using DemoReader? reader = DemoReader.Open(path, out _, metadataOnly: true);
                ReplayType type = reader?.Metadata?.Type ?? ReplayType.FullMatch;
                if (type == ReplayType.FullMatch && !policy.DeleteFullMatches) continue;
                if (type == ReplayType.Clip && !policy.DeleteMaterializedClips) continue;
                try
                {
                    long bytes = file.Length;
                    DemoLibrary.Delete(path);
                    TryDelete(path + ".camera");
                    TryDelete(path + ".analytics.json");
                    TryDelete(path + ".thumb.png");
                    total = Math.Max(0, total - bytes);
                    deleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Keep pruning other eligible files.
                }
            }

            if (policy.DeleteVirtualClips && total > policy.MaxBytes)
            {
                foreach (string path in ReplayVirtualClips.List().OrderBy(File.GetLastWriteTimeUtc))
                {
                    if (total <= policy.MaxBytes) break;
                    try { File.Delete(path); deleted++; } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
            return new ReplayStorageResult(before, total, deleted);
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    internal enum ReplayVideoResolution
    {
        P720,
        P1080,
        P1440,
        P2160
    }

    internal sealed record ReplayVideoExportManifest(
        int Version,
        string Replay,
        uint StartFrame,
        uint EndFrame,
        int Width,
        int Height,
        int Fps,
        bool CleanHud,
        bool Director,
        bool CameraTrack,
        string FramePattern,
        string SuggestedOutput,
        string FfmpegArguments);

    internal static class ReplayVideoExport
    {
        public static ReplayVideoExportManifest CreateManifest(string replay, uint startFrame, uint endFrame,
            ReplayVideoResolution resolution = ReplayVideoResolution.P1080, int fps = 60,
            bool cleanHud = true, bool director = false, bool cameraTrack = true)
        {
            if (startFrame >= endFrame) throw new ArgumentOutOfRangeException(nameof(endFrame));
            (int width, int height) = resolution switch
            {
                ReplayVideoResolution.P720 => (1280, 720),
                ReplayVideoResolution.P1440 => (2560, 1440),
                ReplayVideoResolution.P2160 => (3840, 2160),
                _ => (1920, 1080)
            };
            fps = Math.Clamp(fps, 30, 120);
            string root = Path.Combine(DemoLibrary.Directory, "exports",
                $"render_{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}_{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            string frames = Path.Combine(root, "frame_%08d.png");
            string output = Path.Combine(root, "replay.mp4");
            int sourceFps = fps <= 30 ? 30 : 60;
            string filters = $"scale={width}:{height}:flags=lanczos,fps={fps}";
            string ffmpeg = $"-y -framerate {sourceFps} -i \"{frames}\" "
                + $"-vf \"{filters}\" -c:v libx264 -preset slow -crf 18 "
                + $"-pix_fmt yuv420p -movflags +faststart \"{output}\"";
            var manifest = new ReplayVideoExportManifest(1, Path.GetFullPath(replay), startFrame, endFrame,
                width, height, fps, cleanHud, director, cameraTrack, frames, output, ffmpeg);
            File.WriteAllText(Path.Combine(root, "render.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(root, "encode.txt"), "ffmpeg " + ffmpeg + Environment.NewLine);
            return manifest;
        }
    }
}
