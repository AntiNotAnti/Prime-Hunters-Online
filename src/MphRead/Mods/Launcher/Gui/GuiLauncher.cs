using System;
using System.Collections.Generic;
using Avalonia;
#if MPHREAD_SHELL
using Avalonia.Headless;
#endif
using Avalonia.Themes.Fluent;
using MphRead.Mods.Network;

namespace MphRead.Mods.Launcher.Gui
{
    /// <summary>
    /// Setting the toolkit up, and the way in to the launcher.
    ///
    /// The loop itself is <c>Shell</c>'s: one window for the whole
    /// program, the front screen drawn inside it, a match loaded into it and
    /// unloaded again. What is left here is the decision nobody else can make
    /// -- whether there is a toolkit on this machine at all -- and the
    /// fallback when there is not.
    ///
    /// The toolkit is set up **once, on the thread that calls in**, which is
    /// the game's own thread and the one the GL context belongs to. Three
    /// things make that the right shape and not an optimisation:
    ///
    /// - Avalonia allows one application per process. A second
    ///   <c>AppBuilder.Setup</c> throws, so a launcher that stood one up per
    ///   visit worked exactly once and fell back to the text screen on the way
    ///   back from the first match.
    /// - macOS will not accept UI work off the main thread. AppKit is not
    ///   thread-safe, which rules out the private UI thread the WinForms
    ///   launcher used.
    /// - The screens are rendered in the middle of the game's frame, by the
    ///   thread drawing it. Nothing else can pump the dispatcher they post
    ///   their work to.
    ///
    /// The backend is headless plus Skia rather than the platform's own: the
    /// screens are drawn into a buffer and composited into the game window
    /// (<c>UiSurface</c>, <c>Mods.Render.UiOverlay</c>), so this
    /// program opens exactly one window on every platform.
    /// </summary>
    public static class GuiLauncher
    {
        private static bool _setUp;
        private static bool _failed;

        /// <summary>
        /// Show the launcher, or say why it could not be shown.
        ///
        /// Returns false when there is no usable display -- a machine with no X
        /// or Wayland session, an SSH login without forwarding, a container, or
        /// a system missing the client libraries Avalonia binds. That is not an
        /// error worth stopping for: the text launcher does the same job, and
        /// falling back to it is the difference between "this build has no
        /// launcher on my machine" and "this build does not start".
        /// </summary>
        public static bool TryRun()
        {
            if (!EnsureSetup())
            {
                return false;
            }
            try
            {
#if MPHREAD_SHELL
                return Shell.Run();
#else
                // Android reaches its screens through the activity, not
                // through here; this class only stands the toolkit up there.
                return false;
#endif
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[launcher] the window could not be opened: {ex.Message}");
                Console.WriteLine("[launcher] falling back to the text launcher");
                Mods.Diagnostics.PlatformDiagnostics.Report("libglfw.3.dylib", ex);
                return false;
            }
        }

        /// <summary>
        /// Stand the toolkit up, once per process, on this thread.
        ///
        /// Also what the pause menu calls: in a session started from a command
        /// line rather than from the launcher, nothing has set the toolkit up
        /// and the first Escape is where it is needed.
        /// </summary>
        internal static bool EnsureSetup(bool requireDisplay = true)
        {
            if (_setUp)
            {
                return true;
            }
            // The compatibility diagnostic only rasterizes offscreen. Normal
            // launcher callers still need a display for the game's GLFW window.
            if (_failed || (requireDisplay && !Probe()))
            {
                return false;
            }
            try
            {
#if !MPHREAD_SHELL
                // Android stands the toolkit up itself, from the activity,
                // with a real windowing backend and its screens in a view over
                // the GL surface. Nothing here runs there: this is the desktop
                // arrangement, and the head in src/MphRead.Android is the
                // entry point instead.
                _setUp = false;
                return false;
#else
                // The *headless* backend, deliberately, on every desktop
                // platform: the screens are rendered into a buffer and drawn
                // inside the game window (UiSurface, UiOverlay), so this
                // program opens exactly one window and the launcher is a
                // screen in the game rather than an application beside it.
                //
                // Skia is asked for explicitly because headless defaults to
                // drawing nothing at all -- it is a unit-testing backend by
                // origin, and UseHeadlessDrawing false is what turns the real
                // renderer back on. Nothing else changes: the same controls,
                // the same layout, the same fonts.
                AppBuilder.Configure<LauncherApp>()
                    .UseSkia()
                    .UseHeadless(new AvaloniaHeadlessPlatformOptions
                    {
                        UseHeadlessDrawing = false
                    })
                    .WithInterFont()
                    .SetupWithoutStarting();
                // Before anything asks for a render loop: the backend's own
                // timer renders the whole surface from inside RunJobs, which
                // the frame calls whether or not it wants a redraw. See
                // UiRenderTimer.
                UiRenderTimer.Install();
                _setUp = true;
                return true;
#endif
            }
            catch (Exception ex)
            {
                // Remembered, because everything that asks is in a loop or a
                // frame: a toolkit that could not start on this machine must be
                // asked once, not once a frame.
                _failed = true;
                Console.WriteLine($"[launcher] the window toolkit could not start: {ex.Message}");
                // The whole stack, into the debug log, because the message
                // alone is usually a type name from inside Skia or the X11
                // backend and says nothing about which library is missing.
                Mods.Diagnostics.PlatformDiagnostics.Report("libSkiaSharp.dylib", ex);
                SayWhyOnLinux();
                return false;
            }
        }

        /// <summary>
        /// What a Linux player who has just landed in the text launcher needs
        /// to be told.
        ///
        /// Falling back is the right behaviour -- the text launcher plays the
        /// same game -- but it is also indistinguishable from "this build has
        /// no window on my machine", and the reason is nearly always the same
        /// one: the graphical launcher binds a handful of desktop libraries
        /// that the game itself does not, so a system that runs the match
        /// perfectly well can still have no launcher. Reported from NixOS,
        /// where a prebuilt Linux binary finds no library at the path it was
        /// linked against at all.
        /// </summary>
        private static void SayWhyOnLinux()
        {
            if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                || OperatingSystem.IsAndroid())
            {
                return;
            }
            Console.WriteLine("[launcher] the game itself is unaffected -- the text launcher "
                + "below starts the same matches.");
            Console.WriteLine("[launcher] the screens need fontconfig, which a minimal install "
                + "sometimes lacks:");
            Console.WriteLine("[launcher]   Debian/Ubuntu: sudo apt install libfontconfig1");
            Console.WriteLine("[launcher]   Fedora: sudo dnf install fontconfig");
            Console.WriteLine("[launcher]   NixOS/Guix: run it inside an FHS environment, "
                + "e.g. steam-run ./ProjectPrime -launcher");
            // libICE and libSM used to be on this list, because the launcher
            // was an X11 window. It is drawn inside the game window now and
            // binds no windowing libraries of its own at all.
        }

        /// <summary>
        /// Is there a display at all? Checked before Avalonia is initialised
        /// rather than by catching its failure, because the failure is a native
        /// abort in some configurations and there is nothing to catch.
        /// </summary>
        private static bool Probe()
        {
            if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
            {
                return true;
            }
            string? display = Environment.GetEnvironmentVariable("DISPLAY");
            string? wayland = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
            if (String.IsNullOrEmpty(display) && String.IsNullOrEmpty(wayland))
            {
                Console.WriteLine("[launcher] no DISPLAY or WAYLAND_DISPLAY; "
                    + "using the text launcher");
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// The Avalonia application object. Fluent is here for the handful of stock
    /// controls the screens use -- the text boxes and the scroll bars; every
    /// other control on them is drawn by this code, because a launcher whose
    /// controls are half themed reads as broken rather than as a choice.
    /// </summary>
    internal sealed class LauncherApp : Application
    {
        public override void Initialize()
        {
            Styles.Add(new FluentTheme());
            RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
            base.Initialize();
        }
    }
}
