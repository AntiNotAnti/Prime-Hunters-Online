using System;
using Android.Content;
using MphRead.Mods.Platform;

namespace MphRead.Droid
{
    /// <summary>
    /// Opening an address on a phone: an <c>ACTION_VIEW</c> intent, which the
    /// system hands to whatever the player has set as their browser.
    ///
    /// Without this the front screen's support mark and the credits page's
    /// link both did nothing at all. <see cref="MphRead.Mods.Update.Updater"/>
    /// reaches for <c>xdg-open</c> on anything that is not Windows or macOS,
    /// and refuses first when neither <c>DISPLAY</c> nor <c>WAYLAND_DISPLAY</c>
    /// is set -- which on Android is always.
    ///
    /// <c>NewTask</c> because the context this is given is the application's
    /// rather than an activity's: a task-less start from one is refused.
    /// </summary>
    internal sealed class AndroidWebLink : IWebLink
    {
        private readonly Context _context;

        public AndroidWebLink(Context context)
        {
            _context = context;
        }

        public bool Open(string url)
        {
            try
            {
                Context context = (Context?)MainActivity.Instance ?? _context;
                var intent = new Intent(Intent.ActionView, Android.Net.Uri.Parse(url));
                if (context is not Android.App.Activity)
                {
                    intent.AddFlags(ActivityFlags.NewTask);
                }
                context.StartActivity(intent);
                return true;
            }
            catch (Exception ex)
            {
                // A device with no browser at all, which is rare and not this
                // program's problem beyond saying so: the caller puts the
                // address on the screen instead.
                Console.WriteLine($"[android] could not open {url}: {ex.Message}");
                return false;
            }
        }
    }
}
