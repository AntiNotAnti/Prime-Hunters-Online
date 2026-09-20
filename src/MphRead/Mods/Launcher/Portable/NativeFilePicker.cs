using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace MphRead.Mods.Launcher
{
    /// <summary>
    /// "Open a file", asked of the operating system rather than of the toolkit.
    ///
    /// <para>
    /// The launcher's screens are drawn by Avalonia's <b>headless</b> backend
    /// and composited into the game window, which is what makes this program
    /// open exactly one window on every platform. The price is that its
    /// top-level has no windowing platform behind it, and
    /// <c>TopLevel.StorageProvider</c> then falls back to Avalonia's
    /// <c>NoopStorageProvider</c>: <c>OpenFilePickerAsync</c> returns an empty
    /// list, immediately, without showing anything.
    /// </para>
    ///
    /// <para>
    /// That is not a cosmetic gap. On a fresh install the setup screen is the
    /// whole launcher and the file picker is its only control, so the program
    /// came up and could not be used at all -- "I click and nothing happens",
    /// with no error anywhere, because nothing failed.
    /// </para>
    ///
    /// <para>
    /// So each desktop platform is asked in its own way: comdlg32 on Windows,
    /// zenity or kdialog on Linux, and osascript on macOS. Android is the one
    /// head with a real windowing backend and keeps the toolkit's own picker.
    /// <see cref="Available"/> says whether there is anything to ask, so the
    /// screen can offer a typed path instead rather than a control that does
    /// nothing.
    /// </para>
    /// </summary>
    public static class NativeFilePicker
    {
        /// <summary>
        /// The window a dialog belongs to, where the platform has the idea.
        /// Windows does, and without it a modal dialog can open *behind* a
        /// borderless-fullscreen game: still modal, still waiting for an
        /// answer, and invisible -- which is the same symptom as the button
        /// doing nothing. Set by the desktop shell once its window exists;
        /// zero everywhere else, which is what an unowned dialog gets.
        /// </summary>
        public static IntPtr Owner { get; set; }

        /// <summary>
        /// Answer as a machine with no dialog would, whatever this one has.
        ///
        /// For <c>-shellshot</c>, which presses the setup screen's tick to
        /// prove that a press reaches the screens at all. That button opens a
        /// modal file dialog, and a capture that stops on one waits for a
        /// person who is not there -- on a developer's box with zenity
        /// installed, for ever. Suppressed, the screen takes its own
        /// no-dialog path instead, which is a real path and says so on screen.
        /// </summary>
        public static bool Suppressed { get; set; }

        /// <summary>Whether this machine has a dialog this can open.</summary>
        public static bool Available =>
            !Suppressed
            && (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                || (OperatingSystem.IsLinux() && LinuxTool() != null));

        /// <summary>
        /// Ask for one existing file. Null when the player cancelled, and null
        /// when there was nothing to ask -- the two are the same to a caller
        /// that has <see cref="Available"/> to tell them apart beforehand.
        /// </summary>
        /// <param name="title">The dialog's own title.</param>
        /// <param name="description">What the extension is called, for the filter row.</param>
        /// <param name="extension">The extension, without a dot.</param>
        public static Task<string?> OpenFile(string title, string description,
            string extension)
        {
            if (OperatingSystem.IsWindows())
            {
                return WindowsFile(title, description, extension, Owner);
            }
            if (OperatingSystem.IsMacOS())
            {
                return Task.Run(() => MacFile(title, extension));
            }
            if (OperatingSystem.IsLinux())
            {
                return Task.Run(() => LinuxFile(title, description, extension));
            }
            return Task.FromResult<string?>(null);
        }

        // ------------------------------------------------------------ windows

        private const int MaxPath = 32768;

        // OFN_PATHMUSTEXIST | OFN_FILEMUSTEXIST | OFN_NOCHANGEDIR | OFN_EXPLORER
        private const int OpenFileFlags = 0x800 | 0x1000 | 0x8 | 0x80000;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OpenFileName
        {
            public int StructSize;
            public IntPtr Owner;
            public IntPtr Instance;
            public string? Filter;
            public string? CustomFilter;
            public int MaxCustomFilter;
            public int FilterIndex;
            public IntPtr File;
            public int MaxFile;
            public IntPtr FileTitle;
            public int MaxFileTitle;
            public string? InitialDirectory;
            public string? Title;
            public int Flags;
            public short FileOffset;
            public short FileExtension;
            public string? DefaultExtension;
            public IntPtr CustomData;
            public IntPtr Hook;
            public string? TemplateName;
            public IntPtr Reserved;
            public int ReservedTwo;
            public int FlagsEx;
        }

        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetOpenFileNameW(ref OpenFileName options);

        /// <summary>
        /// The common dialog, on a thread of its own.
        ///
        /// <b>STA, and that is not optional</b>: the Explorer-style dialog is
        /// a shell control, and asking for it from a multi-threaded apartment
        /// fails outright. The game's thread is the one calling in and it is
        /// not ours to re-apartment, so the dialog gets its own thread and the
        /// caller gets a task -- which also keeps the frame loop drawing
        /// behind a modal dialog instead of freezing the window for as long as
        /// somebody takes to find their file.
        /// </summary>
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private static Task<string?> WindowsFile(string title, string description,
            string extension, IntPtr owner)
        {
            var result = new TaskCompletionSource<string?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                IntPtr buffer = Marshal.AllocHGlobal(MaxPath * sizeof(char));
                try
                {
                    for (int i = 0; i < 2; i++)
                    {
                        Marshal.WriteInt16(buffer, i * sizeof(char), 0);
                    }
                    var options = new OpenFileName
                    {
                        StructSize = Marshal.SizeOf<OpenFileName>(),
                        // Owned by the game window, so a dialog opened over a
                        // borderless-fullscreen game is in front of it rather
                        // than behind it and unreachable.
                        Owner = owner,
                        // Nulls inside the filter, which is how comdlg32 reads
                        // it: pairs of strings, the list ended by an empty one.
                        Filter = $"{description} (*.{extension})\0*.{extension}\0"
                            + "Every file (*.*)\0*.*\0\0",
                        FilterIndex = 1,
                        File = buffer,
                        MaxFile = MaxPath,
                        Title = title,
                        DefaultExtension = extension,
                        Flags = OpenFileFlags
                    };
                    result.SetResult(GetOpenFileNameW(ref options)
                        ? Marshal.PtrToStringUni(buffer)
                        : null);
                }
                catch (Exception ex)
                {
                    Mods.DebugLog.Exception("picker", ex);
                    result.SetResult(null);
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            return result.Task;
        }

        // -------------------------------------------------------------- linux

        /// <summary>The first of the two desktop dialogs this box has, or none.</summary>
        private static string? LinuxTool()
        {
            foreach (string tool in new[] { "zenity", "kdialog", "qarma" })
            {
                if (OnPath(tool))
                {
                    return tool;
                }
            }
            return null;
        }

        private static bool OnPath(string tool)
        {
            string paths = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string directory in paths.Split(Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    if (File.Exists(Path.Combine(directory, tool)))
                    {
                        return true;
                    }
                }
                catch (ArgumentException)
                {
                    // A malformed entry in PATH is not this program's problem.
                }
            }
            return false;
        }

        private static string? LinuxFile(string title, string description, string extension)
        {
            string? tool = LinuxTool();
            if (tool == null)
            {
                return null;
            }
            var arguments = new System.Collections.Generic.List<string>();
            if (tool == "kdialog")
            {
                arguments.Add("--title");
                arguments.Add(title);
                arguments.Add("--getopenfilename");
                arguments.Add(Environment.GetFolderPath(
                    Environment.SpecialFolder.UserProfile));
                arguments.Add($"*.{extension}|{description}");
            }
            else
            {
                arguments.Add("--file-selection");
                arguments.Add($"--title={title}");
                arguments.Add($"--file-filter={description} | *.{extension}");
                arguments.Add("--file-filter=Every file | *");
            }
            return RunTool(tool, arguments);
        }

        // --------------------------------------------------------------- macos

        private static string? MacFile(string title, string extension)
        {
            // AppleScript quoting is the same as C's for these two characters,
            // and a title is ours rather than the player's -- but a prompt is
            // still a string going into a script, so it is escaped.
            string prompt = title.Replace("\\", "\\\\").Replace("\"", "\\\"");
            return RunTool("osascript", new System.Collections.Generic.List<string>
            {
                "-e",
                $"POSIX path of (choose file with prompt \"{prompt}\" "
                    + $"of type {{\"{extension}\"}})"
            });
        }

        // ---------------------------------------------------------------- both

        /// <summary>
        /// Run the dialog and read the one line it prints. A non-zero exit is
        /// how all three of them say "cancelled", which is not a failure and
        /// is not logged as one.
        /// </summary>
        private static string? RunTool(string tool,
            System.Collections.Generic.List<string> arguments)
        {
            try
            {
                var start = new ProcessStartInfo(tool)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                };
                foreach (string argument in arguments)
                {
                    start.ArgumentList.Add(argument);
                }
                using Process? process = Process.Start(start);
                if (process == null)
                {
                    return null;
                }
                string output = process.StandardOutput.ReadToEnd();
                process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0)
                {
                    return null;
                }
                string path = output.Trim();
                return path.Length > 0 && File.Exists(path) ? path : null;
            }
            catch (Exception ex)
            {
                Mods.DebugLog.Exception("picker", ex);
                return null;
            }
        }
    }
}
