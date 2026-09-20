#if MPHREAD_AVALONIA
using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using Avalonia;
using Avalonia.Platform;

namespace MphRead.Mods.Launcher.Gui
{
    /// <summary>
    /// Which country an address is in, answered from a table that ships.
    ///
    /// <para>
    /// The obvious way to do this is to ask an API, and it is the wrong way:
    /// a launcher that looks up every server in your browser has told
    /// somebody else what you play, who you play with and when -- for a flag.
    /// So the ranges are carried in the build (about a megabyte, gzipped) and
    /// nothing leaves the machine. It is also why this is never wrong in a
    /// way a timeout would be: there is no request to fail, no order for
    /// answers to arrive in, and the browser does not reflow as they land.
    /// </para>
    ///
    /// <para>
    /// The table is DB-IP Lite under CC BY 4.0, which asks for attribution;
    /// that is on the Credits page rather than in a file nobody opens.
    /// Rebuild it with <c>tools/build-ipcountry.py</c>.
    /// </para>
    /// </summary>
    internal static class GeoCountry
    {
        private static uint[]? _first;
        private static byte[]? _index;
        private static string[]? _codes;
        private static bool _tried;

        /// <summary>
        /// The two-letter code, or an empty string: a private address, an
        /// unresolved name, or a machine whose build has no table.
        /// </summary>
        public static string Of(IPAddress? address)
        {
            if (address == null || address.AddressFamily
                != System.Net.Sockets.AddressFamily.InterNetwork)
            {
                return "";
            }
            if (IPAddress.IsLoopback(address) || IsPrivate(address))
            {
                return "";
            }
            Load();
            if (_first == null || _codes == null || _index == null)
            {
                return "";
            }
            byte[] b = address.GetAddressBytes();
            uint value = ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];

            // The last range whose first address is at or below this one. The
            // table is contiguous, so there is no "between two ranges" case to
            // handle and no end address to store.
            int lo = 0, hi = _first.Length - 1, hit = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (_first[mid] <= value)
                {
                    hit = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }
            if (hit < 0)
            {
                return "";
            }
            string code = _codes[_index[hit]];
            // ZZ is the table's own "nowhere": unallocated space, and not a
            // country to draw a flag for.
            return code == "ZZ" ? "" : code;
        }

        public static bool IsPrivate(IPAddress address)
        {
            byte[] b = address.GetAddressBytes();
            return b.Length == 4 && (b[0] == 10
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 169 && b[1] == 254));
        }

        private static void Load()
        {
            if (_tried)
            {
                return;
            }
            _tried = true;
            try
            {
                using Stream packed = AssetLoader.Open(
                    new Uri("avares://FruityPrime/Assets/Geo/ipcountry.bin.gz"));
                using var gz = new GZipStream(packed, CompressionMode.Decompress);
                using var memory = new MemoryStream();
                gz.CopyTo(memory);
                memory.Position = 0;
                using var reader = new BinaryReader(memory);

                if (new string(reader.ReadChars(6)) != "FPGEO1")
                {
                    return;
                }
                int countries = reader.ReadUInt16();
                var codes = new string[countries];
                for (int i = 0; i < countries; i++)
                {
                    codes[i] = new string(reader.ReadChars(2));
                }
                int count = (int)reader.ReadUInt32();
                var first = new uint[count];
                var index = new byte[count];
                for (int i = 0; i < count; i++)
                {
                    first[i] = reader.ReadUInt32();
                    index[i] = reader.ReadByte();
                }
                _codes = codes;
                _first = first;
                _index = index;
            }
            catch (Exception)
            {
                // A build without the table shows the neutral badge rather
                // than no browser.
            }
        }
    }
}
#endif
