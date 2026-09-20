using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace MphRead.Mods.Diagnostics
{
    internal static class PlatformDiagnostics
    {
        private static string LogPath => Path.Combine(Platform.AppPaths.UserDataDirectory,
            "logs", "platform-startup.log");

        public static void Start()
        {
            var message = new StringBuilder();
            message.AppendLine($"{Branding.Name} {Update.BuildVersion.Display}");
            message.AppendLine($"OS: {RuntimeInformation.OSDescription}");
            message.AppendLine($"OS architecture: {RuntimeInformation.OSArchitecture}");
            message.AppendLine($"Process architecture: {RuntimeInformation.ProcessArchitecture}");
            message.AppendLine($"RID: {RuntimeInformation.RuntimeIdentifier}");
            message.AppendLine($"Runtime: {RuntimeInformation.FrameworkDescription}");
            message.AppendLine($"Base directory: {Platform.AppPaths.ExecutableDirectory}");
            message.AppendLine($"Launch directory: {ConsoleSetup.LaunchDirectory}");
            message.AppendLine($"Current directory: {Environment.CurrentDirectory}");
            message.AppendLine($"User data: {Platform.AppPaths.UserDataDirectory}");
            if (OperatingSystem.IsMacOS())
            {
                foreach (string library in new[] { "libopenal.1.dylib", "libglfw.3.dylib",
                    "libSkiaSharp.dylib", "libAvaloniaNative.dylib", "libminiaudio.dylib" })
                {
                    message.AppendLine($"Native: {Path.Combine(Platform.AppPaths.ExecutableDirectory, library)}");
                }
                Persist(message.ToString(), append: false);
                AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                    Report("unhandled startup/runtime failure", e.ExceptionObject as Exception
                        ?? new Exception(e.ExceptionObject.ToString()));
            }
            Console.Write(message);
        }

        public static void Report(string library, Exception exception)
        {
            if (!OperatingSystem.IsMacOS())
            {
                library = library switch
                {
                    "libopenal.1.dylib" => OperatingSystem.IsWindows() ? "openal32.dll" : "libopenal.so.1",
                    "libglfw.3.dylib" => OperatingSystem.IsWindows() ? "glfw3.dll" : "libglfw.so.3",
                    "libSkiaSharp.dylib" => OperatingSystem.IsWindows() ? "libSkiaSharp.dll" : "libSkiaSharp.so",
                    "libminiaudio.dylib" => OperatingSystem.IsWindows() ? "miniaudio.dll" : "libminiaudio.so",
                    _ => library
                };
            }
            string path = Path.Combine(Platform.AppPaths.ExecutableDirectory, library);
            string message = $"[native] requested={library}; location={path}; "
                + $"process={RuntimeInformation.ProcessArchitecture}; binary={Describe(path)}\n{exception}\n";
            Console.Error.Write(message);
            DebugLog.Exception("native", exception);
            if (OperatingSystem.IsMacOS()) { Persist(message, append: true); }
        }

        private static string Describe(string path)
        {
            if (!File.Exists(path)) { return "not present at the installation path"; }
            if (!OperatingSystem.IsMacOS()) { return "see loader exception"; }
            try
            {
                var start = new ProcessStartInfo("/usr/bin/file")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true
                };
                start.ArgumentList.Add("-b");
                start.ArgumentList.Add(path);
                using Process? process = Process.Start(start);
                if (process != null && process.WaitForExit(2000))
                {
                    return process.StandardOutput.ReadToEnd().Trim();
                }
                process?.Kill();
            }
            catch (Exception) { /* Diagnostics must preserve the original error. */ }
            return "architecture unavailable";
        }

        private static void Persist(string message, bool append)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                if (append) { File.AppendAllText(LogPath, message); }
                else { File.WriteAllText(LogPath, message); }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Could not write platform log: {ex.Message}");
            }
        }
    }
}
