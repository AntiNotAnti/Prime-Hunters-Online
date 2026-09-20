using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace MphRead.Mods.Diagnostics
{
    /// <summary>Startup checks without game data, a display, or an audio device.</summary>
    public static class CompatibilityCheck
    {
        public static int Run()
        {
            Console.WriteLine($"{Branding.Name} compatibility smoke test");
            Console.WriteLine($"Runtime: {RuntimeInformation.FrameworkDescription}");
            Console.WriteLine($"OS: {RuntimeInformation.OSDescription}");
            Console.WriteLine($"Architecture: {RuntimeInformation.ProcessArchitecture}");
            Console.WriteLine($"RID: {RuntimeInformation.RuntimeIdentifier}");
            PlatformDiagnostics.Start();
            int failures = 0;
            void Check(string name, Action action)
            {
                try
                {
                    action();
                    Console.WriteLine($"[OK] {name}");
                }
                catch (Exception ex)
                {
                    failures++;
                    Console.Error.WriteLine($"[FAIL] {name} ({RuntimeInformation.ProcessArchitecture}): {ex}");
                }
            }
            Check("configuration", () =>
            {
                Launcher.LauncherPrefs.Load();
                InputSettings.Load();
                if (OperatingSystem.IsMacOS() &&
                    Launcher.LauncherPrefs.Directory == Platform.AppPaths.ExecutableDirectory)
                {
                    throw new InvalidOperationException("macOS writes must use the user-data directory.");
                }
                string probe = Path.Combine(Launcher.LauncherPrefs.Directory, $".smoke-{Guid.NewGuid():N}");
                try
                {
                    File.WriteAllText(probe, "configuration write probe");
                    if (OperatingSystem.IsMacOS() && !File.Exists(Path.GetFileName(probe)))
                    {
                        throw new InvalidOperationException("Relative writes must also use user data.");
                    }
                }
                finally { File.Delete(probe); }
            });
#if MPHREAD_SHELL
            Check("Avalonia", () =>
            {
                if (!Launcher.Gui.GuiLauncher.EnsureSetup(requireDisplay: false))
                {
                    throw new InvalidOperationException("Avalonia initialization failed.");
                }
            });
            Check("Skia", () =>
            {
                using var bitmap = new SkiaSharp.SKBitmap(2, 2);
                bitmap.Erase(SkiaSharp.SKColors.Green);
                if (bitmap.GetPixel(0, 0) != SkiaSharp.SKColors.Green)
                {
                    throw new InvalidOperationException("Skia rasterization failed.");
                }
            });
            Check("launcher resources", () =>
            {
                foreach (string resource in new[] { "project-prime-mark.png",
                    "Fonts/heyNovember.ttf", "Fonts/Roboto-Bold.ttf", "Backgrounds/launcher-bg.jpg" })
                {
                    using Stream stream = Avalonia.Platform.AssetLoader.Open(
                        new Uri($"avares://ProjectPrime/Assets/{resource}"));
                    if (stream.ReadByte() < 0)
                    {
                        throw new InvalidDataException($"Empty launcher resource: {resource}");
                    }
                }
            });
#endif
            if (OperatingSystem.IsMacOS())
            {
                foreach ((string file, string symbol) in new[] {
                    ("libopenal.1.dylib", "alcOpenDevice"),
                    ("libglfw.3.dylib", "glfwGetVersion"),
                    ("libminiaudio.dylib", "ma_version_string"),
                    ("libSkiaSharp.dylib", "sk_version_get_milestone"),
                    ("libHarfBuzzSharp.dylib", "hb_version_string"),
                    ("libAvaloniaNative.dylib", "CreateAvaloniaNative") })
                {
                    Check(file, () => LoadNative(file, symbol));
                }
#if !MPHREAD_SERVER
                Check("OpenAL bindings", () =>
                {
                    string path = Path.Combine(Platform.AppPaths.ExecutableDirectory, "libopenal.1.dylib");
                    if (new OpenTK.Audio.OpenAL.OpenALLibraryNameContainer().GetLibraryName() != path)
                    {
                        throw new InvalidOperationException("OpenTK is not configured to use bundled OpenAL Soft.");
                    }
                    IntPtr handle = NativeLibrary.Load(path);
                    try
                    {
                        _ = OpenTK.Audio.OpenAL.ALC.GetCurrentContext();
                        // Resolve through the game's binding as well as dlopen.
                        // A null device needs neither audio hardware nor a context.
                        IntPtr bound = OpenTK.Audio.OpenAL.ALC.GetProcAddress(
                            OpenTK.Audio.OpenAL.ALDevice.Null, "alcOpenDevice");
                        if (bound != NativeLibrary.GetExport(handle, "alcOpenDevice"))
                        {
                            throw new InvalidOperationException("OpenTK resolved a different OpenAL library.");
                        }
                    }
                    finally { NativeLibrary.Free(handle); }
                });
#endif
                Check("GLFW bindings", () =>
                {
                    OpenTK.Windowing.GraphicsLibraryFramework.GLFW.GetVersion(out int major, out _, out _);
                    if (major < 3) { throw new InvalidOperationException("Unsupported GLFW version."); }
                });
            }
            Check("maps", () =>
            {
                string maps = MapGen.CustomRooms.MapDirectory;
                if (!Directory.Exists(maps) || !Directory.EnumerateFiles(maps, "*", SearchOption.AllDirectories)
                    .Any(path => Path.GetExtension(path) is ".ppmap" or ".json"))
                {
                    throw new DirectoryNotFoundException($"No bundled maps found in {maps}");
                }
                Console.WriteLine($"Maps: {maps}");
            });
            Console.WriteLine(failures == 0 ? "Smoke test passed." : $"Smoke test failed ({failures} checks).");
            return failures == 0 ? 0 : 1;
        }

        private static void LoadNative(string file, string symbol)
        {
            string path = Path.Combine(Platform.AppPaths.ExecutableDirectory, file);
            Console.WriteLine($"Loading {path}");
            IntPtr handle = NativeLibrary.Load(path);
            try { _ = NativeLibrary.GetExport(handle, symbol); }
            finally { NativeLibrary.Free(handle); }
        }
    }
}
