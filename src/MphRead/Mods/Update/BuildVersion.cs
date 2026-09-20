using System;
using System.Reflection;

namespace MphRead.Mods.Update
{
    /// <summary>
    /// Which release this binary is, if it is one.
    ///
    /// Not <c>Program.Version</c>: that number is upstream's and tracks the
    /// state of the reverse engineering, not what has been published here. The
    /// release workflow stamps the tag it is building into the assembly, so a
    /// downloaded build knows exactly which release it came from and can be
    /// compared against the one on GitHub without guessing.
    ///
    /// A build that was not made by that workflow has no stamp, and
    /// <see cref="Current"/> is null. That is the case the updater has to
    /// respect above all others: a developer's own build must never be
    /// overwritten by a download because its version happened to compare low.
    /// </summary>
    public static class BuildVersion
    {
        private static readonly Lazy<Version?> _current = new(Read);

        /// <summary>The release this binary is, or null for a local build.</summary>
        public static Version? Current => _current.Value;

        /// <summary>True when this build came out of the release workflow.</summary>
        public static bool IsRelease => Current != null;

        /// <summary>"v1.2.0", or "a local build" when there is no stamp.</summary>
        public static string Display => Current == null
            ? "a local build"
            : "v" + Current.ToString(3);

        private static Version? Read()
        {
            // The entry assembly first, and this one when there is no entry
            // assembly at all -- which is every Android launch.
            //
            // `Assembly.GetEntryAssembly` answers null there and it is not a
            // fault: Android starts an *activity*, so the process has no
            // managed entry point for the runtime to name. Every APK
            // therefore called itself "a local build", release or not, and the
            // version line could never be anything but grey. This assembly is
            // the right answer on that platform for a reason worth writing
            // down: the Android head compiles MphRead's own sources into
            // itself (`<Compile Include="..\MphRead\**\*.cs" />`) rather than
            // referencing the project, so the file this class lives in *is*
            // part of the APK's assembly, and that assembly is what
            // release.yml stamps.
            Assembly? assembly = Assembly.GetEntryAssembly() ?? typeof(BuildVersion).Assembly;
            string? text = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;
            if (text == null)
            {
                return null;
            }
            // The SDK appends "+<commit sha>" when the repository is known, and
            // a tag may have been written with its leading v.
            int plus = text.IndexOf('+');
            if (plus >= 0)
            {
                text = text[..plus];
            }
            return Parse(text);
        }

        /// <summary>
        /// "v1.2.0", "1.2.0", "1.2" -> a Version. The explicit "local" stamp
        /// used by unstamped desktop and Android builds is not a release.
        /// </summary>
        public static Version? Parse(string? text)
        {
            if (String.IsNullOrWhiteSpace(text))
            {
                return null;
            }
            text = text.Trim();
            if (text.Length > 1 && (text[0] == 'v' || text[0] == 'V'))
            {
                text = text[1..];
            }
            // A pre-release suffix is not something this compares; a tag like
            // v1.2.0-rc1 is left for a person to install by hand.
            if (text.IndexOf('-') >= 0)
            {
                return null;
            }
            if (!Version.TryParse(text, out Version? version))
            {
                return null;
            }
            return Normalise(version);
        }

        /// <summary>Compare on three parts; the fourth is never in a tag.</summary>
        public static Version Normalise(Version version) => new(
            version.Major, version.Minor, version.Build < 0 ? 0 : version.Build);
    }
}
