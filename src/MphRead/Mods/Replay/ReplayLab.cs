using System;
using System.IO;
using System.Text.Json;
using MphRead.Mods.Network;

namespace MphRead.Mods.Replay
{
    internal sealed record ReplayLabBranch(
        int Version,
        string SourceReplay,
        uint SourceFrame,
        int ControlledSlot,
        DateTime CreatedUtc,
        long SourceBytes,
        long SourceModifiedUtcTicks);

    /// <summary>
    /// Metadata for a replay-derived practice branch. The branch deliberately does
    /// not pretend to be the original recording after control is taken: from this
    /// frame forward it is an offline simulation fork.
    /// </summary>
    internal static class ReplayLab
    {
        public const string Extension = ".fplab";

        public static string WriteBranch(string sourceReplay, uint frame, int slot)
        {
            var source = new FileInfo(sourceReplay);
            if (!source.Exists) throw new FileNotFoundException("Replay source disappeared.", sourceReplay);

            string directory = Path.Combine(DemoLibrary.Directory, "branches");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory,
                $"branch_{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}_{Guid.NewGuid():N}{Extension}");
            var branch = new ReplayLabBranch(
                1,
                source.FullName,
                frame,
                slot,
                DateTime.UtcNow,
                source.Length,
                source.LastWriteTimeUtc.Ticks);
            File.WriteAllText(path, JsonSerializer.Serialize(branch,
                new JsonSerializerOptions { WriteIndented = true }));
            return path;
        }

        public static bool TryRead(string path, out ReplayLabBranch? branch)
        {
            branch = null;
            try
            {
                branch = JsonSerializer.Deserialize<ReplayLabBranch>(File.ReadAllText(path));
                return branch is { Version: 1 } value
                    && value.SourceFrame <= ReplayFormatV3.MaxFrame
                    && value.ControlledSlot is >= 0 and < RosterPacket.MaxSlots
                    && value.SourceReplay.Length > 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or JsonException or ArgumentException)
            {
                return false;
            }
        }
    }
}
