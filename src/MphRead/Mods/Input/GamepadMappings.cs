using System;
using System.IO;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace MphRead.Mods.Input
{
    /// <summary>
    /// Extra controller mappings, for the pads GLFW's own database has never
    /// heard of.
    ///
    /// GLFW carries a snapshot of SDL's controller database, frozen at
    /// whichever version of GLFW the OpenTK redist happens to ship. Everything
    /// released since then -- and every pad that was always too obscure to be
    /// in it -- answers <c>glfwJoystickIsGamepad</c> with false, and a pad that
    /// answers false is one <see cref="GamepadDesktop"/> used to ignore
    /// outright. That is the whole of "my controller does nothing": the device
    /// is plugged in, the operating system sees it, and the game never asks it
    /// anything.
    ///
    /// Two answers, and this file is the first of them. A mapping line is
    /// eleven words of text that says which axis and which button is which,
    /// and SDL's format for it is the one every project shares: a player who
    /// already has a line for their pad -- from Steam, from another game, from
    /// the community database -- can hand it over here rather than wait for a
    /// build. The second answer is <see cref="GamepadDesktop"/>'s raw fallback,
    /// which plays an unmapped pad on a guess; this one plays it correctly.
    ///
    /// Three sources, in the order they are read, later ones winning:
    ///
    /// <list type="bullet">
    /// <item><c>gamecontrollerdb.txt</c> in application resources (beside the executable
    /// for portable builds, Contents/Resources for a macOS app) -- what a
    /// release ships, and where a player who
    /// unzipped the game will naturally drop a file.</item>
    /// <item><c>gamecontrollerdb.txt</c> in the settings directory, beside
    /// <c>controls.txt</c> -- the one that survives reinstalling.</item>
    /// <item><c>SDL_GAMECONTROLLERCONFIG</c>, SDL's own environment variable,
    /// because somebody who has already made their pad work in another game
    /// most likely did it there.</item>
    /// </list>
    ///
    /// Nothing here fails loudly: a missing file is the normal case, and a
    /// malformed line is GLFW's to reject. What it does do is say what it
    /// found, because a mapping file that is being read and a mapping file
    /// that is being ignored look identical from a match.
    /// </summary>
    internal static class GamepadMappings
    {
        private static readonly System.Collections.Generic.Dictionary<string, GamepadCapabilities> MappingCapabilities = new(StringComparer.OrdinalIgnoreCase);
        internal static GamepadCapabilities Capabilities(string guid, GamepadCapabilities fallback)
            => MappingCapabilities.TryGetValue(guid, out var value) ? value : fallback;
        internal static GamepadCapabilities ParseCapabilities(string line)
        {
            bool lx = false, ly = false, rx = false, ry = false, lt = false, rt = false;
            foreach (string part in line.Split(','))
            {
                int split = part.IndexOf(':'); if (split < 0) continue;
                string axis = part[(split + 1)..].TrimStart('+', '-');
                if (!axis.StartsWith('a')) continue;
                switch (part[..split]) { case "leftx": lx = true; break; case "lefty": ly = true; break;
                    case "rightx": rx = true; break; case "righty": ry = true; break;
                    case "lefttrigger": lt = true; break; case "righttrigger": rt = true; break; }
            }
            return (lx && ly ? GamepadCapabilities.AnalogLeftStick : 0) | (rx && ry ? GamepadCapabilities.AnalogRightStick : 0)
                | (lt && rt ? GamepadCapabilities.AnalogTriggers : 0);
        }
        public const string FileName = "gamecontrollerdb.txt";

        private static bool _loaded;
        internal static bool ReloadRequested;
        public static void SaveOverride(string mapping)
        {
            // The wizard constructs one bounded mapping, while the next host poll applies it.
            if (mapping.Length > 4096 || mapping.Contains('\n') || mapping.Contains('\r'))
                throw new ArgumentException("Invalid controller mapping.");
            string path = Path.Combine(Launcher.LauncherPrefs.Directory, FileName);
            string existing = File.Exists(path) ? File.ReadAllText(path) : "";
            Directory.CreateDirectory(Launcher.LauncherPrefs.Directory);
            GamepadProfiles.WriteAtomic(path, ReplaceOverride(existing, mapping));
            _loaded = false; ReloadRequested = true;
        }

        internal static string ReplaceOverride(string existing, string mapping)
        {
            static string Key(string line)
            {
                var parts = line.Split(',');
                if (parts.Length < 3 || parts[0].Length != 32) return "";
                string platform = "";
                foreach (string part in parts) if (part.StartsWith("platform:", StringComparison.Ordinal)) platform = part;
                return parts[0].ToLowerInvariant() + ":" + platform;
            }
            string key = Key(mapping);
            if (key.Length == 0) throw new ArgumentException("Invalid controller mapping GUID.");
            var output = new System.Text.StringBuilder();
            foreach (string line in existing.Replace("\r", "").Split('\n'))
                if (line.Length != 0 && Key(line) != key) output.AppendLine(line);
            output.AppendLine(mapping);
            return output.ToString();
        }
        public static void ResetOverrides()
        {
            string path = Path.Combine(Launcher.LauncherPrefs.Directory, FileName);
            if (File.Exists(path)) GamepadProfiles.WriteAtomic(path, "# Custom controller mappings reset.\n");
            _loaded = false; ReloadRequested = true;
        }

        /// <summary>What was read, for <c>-gamepad</c> to print.</summary>
        public static string Summary { get; private set; } = "no extra mappings loaded";

        /// <summary>
        /// Hand GLFW whatever mappings this machine has, once.
        ///
        /// Called from the first poll rather than from startup: GLFW has to be
        /// initialised before it will take a mapping, and which of the window,
        /// the probe or a settings row got there first is not something this
        /// file should have to know.
        /// </summary>
        public static void EnsureLoaded()
        {
            if (_loaded || OperatingSystem.IsAndroid())
            {
                return;
            }
            _loaded = true;
            MappingCapabilities.Clear();
            int files = 0;
            int lines = 0;
            foreach (string path in Paths())
            {
                string? text = TryRead(path);
                if (text == null)
                {
                    continue;
                }
                if (Apply(text))
                {
                    files++;
                    lines += Count(text);
                    Console.WriteLine($"[input] gamepad mappings: {Count(text)} from {path}");
                }
            }
            string? config = Environment.GetEnvironmentVariable("SDL_GAMECONTROLLERCONFIG");
            if (!String.IsNullOrWhiteSpace(config) && Apply(config))
            {
                files++;
                lines += Count(config);
                Console.WriteLine($"[input] gamepad mappings: {Count(config)} from "
                    + "SDL_GAMECONTROLLERCONFIG");
            }
            Summary = files == 0
                ? "no extra mappings loaded"
                : $"{lines} extra mapping(s) from {files} source(s)";
        }

        /// <summary>
        /// Where a mapping file may sit. The settings directory is second so
        /// that it wins: it is the copy a player edited, and application
        /// resources contain whatever the download came with.
        /// </summary>
        internal static string[] Paths(string? resourceDirectory = null, string? settingsDirectory = null)
        {
            string beside = Path.Combine(resourceDirectory ?? Mods.Platform.AppPaths.ResourceDirectory, FileName);
            string settings = Path.Combine(settingsDirectory ?? Launcher.LauncherPrefs.Directory, FileName);
            return beside == settings
                ? new string[] { beside }
                : new string[] { beside, settings };
        }

        private static string? TryRead(string path)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // A file that cannot be read is the same as no file. Nothing a
                // player does about their controller should be able to stop
                // the game starting.
                return null;
            }
        }

        private static bool Apply(string text)
        {
            try
            {
                // The whole file at once: glfwUpdateGamepadMappings takes a
                // string of newline-separated lines and skips comments itself,
                // so there is nothing to parse here. It returns false only if
                // it could not parse *any* of it.
                bool applied = GLFW.UpdateGamepadMappings(text);
                if (applied) foreach (string line in text.Split('\n'))
                {
                    var parts = line.Trim().Split(',');
                    if (parts.Length < 3 || parts[0].Length != 32) continue;
                    bool compatible = true;
                    foreach (string part in parts) if (part.StartsWith("platform:", StringComparison.Ordinal) && part != "platform:" + Platform()) compatible = false;
                    if (compatible) MappingCapabilities[parts[0]] = ParseCapabilities(line);
                }
                return applied;
            }
            catch (Exception ex) when (ex is DllNotFoundException
                || ex is EntryPointNotFoundException || ex is BadImageFormatException)
            {
                return false;
            }
        }

        /// <summary>Mapping lines in a file: not blank, not a comment.</summary>
        private static int Count(string text)
        {
            int count = 0;
            foreach (string line in text.Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.Length > 0 && !trimmed.StartsWith('#'))
                {
                    count++;
                }
            }
            return count;
        }

        /// <summary>
        /// A mapping line for a pad nothing knows, built from the layout
        /// <see cref="GamepadDesktop"/> is guessing at.
        ///
        /// For <c>-gamepad</c> to print, so that somebody whose pad works only
        /// approximately has something to correct and paste into
        /// <c>gamecontrollerdb.txt</c> -- and something to send here. The GUID
        /// is the part nobody can look up for themselves; the rest is the
        /// guess written out in SDL's own words, which is a far better thing
        /// to hand a person than "unsupported".
        /// </summary>
        public static string Suggest(int slot)
        {
            string guid = GLFW.GetJoystickGUID(slot) ?? "00000000000000000000000000000000";
            string name = (GLFW.GetJoystickName(slot) ?? "gamepad").Replace(',', ' ');
            GamepadLayout layout = GamepadLayout.For(slot);
            return DescribeLayout(guid, name, layout, Platform());
        }

        // Apply only when GLFW has no mapping. Exact bundled/user/environment mappings
        // keep priority. Limit firmware compatibility to the known macOS Series HID shape.
        internal static string? CompatibleMacXboxMapping(string guid, string name, int axes, int buttons, int hats, bool macOS)
        {
            if (!GamepadLayout.IsMacXboxBluetooth(guid, axes, buttons, hats, macOS)) return null;
            return DescribeLayout(guid, name.Replace(',', ' '),
                GamepadLayout.Select(guid, axes, buttons, hats, macOS), "Mac OS X");
        }

        internal static bool TryMapMacXbox(int slot)
        {
            if (!OperatingSystem.IsMacOS() || GLFW.JoystickIsGamepad(slot)) return false;
            string? mapping = CompatibleMacXboxMapping(GLFW.GetJoystickGUID(slot) ?? "",
                GLFW.GetJoystickName(slot) ?? "Xbox controller", GLFW.GetJoystickAxes(slot).Length,
                GLFW.GetJoystickButtons(slot).Length, GLFW.GetJoystickHats(slot).Length, macOS: true);
            return mapping != null && Apply(mapping) && GLFW.JoystickIsGamepad(slot);
        }

        private static string DescribeLayout(string guid, string name, GamepadLayout layout, string platform)
        {
            var text = new System.Text.StringBuilder();
            text.Append(guid).Append(',').Append(name).Append(',');
            text.Append($"a:b{layout.ButtonA},b:b{layout.ButtonB},");
            text.Append($"x:b{layout.ButtonX},y:b{layout.ButtonY},");
            text.Append($"leftshoulder:b{layout.ButtonLeftBumper},");
            text.Append($"rightshoulder:b{layout.ButtonRightBumper},");
            text.Append($"back:b{layout.ButtonBack},start:b{layout.ButtonStart},");
            text.Append($"leftstick:b{layout.ButtonLeftThumb},");
            text.Append($"rightstick:b{layout.ButtonRightThumb},");
            text.Append($"leftx:a{layout.AxisLeftX},lefty:a{layout.AxisLeftY},");
            text.Append($"rightx:a{layout.AxisRightX},righty:a{layout.AxisRightY},");
            text.Append(layout.AxisLeftTrigger >= 0
                ? $"lefttrigger:a{layout.AxisLeftTrigger},"
                : $"lefttrigger:b{layout.ButtonLeftTrigger},");
            text.Append(layout.AxisRightTrigger >= 0
                ? $"righttrigger:a{layout.AxisRightTrigger},"
                : $"righttrigger:b{layout.ButtonRightTrigger},");
            text.Append("dpup:h0.1,dpright:h0.2,dpdown:h0.4,dpleft:h0.8,");
            text.Append("platform:").Append(platform).Append(',');
            return text.ToString();
        }

        /// <summary>
        /// The word SDL puts in a mapping's <c>platform:</c> field. A line
        /// carrying the wrong one is skipped in silence, which is a confusing
        /// thing to hand somebody as a fix.
        /// </summary>
        public static string Platform()
        {
            if (OperatingSystem.IsWindows())
            {
                return "Windows";
            }
            if (OperatingSystem.IsMacOS())
            {
                return "Mac OS X";
            }
            if (OperatingSystem.IsAndroid())
            {
                return "Android";
            }
            return "Linux";
        }
    }
}
