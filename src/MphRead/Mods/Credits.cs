using System;
using System.Collections.Generic;

namespace MphRead.Mods
{
    /// <summary>
    /// Who this is built on.
    ///
    /// Fruity Prime is a fork of NoneGiven's MphRead, which is itself built on
    /// the work of several other projects; the list below is the one in
    /// upstream's README, kept here so that it is in the program a player runs
    /// and not only in a file on GitHub. The multiplayer, the launcher and the
    /// dedicated server are what this fork adds. Everything that makes the game
    /// run at all is upstream's or its sources'.
    /// </summary>
    public static class Credits
    {
        public readonly record struct Entry(string Who, string What, string Where);

        /// <summary>Who made this fork, as distinct from what it is a fork of.</summary>
        public const string Author = "Livetek";

        /// <summary>What this fork adds, in one line.</summary>
        public const string ForkWork = "this fork: multiplayer and the dedicated server, "
            + "the launcher, custom maps, the Android head and the pro HUD";

        /// <summary>
        /// Where to say thank you, for anyone who wants to.
        ///
        /// Https only and opened through the same path the update check uses,
        /// which refuses anything else and reports when there is no browser to
        /// open rather than appearing to do nothing.
        /// </summary>
        public const string SupportUrl = "https://ko-fi.com/livetek";

        public static string Summary =>
            $"{Branding.Name} is {Author}'s fork of {Branding.Upstream} by NoneGiven.";

        /// <summary>
        /// The attribution as a corner of a screen can carry it: the fork, and
        /// then everyone else on one line.
        ///
        /// The full list is <c>-credits</c>. This is not a shortened version of
        /// it that could fall out of step -- the names come from the same array,
        /// so adding an entry there puts it here too.
        /// </summary>
        public static string Compact =>
            $"A fork of {Branding.Upstream} by NoneGiven"
            + $"\n{Names}";

        /// <summary>Everyone but upstream, separated for one line.</summary>
        public static string Names
        {
            get
            {
                var names = new List<string>();
                foreach (Entry entry in Entries)
                {
                    // NoneGiven is named on the line above rather than buried
                    // in the middle of the list.
                    if (entry.Who != "NoneGiven")
                    {
                        names.Add(entry.Who);
                    }
                }
                return String.Join(" · ", names);
            }
        }

        public static IReadOnlyList<Entry> Entries { get; } = new[]
        {
            new Entry("NoneGiven", "MphRead: the model viewer, scene renderer, "
                + "format parsers and gameplay recreation this is built on",
                "https://github.com/NoneGiven/MphRead"),
            new Entry("dsgraph", "the original MPH model viewer, on which all "
                + "other projects are built", ""),
            new Entry("chmcl95", "documentation of the model format",
                "https://gitlab.com/ch-mcl/metroid-prime-hunters-file-document"),
            new Entry("McKay42", "COLLADA export method (mph-model-viewer) and "
                + "ARC file format information (mph-arc-extractor)",
                "https://github.com/McKay42"),
            new Entry("Barubary", "LZ10 compression routines (dsdecmp)",
                "https://github.com/Barubary/dsdecmp"),
            new Entry("loveemu", "SWAV conversion function (swav2wav)",
                "https://github.com/loveemu/loveemu-lab"),
            new Entry("Gericom", "ActImagine VX movie file format information, "
                + "via an ffmpeg patch", ""),
            new Entry("CharlesVanEeckhout", "further understanding of VX video "
                + "decoding", "https://github.com/CharlesVanEeckhout/actimagine"),
            new Entry("CyberBotX", "NCSF converter and player for Nintendo DS "
                + "sequenced music", "https://github.com/CyberBotX/NCSF"),
            new Entry("hackyourlife", "mph-viewer, developed in parallel; the "
                + "transparency rendering was derived from its source",
                "https://github.com/hackyourlife/mph-viewer"),
            new Entry("OpenTK", "the OpenGL bindings the renderer uses",
                "https://github.com/opentk/opentk"),
            new Entry("OpenAL Soft and SoundFlow", "audio",
                "https://github.com/LSXPrime/SoundFlow"),
            // CC BY 4.0 asks for this by name, so it is an entry rather than a
            // line in a file beside the data.
            new Entry("DB-IP", "IP geolocation, for the flags in the server "
                + "browser (DB-IP Lite, CC BY 4.0)", "https://db-ip.com")
        };

        /// <summary>The whole thing, for a console or a log.</summary>
        public static void Print()
        {
            Console.WriteLine();
            Console.WriteLine($"  {Branding.NameAndVersion}");
            Console.WriteLine($"  {Summary}");
            Console.WriteLine();
            Console.WriteLine($"  {Author}");
            Console.WriteLine($"      {ForkWork}");
            Console.WriteLine($"      support this project: {SupportUrl}");
            Console.WriteLine();
            Console.WriteLine("  A significant portion of this project's code is based on the");
            Console.WriteLine("  file format information or source code of these projects:");
            Console.WriteLine();
            foreach (Entry entry in Entries)
            {
                Console.WriteLine($"  {entry.Who}");
                Console.WriteLine($"      {entry.What}");
                if (entry.Where.Length > 0)
                {
                    Console.WriteLine($"      {entry.Where}");
                }
            }
            Console.WriteLine();
            Console.WriteLine("  Metroid Prime Hunters is Nintendo's. No game data is included");
            Console.WriteLine("  with this program: it is unpacked from your own cartridge dump.");
            Console.WriteLine();
        }
    }
}
