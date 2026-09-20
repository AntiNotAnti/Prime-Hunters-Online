using System;
using System.Collections.Generic;
using System.IO;
using Android.App;
using Android.Runtime;
using Avalonia;
using MphRead.Mods;
using MphRead.Mods.Launcher;

namespace MphRead.Droid
{
    /// <summary>
    /// The process's own Android <c>Application</c>, and the one place the
    /// <see cref="AppBuilder"/> is put together.
    ///
    /// Avalonia used to build it from inside <c>MainActivity.OnCreate</c>,
    /// keyed off a generic base class -- but an activity can be destroyed and
    /// recreated any number of times behind one process, and building the
    /// toolkit again on the second one duplicated everything this does. Since
    /// Avalonia 12 the builder belongs here instead, in the object that exists
    /// exactly once per process and before any activity does.
    ///
    /// That last part is why the seams below hand out lazy lookups
    /// (<see cref="AndroidThumbnailHost"/>, <see cref="AndroidUpdateInstaller"/>)
    /// rather than the activity itself: there is no <see cref="MainActivity"/>
    /// yet when this runs, only <see cref="MainActivity.Instance"/> once one
    /// has been created.
    /// </summary>
    [Application]
    public class MainApplication : Avalonia.Android.AvaloniaAndroidApplication<AndroidApp>
    {
        public MainApplication(nint javaReference, JniHandleOwnership transfer)
            : base(javaReference, transfer)
        {
        }

        protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
        {
            // First: everything below reports through Console, and in a
            // release build that goes nowhere unless this is installed.
            AndroidConsole.Install();
            // The package's own directory is read-only on Android, so both the
            // preferences and paths.txt move. They move to *external* files
            // rather than internal ones because the extracted game files are
            // hundreds of megabytes a player has to copy onto the device
            // themselves, and this is the directory they can reach over USB
            // without the app asking for a storage permission.
            string root = ChooseRoot();
            if (root.Length > 0)
            {
                LauncherPrefs.Directory = root;
                GameFiles.Root = root;
                try
                {
                    // Upstream's Paths reads paths.txt relative to the working
                    // directory, so the two have to agree.
                    Directory.SetCurrentDirectory(root);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[android] could not use {root} as the working directory: {ex.Message}");
                }
            }
            // Before the front screen, which lists the rooms: the custom maps
            // have to be out of the package and their directory named before
            // anything reads the room tables, since that list is built once.
            AndroidMaps.Install(Assets, root);
            // Before the front screen is built (see AndroidApp.OnFrameworkInitializationCompleted,
            // which defers that to the activity's own creation): the screen
            // asks whether previews can be rendered while it is being
            // constructed, and on the desktop the same seam is left empty so
            // the batch of worker processes answers instead.
            ThumbnailHost.Current = new AndroidThumbnailHost();
            // Also before the front screen: it decides whether its update
            // entry fetches and installs or opens a page while it is being
            // laid out. A phone is the one platform where the browser round
            // trip is worth removing -- no file manager can reach the app's
            // own download directory, and the system has an installer that
            // does the whole job. See Mods/Update/UpdateInstall.cs.
            MphRead.Mods.Update.UpdateInstall.Current = new AndroidUpdateInstaller();
            // Same shape, and for the same kind of reason: the front screen's
            // Share button exists only where something can receive a file, and
            // on a phone that is the whole answer to "send me your logs" --
            // the app's own directory is one no file manager will browse. This
            // one can take the application context directly: nothing it does
            // needs an activity.
            MphRead.Mods.LogShare.Current = new AndroidLogShare(this);
            // And the third of the same shape: there is no browser to start as
            // a process here and no display variable to prove there is a
            // desktop, so without this the support mark and the credits page's
            // ko-fi link both did nothing. See Mods/Platform/WebLink.cs.
            MphRead.Mods.Platform.WebLink.Current = new AndroidWebLink(this);
            // The hunter the launcher asks about, as the real model rather
            // than as the silhouette in boxes HunterStand falls back to. The
            // desktop draws it into the game window under its screens, which
            // is a window this head does not have. See AndroidHunterShot.
            MphRead.Mods.Render.HunterShot.Current = new AndroidHunterShot();
            ScreenCapture.PngWriter = AndroidPng.Write;
            return base.CustomizeAppBuilder(builder).WithInterFont();
        }

        /// <summary>
        /// The directory everything writable lives in.
        ///
        /// External files by preference, because the extracted game files are
        /// hundreds of megabytes a player copies over USB and that is the
        /// directory they can reach. But a non-null answer from
        /// <c>GetExternalFilesDir</c> is not a promise that it can be used:
        /// early after install it returns the path before the volume is ready,
        /// and every write to it is refused. Taking it on trust is how one
        /// launch put its files internally, the next one looked externally,
        /// and the game appeared to lose the files the player had copied.
        ///
        /// So: whichever already holds a paths.txt wins, and otherwise the
        /// first one that can actually be written to.
        /// </summary>
        private string ChooseRoot()
        {
            var candidates = new List<string>();
            string? external = GetExternalFilesDir(null)?.AbsolutePath;
            if (!String.IsNullOrEmpty(external))
            {
                candidates.Add(external);
            }
            string? internalFiles = FilesDir?.AbsolutePath;
            if (!String.IsNullOrEmpty(internalFiles))
            {
                candidates.Add(internalFiles);
            }
            foreach (string candidate in candidates)
            {
                if (Writable(candidate) && File.Exists(Path.Combine(candidate, "paths.txt")))
                {
                    return candidate;
                }
            }
            foreach (string candidate in candidates)
            {
                if (Writable(candidate))
                {
                    if (candidate != candidates[0])
                    {
                        Console.WriteLine($"[android] {candidates[0]} cannot be written to; using {candidate}");
                    }
                    return candidate;
                }
            }
            return "";
        }

        private static bool Writable(string directory)
        {
            try
            {
                Directory.CreateDirectory(directory);
                string probe = Path.Combine(directory, ".write-probe");
                File.WriteAllBytes(probe, Array.Empty<byte>());
                File.Delete(probe);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
