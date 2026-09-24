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

        // Tuned to the shape of a real extraction. Preview rendering is not
        // part of setup anymore: a successful ROM extraction reaches 100%
        // immediately, while cosmetic previews are filled in afterwards.
        private static readonly Band _validate = new(0.00, 0.03, 2, "Validating ROM");
        private static readonly Band _files = new(0.03, 0.43, 45, "Writing game files");
        private static readonly Band _archives = new(0.43, 0.62, 25, "Unpacking archives");
        private static readonly Band _sound = new(0.62, 0.68, 3, "Converting music");
        private static readonly Band _binaries = new(0.68, 0.99, 18, "Decompressing code");

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

        private Band Classify(string line)
        {
            if (line.StartsWith("Recognised:", StringComparison.Ordinal)
                || line.StartsWith("Validating ROM", StringComparison.Ordinal))
            {
                return _validate;
            }
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
