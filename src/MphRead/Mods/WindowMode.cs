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
    /// Native monitor fullscreen, with desktop-resolution borderless and normal
    /// fullscreen focus policies. GLFW owns taskbar/Dock coverage and display
    /// restoration; a floating, almost-monitor-sized window cannot provide that.
    /// </summary>
    public static class WindowMode
    {
        public static WindowStartMode Startup { get; set; } = WindowStartMode.Windowed;
        public static bool StartupForced { get; private set; }
        public static WindowStartMode Current { get; private set; } = WindowStartMode.Windowed;
        public static bool IsFullscreen => Current != WindowStartMode.Windowed;

        private static WindowStartMode _lastFullscreen = WindowStartMode.Fullscreen;
        private static Vector2i _savedLocation, _savedClientLocation, _savedSize;
        private static WindowState _savedState;
        private static bool _savedAutoIconify, _saved;
        private static NativeWindow? _window;
        private static bool _changing;

        public static Vector2i WindowedSize => _saved ? _savedSize : Vector2i.Zero;
        public static Vector2i WindowedLocation => _savedLocation;
        public static bool WindowedMaximized => _saved && _savedState == WindowState.Maximized;

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
            if (_changing || (Current == mode && IsFullscreen == HasMonitor(window))) return;
            _changing = true;
            try
            {
                if (mode == WindowStartMode.Windowed)
                {
                    RestoreWindow(window);
                }
                else if (IsFullscreen && HasMonitor(window))
                {
                    // Both modes use the current desktop resolution. Switching
                    // focus policy must not replace the saved windowed rectangle.
                    window.AutoIconify = mode == WindowStartMode.Fullscreen;
                    Current = _lastFullscreen = mode;
                }
                else
                {
                    // Resolve the display before changing window state. Entering
                    // fullscreen never moves the game to the primary monitor.
                    MonitorInfo monitor = Monitors.GetMonitorFromWindow(window);
                    _window = window;
                    _savedState = window.WindowState;
                    _savedAutoIconify = window.AutoIconify;
                    Current = mode; // Resize callbacks must not save monitor geometry.
                    if (_savedState == WindowState.Maximized)
                    {
                        window.WindowState = WindowState.Normal;
                        GLFW.PollEvents();
                    }
                    _savedLocation = window.Location;
                    _savedClientLocation = window.ClientLocation;
                    _savedSize = window.ClientSize;
                    _saved = true;
                    window.AutoIconify = mode == WindowStartMode.Fullscreen;
                    // Match the desktop video mode for full display coverage
                    // without a resolution switch, including Retina displays.
                    unsafe
                    {
                        var video = GLFW.GetVideoMode(monitor.Handle.ToUnsafePtr<Monitor>());
                        if (video == null) throw new InvalidOperationException("No video mode for the current monitor.");
                        window.MakeFullscreen(monitor.Handle, video->Width, video->Height, video->RefreshRate);
                    }
                    if (!HasMonitor(window))
                        throw new InvalidOperationException("The window manager did not enter fullscreen.");
                    _lastFullscreen = mode;
                }
                WindowGeometry.NoteMode();
            }
            catch
            {
                RestoreWindow(window);
                throw;
            }
            finally { _changing = false; }
        }

        private static void RestoreWindow(NativeWindow window)
        {
            // Minimized fullscreen still owns its monitor, but OpenTK's cached
            // state no longer says Fullscreen. Explicitly detach that case too.
            window.WindowState = WindowState.Normal;
            if (HasMonitor(window))
            {
                unsafe
                {
                    GLFW.SetWindowMonitor(window.WindowPtr, null,
                        _savedClientLocation.X, _savedClientLocation.Y,
                        _savedSize.X, _savedSize.Y, GLFW.DontCare);
                }
            }
            GLFW.PollEvents();
            if (_saved)
            {
                window.AutoIconify = _savedAutoIconify;
                window.ClientSize = _savedSize;
                window.Location = _savedLocation;
                if (_savedState == WindowState.Maximized) window.WindowState = WindowState.Maximized;
            }
            Current = WindowStartMode.Windowed;
            _saved = false;
        }

        // OpenTK's IsFullscreen reads WindowState, which becomes Minimized on
        // focus loss. The monitor attachment survives iconification.
        internal static unsafe bool HasMonitor(NativeWindow window) => GLFW.GetWindowMonitor(window.WindowPtr) != null;

        /// <summary>Reconcile a monitor unplug or an external fullscreen exit.</summary>
        public static void Sync(NativeWindow window)
        {
            if (!_changing && IsFullscreen && !HasMonitor(window))
            {
                RestoreWindow(window);
                WindowGeometry.NoteMode();
                WindowGeometry.Note(window);
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
