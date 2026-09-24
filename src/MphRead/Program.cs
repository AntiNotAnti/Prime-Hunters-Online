using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using MphRead.Export;
using MphRead.Formats.Sound;

namespace MphRead
{
    internal static class Program
    {
        public static Version Version { get; } = new Version(0, 35, 1, 0);
        private static readonly Version _minExtractVersion = new Version(0, 19, 0, 0);

        private static void Main(string[] args)
        {
            // First, before anything that can throw. A Windows game build is a
            // GUI binary with no console, so without this a fault anywhere in
            // startup is a process that exits with no window, no message and
            // no file: "I double-click it and nothing happens".
            Mods.CrashReport.Install();
            try
            {
                Run(args);
            }
            catch (Exception ex)
            {
                // The main thread's own. UnhandledException is raised for it
                // too, but only after the runtime has already printed to a
                // stderr that a GUI build does not have.
                Mods.CrashReport.Report(ex, "startup");
                Environment.ExitCode = 1;
            }
        }

        private static void Run(string[] args)
        {
            ConsoleSetup.Run();
            // A console only if this run is going to use one: the Windows
            // build is a GUI binary, so double-clicking it opens the launcher
            // with no terminal behind it.
            if (OperatingSystem.IsWindows())
            {
                Mods.ConsoleWindow.Prepare(args);
            }
            // Before the ordinary client setup check: metadata-only replay tools,
            // the directory/master server and other headless diagnostics can run
            // without extracted game data. A dedicated *game* server is also
            // dispatched here, but its own authoritative startup explicitly
            // validates paths.txt/game files and refuses to run without them.
            if (Mods.ModEntry.TryHandleHeadless(args))
            {
                return;
            }
            if (CheckSetup(args))
            {
                return;
            }
            IReadOnlyList<Argument> arguments = ParseArguments(args);
            if (Mods.ModEntry.TryHandle(args))
            {
                return;
            }
            if (arguments.Count == 0)
            {
                //using var renderer = new RenderWindow();
                //renderer.AddRoom("MP3 PROVING GROUND");
                //renderer.AddModel("Crate01");
                //renderer.Run();
                Menu.ShowMenuPrompts();
            }
            else if (arguments.Any(a => a.Name == "setup"))
            {
                foreach (string path in Directory.EnumerateFiles(Paths.Combine(Paths.FileSystem, "archives")))
                {
                    Read.ExtractArchive(Path.GetFileNameWithoutExtension(path));
                }
            }
            else if (TryGetString(arguments, "export", "e", out string? exportValue))
            {
                if (exportValue.ToLower() == "layer2d")
                {
                    Images.ExportHudLayers();
                }
                else if (exportValue.ToLower() == "object2d")
                {
                    Images.ExportHudObjects();
                }
                else if (exportValue.ToLower() == "sfx")
                {
                    SoundRead.ExportSamples();
                }
                else if (exportValue.ToLower() == "wfs")
                {
                    SoundRead.ExportWfsSamples();
                }
                else if (exportValue.ToLower() == "strm")
                {
                    SoundRead.ExportStreams();
                }
                else if (exportValue.ToLower() == "fhsfx")
                {
                    SoundRead.ExportAllFh();
                }
                else if (exportValue.ToLower() == "movie")
                {
                    TryGetArgument(arguments, "export", "e", out Argument? exportArgument);
                    if (exportArgument!.Value.ValueTwo != null)
                    {
                        Formats.VxDecoder.Instance1.Export(exportArgument!.Value.ValueTwo).GetAwaiter().GetResult();
                    }
                    else
                    {
                        Formats.VxDecoder.Instance1.ExportAll().GetAwaiter().GetResult();
                    }
                }
                else
                {
                    bool firstHunt = arguments.Any(a => a.Name == "fh");
                    Read.ReadAndExport(exportValue, firstHunt);
                }
            }
            else if (TryGetString(arguments, "extract", "x", out string? extractValue))
            {
                Read.ExtractArchive(extractValue);
            }
            else
            {
                var rooms = new List<string>();
                var models = new List<(string, int)>();
                GameMode mode = GameMode.None;
                int playerCount = 0;
                BossFlags bossFlags = BossFlags.None;
                int nodeLayerMask = 0;
                int entityLayerId = -1;
                if (TryGetInt(arguments, "room", "r", out int roomId))
                {
                    RoomMetadata? meta = Metadata.GetRoomById(roomId);
                    if (meta == null)
                    {
                        Exit();
                    }
                    rooms.Add(meta.Name);
                }
                else if (TryGetString(arguments, "room", "r", out string? roomName))
                {
                    rooms.Add(roomName);
                }
                if (TryGetInt(arguments, "mode", "g", out int modeValue))
                {
                    mode = (GameMode)modeValue;
                }
                if (TryGetInt(arguments, "players", "p", out int playerValue))
                {
                    playerCount = playerValue;
                }
                if (TryGetInt(arguments, "boss", "b", out int bossValue))
                {
                    bossFlags = (BossFlags)bossValue;
                }
                if (TryGetInt(arguments, "node", "n", out int nodeValue))
                {
                    nodeLayerMask = nodeValue;
                }
                if (TryGetInt(arguments, "entity", "l", out int entityValue))
                {
                    entityLayerId = entityValue;
                }
                foreach ((string, int) pair in GetPairs(arguments, "model", "m"))
                {
                    models.Add(pair);
                }
                if (rooms.Count > 1 || (rooms.Count == 0 && models.Count == 0))
                {
                    Exit();
                }
                using var renderer = new RenderWindow();
                foreach (string room in rooms)
                {
                    renderer.AddRoom(room, mode, playerCount, bossFlags, nodeLayerMask, entityLayerId);
                }
                bool firstHunt = arguments.Any(a => a.Name == "fh");
                foreach ((string model, int recolor) in models)
                {
                    renderer.AddModel(model, recolor, firstHunt);
                }
                renderer.Run();
            }
        }

        private static bool CheckSetup(string[] args)
        {
            if (File.Exists("paths.txt") && !CheckVersion())
            {
                Console.WriteLine($"Your paths.txt file is not compatible with this version of {Mods.Branding.Name} and needs to be recreated.");
                Console.WriteLine("It is recommended that you delete the file as well as any extracted game files, " +
                    "then perform setup again.");
                Console.WriteLine();
                Console.WriteLine("Press any key to exit...");
                ConsoleSetup.PauseIfInteractive();
                return true;
            }
            if (args.Length == 1 && !args[0].StartsWith('-') && File.Exists(args[0]))
            {
                // Use the same structural compatibility gate as the launcher,
                // so drag-and-drop accepts padded/trimmed compatible dumps but
                // still refuses unsupported revisions and rebuilt layouts.
                if (!Mods.Launcher.RomCompatibility.TryIdentify(
                    args[0], out string? label, out string? problem))
                {
                    Console.WriteLine(problem ?? "That .nds file is not compatible with this build.");
                    Console.WriteLine("Nothing was extracted.");
                    Console.WriteLine();
                    Console.WriteLine("Press any key to exit...");
                    ConsoleSetup.PauseIfInteractive();
                    return true;
                }
                Console.WriteLine($"Recognised: Metroid Prime Hunters, {label}");
                Extract.Setup(args[0]);
                return true;
            }
            if (!File.Exists("paths.txt"))
            {
                Console.WriteLine("Could not find the paths.txt file.");
                Console.WriteLine($"You may need to perform first-time setup by dragging a ROM onto the {Mods.Branding.Executable} executable.");
                Console.WriteLine();
                Console.WriteLine("Press any key to exit...");
                ConsoleSetup.PauseIfInteractive();
                return true;
            }
            Paths.UpdatePaths();
            Paths.ChooseMphPath();
            Paths.ChooseFhPath();
            return false;
        }

        private static bool CheckVersion()
        {
            string text = File.ReadAllText("paths.txt").Split('\n')[0].Trim();
            if (Version.TryParse(text, out Version? extractVersion))
            {
                return extractVersion >= _minExtractVersion;
            }
            return false;
        }

        private readonly struct Argument
        {
            public readonly string Name;
            public readonly string? ValueOne;
            public readonly string? ValueTwo;

            public Argument(string name, string? valueOne, string? valueTwo = null)
            {
                Name = name;
                ValueOne = valueOne;
                ValueTwo = valueTwo;
            }
        }

        private static IEnumerable<(string, int)> GetPairs(IEnumerable<Argument> arguments, string fullName, string shortName)
        {
            foreach (Argument argument in arguments.Where(a => a.Name == fullName || a.Name == shortName))
            {
                if (argument.ValueOne != null)
                {
                    Int32.TryParse(argument.ValueTwo, out int valueTwo);
                    yield return (argument.ValueOne, valueTwo);
                }
            }
        }

        private static bool TryGetArgument(IEnumerable<Argument> arguments, string fullName, string shortName,
            [NotNullWhen(true)] out Argument? argument)
        {
            IEnumerable<Argument> matches = arguments.Where(a => a.Name == fullName || a.Name == shortName);
            if (matches.Any())
            {
                argument = matches.First();
                return true;
            }
            argument = null;
            return false;
        }

        private static bool TryGetString(IEnumerable<Argument> arguments, string fullName, string shortName,
            [NotNullWhen(true)] out string? value)
        {
            if (TryGetArgument(arguments, fullName, shortName, out Argument? argument) && argument.Value.ValueOne != null)
            {
                value = argument.Value.ValueOne;
                return true;
            }
            value = null;
            return false;
        }

        private static bool TryGetInt(IEnumerable<Argument> arguments, string fullName, string shortName,
            out int value)
        {
            if (TryGetString(arguments, fullName, shortName, out string? stringValue))
            {
                if (Int32.TryParse(stringValue, out int intValue))
                {
                    value = intValue;
                    return true;
                }
            }
            value = 0;
            return false;
        }

        private static IReadOnlyList<Argument> ParseArguments(string[] args)
        {
            var arguments = new List<Argument>();
            if (args != null)
            {
                for (int i = 0; i < args.Length; i++)
                {
                    string arg = args[i];
                    if (arg.StartsWith('-') && arg.Length > 1)
                    {
                        arg = arg[1..];
                        if (i == args.Length - 1)
                        {
                            arguments.Add(new Argument(arg, null));
                        }
                        else
                        {
                            string valueOne = args[i + 1];
                            if (valueOne.StartsWith('-'))
                            {
                                arguments.Add(new Argument(arg, null));
                            }
                            else
                            {
                                string? valueTwo = null;
                                if (i < args.Length - 2 && !args[i + 2].StartsWith('-'))
                                {
                                    valueTwo = args[i + 2];
                                    i++;
                                }
                                arguments.Add(new Argument(arg, valueOne, valueTwo));
                                i++;
                            }
                        }
                    }
                }
            }
            return arguments;
        }

        [DoesNotReturn]
        private static void Exit()
        {
            Nop();
            Console.WriteLine($"{Mods.Branding.Executable} usage:");
            Console.WriteLine("    -room <room_name -or- room_id>");
            Console.WriteLine("    -model <model_name> [recolor_index]");
            Console.WriteLine("At most one room may be specified. Any number of models may be specified.");
            Console.WriteLine("To load First Hunt models, include -fh in the argument list.");
            Console.WriteLine("Available room options: -mode, -players, -boss, -node, -entity");
            Console.WriteLine("- or -");
            Console.WriteLine("    -extract <archive_path>");
            Console.WriteLine("If the target archive is LZ10-compressed, it will be decompressed.");
            Console.WriteLine("- or -");
            Console.WriteLine("    -export <target_name>");
            Console.WriteLine("The export target may be a model or room name.");
            Environment.Exit(1);
        }

        private static void Nop() { }
    }

    public class ProgramException : Exception
    {
        public ProgramException(string message) : base(message) { }
    }
}
