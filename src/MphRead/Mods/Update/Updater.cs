using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace MphRead.Mods.Update
{
    /// <summary>
    /// Discover a newer release from the public GitHub release channel.
    ///
    /// The launcher checks in the background. Platforms with an installer use
    /// the exact asset URL and SHA-256 digest GitHub returned; other platforms
    /// fall back to the release page. No GitHub credential is stored in or
    /// shipped with the client.
    ///
    /// A server refuses incompatible protocol versions at Hello, so getting a
    /// player onto the same published build is part of multiplayer reliability,
    /// not merely a convenience feature.
    /// </summary>
    public static class Updater
    {
        /// <summary>Set by -noupdate, for anybody who wants none of this.</summary>
        public static bool Disabled { get; set; }

        /// <summary>
        /// What the last check found, or null. Held so a front screen can ask
        /// once in the background and read the answer whenever it draws.
        /// </summary>
        public static UpdateInfo? Available { get; private set; }

        /// <summary>True once a check has finished, whatever it found.</summary>
        public static bool Checked { get; private set; }

        /// <summary>Look now, on this thread.</summary>
        public static UpdateInfo? Check(CancellationToken cancel = default)
        {
            if (Disabled)
            {
                return null;
            }
            Available = UpdateCheck.Latest(cancel);
            Checked = true;
            return Available;
        }

        /// <summary>
        /// Look in the background and call back if there is something.
        ///
        /// The front screens use this: the check reaches across the internet
        /// and a launcher that will not draw until GitHub answers is a launcher
        /// that looks broken on a bad connection.
        /// </summary>
        /// <param name="done">
        /// Called when the check has finished whatever it found, including
        /// when it found nothing. A screen that only hears about a *new*
        /// release can say "there is an update" and can never say "you are on
        /// the latest", because the two answers are the same silence -- and
        /// showing somebody a green "up to date" that only means "nobody has
        /// asked yet" is the one wrong thing a version line can do.
        /// </param>
        public static void CheckInBackground(Action<UpdateInfo> found, Action? done = null)
        {
            if (Disabled)
            {
                return;
            }
            Task.Run(() =>
            {
                try
                {
                    UpdateInfo? update = Check();
                    if (update != null)
                    {
                        found(update.Value);
                    }
                }
                catch (Exception)
                {
                    // A background check is never worth an exception reaching
                    // anybody; LastReason already holds whatever went wrong.
                }
                finally
                {
                    done?.Invoke();
                }
            });
        }

        /// <summary>
        /// Wait a little for a background check to land, and give up quietly.
        ///
        /// For a screen that is drawn once and then waits for input: the check
        /// usually takes well under a second, and a menu that has already been
        /// printed cannot grow a line afterwards. Bounded low on purpose --
        /// a slow answer costs the wait and nothing else, because the entry
        /// still appears the next time the screen is drawn.
        /// </summary>
        public static void WaitForCheck(TimeSpan limit)
        {
            if (Disabled)
            {
                return;
            }
            DateTime until = DateTime.UtcNow + limit;
            while (!Checked && DateTime.UtcNow < until)
            {
                Thread.Sleep(50);
            }
        }

        /// <summary>
        /// "Update now": open the release's page in the browser.
        ///
        /// Returns false when there is no way to open one -- a machine with no
        /// desktop session, or a handler that refused -- so the caller can put
        /// the address on screen instead of appearing to do nothing.
        /// </summary>
        public static bool OpenPage(UpdateInfo update) => OpenUrl(
            update.PageUrl.Length > 0 ? update.PageUrl : UpdateCheck.ReleasesPage);

        /// <summary>
        /// Open any https address the same way, for the credits page's support
        /// link. Same refusal of anything that is not https, and the same
        /// "there is no browser here" answer, as the update page.
        /// </summary>
        public static bool OpenLink(string url) => OpenUrl(url);

        private static bool OpenUrl(string url)
        {
            if (!url.StartsWith("https://", StringComparison.Ordinal))
            {
                return false;
            }
            try
            {
                // The platform's own, where there is one: a phone has no
                // browser to start as a process and no display variable to
                // answer the question below with.
                if (Mods.Platform.WebLink.Current is Mods.Platform.IWebLink link)
                {
                    return link.Open(url);
                }
                if (OperatingSystem.IsWindows())
                {
                    // UseShellExecute is what hands the address to whatever the
                    // user has set as their browser; without it this would try
                    // to execute the URL as a program.
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                    return true;
                }
                if (OperatingSystem.IsMacOS())
                {
                    Process.Start("open", new[] { url });
                    return true;
                }
                // xdg-open is the freedesktop way in and is present on any
                // machine with a desktop on it. On one without, there is
                // nothing to open and the caller prints the address.
                if (String.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))
                    && String.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
                {
                    return false;
                }
                Process.Start("xdg-open", new[] { url });
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>One line for a console, a log, or a server's startup.</summary>
        public static string Describe(UpdateInfo update)
        {
            string which = update.AssetName.Length > 0
                ? $" -- you want {update.AssetName}"
                : "";
            return $"{update.Tag} is available (this is {BuildVersion.Display}){which}";
        }
    }
}
