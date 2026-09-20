using System;
using System.IO;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Provider;

namespace MphRead.Droid
{
    /// <summary>
    /// Hand a downloaded APK to Android's own installer.
    ///
    /// The whole of what this does is put the system's install dialog in front
    /// of the player. It does not install anything itself and it cannot: an
    /// ordinary app has no such power, and the one confirmation Android shows
    /// is the thing standing between "a release was published" and "every
    /// phone runs it". That is deliberate here rather than merely accepted --
    /// see <c>Mods/Update/Updater.cs</c> for the argument.
    ///
    /// **The file is never touched by the player.** It is downloaded into this
    /// app's own cache, which since Android 11 no file manager can browse, and
    /// no file manager is involved: the installer activity is started from
    /// here, so a tap on "Update available" leads straight to the system
    /// dialog.
    ///
    /// <c>PackageInstaller</c> rather than the older
    /// <c>Intent.ACTION_INSTALL_PACKAGE</c>, which is what the obvious
    /// examples use and is deprecated. Three things come of it: no
    /// <c>FileProvider</c> is needed at all, since the bytes are written into
    /// the session rather than exposed through a content URI; the result comes
    /// back as a status with a *reason*, which matters because the failure
    /// this will actually hit is a signing mismatch and "App not installed" on
    /// its own would be unanswerable; and if this is ever made to install
    /// without asking, it is one flag on the same code rather than a second
    /// implementation.
    /// </summary>
    internal static class ApkInstaller
    {
        /// <summary>
        /// Told the result of the last commit, on the UI thread. Set by
        /// whoever asked for the install; the receiver below is a separate
        /// object with no way back to it otherwise.
        /// </summary>
        public static Action<bool, string>? Finished { get; set; }

        /// <summary>Where a downloaded package waits. Cleared as it is replaced.</summary>
        public static string StagingPath(Context context)
        {
            string directory = Path.Combine(
                context.CacheDir?.AbsolutePath ?? Path.GetTempPath(), "update");
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, "update.apk");
        }

        /// <summary>
        /// Whether this app is allowed to be an install source.
        ///
        /// Android 8 replaced the old global "unknown sources" switch with a
        /// per-app one, and it is off until the player turns it on. There is
        /// no way to ask for it in a dialog: it is a Settings screen, and the
        /// only thing an app can do is open it. See
        /// <see cref="RequestPermission"/>.
        /// </summary>
        public static bool Allowed(Context context)
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(26))
            {
                return true;
            }
            return context.PackageManager?.CanRequestPackageInstalls() == true;
        }

        /// <summary>
        /// Open the per-app install-source setting. The player turns it on and
        /// comes back; nothing here can wait for that, so the caller's job is
        /// to leave the button where it was so it can be pressed again.
        /// </summary>
        public static bool RequestPermission(Activity activity)
        {
            try
            {
                var intent = new Intent(Settings.ActionManageUnknownAppSources,
                    Android.Net.Uri.Parse("package:" + activity.PackageName));
                activity.StartActivity(intent);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[update] could not open the install-source setting: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Whether the downloaded package is signed by the same certificate as
        /// this one, which is what Android requires of an update.
        ///
        /// Checked here as well, because Android's own refusal is the words
        /// "App not installed" and nothing else. The failure is not
        /// hypothetical: every release signed with a build machine's debug key
        /// carries a different certificate, so this is the answer somebody
        /// coming from one of those builds will get, and it needs to say so.
        ///
        /// Unreadable is not the same as different. A package whose signatures
        /// cannot be read at all is let through to Android, which is the
        /// authority on this and will refuse it if it is wrong.
        /// </summary>
        public static bool SameSigner(Context context, string apkPath, out string? mismatch)
        {
            mismatch = null;
            try
            {
                PackageManager? manager = context.PackageManager;
                if (manager == null)
                {
                    return true;
                }
#pragma warning disable CA1422, CS0618 // the flags overloads that exist on every version we target
                PackageInfo? installed = manager.GetPackageInfo(
                    context.PackageName!, PackageInfoFlags.Signatures);
                PackageInfo? downloaded = manager.GetPackageArchiveInfo(
                    apkPath, PackageInfoFlags.Signatures);
                System.Collections.Generic.IList<Signature>? mine = installed?.Signatures;
                System.Collections.Generic.IList<Signature>? theirs = downloaded?.Signatures;
#pragma warning restore CA1422, CS0618
                if (mine == null || theirs == null || mine.Count == 0 || theirs.Count == 0)
                {
                    return true;
                }
                foreach (Signature ours in mine)
                {
                    foreach (Signature other in theirs)
                    {
                        if (ours.Equals(other))
                        {
                            return true;
                        }
                    }
                }
                mismatch = "that download is signed with a different key, so Android "
                    + "will not install it over this copy. Install it by hand once.";
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[update] could not compare signatures: {ex.Message}");
                return true;
            }
        }

        /// <summary>
        /// Write the package into an install session and commit it.
        ///
        /// Committing does not install: it asks, and Android answers through
        /// <see cref="InstallResultReceiver"/> -- first with
        /// <c>STATUS_PENDING_USER_ACTION</c>, which is the dialog, and then
        /// with what the player chose. Nothing here blocks on any of that.
        ///
        /// A commit that the player accepts replaces this app, and Android
        /// kills the process to do it. There is no supported way to survive
        /// that, so whatever has to be on disk must already be on disk before
        /// this is called.
        /// </summary>
        public static bool Commit(Context context, string apkPath, out string error)
        {
            error = "";
            if (!File.Exists(apkPath))
            {
                error = "the download is not there";
                return false;
            }
            PackageInstaller? installer = context.PackageManager?.PackageInstaller;
            if (installer == null)
            {
                error = "this device has no package installer";
                return false;
            }
            PackageInstaller.Session? session = null;
            try
            {
                var parameters = new PackageInstaller.SessionParams(
                    PackageInstallMode.FullInstall);
                parameters.SetAppPackageName(context.PackageName);
                int sessionId = installer.CreateSession(parameters);
                session = installer.OpenSession(sessionId);
                using (Stream into = session.OpenWrite("package", 0, new FileInfo(apkPath).Length))
                using (var from = File.OpenRead(apkPath))
                {
                    from.CopyTo(into, 256 * 1024);
                    session.Fsync(into);
                }
                var intent = new Intent(context, typeof(InstallResultReceiver));
                // Mutable, and it has to be: the installer fills the status
                // and the confirmation intent into this before sending it, and
                // an immutable one arrives empty on API 31 and up.
                var pending = PendingIntent.GetBroadcast(context, sessionId, intent,
                    OperatingSystem.IsAndroidVersionAtLeast(31)
                        ? PendingIntentFlags.Mutable | PendingIntentFlags.UpdateCurrent
                        : PendingIntentFlags.UpdateCurrent);
                session.Commit(pending!.IntentSender!);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Console.WriteLine($"[update] the install session failed: {ex}");
                return false;
            }
            finally
            {
                session?.Close();
            }
        }
    }

    /// <summary>
    /// Android's answer to a commit.
    ///
    /// Not exported: only the package installer sends this, through the
    /// PendingIntent handed to <see cref="ApkInstaller.Commit"/>.
    /// </summary>
    [BroadcastReceiver(Exported = false, Enabled = true)]
    internal sealed class InstallResultReceiver : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent)
        {
            if (intent == null)
            {
                return;
            }
            var status = (PackageInstallStatus)intent.GetIntExtra(
                PackageInstaller.ExtraStatus, (int)PackageInstallStatus.Failure);
            string message = intent.GetStringExtra(PackageInstaller.ExtraStatusMessage) ?? "";
            if (status == PackageInstallStatus.PendingUserAction)
            {
                // The dialog. It arrives as an intent to start rather than as
                // something shown for us, and it is started from a receiver,
                // so it needs its own task.
                Intent? confirm = OperatingSystem.IsAndroidVersionAtLeast(33)
                    ? intent.GetParcelableExtra(Intent.ExtraIntent, Java.Lang.Class.FromType(typeof(Intent)))
                        as Intent
#pragma warning disable CA1422 // the pre-33 way, for the devices that need it
                    : intent.GetParcelableExtra(Intent.ExtraIntent) as Intent;
#pragma warning restore CA1422
                if (confirm == null)
                {
                    Report(false, "Android did not offer the install dialog");
                    return;
                }
                confirm.AddFlags(ActivityFlags.NewTask);
                try
                {
                    context?.StartActivity(confirm);
                }
                catch (Exception ex)
                {
                    Report(false, ex.Message);
                }
                return;
            }
            if (status == PackageInstallStatus.Success)
            {
                // Rarely seen: a successful install replaces this app, and the
                // process is killed to do it. Reported anyway, because a
                // *silent* install would land here with the app still running.
                Report(true, "installed");
                return;
            }
            Report(false, Explain(status, message));
        }

        /// <summary>
        /// The status, in words somebody can act on.
        ///
        /// <see cref="PackageInstallStatus.FailureConflict"/> is the one worth
        /// spelling out: it is what a differently-signed package produces, and
        /// "conflicts with an existing package" sends people looking for a
        /// duplicate install that is not there.
        /// </summary>
        private static string Explain(PackageInstallStatus status, string message)
        {
            return status switch
            {
                PackageInstallStatus.FailureAborted => "the update was cancelled",
                PackageInstallStatus.FailureConflict =>
                    "Android refused it: the download is signed with a different key "
                    + "than the copy installed. Install it by hand once.",
                PackageInstallStatus.FailureStorage => "there is not enough room for it",
                PackageInstallStatus.FailureIncompatible => "that package is not for this device",
                PackageInstallStatus.FailureBlocked => "the device blocked the install",
                PackageInstallStatus.FailureInvalid =>
                    message.Length > 0 ? message : "the package could not be read",
                _ => message.Length > 0 ? message : "the install failed"
            };
        }

        private static void Report(bool ok, string message)
        {
            Console.WriteLine($"[update] install: {(ok ? "ok" : "failed")} -- {message}");
            Action<bool, string>? finished = ApkInstaller.Finished;
            if (finished == null)
            {
                return;
            }
            MainActivity? activity = MainActivity.Instance;
            if (activity != null)
            {
                activity.RunOnUiThread(() => finished(ok, message));
            }
            else
            {
                finished(ok, message);
            }
        }
    }
}
