using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Formats.Tar;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using MphRead.Mods.Launcher;
using MphRead.Mods.Update;

namespace MphRead.Mods.Network
{
    /// <summary>
    /// A dedicated server run on the player's own machine, started and joined
    /// from the launcher without anybody opening a terminal.
    ///
    /// This is the other half of the answer to "I want to run a server", and
    /// the half nothing in the launcher could reach before: asking the
    /// directory to run one (<see cref="NetMasterClient.RequestGame"/>) needs
    /// no port forwarding and is therefore the default, but it also means the
    /// match is somebody else's process on somebody else's box, capped by that
    /// directory's port range and reaped when it empties. A server here is the
    /// player's: it keeps their rotation, it stays up, and it is listed like
    /// any other.
    ///
    /// **Which binary runs it matters more than it looks.**
    /// <c>NetConfig.ProtocolVersion</c> makes a server refuse a client on a
    /// different build outright at Hello, so a server started from a *freshly
    /// downloaded* package on a client that is one release behind is a server
    /// that client cannot join -- the exact failure the download was meant to
    /// prevent. Release builds therefore download the server package from the
    /// same release tag as the client, while local/development builds use the
    /// latest package as a fallback. This build's own binary is still preferred
    /// wherever it can run a server, which is everywhere but Android.
    /// </summary>
    public static class LocalServer
    {
        /// <summary>Where a downloaded server package is unpacked.</summary>
        public static string Directory =>
            Path.Combine(LauncherPrefs.Directory, "server");

        /// <summary>Why the last attempt produced nothing.</summary>
        public static string? LastError { get; private set; }
        public static Guid OwnerToken { get; private set; }

        /// <summary>
        /// The release tag of the package last installed, or "".
        ///
        /// Release builds install the package from their own release tag so
        /// client and server stay on the same protocol. Local/development
        /// builds have no tag to match and use the latest package fallback;
        /// naming the installed tag still makes that case diagnosable.
        /// </summary>
        public static string InstalledTag { get; private set; } = "";

        /// <summary>The process started by <see cref="Start"/>, while it lives.</summary>
        public static Process? Running { get; private set; }

        /// <summary>
        /// What would be started, or null when nothing here can start a
        /// server.
        ///
        /// **On Windows the game binary is not a candidate**, even though it
        /// accepts <c>-server</c>. It is a GUI binary (`WinExe`), so a server
        /// started from it is a process with no console: nothing it logs is
        /// ever seen, and an operator with a server running has no window
        /// saying so and nothing to close. The console binary out of the
        /// server package is what belongs here, and its absence is what the
        /// install mark on the screen is for.
        ///
        /// Everywhere else the game binary *is* the right answer and is tried
        /// first -- it is already a console program there, and it is this
        /// build, which is the protocol-version argument in the class note.
        /// </summary>
        public static ServerBinary? Available()
        {
            if (OperatingSystem.IsWindows())
            {
                // Beside the game first: somebody who unpacked both packages
                // into one folder has it there, and a copy they already have
                // beats one fetched over the network.
                string beside = Path.Combine(AppContext.BaseDirectory,
                    UpdateCheck.ServerBinaryName());
                if (File.Exists(beside))
                {
                    return new ServerBinary(beside, Array.Empty<string>(),
                        AppContext.BaseDirectory, downloaded: false);
                }
            }
            else
            {
                ServerBinary? own = OwnBinary();
                if (own != null)
                {
                    return own;
                }
            }
            string installed = Path.Combine(Directory, UpdateCheck.ServerBinaryName());
            if (File.Exists(installed))
            {
                return new ServerBinary(installed, Array.Empty<string>(), Directory,
                    downloaded: true);
            }
            return null;
        }

        public static bool Ready => Available() != null;

        /// <summary>
        /// This program, started again with <c>-server</c>.
        ///
        /// Two shapes, because a published build and a developer's build are
        /// launched differently: a self-contained release has an apphost and
        /// is simply run, while a framework-dependent one is being run *by*
        /// <c>dotnet</c> and has to be started the same way -- its apphost
        /// cannot find a runtime here (see the DOTNET_ROOT note in CLAUDE.md),
        /// so spawning it directly would die with "You must install .NET".
        /// </summary>
        private static ServerBinary? OwnBinary()
        {
            // An app is not an executable a process can spawn.
            if (OperatingSystem.IsAndroid())
            {
                return null;
            }
            string? exe = Environment.ProcessPath;
            if (String.IsNullOrEmpty(exe) || !File.Exists(exe))
            {
                return null;
            }
            string directory = AppContext.BaseDirectory;
            string name = Path.GetFileNameWithoutExtension(exe);
            if (name.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                string dll = Path.Combine(directory, Mods.Branding.FileName + ".dll");
                if (!File.Exists(dll))
                {
                    return null;
                }
                return new ServerBinary(exe, new[] { dll }, Platform.AppPaths.UserDataDirectory, downloaded: false);
            }
            return new ServerBinary(exe, Array.Empty<string>(), Platform.AppPaths.UserDataDirectory, downloaded: false);
        }

        // --------------------------------------------------------- installing

        /// <summary>
        /// Whether a package could be fetched at all: a platform one is
        /// published for. macOS is the exception and gets no server package
        /// out of release.yml, so offering the download there would be
        /// offering a 404.
        /// </summary>
        public static bool CanInstall => UpdateCheck.ServerRid().Length > 0;

        /// <summary>
        /// Fetch this client's matching release server package (or the latest
        /// package for a local/development build) and unpack it into
        /// <see cref="Directory"/>, then put a copy of <c>paths.txt</c> beside
        /// it.
        ///
        /// The copy is the part that is easy to forget and impossible to
        /// diagnose: a server runs the match itself, so it needs the extracted
        /// game files, and it looks for <c>paths.txt</c> *next to its own
        /// binary* rather than in the working directory. Without it the server
        /// refuses to start with a message nobody sees, because it is a
        /// process with no console attached to it.
        /// </summary>
        public static bool Install(Action<float>? progress = null,
            CancellationToken cancel = default)
        {
            LastError = null;
            if (!CanInstall)
            {
                LastError = "there is no dedicated-server package for this platform";
                return false;
            }
            UpdateInfo? found = UpdateCheck.ServerAsset(cancel);
            if (found == null)
            {
                LastError = UpdateCheck.LastReason ?? "no server package was found";
                return false;
            }
            UpdateInfo package = found.Value;
            if (!UpdateDownload.SupportsDigest(package.AssetDigest))
            {
                LastError = "the server release asset has no supported SHA-256 digest";
                return false;
            }
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                bool zip = package.AssetName.EndsWith(".zip",
                    StringComparison.OrdinalIgnoreCase);
                string archive = Path.Combine(Directory,
                    zip ? "package.zip" : "package.tar.gz");
                if (!UpdateDownload.Fetch(package.AssetUrl, archive, package.AssetSize,
                    progress, cancel, expectedDigest: package.AssetDigest))
                {
                    LastError = UpdateDownload.LastError ?? "the download failed";
                    return false;
                }
                if (zip)
                {
                    ZipFile.ExtractToDirectory(archive, Directory, overwriteFiles: true);
                }
                else
                {
                    using FileStream compressed = File.OpenRead(archive);
                    using var plain = new GZipStream(compressed, CompressionMode.Decompress);
                    // The tar reader is what carries the executable bit
                    // across; a zip has none to carry.
                    TarFile.ExtractToDirectory(plain, Directory, overwriteFiles: true);
                }
                File.Delete(archive);
                string binary = Path.Combine(Directory, UpdateCheck.ServerBinaryName());
                if (!File.Exists(binary))
                {
                    LastError = $"the package does not contain {UpdateCheck.ServerBinaryName()}";
                    return false;
                }
                MakeExecutable(binary);
                InstalledTag = package.Tag;
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return false;
            }
        }

        private static void MakeExecutable(string path)
        {
            if (OperatingSystem.IsWindows())
            {
                return;
            }
            try
            {
                File.SetUnixFileMode(path, File.GetUnixFileMode(path)
                    | UnixFileMode.UserExecute | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherExecute);
            }
            catch (Exception)
            {
                // A package that unpacked without the bit is worth trying
                // anyway: the failure to start is a clearer report than a
                // refusal here.
            }
        }

        // ----------------------------------------------------------- starting

        /// <summary>
        /// Write the rotation, start the server, and wait until it answers.
        ///
        /// Returns the port it is listening on, or -1 with
        /// <see cref="LastError"/> saying why. Waiting for the answer rather
        /// than returning as soon as the process exists is what makes the join
        /// that follows reliable: the socket binds a moment after the process
        /// does, and loading the first room is slower than either.
        /// </summary>
        public static int Start(string serverName,
            IReadOnlyList<(string RoomKey, GameMode Mode)> rotation,
            int maxPlayers, float timeLimit, int pointGoal,
            string masterHost, int masterPort, bool listed,
            CancellationToken cancel = default, bool lobby = false,
            int? requestedPort = null, Guid? ownerToken = null,
            MatchFormat format = MatchFormat.Auto, bool requireReady = false,
            bool allowJoinInProgress = true, bool friendlyFire = false,
            bool shadowFreeze = true, bool affinityWeapons = false,
            bool waitUntilReady = true)
        {
            LastError = null;
            OwnerToken = lobby
                ? ownerToken ?? new Guid(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16))
                : Guid.Empty;
            ServerBinary? found = Available();
            if (found == null)
            {
                LastError = "there is no server binary on this machine yet";
                return -1;
            }
            ServerBinary binary = found.Value;
            // The game files, checked here rather than left to the child: a
            // server that refuses to start says so on a console this process
            // never gave it, so the refusal would arrive as silence.
            string? problem = GameFiles.Problem();
            if (problem != null)
            {
                LastError = $"a server runs the match itself and needs the game files: {problem}";
                return -1;
            }
            int port = requestedPort ?? FreePort();
            if (port <= 0 || port > 65535)
            {
                LastError = requestedPort.HasValue
                    ? $"requested UDP port {requestedPort.Value} is invalid"
                    : "no free UDP port could be found for a server";
                return -1;
            }
            if (requestedPort.HasValue && !PortAvailable(port))
            {
                LastError = $"requested UDP port {port} is already in use";
                return -1;
            }
            // One file per port. Hosted games may be started side by side by
            // the same directory process; sharing maprotation-launcher.txt
            // lets the second request rewrite the first server's file while
            // it is still starting.
            string rotationPath = Path.Combine(binary.WorkingDirectory,
                $"maprotation-launcher-{port}.txt");
            try
            {
                MapRotation.WriteList(rotationPath, rotation, timeLimit, pointGoal);
                CopyPaths(binary.WorkingDirectory);
            }
            catch (Exception ex)
            {
                LastError = $"the rotation could not be written: {ex.Message}";
                return -1;
            }
            // **Its own window, and its own life.**
            //
            // A server the player started has to outlive the client that
            // started it -- somebody who quits to the front screen, or closes
            // the game, has not asked for the match everybody else is in to
            // end. On Windows that means ShellExecute: it starts the process
            // independently of this one and gives a console binary a console
            // window of its own, which is also the only place the server's log
            // can go in a build that has no console at all. Elsewhere a child
            // already outlives its parent and the binary is already a console
            // program, so there is nothing to arrange.
            bool shell = OperatingSystem.IsWindows();
            var start = new ProcessStartInfo(binary.Executable)
            {
                WorkingDirectory = binary.WorkingDirectory,
                UseShellExecute = shell,
                CreateNoWindow = false
            };
            foreach (string argument in binary.Prefix)
            {
                start.ArgumentList.Add(argument);
            }
            start.ArgumentList.Add("-server");
            if (lobby)
            {
                start.ArgumentList.Add("-lobby");
                start.ArgumentList.Add("-ownertoken");
                start.ArgumentList.Add(OwnerToken.ToString("N"));
            }
            start.ArgumentList.Add("-format");
            start.ArgumentList.Add(format.ToString());
            if (requireReady)
            {
                start.ArgumentList.Add("-requireready");
            }
            if (!allowJoinInProgress)
            {
                start.ArgumentList.Add("-nojoininprogress");
            }
            if (friendlyFire)
            {
                start.ArgumentList.Add("-friendlyfire");
            }
            if (!shadowFreeze)
            {
                start.ArgumentList.Add("-noshadowfreeze");
            }
            if (affinityWeapons)
            {
                start.ArgumentList.Add("-affinityweapons");
            }
            start.ArgumentList.Add("-port");
            start.ArgumentList.Add(port.ToString(CultureInfo.InvariantCulture));
            start.ArgumentList.Add("-players");
            start.ArgumentList.Add(maxPlayers.ToString(CultureInfo.InvariantCulture));
            start.ArgumentList.Add("-servername");
            start.ArgumentList.Add(serverName);
            start.ArgumentList.Add("-rotation");
            start.ArgumentList.Add(rotationPath);
            // Whatever this player joins through, the server reports to. A
            // server listed somewhere the launcher does not ask is a server
            // nobody finds.
            start.ArgumentList.Add("-master");
            start.ArgumentList.Add(masterHost);
            start.ArgumentList.Add("-masterport");
            start.ArgumentList.Add(masterPort.ToString(CultureInfo.InvariantCulture));
            if (!listed)
            {
                start.ArgumentList.Add("-nomaster");
            }
            // It was started with this build's protocol on purpose. Letting it
            // replace itself half an hour later with a release this client
            // cannot speak to would undo that in the one way nobody would
            // think to look for.
            start.ArgumentList.Add("-noautoupdate");
            try
            {
                // The previous one is deliberately left running. Starting a
                // second server is not a request to end the first, and the
                // first may well have people in it -- FreePort has already
                // moved past its port.
                Running = Process.Start(start);
                if (Running == null)
                {
                    LastError = "the server process would not start";
                    return -1;
                }
            }
            catch (Exception ex)
            {
                LastError = $"the server could not be started: {ex.Message}";
                return -1;
            }
            // A pooled/directory-hosted child is launched from another
            // server's packet loop. Waiting here for the child to finish
            // loading would freeze that already-running match. Its client
            // connection retries while the new process boots, and UDP packets
            // sent after bind remain queued until the child reaches its loop.
            if (!waitUntilReady)
            {
                return port;
            }
            // Loading the first room on a cold cache is not fast. Keep the
            // thirty-second promise as a wall-clock deadline: each status
            // probe has its own timeout, so "120 probes plus sleeps" can take
            // minutes when nothing is listening.
            const int startupTimeoutMs = 30_000;
            var readyClock = Stopwatch.StartNew();
            while (!cancel.IsCancellationRequested
                && readyClock.ElapsedMilliseconds < startupTimeoutMs)
            {
                if (Running.HasExited)
                {
                    LastError = "the server stopped while starting up -- its window says "
                        + "why; usually the game files or a port already in use";
                    Running.Dispose();
                    Running = null;
                    return -1;
                }

                int remaining = startupTimeoutMs - (int)readyClock.ElapsedMilliseconds;
                int probeTimeout = Math.Clamp(remaining, 20, 200);
                ServerStatus status = NetStatus.Query("127.0.0.1", port,
                    allowJoinProbe: false, timeoutMs: probeTimeout);
                if (status.Online)
                {
                    if (status.Protocol > 0 && status.Protocol != NetConfig.ProtocolVersion)
                    {
                        LastError = $"the installed server speaks protocol {status.Protocol}, "
                            + $"but this build speaks {NetConfig.ProtocolVersion}; reinstall "
                            + "the server files for this version";
                        Stop();
                        return -1;
                    }
                    return port;
                }

                remaining = startupTimeoutMs - (int)readyClock.ElapsedMilliseconds;
                if (remaining > 0)
                {
                    Thread.Sleep(Math.Min(50, remaining));
                }
            }
            LastError = cancel.IsCancellationRequested
                ? "server startup was cancelled"
                : "the server did not answer in thirty seconds";
            Stop();
            return -1;
        }

        /// <summary>
        /// Put this installation's <c>paths.txt</c> beside a server that is
        /// somewhere else. Nothing to do for the ordinary case, where the
        /// server *is* this installation.
        /// </summary>
        private static void CopyPaths(string directory)
        {
            string source = Path.Combine(GameFiles.Root, "paths.txt");
            string target = Path.Combine(directory, "paths.txt");
            if (!File.Exists(source)
                || Path.GetFullPath(source) == Path.GetFullPath(target))
            {
                return;
            }
            string[] lines = File.ReadAllLines(source);
            for (int i = 0; i < lines.Length; i++)
            {
                // The copy is read from a directory with no files/ beside it.
                int split = lines[i].IndexOf('=');
                string value = split == -1 ? "" : lines[i][(split + 1)..].Trim();
                if (value.Length > 0)
                {
                    lines[i] = $"{lines[i][..split].Trim()}={Path.GetFullPath(value, GameFiles.Root)}";
                }
            }
            File.WriteAllText(target, String.Join(Environment.NewLine, lines));
        }

        /// <summary>
        /// Stop the last server this process started.
        ///
        /// Not called when the launcher closes, and that is the point: a
        /// server is started to outlive the client. This exists for
        /// <c>-hostlocal</c>, which starts one to measure it and has to clean
        /// up after itself.
        /// </summary>
        public static void Stop()
        {
            Process? process = Running;
            Running = null;
            if (process == null)
            {
                return;
            }
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
                process.Dispose();
            }
            catch (Exception)
            {
                // Already gone, or not ours to kill.
            }
        }

        /// <summary>
        /// The game port if it is free, and the next one that is otherwise.
        ///
        /// The default first because it is the port a player will have
        /// forwarded if they forwarded anything, and the one every piece of
        /// documentation names.
        /// </summary>
        private static int FreePort()
        {
            var taken = new HashSet<int>();
            try
            {
                foreach (IPEndPoint endPoint in IPGlobalProperties
                    .GetIPGlobalProperties().GetActiveUdpListeners())
                {
                    taken.Add(endPoint.Port);
                }
            }
            catch (Exception)
            {
                // Not every platform will say. Binding is the real test and
                // is done below anyway.
            }
            for (int port = NetConfig.DefaultPort; port < NetConfig.DefaultPort + 40; port++)
            {
                if (taken.Contains(port) || !CanBind(port))
                {
                    continue;
                }
                return port;
            }
            return -1;
        }

        internal static bool PortAvailable(int port) => CanBind(port);

        private static bool CanBind(int port)
        {
            try
            {
                using var probe = new UdpClient(new IPEndPoint(IPAddress.Any, port));
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
        }
    }

    /// <summary>What starts a server, and how it has to be invoked.</summary>
    public readonly struct ServerBinary
    {
        public ServerBinary(string executable, IReadOnlyList<string> prefix,
            string workingDirectory, bool downloaded)
        {
            Executable = executable;
            Prefix = prefix;
            WorkingDirectory = workingDirectory;
            Downloaded = downloaded;
        }

        public string Executable { get; }

        /// <summary>Arguments before the server's own -- the dll, under <c>dotnet</c>.</summary>
        public IReadOnlyList<string> Prefix { get; }

        /// <summary>
        /// Where it runs, which is also where its <c>paths.txt</c> and its
        /// rotation file have to be: <c>ConsoleSetup.Run</c> makes the
        /// binary's own directory current before anything reads either.
        /// </summary>
        public string WorkingDirectory { get; }

        /// <summary>Whether this came out of a release package rather than being this build.</summary>
        public bool Downloaded { get; }

        public string Describe() => Downloaded
            ? $"the server package in {Path.GetFileName(Path.GetDirectoryName(Executable))}"
            : "this build";
    }
}
