using System;

namespace MphRead.Mods.Update
{
    /// <summary>
    /// A platform that can take its own update, for the front screen to drive.
    ///
    /// Two implementations and they have almost nothing in common: a phone
    /// hands a downloaded package to the system installer and is then killed
    /// and replaced by it, and a desktop unpacks an archive over itself with
    /// the help of a second process. What they *do* share is the shape of the
    /// conversation with the player, which is the only thing the front screen
    /// wants: ask whether it is allowed, fetch while saying how far along it
    /// is, and then a step that cannot be taken back.
    ///
    /// The split between <see cref="Prepare"/> and <see cref="Install"/> is
    /// that last part. Everything that can fail belongs in Prepare, while the
    /// program is still running and can put the reason on screen; by the time
    /// Install is called there is a complete package on disk and the only
    /// thing left is the swap.
    ///
    /// Left null -- which is every platform that has neither -- the front
    /// screen opens the release page exactly as it always did.
    /// </summary>
    public interface IUpdateInstaller
    {
        /// <summary>
        /// Whether the platform will let this happen at all yet. False is not
        /// a failure: on a phone it means the player has to allow this app as
        /// an install source, once.
        /// </summary>
        bool Allowed { get; }

        /// <summary>
        /// Send the player to wherever that is granted. Nothing can wait for
        /// the answer, so the caller leaves its button pressable.
        /// </summary>
        bool RequestPermission();

        /// <summary>
        /// Fetch the release's package and get as far as the point of no
        /// return without crossing it. Reports 0 to 1, or -1 while the size is
        /// unknown. Runs off the UI thread.
        /// </summary>
        bool Prepare(UpdateInfo update, Action<float>? progress, out string error);

        /// <summary>
        /// Cross it. Returns whether the swap was *started*: on a phone what
        /// the player then chooses arrives at <see cref="Finished"/>, and on a
        /// desktop the work happens in another process after this one exits.
        /// </summary>
        bool Install(out string error);

        /// <summary>
        /// Whether the caller must now close the program.
        ///
        /// True on a desktop, where the copying process is waiting for this
        /// one to be gone before it touches anything. False on a phone, where
        /// the system kills the app itself as it replaces it -- and where
        /// quitting early would take the screen away before the player has
        /// answered the install dialog.
        /// </summary>
        bool ExitAfterInstall { get; }

        /// <summary>Told how it went, on the UI thread. Only ever on failure.</summary>
        Action<bool, string>? Finished { get; set; }
    }

    /// <summary>The installer this build has, or null. Set by the platform head.</summary>
    public static class UpdateInstall
    {
        public static IUpdateInstaller? Current { get; set; }

        /// <summary>
        /// Whether the front screen should offer to fetch and install rather
        /// than to open a page: a platform that can, and a release that
        /// actually published a file for it.
        /// </summary>
        public static bool CanInstall(UpdateInfo update) =>
            Current != null && update.AssetUrl.Length > 0;

        /// <summary>
        /// Install the desktop one if this build can use it.
        ///
        /// Called once at startup rather than set from a platform head,
        /// because there is no desktop head: the same assembly is the Windows,
        /// Linux and macOS program, and the only question is whether this copy
        /// sits somewhere it may write to. The Android head overwrites this
        /// with its own.
        /// </summary>
        public static void UseDesktopIfPossible()
        {
            if (Current == null && DesktopUpdate.Supported)
            {
                Current = new DesktopUpdateInstaller();
            }
        }
    }

    /// <summary>The desktop's half. See <see cref="DesktopUpdate"/>.</summary>
    internal sealed class DesktopUpdateInstaller : IUpdateInstaller
    {
        /// <summary>Nothing to ask for: it is this user's own directory.</summary>
        public bool Allowed => true;

        public bool RequestPermission() => true;

        public bool ExitAfterInstall => true;

        public Action<bool, string>? Finished { get; set; }

        public bool Prepare(UpdateInfo update, Action<float>? progress, out string error)
        {
            bool ok = DesktopUpdate.Stage(update, progress);
            error = ok ? "" : DesktopUpdate.LastError ?? "the download failed";
            return ok;
        }

        public bool Install(out string error)
        {
            bool ok = DesktopUpdate.Launch();
            error = ok ? "" : DesktopUpdate.LastError ?? "the update could not be started";
            return ok;
        }
    }
}
