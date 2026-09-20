#if MPHREAD_SHELL
using System;
using System.IO;
using MphRead.Mods.Launcher;

namespace MphRead.Mods.Diagnostics
{
    /// <summary>Check native initialization without a GPU or cartridge data.</summary>
    internal static class GlfwPathCheck
    {
        public static int Run()
        {
            string previousDirectory = Directory.GetCurrentDirectory();
            string previousRoot = GameFiles.Root;
            string fixture = Directory.CreateTempSubdirectory("fruity-extraction-").FullName;
            try
            {
                Directory.SetCurrentDirectory(fixture);
                // macOS can canonicalize /var to /private/var in getcwd.
                fixture = Directory.GetCurrentDirectory();
                GameFiles.Root = fixture;
                Directory.CreateDirectory(Path.Combine(fixture, "files", Ver.AMHE1));
                File.WriteAllText("paths.txt", $"{Program.Version}\n{Ver.AMHE1}=files/{Ver.AMHE1}\n");
                if (GameFiles.Problem() is string before)
                    throw new InvalidOperationException($"Invalid path fixture: {before}");

                // NativeWindowSettings initializes GLFW/monitors, but does not
                // create a GL context. In a .app this used to switch to Resources.
                var settings = ThumbnailCapture.WindowSettings(64, 64);
                var version = OperatingSystem.IsMacOS() ? new Version(2, 1) : new Version(3, 2);
                var profile = OperatingSystem.IsMacOS()
                    ? OpenTK.Windowing.Common.ContextProfile.Any
                    : OpenTK.Windowing.Common.ContextProfile.Compatability;
                if (settings.APIVersion != version || settings.Profile != profile
                    || settings.Flags != OpenTK.Windowing.Common.ContextFlags.Default
                    || settings.StartVisible)
                    throw new InvalidOperationException("Thumbnail worker requested an incompatible GL context.");
                Console.WriteLine($"Thumbnail context policy passed: {version}, {profile}.");
                if (Directory.GetCurrentDirectory() != fixture)
                    throw new InvalidOperationException("GLFW changed the extraction working directory.");
                if (GameFiles.Problem() is string after)
                    throw new InvalidOperationException($"Extracted files disappeared after GLFW initialization: {after}");
                Console.WriteLine("GLFW extraction path check passed.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[glfwpathcheck] {ex}");
                return 1;
            }
            finally
            {
                Directory.SetCurrentDirectory(previousDirectory);
                GameFiles.Root = previousRoot;
                Paths.UpdatePaths();
                Directory.Delete(fixture, recursive: true);
            }
        }
    }
}
#endif
