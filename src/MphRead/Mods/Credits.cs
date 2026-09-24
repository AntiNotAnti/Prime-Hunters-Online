using System;
using System.Collections.Generic;

namespace MphRead.Mods
{
    /// <summary>
    /// Runtime attribution, following the repository README's Credits section.
    /// Keep the project lineage and required dependency acknowledgements together.
    /// </summary>
    public static class Credits
    {
        public readonly record struct Entry(string Who, string What, string Where);

        public const string Author = "Livetek";
        public const string ForkWork = "Development of Fruity Prime, an earlier fork in Project Prime's development lineage.";

        public static string Summary =>
            $"{Branding.Name} is a community-developed continuation of Metroid Prime Hunters, built on the work of the projects and developers credited below.";

        public static Entry Foundation { get; } = new("NoneGiven",
            "MphRead: the foundational Metroid Prime Hunters recreation, model viewer, renderer, file format parsers and related systems.",
            "https://github.com/NoneGiven/MphRead");

        /// <summary>
        /// The attribution as a corner of a screen can carry it: the fork, and
        /// then everyone else on one line.
        ///
        /// The full list is <c>-credits</c>. This is not a shortened version of
        /// it that could fall out of step -- the names come from the same array,
        /// so adding an entry there puts it here too.
        /// </summary>
        public static string Compact =>
            $"{Summary}"
            + $"\nBuilt on {Branding.Upstream} by NoneGiven · {Names}";

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
                return $"{Author} (Fruity Prime) · " + String.Join(" · ", names);
            }
        }

        public static IReadOnlyList<Entry> Entries { get; } = new[]
        {
            Foundation,
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
            new Entry("OpenAL Soft", "audio playback", "https://github.com/kcat/openal-soft"),
            new Entry("SoundFlow", "audio", "https://github.com/LSXPrime/SoundFlow"),
            new Entry("Indian Type Foundry", "Rajdhani: tactical headings and controls. SIL Open Font License 1.1; "
                + "unmodified SemiBold and Bold fonts, with license bundled in the application.",
                "https://github.com/itfoundry/rajdhani"),
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
            Console.WriteLine($"  Fruity Prime — {Author}");
            Console.WriteLine($"      {ForkWork}");
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
