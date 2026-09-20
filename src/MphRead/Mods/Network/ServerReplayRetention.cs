using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MphRead.Mods.Network
{
    /// <summary>
    /// Server-owned canonical replay retention. Zero storage/age values mean
    /// unlimited, while KeepLast always protects the newest finalized recordings.
    /// </summary>
    public sealed record ServerReplayPolicy(
        bool Enabled = true,
        int StorageLimitGb = 25,
        int RetentionDays = 14,
        int KeepLast = 100)
    {
        public static ServerReplayPolicy Default { get; } = new();

        public ServerReplayPolicy Normalize()
            => this with
            {
                StorageLimitGb = Math.Clamp(StorageLimitGb, 0, 1024),
                RetentionDays = Math.Clamp(RetentionDays, 0, 36500),
                KeepLast = Math.Clamp(KeepLast, 0, 100000)
            };

        public long StorageLimitBytes => StorageLimitGb <= 0
            ? 0 : StorageLimitGb * 1024L * 1024L * 1024L;
    }

    internal readonly record struct ServerReplayRetentionResult(
        long BeforeBytes,
        long AfterBytes,
        int DeletedFiles,
        int ProtectedFiles,
        bool LimitSatisfied);

    internal static class ServerReplayRetention
    {
        public static ServerReplayRetentionResult Apply(string directory,
            ServerReplayPolicy policy, string? activePath = null, DateTime? nowUtc = null,
            long? storageLimitBytes = null)
        {
            policy = policy.Normalize();
            if (!Directory.Exists(directory))
                return new ServerReplayRetentionResult(0, 0, 0, 0, true);

            FileInfo[] files = new DirectoryInfo(directory)
                .EnumerateFiles("*" + DemoFile.Extension, SearchOption.TopDirectoryOnly)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ThenByDescending(file => file.Name, StringComparer.Ordinal)
                .ToArray();

            long before = files.Sum(file => file.Length);
            long total = before;
            int deleted = 0;

            var protectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < Math.Min(policy.KeepLast, files.Length); i++)
                protectedPaths.Add(files[i].FullName);

            foreach (FileInfo file in files)
            {
                if (File.Exists(file.FullName + ".favorite"))
                    protectedPaths.Add(file.FullName);
            }
            if (!String.IsNullOrWhiteSpace(activePath))
                protectedPaths.Add(Path.GetFullPath(activePath));

            DateTime now = nowUtc ?? DateTime.UtcNow;
            DateTime cutoff = policy.RetentionDays <= 0
                ? DateTime.MinValue
                : now.AddDays(-policy.RetentionDays);

            bool Delete(FileInfo file)
            {
                if (protectedPaths.Contains(file.FullName) || !file.Exists)
                    return false;
                try
                {
                    long bytes = file.Length;
                    string path = file.FullName;
                    file.Delete();
                    DeleteSidecars(path);
                    total = Math.Max(0, total - bytes);
                    deleted++;
                    return true;
                }
                catch (Exception ex) when (ex is IOException
                    or UnauthorizedAccessException)
                {
                    Console.WriteLine($"[replay] retention could not delete "
                        + $"{file.Name}: {ex.Message}");
                    return false;
                }
            }

            if (policy.RetentionDays > 0)
            {
                foreach (FileInfo file in files.OrderBy(file => file.LastWriteTimeUtc))
                {
                    if (file.LastWriteTimeUtc >= cutoff) break;
                    Delete(file);
                }
            }

            long limit = storageLimitBytes ?? policy.StorageLimitBytes;
            if (limit < 0) limit = 0;
            if (limit > 0 && total > limit)
            {
                foreach (FileInfo file in files.OrderBy(file => file.LastWriteTimeUtc))
                {
                    if (total <= limit) break;
                    Delete(file);
                }
            }

            bool satisfied = limit <= 0 || total <= limit;
            return new ServerReplayRetentionResult(
                before, total, deleted, protectedPaths.Count, satisfied);
        }

        private static void DeleteSidecars(string path)
        {
            TryDelete(path + ".name");
            TryDelete(path + ".favorite");
            TryDelete(path + ".camera");
            TryDelete(path + ".analytics.json");
            for (int i = 0; i < 3; i++)
                TryDelete(path + $".thumb{i}.png");
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
