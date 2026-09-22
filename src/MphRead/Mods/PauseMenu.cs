using System;
using OpenTK.Windowing.Desktop;

namespace MphRead.Mods
{
    /// <summary>
    /// The menu Escape opens during a match: let go of the mouse, offer the
    /// way out, and put the settings within reach without leaving the game.
    ///
    /// The menu is an Avalonia window on the game's own thread, and talks to
    /// the game through the flags below. It has to be that thread: GLFW window
    /// calls -- closing it, changing its border -- belong to the thread that
    /// created it, macOS accepts windows only on the main one, and Avalonia has
    /// a single UI thread per process in any case. So the menu asks, and
    /// <see cref="Poll"/> does the window work between frames.
    ///
    /// The cost of sharing the thread is that the toolkit only runs when the
    /// game lets it: <see cref="Poll"/> hands it the tail of every frame while
    /// the menu is up, which ties the menu's responsiveness to the frame rate
    /// and is why the match keeps drawing behind it.
    /// </summary>
    public static class PauseMenu
    {
        private static volatile bool _open;
        private static volatile bool _leave;
        private static volatile bool _quit;
        private static volatile bool _toggleFullscreen;
        private static volatile bool _refocus;
        /// <summary>Open right now: the cursor is free and the player is not driving.</summary>
        public static bool Open => _open;

        /// <summary>The player asked to leave the match but not the program.</summary>
        public static bool LeftMatch { get; private set; }

        /// <summary>The player asked to close the program outright.</summary>
        public static bool QuitProgram { get; private set; }

        /// <summary>
        /// Escape, from the game window. True when the menu took it, so the
        /// caller's own Escape handling -- which quits -- must not run.
        /// </summary>
        public static bool HandleEscape(NativeWindow window)
        {
            if (KillCam.RequestSkip()) return true;
#if MPHREAD_AVALONIA
            if (!Launcher.Gui.GuiLauncher.EnsureSetup())
            {
                // No toolkit on this machine -- no display, or a session that
                // could not bind one. Escape keeps its old meaning rather than
                // doing nothing at all.
                return false;
            }
            if (_open)
            {
                Close();
                return true;
            }
            OpenMenu();
            return true;
#else
            return false;
#endif
        }

        /// <summary>Called once a frame by the game window.</summary>
        public static void Poll(GameWindow window)
        {
            // Nothing to pump here any more. The menu is a screen in this
            // window rather than a window of its own, and the toolkit is given
            // its slice of the frame by Shell.TickUi, on the way to drawing
            // it -- which is also what makes the menu redraw at the frame rate
            // rather than at whatever rate a second window was being pumped.
            if (_refocus)
            {
                _refocus = false;
                // Give the keyboard back to the game.
                //
                // Closing the menu does not reliably make the game window the
                // foreground one again -- which window manager decides that,
                // and on what grounds, is a per-platform matter -- and an
                // unfocused GLFW window receives no keys and cannot grab the
                // pointer. The game goes on simulating, so nothing looks
                // crashed: the mouse still moves, the picture still draws, and
                // nothing the player presses arrives. That reads as a freeze.
                try
                {
                    window.Focus();
                }
                catch (Exception)
                {
                    // Not worth losing the match over.
                }
            }
            // The game window floats above the shell while it is fullscreen,
            // and stands down while this menu is up. Here rather than in
            // OpenMenu/Close because both of those are called from the menu's
            // own event handlers, and a GLFW window attribute belongs to the
            // thread that created the window -- which is this one, between
            // frames. Cached inside, so a frame that changes nothing is a
            // comparison and no call.
            WindowMode.SyncTopmost(window);
            if (_toggleFullscreen)
            {
                _toggleFullscreen = false;
                WindowMode.Toggle(window);
            }
            if (_quit)
            {
                _quit = false;
                QuitProgram = true;
                Close();
#if MPHREAD_SHELL
                Launcher.Gui.Shell.Quit(window);
#else
                window.Close();
#endif
            }
            else if (_leave)
            {
                _leave = false;
                LeftMatch = true;
                Close();
                // Not the window: in the shell the window is the program, and
                // leaving a match is the launcher coming back into it. A
                // harness window has no launcher behind it and still closes.
#if MPHREAD_SHELL
                Launcher.Gui.Shell.LeaveMatch(window);
#else
                window.Close();
#endif
            }
        }

        /// <summary>Forget what the last match asked for.</summary>
        public static void Reset()
        {
            LeftMatch = false;
            QuitProgram = false;
            _leave = false;
            _quit = false;
        }

        internal static void RequestLeave() => _leave = true;

        internal static void RequestQuit() => _quit = true;

        internal static void RequestFullscreenToggle() => _toggleFullscreen = true;

        internal static void MarkClosed()
        {
            _open = false;
            // Asked for here, done in Poll: this runs inside the toolkit's
            // teardown for the menu window, and handing focus to the game
            // window in the middle of that is how a window manager is given two
            // contradictory instructions in one turn.
            _refocus = true;
        }

        private static void OpenMenu()
        {
#if MPHREAD_SHELL
            _open = Launcher.Gui.Shell.OpenPauseMenu();
#endif
        }

        private static void Close()
        {
#if MPHREAD_SHELL
            Launcher.Gui.Shell.CloseMenu();
#endif
            _open = false;
        }
    }
}
