using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MphRead.Mods
{
    /// <summary>
    /// Gives this process a console only when something is going to use one.
    ///
    /// The Windows build is a GUI binary (`WinExe`), so double-clicking it
    /// opens the launcher and nothing else -- no black window behind the game,
    /// not even for the instant it takes to hide one. Everything that talks on
    /// the console asks for it here: a command run from a terminal attaches to
    /// that terminal, a command run by double-click gets a window of its own,
    /// and a child process whose output is being captured gets neither,
    /// because its streams are already pipes.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static class ConsoleWindow
    {
        private const int _attachParentProcess = -1;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AllocConsole();

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("kernel32.dll")]
        private static extern bool GetConsoleMode(IntPtr handle, out uint mode);

        [DllImport("kernel32.dll")]
        private static extern bool SetConsoleMode(IntPtr handle, uint mode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int handle);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr window, int command);

        [DllImport("kernel32.dll")]
        private static extern uint GetConsoleProcessList(uint[] processList, uint count);

        /// <summary>
        /// True when this process is the only one attached to its console.
        ///
        /// That means Windows created the console for it -- it was
        /// double-clicked rather than started from a terminal -- and so the
        /// window and everything printed to it disappear the instant the
        /// process returns. Anything that only prints has to wait, and
        /// anything run from a shell must not.
        /// </summary>
        public static bool OwnsItsConsole()
        {
            if (!OperatingSystem.IsWindows() || Console.IsOutputRedirected)
            {
                return false;
            }
            try
            {
                // Sized for the answer, not for the truth: any count above one
                // means somebody else is attached, and which processes those
                // are does not matter here.
                var processes = new uint[4];
                return GetConsoleProcessList(processes, (uint)processes.Length) == 1;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Decide, once, whether this run needs a console. Called from Main
        /// before anything prints.
        /// </summary>
        public static void Prepare(string[] args)
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }
            bool forced = HasFlag(args, "console");
            // No arguments means the launcher, and the launcher is a window.
            bool guiOnly = args.Length == 0 || HasFlag(args, "launcher");
            if (guiOnly && !forced)
            {
                return;
            }
            Show();
        }

        /// <summary>
        /// Make sure there is somewhere to print, and that Console points at
        /// it. Used by Prepare and by the launcher when the game fails to
        /// start, where the alternative is a window that never appears and no
        /// explanation anywhere.
        /// </summary>
        public static void Show()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }
            if (Console.IsOutputRedirected || Console.IsInputRedirected)
            {
                // A parent is capturing us -- the launcher's extraction step
                // does exactly this. The streams already work; a console
                // window here would be a flash of black for nothing.
                return;
            }
            if (GetConsoleWindow() != IntPtr.Zero)
            {
                ShowWindow(GetConsoleWindow(), 5); // SW_SHOW
                return;
            }
            if (!AttachConsole(_attachParentProcess) && !AllocConsole())
            {
                return;
            }
            Rebind();
        }

        /// <summary>
        /// Point Console at the console that now exists. Without this the
        /// streams captured at startup -- when there was none -- stay
        /// pointing at nothing.
        /// </summary>
        private static void Rebind()
        {
            try
            {
                var output = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
                Console.SetOut(output);
                var error = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
                Console.SetError(error);
                Console.SetIn(new StreamReader(Console.OpenStandardInput()));
                // The escape-sequence mode ConsoleSetup asks for, re-applied:
                // it ran before this console existed.
                IntPtr handle = GetStdHandle(-11);
                if (GetConsoleMode(handle, out uint mode))
                {
                    SetConsoleMode(handle, mode | 4);
                }
            }
            catch (IOException)
            {
                // Nothing to print to is survivable; a crash here is not.
            }
        }

        private static bool HasFlag(string[] args, string name)
        {
            return args.Any(a => a.TrimStart('-').Equals(name, StringComparison.OrdinalIgnoreCase));
        }
    }
}
