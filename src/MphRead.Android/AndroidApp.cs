using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using MphRead.Mods;
using MphRead.Mods.Launcher;
using MphRead.Mods.Launcher.Gui;

namespace MphRead.Droid
{
    /// <summary>
    /// The Avalonia application on Android.
    ///
    /// A phone has one view rather than a desktop full of windows, so this is a
    /// single view lifetime -- and the view it shows is <see cref="StartScreen"/>,
    /// the desktop front screen itself. Not a copy of it, not a phone-shaped
    /// rewrite of it: the same file, which opens every other screen on one
    /// stack over the picture -- the same stack the desktop uses, since there
    /// is no second window to open anything in here.
    /// A change to the launcher is a change to both platforms,
    /// which is the whole reason this is Avalonia.
    ///
    /// What is left here is the front half of the loop the desktop's
    /// <c>GuiLauncher</c> runs: read the settings, ask the screen, start what it
    /// asked for. The difference is that a match is a view swap in
    /// <see cref="MainActivity"/> rather than a window on this thread.
    /// </summary>
    public class AndroidApp : Application
    {
        /// <summary>The front screen, for the activity to drive after a match.</summary>
        internal static StartScreen? Home { get; private set; }

        public override void Initialize()
        {
            // Before anything else builds a screen. Android has no console to
            // print a stack into and the process is gone by the time anybody
            // could look, so a crash writes a file the log-sharing entry can
            // hand out -- see Mods/CrashReport.cs, which exists because the
            // same fault on Windows was invisible for the same reason.
            CrashReport.Install();
            Android.Runtime.AndroidEnvironment.UnhandledExceptionRaiser += (_, e) =>
            {
                // The Java side's own: an exception crossing back out of a
                // callback the runtime invoked. AppDomain never sees these.
                CrashReport.Report(e.Exception, "android");
            };
            Styles.Add(new FluentTheme());
            RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
            base.Initialize();
        }

        public override void OnFrameworkInitializationCompleted()
        {
            // A factory, not the view itself: this runs from MainApplication,
            // Android's Application.OnCreate, before any activity exists, and
            // BuildHome reaches things -- ThumbnailHost.Current's activity,
            // MainActivity.Instance -- that are only there once one has been
            // created. Avalonia's own activity is what calls this factory,
            // from MainActivity.OnCreate, which is late enough.
            if (ApplicationLifetime is IActivityApplicationLifetime activity)
            {
                activity.MainViewFactory = () =>
                {
                    // Wrapped, not handed over bare. The screens in
                    // Mods/Launcher/Gui are authored for a box near 960x600
                    // points and a phone in landscape is about 830x390, so
                    // without the scale host every one of them is laid out
                    // three times too large for the view it is in -- which is
                    // a front screen with its words off the edges and a
                    // server browser arranged somewhere off the side of the
                    // display. The desktop scales the same screens the same
                    // way; see UiScaleHost.
                    Home = BuildHome();
                    return new UiScaleHost(Home);
                };
            }
            else if (ApplicationLifetime is ISingleViewApplicationLifetime single)
            {
                Home = BuildHome();
                single.MainView = new UiScaleHost(Home);
            }
            base.OnFrameworkInitializationCompleted();
        }

        private static StartScreen BuildHome()
        {
            LauncherPrefs.Load();
            // Keys, mouse feel, pad bindings and the touch layout. The
            // desktop reads these from ModEntry.TryHandleHeadless, which the
            // head never runs -- so nothing here ever loaded them, and every
            // control the player changed was back to its default the next
            // time the app opened. After LauncherPrefs.Load, since
            // controls.txt sits in the directory that has just been named.
            Mods.InputSettings.Load();
            // After the load, because the head has just pointed the
            // preferences at the app's own data directory -- the package's
            // directory is read-only, and that is also where the log has to
            // go. Same switch, same file, same corner of the same screen as
            // the desktop's.
            Mods.DebugLog.Attach();
            MenuSettings settings = GameState.LoadSettings();
            GameSettings.Apply(settings);
            IReadOnlyList<string> rooms = Array.Empty<string>();
            if (GameFiles.Ready)
            {
                // The room list is read out of the extracted files, and the
                // launcher runs before upstream's own setup check does.
                GameFiles.ApplyPaths();
                rooms = ThumbnailGenerator.MultiplayerRooms();
            }
            var home = new StartScreen(settings, rooms);
            home.Done += (_, plan) =>
            {
                if (plan.Kind == LaunchKind.None)
                {
                    MainActivity.Instance?.Finish();
                    return;
                }
                MainActivity.Instance?.StartMatch(plan);
            };
            home.MatchRequested += (_, plan) => MainActivity.Instance?.StartMatch(plan);
            return home;
        }
    }
}
