using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace MphRead.Mods.Launcher
{
    /// <summary>
    /// The exact seven retail Metroid Prime Hunters dumps this build will
    /// extract from, checked by the ROM's own MD5 rather than its name or its
    /// header's game code -- a renamed file or a hand-patched one reads both
    /// of those back unchanged. See the "ROM not retail" memory: a non-retail
    /// dump can pass a header check and then crash deep inside a hunter
    /// model's palette, which is what this catches before extraction rather
    /// than mid-match.
    /// </summary>
    public static class RomWhitelist
    {
        private static readonly Dictionary<string, string> _known =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["c71a2d5fd41727c31f1619fb3085d4df"] = "Europe 1.0 (AMHP0)",
                ["378297159f176802e27384e18e33a1c4"] = "Europe 1.1 (AMHP1)",
                ["42850a19d7be2ee5e067df6984aa900e"] = "Japan 1.0 (AMHJ0)",
                ["11db1b8b49065f955f68e10062416ab3"] = "Japan 1.1 (AMHJ1)",
                ["e83239677f0e2b1f75210bb0978f9007"] = "Korea 1.0 (AMHK0)",
                ["b4c8a9398866b49c7be17d75736a223b"] = "USA 1.0 (AMHE0)",
                ["9fe5f1eb1eb9dc5d90130408f813b39e"] = "USA 1.1 (AMHE1)",
            };

        /// <summary>The file's MD5 as lowercase hex, or null when it could not be read.</summary>
        public static string? Hash(string path)
        {
            try
            {
                using FileStream stream = File.OpenRead(path);
                using MD5 md5 = MD5.Create();
                return Convert.ToHexString(md5.ComputeHash(stream)).ToLowerInvariant();
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>
        /// True and the dump's label ("USA 1.1 (AMHE1)") when the file's MD5
        /// is one of the seven; false and null for anything else, including a
        /// file that could not be read.
        /// </summary>
        public static bool TryIdentify(string path, out string? label)
        {
            string? hash = Hash(path);
            if (hash != null && _known.TryGetValue(hash, out string? found))
            {
                label = found;
                return true;
            }
            label = null;
            return false;
        }
    }
}
