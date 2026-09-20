using System;

namespace MphRead.Mods.Platform
{
    /// <summary>
    /// A platform that opens an address in whatever it uses for a browser.
    ///
    /// The seam exists for the same reason <see cref="Mods.ILogShare"/>'s
    /// does: the shared code must not know what an Android intent is.
    /// <see cref="Update.Updater"/> asks this first and falls back to the
    /// desktop's own answers -- <c>ShellExecute</c>, <c>open</c>,
    /// <c>xdg-open</c> -- when nothing is installed.
    ///
    /// Android is the one head that needs it, and it needed it badly: there
    /// is no <c>DISPLAY</c> and no <c>WAYLAND_DISPLAY</c> on a phone, so the
    /// Linux branch answered "there is no browser here" and the support mark
    /// on the front screen did nothing at all.
    /// </summary>
    public interface IWebLink
    {
        /// <summary>
        /// Open it. False when the platform refused, so the caller can put
        /// the address on screen instead of appearing to do nothing.
        /// </summary>
        bool Open(string url);
    }

    /// <summary>The opener this build has, or null. Set by the platform head.</summary>
    public static class WebLink
    {
        public static IWebLink? Current { get; set; }
    }
}
