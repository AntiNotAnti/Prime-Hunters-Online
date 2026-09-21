using System;
using System.Globalization;
using System.IO;
using MphRead.Mods;

namespace MphRead.Mods.Launcher
{
    /// <summary>
    /// Launcher-only preferences, kept in their own file beside the
    /// executable.
    ///
    /// Deliberately not folded into upstream's MenuSettings: that type is
    /// serialized by GameState and gains fields as upstream develops, so
    /// adding mod-specific keys to it would guarantee a merge conflict. A
    /// separate file costs nothing and keeps the fetch clean.
    /// </summary>
    public static class LauncherPrefs
    {
        /// <summary>
        /// Where launcher.txt lives: Application Support on macOS and beside
        /// the executable in portable Windows/Linux installs. Android's head
        /// points this at the app's writable data directory before any reads.
        /// </summary>
        public static string Directory { get; set; } = Platform.AppPaths.UserDataDirectory;

        private static string Path => System.IO.Path.Combine(Directory, "launcher.txt");

        /// <summary>
        /// The project's own server, so that a fresh install can press "play
        /// online" and be in a match without being asked for an address it has
        /// no way to know. Typing another one over it is one field on the front
        /// screen, and whatever was typed is what gets saved here.
        ///
        /// The address, not the hostname: the hostname is for the people
        /// working on this, and a name that resolves somewhere else later
        /// would send every copy of the launcher with it.
        /// </summary>
        public const string DefaultServer = "51.161.113.128";
        private const string LegacyDefaultServer = "89.160.162.50";

        public static string ServerAddress { get; set; } = DefaultServer;
        public static int ServerPort { get; set; } = Network.NetConfig.DefaultPort;

        /// <summary>
        /// The directory the server browser asks. Project Prime currently runs
        /// the public directory on the same host as the default game server.
        /// Existing installs using the former hostname are migrated on load.
        /// </summary>
        private const string LegacyDefaultMasterHost = "net.livetek.fr";
        public static string MasterHost { get; set; } = Network.NetMasterConfig.DefaultHost;
        public static int MasterPort { get; set; } = Network.NetMasterConfig.DefaultPort;
        public static int LastRole { get; set; }
        public static string PlayerName { get; set; } = "Player";
        /// <summary>Hunter last chosen, possibly <see cref="Hunter.Random"/>.</summary>
        public static Hunter LastHunter { get; set; } = Hunter.Samus;

        /// <summary>
        /// Which of the hunter's four suits this player wears, 0-3.
        ///
        /// A preference rather than a per-launch question, which is why it
        /// sits here beside the name and not on the Host and Join cards: it
        /// is what you look like, and nobody wants to answer it twice a
        /// session. Team modes ignore it -- the team's own two palettes are
        /// the point there -- and a match where somebody in a lower slot has
        /// already taken this suit on this hunter moves it along by one. See
        /// <see cref="Network.PlayerColors"/>.
        /// </summary>
        public static int LastColor { get; set; }
        /// <summary>Bots in an offline match.</summary>
        public static int Bots { get; set; } = 3;
        /// <summary>0 easy, 1 normal, 2 hard, 3 insane -- PlayerEntity.BotLevel.</summary>
        public static int BotLevel { get; set; } = 1;
        public static int HostPort { get; set; } = Network.NetConfig.DefaultPort;

        /// <summary>
        /// Whether a hosted game announces itself to the directory.
        ///
        /// On by default -- a game nobody can find is a game nobody joins --
        /// but it does publish this machine's address on a public list, which
        /// is why it is a switch on the card rather than a decision made for
        /// the person hosting.
        /// </summary>
        public static bool ListHostedGame { get; set; } = true;

        /// <summary>
        /// Whether "Host a game" asks the directory to run the match instead
        /// of running it on this machine.
        ///
        /// The default, because it is the one that works: a server on a home
        /// PC is unreachable from outside unless UDP is forwarded to it, and
        /// asking somebody to configure their router is asking most people not
        /// to play.
        /// </summary>
        public static bool HostOnMaster { get; set; } = true;
        /// <summary>Last LaunchKind, so the front screen can offer it again.</summary>
        public static int LastKind { get; set; }

        /// <summary>
        /// Whether the front screen looks for a new release.
        ///
        /// Looks only. Finding one puts "Update now" on the screen, and that
        /// opens the release page in a browser; the download and the unpacking
        /// are the player's. On by default because a server refuses a client on
        /// a different protocol version outright, so an out-of-date copy is not
        /// a slightly worse copy, it is one that cannot join anything -- and
        /// nobody should have to work that out from a failed connection.
        /// </summary>
        public static bool AutoUpdate { get; set; } = true;

        /// <summary>
        /// Suppress launcher transition motion while keeping hover/focus state changes.
        /// </summary>
        public static bool ReduceMotion { get; set; }

        /// <summary>
        /// How the game window opens. Kept here rather than in MenuSettings
        /// for the same reason as everything else in this file, and read by
        /// Mods.WindowMode, which is where the window itself lives.
        /// </summary>
        public static WindowStartMode WindowMode { get; set; } = WindowStartMode.Windowed;

        /// <summary>
        /// The size and corner the game window last had, or zeroes for a
        /// first run.
        ///
        /// The *windowed* geometry, never fullscreen's: a window remembered at
        /// the size of the monitor and then opened with a title bar is a
        /// window taller than the screen, and the thing worth putting back is
        /// what the player dragged it to.
        ///
        /// The position travels with the size because half of it is no
        /// feature: a window that comes back the right shape in the middle of
        /// the screen has still been moved. Both are checked against the
        /// displays that exist now before they are used -- see
        /// <see cref="Mods.WindowGeometry"/> -- because a monitor that has been
        /// unplugged is a window nobody can reach.
        /// </summary>
        public static int WindowWidth { get; set; }
        public static int WindowHeight { get; set; }

        /// <summary>Where the window's client area started. Meaningless while both sizes are 0.</summary>
        public static int WindowX { get; set; }
        public static int WindowY { get; set; }

        /// <summary>
        /// Whether it was maximized, which is not a size.
        ///
        /// Kept apart because restoring a maximized window by its rectangle
        /// gets it visibly wrong: it comes back filling the screen but not
        /// *maximized*, so the button says restore, dragging it does nothing
        /// expected, and it does not follow a change of resolution. The
        /// rectangle underneath is still saved, so un-maximizing lands where
        /// it used to.
        /// </summary>
        public static bool WindowMaximized { get; set; }

        /// <summary>
        /// Whether the program writes a file of everything it can say about
        /// itself. See <see cref="Mods.DebugLog"/>.
        ///
        /// On, everywhere, and switchable off in the corner of the front
        /// screen.
        ///
        /// It used to be off and asked for, which reads as the careful choice
        /// and is the wrong one: the reports it exists to answer -- a crash
        /// while a map loads, on a machine nobody here can plug in -- are
        /// exactly the ones where nobody thought to turn it on beforehand, and
        /// asking a player to reproduce a crash with logging enabled is asking
        /// them to hit it twice. It costs a directory that grows and a lock on
        /// every line printed; that is a cheaper price than a bug report
        /// nobody can act on.
        /// </summary>
        public static bool DebugLogs { get; set; } = true;

        /// <summary>Replay library soft limit. Zero means unlimited.</summary>
        public static int ReplayStorageLimitGb { get; set; } = 10;
        public static bool ReplayAutoPrune { get; set; } = true;
        /// <summary>Full recordings prune first; clips remain protected unless opted in.</summary>
        public static bool ReplayDeleteClips { get; set; }
        public static bool KillCamEnabled { get; set; } = true;
        public static bool FinalKillCamEnabled { get; set; } = true;


        public static void Load()
        {
            if (!File.Exists(Path))
            {
                return;
            }
            try
            {
                foreach (string raw in File.ReadAllLines(Path))
                {
                    string line = raw.Trim();
                    int split = line.IndexOf('=');
                    if (line.Length == 0 || line[0] == '#' || split <= 0)
                    {
                        continue;
                    }
                    string key = line[..split].Trim();
                    string value = line[(split + 1)..].Trim();
                    switch (key)
                    {
                        case "bright_skins":
                            if (Boolean.TryParse(value, out bool brightSkins))
                            {
                                RenderOptions.BrightSkins = brightSkins;
                            }
                            break;
                        case "bright_skin_style":
                            if (Enum.TryParse(value, ignoreCase: true, out PlayerSkinStyle skinStyle)
                                && Enum.IsDefined(skinStyle))
                            {
                                RenderOptions.BrightSkinStyle = skinStyle;
                            }
                            break;
                        case "player_outline":
                            if (Enum.TryParse(value, ignoreCase: true, out PlayerOutlineStyle outlineStyle)
                                && Enum.IsDefined(outlineStyle))
                            {
                                RenderOptions.PlayerOutline = outlineStyle;
                            }
                            break;
                        case "player_outline_width":
                            if (Int32.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture,
                                out int outlineWidth))
                            {
                                RenderOptions.PlayerOutlineWidth = outlineWidth;
                            }
                            break;
                        case "server_address":
                            ServerAddress = value.Equals(LegacyDefaultServer, StringComparison.OrdinalIgnoreCase)
                                ? DefaultServer
                                : value;
                            break;
                        case "master_host":
                            if (value.Length > 0)
                            {
                                MasterHost = value.Equals(LegacyDefaultMasterHost,
                                    StringComparison.OrdinalIgnoreCase)
                                    ? Network.NetMasterConfig.DefaultHost
                                    : value;
                            }
                            break;
                        case "master_port":
                            if (Int32.TryParse(value, NumberStyles.Integer,
                                CultureInfo.InvariantCulture, out int masterPort)
                                && masterPort > 0 && masterPort <= 65535)
                            {
                                MasterPort = masterPort;
                            }
                            break;
                        case "server_port":
                            if (Int32.TryParse(value, NumberStyles.Integer,
                                CultureInfo.InvariantCulture, out int port))
                            {
                                ServerPort = port;
                            }
                            break;
                        case "player_name":
                            if (value.Length > 0)
                            {
                                PlayerName = value;
                            }
                            break;
                        case "last_role":
                            if (Int32.TryParse(value, NumberStyles.Integer,
                                CultureInfo.InvariantCulture, out int role))
                            {
                                LastRole = role;
                            }
                            break;
                        case "hunter":
                            if (Enum.TryParse(value, ignoreCase: true, out Hunter hunter))
                            {
                                LastHunter = hunter;
                            }
                            break;
                        case "color":
                            if (Int32.TryParse(value, NumberStyles.Integer,
                                CultureInfo.InvariantCulture, out int color))
                            {
                                LastColor = Network.PlayerColors.Clamp(color);
                            }
                            break;
                        case "bots":
                            if (Int32.TryParse(value, NumberStyles.Integer,
                                CultureInfo.InvariantCulture, out int bots))
                            {
                                Bots = bots;
                            }
                            break;
                        case "bot_level":
                            if (Int32.TryParse(value, NumberStyles.Integer,
                                CultureInfo.InvariantCulture, out int level))
                            {
                                BotLevel = level;
                            }
                            break;
                        case "host_on_master":
                            if (Boolean.TryParse(value, out bool hostOnMaster))
                            {
                                HostOnMaster = hostOnMaster;
                            }
                            break;
                        case "list_hosted":
                            if (Boolean.TryParse(value, out bool listHosted))
                            {
                                ListHostedGame = listHosted;
                            }
                            break;
                        case "host_port":
                            if (Int32.TryParse(value, NumberStyles.Integer,
                                CultureInfo.InvariantCulture, out int hostPort))
                            {
                                HostPort = hostPort;
                            }
                            break;
                        case "window_mode":
                            WindowMode = Mods.WindowMode.Parse(value, WindowMode);
                            break;
                        case "window_size":
                            ReadPair(value, out int width, out int height);
                            WindowWidth = width;
                            WindowHeight = height;
                            break;
                        case "window_pos":
                            ReadPair(value, out int x, out int y);
                            WindowX = x;
                            WindowY = y;
                            break;
                        case "window_maximized":
                            if (Boolean.TryParse(value, out bool maximized))
                            {
                                WindowMaximized = maximized;
                            }
                            break;
                        case "auto_update":
                            if (Boolean.TryParse(value, out bool autoUpdate))
                            {
                                AutoUpdate = autoUpdate;
                            }
                            break;
                        case "debug_logs":
                            if (Boolean.TryParse(value, out bool debugLogs))
                            {
                                DebugLogs = debugLogs;
                            }
                            break;
                        case "reduce_motion":
                            if (Boolean.TryParse(value, out bool reduceMotion))
                            {
                                ReduceMotion = reduceMotion;
                            }
                            break;
                        case "replay_storage_gb":
                            if (Int32.TryParse(value, NumberStyles.Integer,
                                CultureInfo.InvariantCulture, out int replayStorage)
                                && replayStorage is >= 0 and <= 1024)
                            {
                                ReplayStorageLimitGb = replayStorage;
                            }
                            break;
                        case "replay_auto_prune":
                            if (Boolean.TryParse(value, out bool replayPrune))
                            {
                                ReplayAutoPrune = replayPrune;
                            }
                            break;
                        case "replay_delete_clips":
                            if (Boolean.TryParse(value, out bool replayDeleteClips))
                            {
                                ReplayDeleteClips = replayDeleteClips;
                            }
                            break;
                        case "kill_cam":
                            if (Boolean.TryParse(value, out bool killCam))
                            {
                                KillCamEnabled = killCam;
                            }
                            break;
                        case "final_kill_cam":
                            if (Boolean.TryParse(value, out bool finalKillCam))
                            {
                                FinalKillCamEnabled = finalKillCam;
                            }
                            break;
                        case "last_kind":
                            if (Int32.TryParse(value, NumberStyles.Integer,
                                CultureInfo.InvariantCulture, out int kind))
                            {
                                LastKind = kind;
                            }
                            break;
                    }
                }
            }
            catch (Exception)
            {
                // Preferences are a convenience; a unreadable file must not
                // stop the launcher from opening.
            }
        }

        /// <summary>
        /// "1280x768" or "40,60" -- one parser for both, since the only
        /// difference is which character is in the middle. Leaves both at zero
        /// on anything it does not understand, which is the value that means
        /// "no saved geometry".
        /// </summary>
        private static void ReadPair(string value, out int first, out int second)
        {
            first = 0;
            second = 0;
            int at = value.IndexOfAny(new[] { 'x', 'X', ',' });
            if (at <= 0)
            {
                return;
            }
            if (Int32.TryParse(value[..at], NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int a)
                && Int32.TryParse(value[(at + 1)..], NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int b))
            {
                first = a;
                second = b;
            }
        }

        public static void Save()
        {
            try
            {
                File.WriteAllLines(Path, new[]
                {
                    $"# {Branding.Name} launcher preferences.",
                    $"server_address={ServerAddress}",
                    $"server_port={ServerPort.ToString(CultureInfo.InvariantCulture)}",
                    $"master_host={MasterHost}",
                    $"master_port={MasterPort.ToString(CultureInfo.InvariantCulture)}",
                    $"last_role={LastRole.ToString(CultureInfo.InvariantCulture)}",
                    $"player_name={PlayerName}",
                    $"hunter={LastHunter}",
                    $"color={LastColor.ToString(CultureInfo.InvariantCulture)}",
                    $"bots={Bots.ToString(CultureInfo.InvariantCulture)}",
                    $"bot_level={BotLevel.ToString(CultureInfo.InvariantCulture)}",
                    $"host_port={HostPort.ToString(CultureInfo.InvariantCulture)}",
                    $"list_hosted={ListHostedGame.ToString().ToLowerInvariant()}",
                    $"host_on_master={HostOnMaster.ToString().ToLowerInvariant()}",
                    $"last_kind={LastKind.ToString(CultureInfo.InvariantCulture)}",
                    $"auto_update={AutoUpdate.ToString().ToLowerInvariant()}",
                    $"debug_logs={DebugLogs.ToString().ToLowerInvariant()}",
                    $"bright_skins={RenderOptions.BrightSkins.ToString().ToLowerInvariant()}",
                    $"bright_skin_style={RenderOptions.BrightSkinStyle.ToString().ToLowerInvariant()}",
                    $"player_outline={RenderOptions.PlayerOutline.ToString().ToLowerInvariant()}",
                    $"player_outline_width={RenderOptions.PlayerOutlineWidth.ToString(CultureInfo.InvariantCulture)}",
                    $"reduce_motion={ReduceMotion.ToString().ToLowerInvariant()}",
                    $"replay_storage_gb={ReplayStorageLimitGb.ToString(CultureInfo.InvariantCulture)}",
                    $"replay_auto_prune={ReplayAutoPrune.ToString().ToLowerInvariant()}",
                    $"replay_delete_clips={ReplayDeleteClips.ToString().ToLowerInvariant()}",
                    $"kill_cam={KillCamEnabled.ToString().ToLowerInvariant()}",
                    $"final_kill_cam={FinalKillCamEnabled.ToString().ToLowerInvariant()}",
                    $"window_mode={(WindowMode == WindowStartMode.BorderlessFullscreen ? "borderless" : "windowed")}",
                    $"window_size={WindowWidth.ToString(CultureInfo.InvariantCulture)}x"
                        + WindowHeight.ToString(CultureInfo.InvariantCulture),
                    $"window_pos={WindowX.ToString(CultureInfo.InvariantCulture)},"
                        + WindowY.ToString(CultureInfo.InvariantCulture),
                    $"window_maximized={WindowMaximized.ToString().ToLowerInvariant()}"
                });
            }
            catch (Exception)
            {
                // Same rationale as Load: never block launching over this.
            }
        }
    }
}
