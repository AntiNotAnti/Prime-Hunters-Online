using System;
using System.IO;
using System.Threading;
using System.Reflection;

namespace MphRead.Mods
{
    /// <summary>
    /// A file beside the executable saying what happened during a preview
    /// generation.
    ///
    /// The Windows build is a GUI binary, so Windows gives it no terminal.
    /// ConsoleWindow attaches to the parent's when a command was typed, but a
    /// shell does not wait for a GUI process: it returns to the prompt and
    /// whatever the process prints afterwards goes nowhere anybody looks. Ask
    /// somebody to run a diagnostic command and they get an empty prompt back
    /// -- which is exactly what happened, twice, while trying to find out why
    /// a machine produced nothing but black previews.
    ///
    /// Generation also runs as one worker process per room, so there is no
    /// single console to own even when there is one. A file they all append to
    /// is the only place the whole run can be read afterwards.
    /// </summary>
    public static class ThumbnailLog
    {
        private static readonly object _lock = new();
        private static bool _failed;

        public static string Path =>
            System.IO.Path.Combine(Launcher.GameFiles.Root, "thumbnails.log");

        /// <summary>Start a fresh file. Called once by whoever runs a batch.</summary>
        public static void Begin(int rooms)
        {
            try
            {
                // The build identifies itself, because "is this the version
                // with the diagnostic in it?" cost a whole round trip once:
                // a log came back with no sign of a feature that had been
                // added, and there was no way to tell a missing feature from
                // an older executable except by its absence.
                Assembly assembly = Assembly.GetEntryAssembly() ?? typeof(ThumbnailLog).Assembly;
                string build = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                    ?.InformationalVersion ?? "unknown";
                File.WriteAllText(Path,
                    $"=== {Branding.Name} preview generation, {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==="
                    + Environment.NewLine
                    + $"build {Update.BuildVersion.Display}, assembly "
                    + $"{System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "?"}, "
                    + $"file {File.GetLastWriteTime(AppContext.BaseDirectory):yyyy-MM-dd HH:mm}"
                    + Environment.NewLine + $"source {build}; executable {Environment.ProcessPath}"
                    + Environment.NewLine + $"{rooms} room(s) to render" + Environment.NewLine);
                _failed = false;
            }
            catch (Exception)
            {
                // A read-only directory throws UnauthorizedAccess rather than
                // IOException, and a log nobody can write is not worth a crash.
                _failed = true;
            }
        }

        public static void Write(string line)
        {
            if (_failed)
            {
                return;
            }
            // Every room is its own process, so several of them append at
            // once. Retry rather than lose the line that explains the run.
            lock (_lock)
            {
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    try
                    {
                        File.AppendAllText(Path, $"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
                        return;
                    }
                    catch (IOException)
                    {
                        Thread.Sleep(20);
                    }
                    catch (Exception)
                    {
                        _failed = true;
                        return;
                    }
                }
            }
        }
    }
}
