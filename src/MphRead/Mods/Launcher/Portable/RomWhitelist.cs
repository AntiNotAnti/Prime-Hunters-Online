using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace MphRead.Mods.Launcher
{
    /// <summary>
    /// Identifies Metroid Prime Hunters ROMs by the data layout Project Prime
    /// actually consumes rather than by an exact whole-file hash.
    ///
    /// A cartridge dump can be padded, trimmed, renamed or otherwise byte
    /// different without changing the executable/filesystem layout. Those
    /// harmless differences should not make setup fail. Conversely, accepting
    /// only the four-character game code is too loose: a rebuilt/hacked ROM
    /// can keep that header while moving the executable tables that Project
    /// Prime reads at revision-specific offsets.
    ///
    /// This is therefore the cheap first gate. It checks a supported full-game
    /// code/revision, verifies every header range and FAT entry lies inside the
    /// file, and requires the overlay records used by the runtime profile.
    /// <see cref="Extract.Setup"/> performs the deeper decompressed-layout
    /// validation before it writes extracted game files.
    /// </summary>
    public static class RomCompatibility
    {
        private static readonly IReadOnlyDictionary<string, string> _supported =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [Ver.AMHE0] = "USA 1.0 (AMHE0)",
                [Ver.AMHE1] = "USA 1.1 (AMHE1)",
                [Ver.AMHP0] = "Europe 1.0 (AMHP0)",
                [Ver.AMHP1] = "Europe 1.1 (AMHP1)",
                [Ver.AMHJ0] = "Japan 1.0 (AMHJ0)",
                [Ver.AMHJ1] = "Japan 1.1 (AMHJ1)",
                [Ver.AMHK0] = "Korea 1.0 (AMHK0)",
                [Ver.A76E0] = "Kiosk demo (A76E0)"
            };

        public static bool TryIdentify(string path, out string? label)
        {
            return TryIdentify(path, out label, out _);
        }

        /// <summary>
        /// Returns true for a structurally compatible supported MPH revision.
        /// The validation reads only the header/FAT/overlay tables, not the
        /// entire ROM, so selecting a large dump no longer begins with a
        /// silent full-file MD5 pass.
        /// </summary>
        public static bool TryIdentify(string path, out string? label, out string? problem)
        {
            label = null;
            problem = null;
            try
            {
                using FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                int headerSize = Marshal.SizeOf<Extract.RomHeader>();
                if (stream.Length < headerSize)
                {
                    problem = "That file is too small to contain a Nintendo DS ROM header.";
                    return false;
                }

                byte[] headerBytes = new byte[headerSize];
                stream.ReadExactly(headerBytes);
                Extract.RomHeader header = Read.ReadStruct<Extract.RomHeader>(headerBytes);
                string gameCode = header.GameCode.MarshalString();
                string key = gameCode + header.Version;

                if (!_supported.TryGetValue(key, out string? found))
                {
                    if (gameCode == "AMFE" || gameCode == "AMFP")
                    {
                        problem = "That is a Metroid Prime Hunters: First Hunt ROM. "
                            + "Project Prime needs the full Metroid Prime Hunters cartridge dump "
                            + "for its primary game files.";
                    }
                    else if (gameCode.StartsWith("AMH", StringComparison.Ordinal)
                        || gameCode == "A76E")
                    {
                        problem = $"Metroid Prime Hunters ROM revision {key} is not supported by "
                            + "this build.";
                    }
                    else
                    {
                        string shown = String.IsNullOrWhiteSpace(gameCode) ? "????" : gameCode;
                        problem = $"The selected .nds is not a supported Metroid Prime Hunters ROM "
                            + $"(game code {shown}).";
                    }
                    return false;
                }

                if (!ValidateTables(stream, header, key, out problem))
                {
                    return false;
                }

                label = found;
                return true;
            }
            catch (IOException ex)
            {
                problem = $"The ROM could not be read: {ex.Message}";
                return false;
            }
            catch (UnauthorizedAccessException ex)
            {
                problem = $"The ROM could not be read: {ex.Message}";
                return false;
            }
            catch (Exception ex) when (ex is InvalidDataException
                or ArgumentException or OverflowException)
            {
                problem = $"The ROM header or filesystem is invalid: {ex.Message}";
                return false;
            }
        }

        private static bool ValidateTables(FileStream stream, Extract.RomHeader header,
            string key, out string? problem)
        {
            long length = stream.Length;
            if (!InFile(header.ARM9Offset, header.ARM9Size, length))
            {
                problem = "The ROM's ARM9 executable range is invalid.";
                return false;
            }
            if (!InFile(header.ARM7Offset, header.ARM7Size, length))
            {
                problem = "The ROM's ARM7 executable range is invalid.";
                return false;
            }
            if (!InFile(header.FntOffset, header.FntSize, length) || header.FntSize < 8)
            {
                problem = "The ROM's filename table is invalid.";
                return false;
            }
            if (!InFile(header.FatOffset, header.FatSize, length)
                || header.FatSize < 8 || header.FatSize % 8 != 0
                || header.FatSize > Int32.MaxValue)
            {
                problem = "The ROM's file allocation table is invalid.";
                return false;
            }
            if (!InFile(header.Overlay9Offset, header.Overlay9Size, length)
                || header.Overlay9Size < 32 || header.Overlay9Size % 32 != 0)
            {
                problem = "The ROM's ARM9 overlay table is invalid.";
                return false;
            }
            if (!InFile(header.BannerOffset, 0x840, length))
            {
                problem = "The ROM's banner range is invalid.";
                return false;
            }

            byte[] fntRoot = ReadRange(stream, header.FntOffset, 8);
            uint namesOffset = BitConverter.ToUInt32(fntRoot, 0);
            ushort directoryCount = BitConverter.ToUInt16(fntRoot, 6);
            if (directoryCount == 0
                || (long)directoryCount * 8 > header.FntSize
                || namesOffset >= header.FntSize)
            {
                problem = "The ROM's filename table directory data is invalid.";
                return false;
            }

            int fatSize = checked((int)header.FatSize);
            byte[] fat = ReadRange(stream, header.FatOffset, fatSize);
            int fileCount = fat.Length / 8;
            for (int i = 0; i < fileCount; i++)
            {
                uint start = BitConverter.ToUInt32(fat, i * 8);
                uint end = BitConverter.ToUInt32(fat, i * 8 + 4);
                if (end < start || end > length)
                {
                    problem = $"ROM file allocation entry {i} points outside the dump.";
                    return false;
                }
            }

            byte[] overlays = ReadRange(stream, header.Overlay9Offset, header.Overlay9Size);
            int platformOverlay = key.Equals(Ver.A76E0, StringComparison.OrdinalIgnoreCase)
                ? 12 : 15;
            bool foundDataOverlay = false;
            bool foundPlatformOverlay = false;
            for (int offset = 0; offset < overlays.Length; offset += 32)
            {
                int overlayId = BitConverter.ToInt32(overlays, offset);
                int fileId = BitConverter.ToInt32(overlays, offset + 24);
                if (fileId < 0 || fileId >= fileCount)
                {
                    problem = $"ARM9 overlay {overlayId} refers to invalid file id {fileId}.";
                    return false;
                }

                uint start = BitConverter.ToUInt32(fat, fileId * 8);
                uint end = BitConverter.ToUInt32(fat, fileId * 8 + 4);
                if (end <= start)
                {
                    problem = $"ARM9 overlay {overlayId} has no executable data.";
                    return false;
                }

                if (overlayId == 2)
                {
                    foundDataOverlay = true;
                }
                if (overlayId == platformOverlay)
                {
                    foundPlatformOverlay = true;
                }
            }

            if (!foundDataOverlay || !foundPlatformOverlay)
            {
                problem = $"The ROM does not contain the executable overlays required by {key}.";
                return false;
            }

            problem = null;
            return true;
        }

        private static byte[] ReadRange(FileStream stream, long offset, int count)
        {
            byte[] bytes = new byte[count];
            stream.Position = offset;
            stream.ReadExactly(bytes);
            return bytes;
        }

        private static bool InFile(long offset, long size, long length)
        {
            return offset > 0 && size > 0 && offset <= length && size <= length - offset;
        }
    }
}
