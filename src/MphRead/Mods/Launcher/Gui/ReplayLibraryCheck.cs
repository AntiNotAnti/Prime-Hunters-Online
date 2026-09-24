#if MPHREAD_AVALONIA
using System;
using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Threading;

namespace MphRead.Mods.Launcher.Gui;

internal static class ReplayLibraryCheck
{
    internal static int Run(string directory)
    {
        try
        {
            if (!GuiLauncher.EnsureSetup()) throw new InvalidOperationException("No UI backend.");
            Directory.CreateDirectory(directory);
            Dispatcher.UIThread.Invoke(() =>
            {
                foreach (int count in new[] { 100, 500, 1000, 5000 })
                {
                    using var view = new TheatreWorkspace(manageStorage: false);
                    long started = Stopwatch.GetTimestamp();
                    view.LoadCheckEntries(count);
                    double populate = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    if (!UiCapture.Capture(view, Path.Combine(directory, $"library-{count}.png"), new Size(1280,720)))
                        throw new InvalidOperationException("Library capture failed.");
                    if (view.ShownCount != count || view.MaximumRealizedRows is <= 0 or >= 100)
                        throw new InvalidOperationException($"Library failed virtualization: {count} entries, {view.MaximumRealizedRows} controls.");
                    started = Stopwatch.GetTimestamp(); view.SearchCheck($"Replay {count-1:D5}");
                    double search = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    if (view.ShownCount != 1) throw new InvalidOperationException("Library memory search returned the wrong rows.");
                    Console.WriteLine($"[replaylibrary] {count} entries: populate {populate:0.00} ms, search {search:0.00} ms, {view.MaximumRealizedRows} realized rows");
                }
            });
            Console.WriteLine("[replaylibrary] PASS: 100/500/1000/5000-entry virtualization and in-memory filtering.");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("[replaylibrary] FAIL: " + ex); return 1; }
    }
}
#endif
