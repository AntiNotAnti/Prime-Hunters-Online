using System;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace MphRead.Mods
{
    public enum WindowStartMode
    {
        Windowed,
        BorderlessFullscreen,
        Fullscreen
    }

    /// <summary>
    /// Three distinct desktop window modes.
    ///
    /// Borderless is deliberately a compositor-managed, undecorated window
    /// rather than a GLFW monitor-attached fullscreen window. That keeps
    /// Alt+Tab/focus changes fast and predictable while still covering the
    /// display. Fullscreen is the native monitor-attached mode and keeps
    /// GLFW's normal auto-iconify-on-focus-loss behaviour.
    /// </summary>
    public static class WindowMode
    {
        public static WindowStartMode Startup { get; set; } = WindowStartMode.Windowed;
        public static bool StartupForced { get; private set; }
        public static WindowStartMode Current { get; private set; } = WindowStartMode.Windowed;
        public static bool IsFullscreen => Current != WindowStartMode.Windowed;

        private static WindowStartMode _lastFullscreen = WindowStartMode.Fullscreen;
        private static WindowBorder _savedBorder = WindowBorder.Resizable;
        private static Vector2i _savedLocation, _savedClientLocation, _savedSize;
        private static WindowState _savedState;
        private static bool _savedAutoIconify, _saved;
        private static bool _topmost;
        private static NativeWindow? _window;
        private static bool _changing;

        public static Vector2i WindowedSize => _saved ? _savedSize : Vector2i.Zero;
        public static Vector2i WindowedLocation => _savedLocation;
        public static bool WindowedMaximized => _saved && _savedState == WindowState.Maximized;
        public static bool IsTopmost => _topmost;

        public static void ForceStartup(WindowStartMode mode)
        {
            Startup = mode;
            StartupForced = true;
        }

        public static void ApplyStartup(NativeWindow window)
        {
            if (!ReferenceEquals(_window, window))
            {
                _window = window;
                Current = WindowStartMode.Windowed;
                _saved = false;
                _topmost = false;
                _lastFullscreen = Startup == WindowStartMode.BorderlessFullscreen
                    ? Startup : WindowStartMode.Fullscreen;
            }
            Set(window, Startup);
        }

        public static bool HandleKey(NativeWindow window, KeyboardKeyEventArgs e)
        {
            if (e.Key != Keys.F11 && !(e.Key == Keys.Enter && e.Alt)) return false;
            Toggle(window);
            return true;
        }

        public static void Toggle(NativeWindow window) =>
            Set(window, IsFullscreen ? WindowStartMode.Windowed : _lastFullscreen);

        public static void Enter(NativeWindow window) => Set(window, _lastFullscreen);
        public static void Leave(NativeWindow window) => Set(window, WindowStartMode.Windowed);

        /// <summary>Called on the window thread, including requests from Settings.</summary>
        public static void Set(NativeWindow window, WindowStartMode mode)
        {
            if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
            if (_changing || ModeMatches(window, mode))
            {
                if (mode == WindowStartMode.BorderlessFullscreen)
                {
                    SetTopmost(window, window.IsFocused);
                }
                return;
            }

            _changing = true;
            try
            {
                if (mode == WindowStartMode.Windowed)
                {
                    RestoreWindow(window);
                }
                else
                {
                    // Resolve the display before changing window state so both
                    // fullscreen modes stay on the display the player chose.
                    MonitorInfo monitor = Monitors.GetMonitorFromWindow(window);
                    SaveWindowed(window);
                    Current = mode; // Resize callbacks must not save display geometry.

                    if (mode == WindowStartMode.BorderlessFullscreen)
                    {
                        EnterBorderless(window, monitor);
                    }
                    else
                    {
                        EnterNativeFullscreen(window, monitor);
                    }
                    _lastFullscreen = mode;
                }
                WindowGeometry.NoteMode();
            }
            catch
            {
                RestoreWindow(window);
                throw;
            }
            finally
            {
                _changing = false;
            }
        }

        private static bool ModeMatches(NativeWindow window, WindowStartMode mode)
        {
            if (Current != mode) return false;
            return mode switch
            {
                WindowStartMode.Fullscreen => HasMonitor(window),
                WindowStartMode.BorderlessFullscreen =>
                    !HasMonitor(window) && window.WindowBorder == WindowBorder.Hidden,
                _ => !HasMonitor(window)
            };
        }

        private static void SaveWindowed(NativeWindow window)
        {
            if (_saved) return;

            _window = window;
            _savedState = window.WindowState;
            _savedBorder = window.WindowBorder;
            _savedAutoIconify = window.AutoIconify;

            // Save the restored client rectangle, but remember that the player
            // had the window maximized so that state can be reinstated later.
            if (_savedState == WindowState.Maximized || _savedState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
                GLFW.PollEvents();
            }
            _savedLocation = window.Location;
            _savedClientLocation = window.ClientLocation;
            _savedSize = window.ClientSize;
            _saved = true;
        }

        private static unsafe void EnterBorderless(NativeWindow window, MonitorInfo monitor)
        {
            Monitor* handle = monitor.Handle.ToUnsafePtr<Monitor>();
            var video = GLFW.GetVideoMode(handle);
            if (video == null) throw new InvalidOperationException("No video mode for the current monitor.");

            GLFW.GetMonitorPos(handle, out int x, out int y);
            int width = Math.Max(1, video->Width);
            // On Windows, one pixel short avoids promotion of an exact
            // monitor-sized hidden-border window into an exclusive-like
            // optimization path. Other desktops can use the full height.
            int height = Math.Max(1, video->Height - (OperatingSystem.IsWindows() ? 1 : 0));

            SetTopmost(window, false);
            window.AutoIconify = false;
            window.WindowState = WindowState.Normal;

            // Switching from native fullscreen must first release the monitor.
            // Pass the final borderless rectangle during the detach so there is
            // no intermediate jump back to the saved windowed rectangle.
            if (HasMonitor(window))
            {
                GLFW.SetWindowMonitor(window.WindowPtr, null, x, y, width, height, GLFW.DontCare);
                GLFW.PollEvents();
            }

            window.WindowBorder = WindowBorder.Hidden;
            GLFW.PollEvents();
            window.Location = new Vector2i(x, y);
            window.ClientSize = new Vector2i(width, height);

            // The one-pixel-short borderless window needs to sit above the
            // taskbar only while it owns focus. Dropping this immediately on
            // focus loss is what makes Alt+Tab behave like an ordinary window.
            SetTopmost(window, window.IsFocused);
        }

        private static unsafe void EnterNativeFullscreen(NativeWindow window, MonitorInfo monitor)
        {
            SetTopmost(window, false);
            window.AutoIconify = true;
            window.WindowState = WindowState.Normal;

            var video = GLFW.GetVideoMode(monitor.Handle.ToUnsafePtr<Monitor>());
            if (video == null) throw new InvalidOperationException("No video mode for the current monitor.");
            window.MakeFullscreen(monitor.Handle, video->Width, video->Height, video->RefreshRate);
            if (!HasMonitor(window))
            {
                throw new InvalidOperationException("The window manager did not enter fullscreen.");
            }
        }

        private static void RestoreWindow(NativeWindow window)
        {
            SetTopmost(window, false);
            window.WindowState = WindowState.Normal;

            if (HasMonitor(window))
            {
                unsafe
                {
                    GLFW.SetWindowMonitor(window.WindowPtr, null,
                        _savedClientLocation.X, _savedClientLocation.Y,
                        Math.Max(1, _savedSize.X), Math.Max(1, _savedSize.Y), GLFW.DontCare);
                }
                GLFW.PollEvents();
            }

            if (_saved)
            {
                window.WindowBorder = _savedBorder;
                window.AutoIconify = _savedAutoIconify;
                GLFW.PollEvents();
                window.ClientSize = _savedSize;
                window.Location = _savedLocation;
                if (_savedState == WindowState.Maximized)
                {
                    window.WindowState = WindowState.Maximized;
                }
            }

            Current = WindowStartMode.Windowed;
            _saved = false;
        }

        // OpenTK's IsFullscreen reads WindowState, which becomes Minimized on
        // focus loss. The monitor attachment survives iconification.
        internal static unsafe bool HasMonitor(NativeWindow window) =>
            GLFW.GetWindowMonitor(window.WindowPtr) != null;

        /// <summary>
        /// Reconcile focus changes and external/native fullscreen exits.
        /// Called once per frame on the window thread.
        /// </summary>
        public static void Sync(NativeWindow window)
        {
            if (_changing) return;

            if (Current == WindowStartMode.BorderlessFullscreen)
            {
                // This is the important Alt+Tab path: a focused borderless
                // window floats over the taskbar; an unfocused one immediately
                // drops back into the normal desktop z-order.
                SetTopmost(window, window.IsFocused);
                if (HasMonitor(window))
                {
                    // An external monitor attach changed the meaning of the
                    // mode. Reapply borderless instead of silently becoming
                    // native fullscreen.
                    Set(window, WindowStartMode.BorderlessFullscreen);
                }
                return;
            }

            if (_topmost)
            {
                SetTopmost(window, false);
            }

            if (Current == WindowStartMode.Fullscreen && !HasMonitor(window))
            {
                RestoreWindow(window);
                WindowGeometry.NoteMode();
                WindowGeometry.Note(window);
            }
        }

        private static void SetTopmost(NativeWindow window, bool topmost)
        {
            if (_topmost == topmost) return;
            _topmost = topmost;
            try
            {
                unsafe
                {
                    GLFW.SetWindowAttrib(window.WindowPtr, WindowAttribute.Floating, topmost);
                }
            }
            catch (Exception)
            {
                // Floating is best effort on X11 and has no equivalent on
                // Wayland. Failure here must never take down a match.
            }
        }

        public static string Serialize(WindowStartMode mode) => mode switch
        {
            WindowStartMode.Fullscreen => "fullscreen",
            WindowStartMode.BorderlessFullscreen => "borderless",
            _ => "windowed"
        };

        public static WindowStartMode Parse(string? value, WindowStartMode fallback)
        {
            return value?.Trim().ToLowerInvariant() switch
            {
                "fullscreen" or "exclusive" or "2" => WindowStartMode.Fullscreen,
                "borderless" or "borderless fullscreen" or "1" or "true" => WindowStartMode.BorderlessFullscreen,
                "windowed" or "window" or "0" or "false" => WindowStartMode.Windowed,
                _ => fallback
            };
        }
    }
}
