using System;
using System.Text;

namespace MphRead.Mods.Launcher
{
    /// <summary>
    /// How far the extraction has got, read off what it prints.
    ///
    /// **The total is not known in advance.** Upstream's setup walks the ROM's
    /// directory tree and writes as it goes; nothing counts the files first,
    /// and adding a counting pass would mean reading the whole cartridge twice
    /// to draw a nicer bar. So this is milestone-driven rather than a
    /// percentage of anything: each phase the extraction announces has a band,
    /// and within a band the bar creeps towards that band's ceiling without
    /// ever reaching it.
    ///
    /// The consequences are worth stating, because a progress bar that lies is
    /// worse than none. It never goes backwards, it never sits at 100% while
    /// work continues, and the *rate* inside a band means nothing -- a band
    /// that is nearly full only means that phase has printed a lot of lines,
    /// not that it is nearly done. What it is good for is the thing a person
    /// actually wants during a five-minute extraction: knowing it is alive and
    /// roughly where it has got to.
    /// </summary>
    public sealed class SetupProgress
    {
        private readonly struct Band
        {
            public Band(double start, double end, double scale, string stage)
            {
                Start = start;
                End = end;
                Scale = scale;
                Stage = stage;
            }

            public double Start { get; }
            public double End { get; }
            /// <summary>Lines it takes to cross most of the band.</summary>
            public double Scale { get; }
            public string Stage { get; }
        }

        // Tuned to the shape of a real extraction: the file tree is by far the
        // longest phase and prints one line per directory, the archives are a
        // few dozen, and the decompression is a handful. They stop at 0.72
        // because rendering the map previews follows and is part of the same
        // wait -- a bar that filled and then left the player watching a
        // seemingly idle screen for another minute was the worst of both.
        private static readonly Band _files = new(0.03, 0.40, 45, "Writing game files");
        private static readonly Band _archives = new(0.40, 0.57, 25, "Unpacking archives");
        private static readonly Band _sound = new(0.57, 0.63, 3, "Converting music");
        private static readonly Band _binaries = new(0.63, 0.72, 6, "Decompressing code");

        /// <summary>
        /// The one phase whose total *is* known: every preview run counts its
        /// rooms, so this band is a real fraction rather than a creep.
        /// </summary>
        private const double _previewStart = 0.72;

        private Band _band = new(0, 0.03, 1, "Starting");
        private int _seen;

        /// <summary>0 to 1. Only ever increases.</summary>
        public double Fraction { get; private set; }

        /// <summary>What is happening, for a caption beside the bar.</summary>
        public string Stage { get; private set; } = "Starting";

        /// <summary>True when the extraction has finished and the bar is full.</summary>
        public bool Done { get; private set; }

        /// <summary>
        /// Take one line of the child's output. Returns true when the bar or
        /// the caption changed, so a caller can avoid redrawing for nothing.
        /// </summary>
        public bool Observe(string line)
        {
            if (Done)
            {
                return false;
            }
            if (TryPreviewCount(line, out int done, out int total) && total > 0)
            {
                _band = new Band(_previewStart, 1, 1, $"Rendering map previews ({done}/{total})");
                return Set(_previewStart + (1 - _previewStart) * done / total, _band.Stage);
            }
            Band next = Classify(line);
            if (next.Stage != _band.Stage)
            {
                // A new phase starts at its own floor, which is also what stops
                // the bar going backwards when a band's creep has overshot the
                // next band's start.
                _band = next;
                _seen = 0;
            }
            _seen++;
            double span = _band.End - _band.Start;
            // Asymptotic: crossing the band takes ever more lines, so a phase
            // with more files than expected slows down instead of finishing
            // early and then waiting at the ceiling.
            double eased = 1 - Math.Exp(-_seen / _band.Scale);
            return Set(_band.Start + span * eased, _band.Stage);
        }

        /// <summary>The extraction finished. Fill the bar.</summary>
        public void Finish(bool ok)
        {
            Done = true;
            Fraction = 1;
            Stage = ok ? "Ready to play" : "Setup did not finish";
        }

        /// <summary>
        /// "[thumbnails] 8/33 ...", which every preview run prints whichever
        /// platform and however many workers produced it.
        /// </summary>
        private static bool TryPreviewCount(string line, out int done, out int total)
        {
            done = 0;
            total = 0;
            const string prefix = "[thumbnails] ";
            if (!line.StartsWith(prefix, StringComparison.Ordinal))
            {
                return false;
            }
            ReadOnlySpan<char> rest = line.AsSpan(prefix.Length);
            int slash = rest.IndexOf('/');
            if (slash <= 0)
            {
                return false;
            }
            ReadOnlySpan<char> after = rest[(slash + 1)..];
            int end = 0;
            while (end < after.Length && Char.IsAsciiDigit(after[end]))
            {
                end++;
            }
            return Int32.TryParse(rest[..slash], out done)
                && end > 0 && Int32.TryParse(after[..end], out total);
        }

        private Band Classify(string line)
        {
            if (line.StartsWith("Writing ", StringComparison.Ordinal))
            {
                return _files;
            }
            if (line.StartsWith("Reading ", StringComparison.Ordinal)
                || line.StartsWith("Extracted ", StringComparison.Ordinal))
            {
                return _archives;
            }
            if (line.StartsWith("Converting ", StringComparison.Ordinal))
            {
                return _sound;
            }
            if (line.StartsWith("Decompressing ", StringComparison.Ordinal))
            {
                return _binaries;
            }
            // Anything else -- a prompt, a warning, a blank line -- stays in
            // whatever phase is running. Returning a different band here would
            // reset the phase and its line count, and a stray blank line in the
            // middle of the file tree would send the caption back to "Starting".
            return _band;
        }

        private bool Set(double fraction, string stage)
        {
            double clamped = Math.Clamp(fraction, 0, 0.99);
            bool changed = clamped > Fraction + 0.0005 || stage != Stage;
            if (clamped > Fraction)
            {
                Fraction = clamped;
            }
            Stage = stage;
            return changed;
        }

        /// <summary>
        /// The bar as text, for the console launcher: [######----] 62%.
        /// </summary>
        public string Bar(int width = 28)
        {
            int filled = (int)Math.Round(Fraction * width);
            var text = new StringBuilder(width + 8);
            text.Append('[');
            text.Append('#', filled);
            text.Append('-', Math.Max(0, width - filled));
            text.Append($"] {(int)Math.Round(Fraction * 100),3}%");
            return text.ToString();
        }
    }
}
