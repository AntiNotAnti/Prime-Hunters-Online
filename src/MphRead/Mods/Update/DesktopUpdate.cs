using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;

namespace MphRead.Mods.Update
{
    /// <summary>
    /// Replace this installation with the release's own package, without
    /// anybody opening a browser.
    ///
    /// The awkward part is that a program cannot overwrite the file it is
    /// running from -- Windows refuses outright, and on Unix it works in a way
    /// that is worse than refusing. So the swap is done by a **second copy of
    /// the new build**: the archive is unpacked beside the installation, the
    /// unpacked binary is started with <c>-applyupdate</c>, this process
    /// exits, and that copy waits for it to be gone, copies itself and
    /// everything beside it over the installation, and starts it again.
    ///
    /// Running the *new* binary as the one doing the copying, rather than the
    /// old one, is what makes it a single mechanism: the old build never has
    /// to know how a future release wants to be laid out, and the file doing
    /// the work is never one of the files being replaced.
    ///
    /// Nothing is deleted from the installation. The copy is a copy over the
    /// top, which is exactly what the instructions on the release page have
    /// always said to do by hand -- so a player's <c>paths.txt</c>, their
    /// <c>controls.txt</c>, their saves and their extracted game files are
    /// where they were.
    /// </summary>
    public static class DesktopUpdate
    {
        /// <summary>The argument that turns a launch into the copying half.</summary>
        public const string ApplyFlag = "applyupdate";

        /// <summary>Where the download and the unpacked build wait.</summary>
        private static string Staging => Path.Combine(AppContext.BaseDirectory, ".update");

        private static string StagedBuild => Path.Combine(Staging, "staged");

        /// <summary>
        /// Where a staged build sits, for an installer that is not this one.
        /// <see cref="ServerUpdate"/> stages with the code here and then
        /// applies it in a way a supervised server survives.
        /// </summary>
        public static string StagedBuildPath => StagedBuild;

        /// <summary>Why the last attempt produced nothing.</summary>
        public static string? LastError { get; private set; }

        /// <summary>
        /// Whether this installation can be replaced in place: a published
        /// build, in a directory this user may write to.
        ///
        /// A read-only directory is the ordinary case for a system-wide
        /// install, and the answer there is the release page, not a failure
        /// half way through a copy.
        /// </summary>
        public static bool Supported
        {
            get
            {
                // Copying files into a signed app invalidates its resource seal.
                // macOS updates use the release page and replace the whole app.
                if (OperatingSystem.IsMacOS() || OperatingSystem.IsAndroid() || !BuildVersion.IsRelease)
                {
                    return false;
                }
                try
                {
                    string probe = Path.Combine(AppContext.BaseDirectory, ".update-probe");
                    File.WriteAllBytes(probe, Array.Empty<byte>());
                    File.Delete(probe);
                    return true;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// Fetch and unpack, and report whether the swap can now be started.
        ///
        /// Everything that can fail happens here, while the program is still
        /// running and can say so on screen. By the time <see cref="Launch"/>
        /// is called there is a complete, unpacked build on disk and the only
        /// work left is copying it.
        /// </summary>
        public static bool Stage(UpdateInfo update, Action<float>? progress = null,
            CancellationToken cancel = default)
        {
            LastError = null;
            if (update.AssetUrl.Length == 0)
            {
                LastError = "this release has no package for this platform";
                return false;
            }
            if (!UpdateDownload.SupportsDigest(update.AssetDigest))
            {
                LastError = "this release asset has no supported SHA-256 digest";
                return false;
            }
            try
            {
                Clean();
                Directory.CreateDirectory(Staging);
                bool zip = update.AssetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
                string archive = Path.Combine(Staging, zip ? "package.zip" : "package.tar.gz");
                if (!UpdateDownload.Fetch(update.AssetUrl, archive, update.AssetSize,
                    progress, cancel, expectedDigest: update.AssetDigest))
                {
                    LastError = UpdateDownload.LastError ?? "the download failed";
                    return false;
                }
                Directory.CreateDirectory(StagedBuild);
                if (zip)
                {
                    ZipFile.ExtractToDirectory(archive, StagedBuild, overwriteFiles: true);
                }
                else
                {
                    using FileStream compressed = File.OpenRead(archive);
                    using var plain = new GZipStream(compressed, CompressionMode.Decompress);
                    // The tar reader is what carries the executable bit across;
                    // a zip has none to carry, which is why Windows ships one.
                    TarFile.ExtractToDirectory(plain, StagedBuild, overwriteFiles: true);
                }
                File.Delete(archive);
                string binary = Path.Combine(StagedBuild, UpdateCheck.BinaryName());
                if (!File.Exists(binary))
                {
                    LastError = $"the package does not contain {UpdateCheck.BinaryName()}";
                    return false;
                }
                MakeExecutable(binary);
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Console.WriteLine($"[update] could not stage the update: {ex}");
                return false;
            }
        }

        /// <summary>
        /// Start the unpacked build in its copying mode and return.
        ///
        /// The caller's next act must be to exit: the copy waits for this
        /// process to be gone before it touches anything, and a launcher that
        /// stayed open would leave it waiting until its own deadline.
        /// </summary>
        /// <param name="relaunchArgs">
        /// What to start the updated build with, or null for nothing -- which
        /// is right for the launcher, where a bare invocation is the front
        /// screen and is exactly where the player was.
        ///
        /// It is not right for anything else. A dedicated server is the same
        /// binary told what to be by its command line, so restarting it bare
        /// does not restart the server: it opens a launcher, or a console
        /// menu, on a machine with nobody at it, and the port stays shut until
        /// somebody notices. Whatever was typed has to come back.
        /// </param>
        public static bool Launch(IReadOnlyList<string>? relaunchArgs = null)
        {
            try
            {
                string binary = Path.Combine(StagedBuild, UpdateCheck.BinaryName());
                var start = new ProcessStartInfo(binary)
                {
                    WorkingDirectory = StagedBuild,
                    UseShellExecute = false
                };
                start.ArgumentList.Add("-" + ApplyFlag);
                start.ArgumentList.Add(AppContext.BaseDirectory);
                start.ArgumentList.Add(Environment.ProcessId.ToString());
                if (relaunchArgs != null)
                {
                    // Last, and behind a separator, because everything before
                    // it is read by position: the copying half takes the two
                    // values it needs and hands the rest to the build it
                    // starts.
                    start.ArgumentList.Add(RelaunchSeparator);
                    for (int i = 0; i < relaunchArgs.Count; i++)
                    {
                        start.ArgumentList.Add(relaunchArgs[i]);
                    }
                }
                return Process.Start(start) != null;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Console.WriteLine($"[update] could not start the update: {ex}");
                return false;
            }
        }

        /// <summary>
        /// The copying half, in the new build: wait for the old one to go,
        /// copy over it, start it again.
        ///
        /// Console output rather than a window on purpose. This runs for about
        /// a second between one launcher closing and the next opening, and a
        /// window in the middle of that would be a flash nobody can read; what
        /// it is for is the log somebody reads when the game did not come
        /// back.
        /// </summary>
        /// <summary>
        /// Marks the end of what <see cref="Apply"/> reads by position and the
        /// start of what it passes on. A literal rather than a dash-flag so it
        /// cannot collide with an argument the server was actually given.
        /// </summary>
        public const string RelaunchSeparator = "--relaunch";

        public static int Apply(string target, int waitFor,
            IReadOnlyList<string>? relaunchArgs = null)
        {
            Console.WriteLine($"[update] applying to {target}");
            WaitForExit(waitFor);
            string source = AppContext.BaseDirectory;
            try
            {
                EnsureReleaseManifest(source);
                RemoveObsoleteReleaseFiles(source, target);
                Copy(source, target);
            }
            catch (Exception ex)
            {
                // Half a copy is the one outcome worth being loud about: the
                // installation may be a mix of two builds, and the staged one
                // is still on disk to finish by hand.
                Console.WriteLine($"[update] the copy failed: {ex.Message}");
                Console.WriteLine($"[update] the new build is in {source} -- "
                    + $"copy it over {target} by hand");
                return 1;
            }
            try
            {
                string binary = Path.Combine(target, UpdateCheck.BinaryName());
                MakeExecutable(binary);
                var restart = new ProcessStartInfo(binary)
                {
                    WorkingDirectory = target,
                    UseShellExecute = false
                };
                for (int i = 0; relaunchArgs != null && i < relaunchArgs.Count; i++)
                {
                    restart.ArgumentList.Add(relaunchArgs[i]);
                }
                Process.Start(restart);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[update] updated, but could not restart: {ex.Message}");
                return 1;
            }
            Console.WriteLine("[update] done");
            return 0;
        }

        /// <summary>
        /// Wait for the old process to be gone, and give up rather than hang.
        ///
        /// Thirty seconds is far longer than a launcher takes to close and
        /// short enough that a process which is never going to exit -- one
        /// stuck on a dialog, one already replaced by something else with the
        /// same id -- does not leave this waiting for ever with nothing on
        /// screen.
        /// </summary>
        private static void WaitForExit(int pid)
        {
            try
            {
                using Process old = Process.GetProcessById(pid);
                if (!old.WaitForExit(30_000))
                {
                    Console.WriteLine($"[update] process {pid} is still running; carrying on");
                }
            }
            catch (ArgumentException)
            {
                // Already gone, which is the normal case: it exits the moment
                // it has started this one.
            }
            // Windows keeps a file handle a moment past exit, and virus
            // scanners keep it longer. The copy retries anyway; this is the
            // cheap part of not needing to.
            Thread.Sleep(400);
        }

        private static void Copy(string source, string target)
        {
            foreach (string path in Directory.EnumerateFiles(source, "*",
                SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(source, path);
                string destination = Path.Combine(target, relative);
                string? directory = Path.GetDirectoryName(destination);
                if (!String.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                CopyWithRetries(path, destination);
            }
        }

        /// <summary>
        /// A file that is still held is a file that will be free in a moment,
        /// not a failed update. The binary itself is the one this happens to.
        /// </summary>
        private static void CopyWithRetries(string from, string to)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    File.Copy(from, to, overwrite: true);
                    return;
                }
                catch (IOException) when (attempt < 20)
                {
                    Thread.Sleep(250);
                }
                catch (UnauthorizedAccessException) when (attempt < 20)
                {
                    Thread.Sleep(250);
                }
            }
        }

        /// <summary>
        /// Remove what a previous update left behind.
        ///
        /// Called at startup, because the copying process cannot delete the
        /// directory it is running from: by the time anybody could, the
        /// program doing it is the one that was just installed.
        /// </summary>
        public static void Clean()
        {
            if (OperatingSystem.IsMacOS()) { return; }
            try
            {
                if (Directory.Exists(Staging))
                {
                    Directory.Delete(Staging, recursive: true);
                }
            }
            catch (Exception)
            {
                // Litter, not a failure. The next stage overwrites it.
            }
        }

        public const string ReleaseManifestName = ".project-prime-files.json";
        private const int ReleaseManifestVersion = 1;

        private sealed record ReleaseManifest(
            int Version,
            string[] Files,
            Dictionary<string, string>? Hashes = null);

        /// <summary>
        /// Make staged/fresh packages self-describing. New release archives
        /// already contain this file, but generating it here keeps upgrades
        /// from older packages safe too.
        /// </summary>
        private static void EnsureReleaseManifest(string root)
        {
            string path = Path.Combine(root, ReleaseManifestName);
            if (File.Exists(path)) return;
            var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(file => Path.GetRelativePath(root, file).Replace('\\', '/'))
                .Where(file => !String.Equals(file, ReleaseManifestName,
                    StringComparison.OrdinalIgnoreCase))
                .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var hashes = files.ToDictionary(
                relative => relative,
                relative => HashFile(Path.Combine(root,
                    relative.Replace('/', Path.DirectorySeparatorChar))),
                StringComparer.OrdinalIgnoreCase);
            File.WriteAllText(path, JsonSerializer.Serialize(
                new ReleaseManifest(ReleaseManifestVersion, files, hashes)));
        }

        private static ReleaseManifest? ReadReleaseManifest(string root)
        {
            try
            {
                string path = Path.Combine(root, ReleaseManifestName);
                if (!File.Exists(path)) return null;
                ReleaseManifest? manifest = JsonSerializer.Deserialize<ReleaseManifest>(
                    File.ReadAllText(path));
                return manifest?.Version == ReleaseManifestVersion ? manifest : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// Delete only files the previous release explicitly owned. Player
        /// data is never inferred from absence in the new archive.
        /// </summary>
        internal static void RemoveObsoleteReleaseFiles(string source, string target)
        {
            ReleaseManifest? next = ReadReleaseManifest(source);
            if (next == null) return;
            var keep = next.Files.ToHashSet(StringComparer.OrdinalIgnoreCase);
            ReleaseManifest? previous = ReadReleaseManifest(target);
            if (previous != null)
            {
                foreach (string relative in previous.Files)
                {
                    if (keep.Contains(relative)) continue;
                    DeleteOwnedFile(target, relative);
                }
                RemoveEmptyReleaseDirectories(target, previous.Files, keep);
                return;
            }

            // Pre-manifest installs: remove only old executable/runtime names
            // Project Prime itself has used. Never sweep arbitrary files.
            foreach (string path in Directory.EnumerateFiles(target, "*",
                SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(path);
                string relative = Path.GetRelativePath(target, path).Replace('\\', '/');
                if (keep.Contains(relative)) continue;
                bool legacy = name.Equals("MphRead", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("MphRead.exe", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("FruityPrime", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("FruityPrime.exe", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("PrimeHuntersOnline", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("PrimeHuntersOnline.exe", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("MphRead.", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("FruityPrime.", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("PrimeHuntersOnline.", StringComparison.OrdinalIgnoreCase);
                if (legacy)
                {
                    try { File.Delete(path); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
        }

        private static void DeleteOwnedFile(string root, string relative)
        {
            try
            {
                string fullRoot = Path.GetFullPath(root);
                string path = Path.GetFullPath(Path.Combine(root,
                    relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!path.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) return;
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or ArgumentException) { }
        }

        private static void RemoveEmptyReleaseDirectories(string target,
            IEnumerable<string> previous, HashSet<string> keep)
        {
            var directories = previous.Where(path => !keep.Contains(path))
                .Select(path => Path.GetDirectoryName(path.Replace('/',
                    Path.DirectorySeparatorChar)))
                .Where(path => !String.IsNullOrEmpty(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(path => path!.Length);
            foreach (string? relative in directories)
            {
                try
                {
                    string directory = Path.Combine(target, relative!);
                    if (Directory.Exists(directory)
                        && !Directory.EnumerateFileSystemEntries(directory).Any())
                    {
                        Directory.Delete(directory);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }

        public static string VerifyInstallation()
        {
            ReleaseManifest? manifest = ReadReleaseManifest(AppContext.BaseDirectory);
            if (manifest == null)
            {
                return "No release manifest yet. The next in-app update will create one.";
            }
            int missing = 0, changed = 0;
            foreach (string relative in manifest.Files)
            {
                string path = Path.Combine(AppContext.BaseDirectory,
                    relative.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path))
                {
                    missing++;
                    continue;
                }
                if (manifest.Hashes != null
                    && manifest.Hashes.TryGetValue(relative, out string? expected))
                {
                    try
                    {
                        if (!String.Equals(HashFile(path), expected,
                            StringComparison.OrdinalIgnoreCase))
                        {
                            changed++;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        changed++;
                    }
                }
            }
            if (missing == 0 && changed == 0)
            {
                return manifest.Hashes == null
                    ? $"Release files verified ({manifest.Files.Length} files present; legacy manifest has no hashes)."
                    : $"Release files verified ({manifest.Files.Length} SHA-256 checks passed).";
            }
            return $"{missing} release file(s) missing; {changed} file(s) failed SHA-256 verification.";
        }

        private static string HashFile(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }

        private static void MakeExecutable(string path)
        {
            if (OperatingSystem.IsWindows())
            {
                return;
            }
            try
            {
                // A zip carries no mode bits and a tar does; setting it either
                // way costs one syscall and removes the difference.
                File.SetUnixFileMode(path, File.GetUnixFileMode(path)
                    | UnixFileMode.UserExecute | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherExecute);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[update] could not make {path} executable: {ex.Message}");
            }
        }
    }
}
