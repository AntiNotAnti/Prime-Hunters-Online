using System;
using System.IO;
using System.Text;

namespace MphRead.Mods
{
    /// <summary>
    /// The last thing that runs when nothing else could: what a crash before
    /// the window looks like from the outside, and where it is written down.
    ///
    /// <para>
    /// Not the same thing as <see cref="DebugLog"/>, which is a session-long
    /// transcript kept only when a player has asked for one. This is always
    /// installed and writes exactly one file, because the failure it exists
    /// for is the one nobody can report: the Windows build is a GUI binary
    /// (see <see cref="ConsoleWindow"/>), so an exception on the way to the
    /// first window has nowhere to print. The process disappears, the player
    /// double-clicks the executable again and again, and every symptom there
    /// is is "nothing happens".
    /// </para>
    ///
    /// <para>
    /// So: a file beside the executable, a console with the message in it on
    /// Windows, and a pause when that console belongs to us -- otherwise the
    /// window it was printed into closes with the process.
    /// </para>
    /// </summary>
    public static class CrashReport
    {
        private static bool _installed;
        private static bool _reported;

        /// <summary>Where the last report went, if one was written.</summary>
        public static string? Path { get; private set; }

        /// <summary>
        /// Install the handler. Called first thing in Main, before anything
        /// that can throw -- which on a fresh install is most of startup.
        /// </summary>
        public static void Install()
        {
            if (_installed)
            {
                return;
            }
            _installed = true;
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                Report(e.ExceptionObject as Exception, "unhandled");
            };
        }

        /// <summary>
        /// Write one report and, on Windows, make sure it is on the screen.
        ///
        /// Only the first is written: an exception on the way out of another
        /// one would otherwise overwrite the report naming the fault that
        /// actually started it.
        /// </summary>
        public static void Report(Exception? ex, string source)
        {
            if (_reported || ex == null)
            {
                return;
            }
            _reported = true;
            string? path = TryWrite(ex, source);
            Path = path;
            // A GUI binary that got this far has no console at all; without
            // one the message below goes nowhere, which is the whole failure
            // this class exists for.
            if (OperatingSystem.IsWindows())
            {
                ConsoleWindow.Show();
            }
            Console.Error.WriteLine();
            Console.Error.WriteLine($"{Branding.Name} could not start.");
            Console.Error.WriteLine();
            Console.Error.WriteLine($"  {ex.GetType().Name}: {ex.Message}");
            if (path != null)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine($"The details are in {path}.");
            }
            Console.Error.WriteLine();
            // Started by double-click: the console window is this process's
            // own and closes with it, so everything above would be a flash of
            // black without this.
            if (OperatingSystem.IsWindows() && ConsoleWindow.OwnsItsConsole())
            {
                Console.Error.WriteLine("Press any key to close...");
                try
                {
                    Console.ReadKey(intercept: true);
                }
                catch (Exception)
                {
                    // No key to read is no reason to fail on the way out.
                }
            }
        }

        private static string? TryWrite(Exception ex, string source)
        {
            var text = new StringBuilder();
            text.AppendLine($"{Branding.Name} {Update.BuildVersion.Display}, "
                + $"data format {Program.Version}");
            text.AppendLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
            text.AppendLine($"{Environment.OSVersion}, .NET {Environment.Version}, "
                + $"64-bit process={Environment.Is64BitProcess}");
            text.AppendLine($"base={AppContext.BaseDirectory}");
            text.AppendLine($"command line={Environment.CommandLine}");
            text.AppendLine($"source={source}");
            DebugLog.AppendRecent(text);
            text.AppendLine();
            text.AppendLine(ex.ToString());
            // Named .log because LogArchive gathers "*.log", and this has to
            // travel with the rest of them.
            string name = $"{Branding.Name.Replace(" ", "")}-crash-"
                + $"{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log";
            // The logs folder first, and not because it is tidy: that is the
            // one directory LogArchive gathers and the share sheet hands out,
            // so a crash written anywhere else is a crash a player on a phone
            // has no way to send. Then beside the executable, where somebody
            // who has just downloaded a desktop release will look, and the
            // temporary directory for an installation under Program Files.
            foreach (string directory in new[]
                     {
                         LogArchive.Directory,
                         Launcher.LauncherPrefs.Directory,
                         AppContext.BaseDirectory,
                         System.IO.Path.GetTempPath()
                     })
            {
                try
                {
                    Directory.CreateDirectory(directory);
                    string path = System.IO.Path.Combine(directory, name);
                    File.WriteAllText(path, text.ToString());
                    return path;
                }
                catch (Exception)
                {
                    // Try the next one; a report that cannot be written is
                    // still a message that can be printed.
                }
            }
            return null;
        }
    }
}
