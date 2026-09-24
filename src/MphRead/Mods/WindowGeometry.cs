using System;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using MphRead.Mods.Launcher;

namespace MphRead.Mods
{
    /// <summary>
    /// Put the game window back the size and in the corner it was left.
    ///
    /// Only the **shell** window -- the one window the program spends its life
    /// in. Every other `RenderWindow` this process opens is a measuring
    /// instrument (`-maptest`, `-renderprobe`, the thumbnail runs, the network
    /// harness) that was given a size on purpose, and letting those write the
    /// preference would mean a player who ran one screenshot command opens the
    /// game at 400x240 afterwards.
    ///
    /// Two things here are not obvious and are the whole of why this is a file
    /// rather than four lines in the constructor:
    ///
    /// - **A saved rectangle is a claim about hardware that may be gone.** A
    ///   window restored onto a monitor that has since been unplugged, or onto
    ///   a laptop whose dock is not attached, is a window nobody can reach and
    ///   cannot be dragged back. So the rectangle is checked against the
    ///   displays that exist *now* and pulled onto one if it does not overlap
    ///   any of them.
    /// - **Maximized is not a size.** Restoring a maximized window by its
    ///   rectangle gets it visibly wrong: it fills the screen without being
    ///   maximized, so the caption button offers to restore a window that is
    ///   not maximized, and it will not follow a change of resolution. The
    ///   state is saved separately and the rectangle underneath it is kept, so
    ///   un-maximizing lands where it used to.
    /// </summary>
    public static class WindowGeometry
    {
        /// <summary>
        /// How much of the window has to be on a display for the saved
        /// position to be used as it is.
        ///
        /// A margin rather than "any overlap at all": a window one pixel onto
        /// the screen is a window that cannot be grabbed by its title bar,
        /// which is the same problem as being off it entirely.
        /// </summary>
        private const int _visibleMargin = 80;

        /// <summary>
        /// Whether the shell window's size is the player's to keep.
        ///
        /// False while a capture is driving it. `-shellshot` walks the real
        /// window through the real loop -- which includes maximizing it
        /// mid-sequence -- so without this, photographing the launcher would
        /// leave a player's next session maximized, and a capture would open
        /// at whatever size they had last dragged it to instead of the
        /// deterministic one its pictures are compared at.
        /// </summary>
        public static bool Enabled { get; set; } = true;

        /// <summary>
        /// Whether this process has a shell window at all -- the one window a
        /// player uses, as opposed to the measuring instruments.
        ///
        /// <see cref="Enabled"/> says whether the shape is the player's to
        /// keep; this says whether there is a player's window in the first
        /// place. Both are needed because <see cref="NoteMode"/> is called
        /// from <see cref="WindowMode"/>, which is handed a window and has no
        /// way to tell which one it is: `-maptest -fullscreen` enters
        /// fullscreen too, and it must not leave the launcher opening that way
        /// afterwards.
        /// </summary>
        public static bool Owned { get; set; }

        /// <summary>
        /// Apply the saved geometry, before the window is shown.
        ///
        /// Everything here is best-effort by nature. GLFW will refuse a
        /// position on Wayland (there is no concept of one), a window manager
        /// is free to ignore a size, and a headless run has no monitor to ask
        /// -- none of which is a reason to fail to open a window.
        /// </summary>
        public static void Restore(NativeWindow window, Vector2i minimum)
        {
            int width = LauncherPrefs.WindowWidth;
            int height = LauncherPrefs.WindowHeight;
            if (!Enabled || width <= 0 || height <= 0)
            {
                return;
            }
            try
            {
                var size = new Vector2i(Math.Max(width, minimum.X), Math.Max(height, minimum.Y));
                var position = new Vector2i(LauncherPrefs.WindowX, LauncherPrefs.WindowY);
                Box2i? screen = Fit(window, position, size);
                if (screen == null)
                {
                    // Nothing this rectangle touches any more. The size is
                    // still what the player chose, so it is kept and only the
                    // corner is given up -- GLFW puts an unplaced window
                    // somewhere sensible on the primary display.
                    window.ClientSize = size;
                    DebugLog.Line("window", $"restored {size.X}x{size.Y}; the saved corner "
                        + $"({position.X},{position.Y}) is on no display now");
                    LauncherPrefs.WindowX = 0;
                    LauncherPrefs.WindowY = 0;
                }
                else
                {
                    Box2i area = screen.Value;
                    // Clamped rather than refused: a window saved on a bigger
                    // screen than this one should come back as large as this
                    // one allows, not at the default.
                    size = new Vector2i(
                        Math.Clamp(size.X, minimum.X, Math.Max(minimum.X, area.Size.X)),
                        Math.Clamp(size.Y, minimum.Y, Math.Max(minimum.Y, area.Size.Y)));
                    window.ClientSize = size;
                    window.Location = position;
                    DebugLog.Line("window",
                        $"restored {size.X}x{size.Y} at {position.X},{position.Y}");
                }
                if (LauncherPrefs.WindowMaximized)
                {
                    window.WindowState = WindowState.Maximized;
                }
            }
            catch (Exception ex)
            {
                DebugLog.Line("window", $"could not restore the window geometry: {ex.Message}");
            }
        }

        /// <summary>
        /// The work area of the display this rectangle is usably on, or null
        /// when it is on none of them.
        /// </summary>
        private static Box2i? Fit(NativeWindow window, Vector2i position, Vector2i size)
        {
            foreach (MonitorInfo monitor in Monitors.GetMonitors())
            {
                Box2i area = monitor.ClientArea;
                bool acrossX = position.X + size.X > area.Min.X + _visibleMargin
                    && position.X < area.Max.X - _visibleMargin;
                // The *top* edge, not the whole height: a title bar below the
                // bottom of the screen is a window that cannot be moved, and
                // that is the only part of being off the edge that traps
                // anybody.
                bool downY = position.Y >= area.Min.Y - _visibleMargin
                    && position.Y < area.Max.Y - _visibleMargin;
                if (acrossX && downY)
                {
                    return area;
                }
            }
            // Not an error: a headless run has no monitors and neither has a
            // display that went away between calls.
            _ = window;
            return null;
        }

        /// <summary>
        /// The window moved or was resized: keep the new shape, and start the
        /// clock on writing it down.
        ///
        /// Called from the resize and move callbacks, which fire continuously
        /// while a window is being dragged -- so this only touches memory. The
        /// file is written by <see cref="Flush"/> once the dragging stops.
        ///
        /// Recording as it happens rather than only as the window closes is
        /// what makes the feature survive the ways a program actually ends:
        /// a crash, a kill, a machine that went to sleep and never came back,
        /// and -- the one that started this -- an exit path that does not run
        /// the close handler. A window size is not worth losing to any of
        /// them.
        /// </summary>
        public static void Note(NativeWindow window)
        {
            if (!Enabled)
            {
                return;
            }
            if (!Capture(window))
            {
                return;
            }
            _dirty = true;
            _changedAt = DateTime.UtcNow;
        }

        /// <summary>
        /// Write it down, once the shape has been still for a moment.
        ///
        /// Called once a frame, and almost always does nothing: the check is
        /// a bool. The wait is what keeps a drag -- which is dozens of resize
        /// callbacks a second -- from being dozens of file writes.
        /// </summary>
        public static void Flush(bool force = false)
        {
            if (!_dirty)
            {
                return;
            }
            if (!force && DateTime.UtcNow - _changedAt < _settle)
            {
                return;
            }
            _dirty = false;
            LauncherPrefs.Save();
            DebugLog.Line("window", $"kept {LauncherPrefs.WindowWidth}x"
                + $"{LauncherPrefs.WindowHeight} at {LauncherPrefs.WindowX},"
                + $"{LauncherPrefs.WindowY}"
                + (LauncherPrefs.WindowMaximized ? ", maximized" : "")
                + (LauncherPrefs.WindowMode != WindowStartMode.Windowed
                    ? ", fullscreen" : ""));
        }

        /// <summary>
        /// The window went fullscreen, or came back: keep that too, so the
        /// next session opens the way this one was left.
        ///
        /// The size and the corner have been remembered since they existed;
        /// the *mode* was not, and only the drop-down in Settings ever wrote
        /// it. So F11 was a decision the program forgot the moment it closed
        /// -- a player who plays fullscreen pressed it again every single
        /// launch -- and now that the launcher and the match are one window,
        /// that is the launcher opening in a window as well.
        ///
        /// The window's live state, not an intention: whichever of F11,
        /// Alt+Enter, the pause menu or the settings row put it there, what
        /// gets written down is where it ended up.
        /// </summary>
        public static void NoteMode()
        {
            if (!Enabled || !Owned)
            {
                return;
            }
            WindowStartMode mode = WindowMode.Current;
            if (LauncherPrefs.WindowMode == mode)
            {
                return;
            }
            LauncherPrefs.WindowMode = mode;
            _dirty = true;
            _changedAt = DateTime.UtcNow;
        }

        /// <summary>How long the window has to hold still before the file is written.</summary>
        private static readonly TimeSpan _settle = TimeSpan.FromSeconds(1.5);

        private static bool _dirty;
        private static DateTime _changedAt;

        /// <summary>
        /// Take the geometry now and write it out, for the window closing.
        ///
        /// A backstop rather than the mechanism: <see cref="Note"/> has
        /// normally already kept it. It still matters for the window that is
        /// closed without ever being resized, where there is nothing dirty to
        /// flush and the shape is still worth keeping.
        /// </summary>
        public static void Remember(NativeWindow window)
        {
            if (!Enabled)
            {
                return;
            }
            if (Capture(window))
            {
                _dirty = true;
            }
            Flush(force: true);
        }

        /// <summary>
        /// Read the window's shape into the preference, in memory. Returns
        /// whether there was anything worth keeping.
        ///
        /// **Fullscreen is read through <see cref="WindowMode"/>, not off the
        /// window.** A window in borderless fullscreen reports the size of the
        /// monitor and a corner of 0,0, and saving that gives a player who
        /// quit from fullscreen a windowed session the size of their screen
        /// with a title bar pushing it off the bottom. What belongs here is
        /// the geometry fullscreen was entered *from*, which is exactly what
        /// WindowMode already keeps in order to put the window back.
        /// </summary>
        private static bool Capture(NativeWindow window)
        {
            try
            {
                if (WindowMode.IsFullscreen)
                {
                    Vector2i size = WindowMode.WindowedSize;
                    return size.X > 0 && size.Y > 0
                        && Store(size, WindowMode.WindowedLocation, maximized: WindowMode.WindowedMaximized);
                }
                if (window.WindowState == WindowState.Minimized)
                {
                    // A minimized window has no useful rectangle and its
                    // state is not one to reopen in. Whatever was kept last
                    // is a better answer than this.
                    return false;
                }
                return Store(window.ClientSize, window.Location,
                    window.WindowState == WindowState.Maximized);
            }
            catch (Exception ex)
            {
                DebugLog.Line("window", $"could not read the window geometry: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Put it in the preference. Returns false for a shape not worth
        /// keeping, and for one that is already what is held -- which is most
        /// calls, since a move callback fires for a resize as well.
        /// </summary>
        private static bool Store(Vector2i size, Vector2i position, bool maximized)
        {
            if (size.X <= 0 || size.Y <= 0)
            {
                return false;
            }
            if (LauncherPrefs.WindowWidth == size.X && LauncherPrefs.WindowHeight == size.Y
                && LauncherPrefs.WindowX == position.X && LauncherPrefs.WindowY == position.Y
                && LauncherPrefs.WindowMaximized == maximized)
            {
                return false;
            }
            LauncherPrefs.WindowWidth = size.X;
            LauncherPrefs.WindowHeight = size.Y;
            LauncherPrefs.WindowX = position.X;
            LauncherPrefs.WindowY = position.Y;
            LauncherPrefs.WindowMaximized = maximized;
            return true;
        }
    }
}
