namespace MphRead.Mods
{
    /// <summary>
    /// What this program is called, in one place.
    ///
    /// The project is Fruity Prime; the code is still <c>namespace MphRead</c>
    /// and always will be. Upstream is NoneGiven/MphRead and the whole mod is
    /// arranged so that pulling from it stays a fast-forward -- renaming the
    /// namespace would put a conflict in all 221 files that declare it and all
    /// 271 that import it, for a string nobody but a developer ever reads.
    /// So the rename is the product, the binaries and the window title, and
    /// this class is where the product name lives.
    ///
    /// <see cref="Executable"/> is deliberately not a constant: the game and
    /// the dedicated server ship as differently named binaries on Windows, and
    /// usage text that names the wrong one is worse than usage text with no
    /// name in it at all.
    /// </summary>
    public static class Branding
    {
        /// <summary>The product, as a person would write it.</summary>
        public const string Name = "Fruity Prime";

        /// <summary>The product with no space, for file names and archives.</summary>
        public const string FileName = "FruityPrime";

        /// <summary>What upstream is, and what this is a fork of.</summary>
        public const string Upstream = "MphRead";

        /// <summary>
        /// The repository releases are published to and fetched from.
        ///
        /// The project was forked as MphRead and the repository has since been
        /// renamed to match; GitHub keeps the old slug redirecting for API
        /// calls too, but a redirect is not a guarantee -- it lapses if
        /// somebody else ever claims the old name, so this is the current one,
        /// not the original one.
        /// </summary>
        public const string Repository = "liveteklol/Fruity-Prime";

        /// <summary>
        /// The name of the running executable, without its extension. Read
        /// rather than assumed, so that a renamed copy still prints commands
        /// somebody can actually type.
        /// </summary>
        public static string Executable
        {
            get
            {
                string? path = System.Environment.ProcessPath;
                if (path == null)
                {
                    return FileName;
                }
                string name = System.IO.Path.GetFileNameWithoutExtension(path);
                return name.Length > 0 ? name : FileName;
            }
        }

        /// <summary>
        /// "Fruity Prime v1.2.0", or "Fruity Prime (a local build)" when this
        /// was not made by the release workflow -- which is worth saying out
        /// loud, because it is also the case where the updater stands down.
        /// </summary>
        public static string NameAndVersion => Update.BuildVersion.IsRelease
            ? $"{Name} {Update.BuildVersion.Display}"
            : $"{Name} ({Update.BuildVersion.Display})";
    }
}
