using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using CommunityToolkit.HighPerformance.Buffers;
using NCSFCommon.NC;
using NCSFCommon.ReplayGain;

namespace MphRead
{
    public static class Extract
    {
        private static readonly object _runtimeDataGate = new();
        private static string _runtimeDataStamp = "";

        public static bool Setup(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            RomHeader header = Read.ReadStruct<RomHeader>(bytes);
            var mphCodes = new Dictionary<string, List<byte>>()
            {
                { "AMHE", new List<byte>() { 0, 1 } },
                { "AMHP", new List<byte>() { 0, 1 } },
                { "AMHJ", new List<byte>() { 0, 1 } },
                { "AMHK", new List<byte>() { 0 } },
                { "A76E", new List<byte>() { 0 } }
            };
            bool isFh = false;
            string gameCode = header.GameCode.MarshalString();
            if (!mphCodes.TryGetValue(gameCode, out List<byte>? mphVersions))
            {
                var fhCodes = new Dictionary<string, List<byte>>()
                {
                    { "AMFE", new List<byte>() { 0 } },
                    { "AMFP", new List<byte>() { 0 } }
                };
                if (!fhCodes.TryGetValue(gameCode, out List<byte>? fhVersions))
                {
                    PrintExit($"The specified ROM file has invalid game code {gameCode}.");
                    return false;
                }
                if (!fhVersions.Contains(header.Version))
                {
                    PrintExit($"The specified {gameCode} ROM has unexpected version {header.Version}.");
                    return false;
                }
                isFh = true;
            }
            else if (!mphVersions.Contains(header.Version))
            {
                PrintExit($"The specified {gameCode} ROM has unexpected version {header.Version}.");
                return false;
            }
            Paths.UpdatePaths();
            if (File.Exists("paths.txt"))
            {
                if ((!isFh && !String.IsNullOrWhiteSpace(Paths.FileSystem))
                    || (isFh && !String.IsNullOrWhiteSpace(Paths.FhFileSystem)))
                {
                    Console.Write($"A path has already been specified for {(isFh ? "FH" : "MPH")} files. " +
                        $"Do you want to update it? (y/n) ");
                    string input = (Console.ReadLine() ?? "").Trim().ToLower();
                    if (input != "y" && input != "yes")
                    {
                        return false;
                    }
                }
            }
            string rootName = $"{header.GameCode.MarshalString()}{header.Version}";
            if (!isFh)
            {
                Console.WriteLine("Validating ROM layout...");
                if (!ValidateRuntimeProfile(header, bytes, rootName, out string? layoutProblem))
                {
                    PrintExit(layoutProblem ?? $"The {rootName} ROM layout is not compatible with this build.");
                    return false;
                }
            }
            ExtractRomFs(header, bytes, rootName, hasArchives: !isFh);
            ExtractRomData(rootName);
            string newPath;
            // Relative, so files/ and paths.txt survive the installation being moved.
            if (isFh)
            {
                newPath = Paths.Combine("files", rootName, "data");
            }
            else
            {
                newPath = Paths.Combine("files", rootName);
            }
            Paths.SetPath(rootName, newPath);
            var lines = new List<string>();
            lines.Add(Program.Version.ToString());
            lines.Add($"{Ver.AMFE0}={Paths.AllPaths[Ver.AMFE0]}");
            lines.Add($"{Ver.AMFP0}={Paths.AllPaths[Ver.AMFP0]}");
            lines.Add($"{Ver.A76E0}={Paths.AllPaths[Ver.A76E0]}");
            lines.Add($"{Ver.AMHE0}={Paths.AllPaths[Ver.AMHE0]}");
            lines.Add($"{Ver.AMHE1}={Paths.AllPaths[Ver.AMHE1]}");
            lines.Add($"{Ver.AMHP0}={Paths.AllPaths[Ver.AMHP0]}");
            lines.Add($"{Ver.AMHP1}={Paths.AllPaths[Ver.AMHP1]}");
            lines.Add($"{Ver.AMHJ0}={Paths.AllPaths[Ver.AMHJ0]}");
            lines.Add($"{Ver.AMHJ1}={Paths.AllPaths[Ver.AMHJ1]}");
            lines.Add($"{Ver.AMHK0}={Paths.AllPaths[Ver.AMHK0]}");
            lines.Add($"Export={Paths.AllPaths["Export"]}");
            File.WriteAllText("paths.txt", String.Join(Environment.NewLine, lines));
            Nop();
            return true;
        }

        private class RomDataValues
        {
            public string File { get; set; }
            public int Offset { get; set; }
            public int Size { get; set; }

            public RomDataValues(string file, int offset, int size)
            {
                File = file;
                Offset = offset;
                Size = size;
            }
        }

        private class RomData
        {
            public RomDataValues FontModel { get; set; } = null!;
            public RomDataValues FontWidths { get; set; } = null!;
            public RomDataValues FontOffsets { get; set; } = null!;
            public RomDataValues FontCharData { get; set; } = null!;
            public RomDataValues TerrianSfx { get; set; } = null!;
            public RomDataValues BeamSfx { get; set; } = null!;
            public RomDataValues HunterSfx { get; set; } = null!;
            public RomDataValues EnemyDamageSfx { get; set; } = null!;
            public RomDataValues EnemyDeathSfx { get; set; } = null!;
            public RomDataValues PlatformSfx { get; set; } = null!;
        }

        private static void ExtractRomData(string rootName)
        {
            if (!_romData.TryGetValue(rootName, out RomData? data))
            {
                return;
            }
            byte[] bytes = File.ReadAllBytes(Paths.Combine("files", rootName, "_bin", data.FontModel.File));
            File.WriteAllBytes(Paths.Combine("files", rootName, @"models\hudfont_Model.bin"),
                bytes[data.FontModel.Offset..(data.FontModel.Offset + data.FontModel.Size)]);
        }

        /// <summary>
        /// The game code/revision selects a fixed runtime-data profile, but a
        /// rebuilt ROM can keep that header while moving those tables. Measure
        /// the three decompressed binaries the profile reads before writing any
        /// extracted files. This accepts padded/trimmed dumps whose layout is
        /// unchanged and rejects incompatible hacks before they can crash later.
        /// </summary>
        private static bool ValidateRuntimeProfile(RomHeader header, byte[] rom,
            string rootName, out string? problem)
        {
            problem = null;
            if (!_romData.TryGetValue(rootName, out RomData? data))
            {
                problem = $"No runtime-data profile exists for {rootName}.";
                return false;
            }

            try
            {
                IReadOnlyList<(int Start, int End)> files = ReadFileOffsets(header, rom);
                long arm9Length = MeasureDecompressedLength(
                    rom, header.ARM9Offset, checked(header.ARM9Offset + header.ARM9Size));
                long arm9Required = RequiredLength(data.FontModel, data.FontWidths,
                    data.FontOffsets, data.FontCharData, data.EnemyDamageSfx, data.EnemyDeathSfx);
                if (arm9Length < arm9Required)
                {
                    problem = $"The {rootName} ARM9 layout is incompatible "
                        + $"(needs at least 0x{arm9Required:X} decompressed bytes, "
                        + $"found 0x{arm9Length:X}).";
                    return false;
                }

                if (!TryOverlayRange(header, rom, files, OverlayId(data.BeamSfx.File),
                    out (int Start, int End) dataOverlay))
                {
                    problem = $"The {rootName} ROM is missing {data.BeamSfx.File}.";
                    return false;
                }
                long dataLength = MeasureDecompressedLength(rom, dataOverlay.Start, dataOverlay.End);
                long dataRequired = RequiredLength(data.TerrianSfx, data.BeamSfx, data.HunterSfx);
                if (dataLength < dataRequired)
                {
                    problem = $"The {rootName} data-overlay layout is incompatible "
                        + $"(needs at least 0x{dataRequired:X} decompressed bytes, "
                        + $"found 0x{dataLength:X}).";
                    return false;
                }

                if (!TryOverlayRange(header, rom, files, OverlayId(data.PlatformSfx.File),
                    out (int Start, int End) platformOverlay))
                {
                    problem = $"The {rootName} ROM is missing {data.PlatformSfx.File}.";
                    return false;
                }
                long platformLength = MeasureDecompressedLength(
                    rom, platformOverlay.Start, platformOverlay.End);
                long platformRequired = RequiredLength(data.PlatformSfx);
                if (platformLength < platformRequired)
                {
                    problem = $"The {rootName} platform-overlay layout is incompatible "
                        + $"(needs at least 0x{platformRequired:X} decompressed bytes, "
                        + $"found 0x{platformLength:X}).";
                    return false;
                }
                return true;
            }
            catch (Exception ex) when (ex is InvalidDataException
                or ProgramException or ArgumentException or OverflowException
                or EndOfStreamException)
            {
                problem = $"The {rootName} executable layout could not be validated: {ex.Message}";
                return false;
            }
        }

        private static IReadOnlyList<(int Start, int End)> ReadFileOffsets(
            RomHeader header, byte[] rom)
        {
            long fatEnd = (long)header.FatOffset + header.FatSize;
            if (header.FatOffset == 0 || header.FatSize == 0 || header.FatSize % 8 != 0
                || fatEnd > rom.Length)
            {
                throw new InvalidDataException("invalid FAT range");
            }

            IReadOnlyList<uint> addresses =
                Read.DoOffsets<uint>(rom, header.FatOffset, header.FatSize / 4);
            var result = new List<(int Start, int End)>(addresses.Count / 2);
            for (int i = 0; i < addresses.Count; i += 2)
            {
                uint start = addresses[i];
                uint end = addresses[i + 1];
                if (end < start || end > rom.Length || start > Int32.MaxValue || end > Int32.MaxValue)
                {
                    throw new InvalidDataException($"invalid FAT entry {i / 2}");
                }
                result.Add(((int)start, (int)end));
            }
            return result;
        }

        private static bool TryOverlayRange(RomHeader header, byte[] rom,
            IReadOnlyList<(int Start, int End)> files, int wantedId,
            out (int Start, int End) range)
        {
            range = default;
            long tableEnd = (long)header.Overlay9Offset + header.Overlay9Size;
            if (header.Overlay9Offset <= 0 || header.Overlay9Size <= 0
                || header.Overlay9Size % 32 != 0 || tableEnd > rom.Length)
            {
                return false;
            }

            int count = header.Overlay9Size / 32;
            for (int i = 0; i < count; i++)
            {
                int offset = header.Overlay9Offset + i * 32;
                int overlayId = BitConverter.ToInt32(rom, offset);
                if (overlayId != wantedId)
                {
                    continue;
                }
                int fileId = BitConverter.ToInt32(rom, offset + 24);
                if (fileId < 0 || fileId >= files.Count)
                {
                    return false;
                }
                range = files[fileId];
                return range.End > range.Start;
            }
            return false;
        }

        private static int OverlayId(string file)
        {
            const string prefix = "overlay9_";
            if (!file.StartsWith(prefix, StringComparison.Ordinal)
                || !Int32.TryParse(file.AsSpan(prefix.Length), out int id))
            {
                throw new InvalidDataException($"invalid overlay profile name {file}");
            }
            return id;
        }

        private static long MeasureDecompressedLength(byte[] rom, int start, int end)
        {
            if (start < 0 || end <= start || end > rom.Length)
            {
                throw new InvalidDataException("compressed executable range is invalid");
            }
            using var input = new MemoryStream(rom, start, end - start, writable: false);
            return LZBackward.Decompress(input, end - start, Stream.Null);
        }

        private static long RequiredLength(params RomDataValues[] values)
        {
            long required = 0;
            foreach (RomDataValues value in values)
            {
                if (value.Offset < 0 || value.Size <= 0)
                {
                    throw new InvalidDataException($"invalid runtime-data profile for {value.File}");
                }
                required = Math.Max(required, (long)value.Offset + value.Size);
            }
            return required;
        }

        private static void ExtractRomFs(RomHeader header, byte[] bytes, string rootName, bool hasArchives)
        {
            Debug.Assert(header.FntOffset > 0 && header.FatSize > 0);
            Debug.Assert(header.FatOffset > 0 && header.FatSize > 0 && header.FatSize % 8 == 0);
            DirTableEntry dirStart = Read.DoOffset<DirTableEntry>(bytes, header.FntOffset);
            IReadOnlyList<DirTableEntry> entries = Read.DoOffsets<DirTableEntry>(bytes, header.FntOffset, dirStart.DirNum);
            var fileOffsets = new List<(int, int)>();
            IReadOnlyList<uint> addresses = Read.DoOffsets<uint>(bytes, header.FatOffset, header.FatSize / 4);
            for (int i = 0; i < addresses.Count; i += 2)
            {
                fileOffsets.Add(((int)addresses[i], (int)addresses[i + 1]));
            }
            void PopulateDir(DirInfo dir)
            {
                DirTableEntry entry = entries[(int)dir.Index];
                uint offset = header.FntOffset + entry.Offset;
                ushort fileIndex = entry.FirstFileIndex;
                byte type = 1;
                while (type != 0)
                {
                    type = bytes[offset];
                    offset++;
                    if (type >= 1 && type <= 127)
                    {
                        int length = type;
                        string name = Read.ReadString(bytes, offset, length);
                        offset += (uint)length;
                        dir.Files.Add(new FileInfo(name, fileIndex++));
                    }
                    else if (type >= 129 && type <= 255)
                    {
                        int length = type - 128;
                        string name = Read.ReadString(bytes, offset, length);
                        offset += (uint)length;
                        ushort id = Read.SpanReadUshort(bytes, offset);
                        offset += sizeof(ushort);
                        dir.Subdirectories.Add(new DirInfo(name, id - 0xF000u));
                    }
                }
                foreach (DirInfo subdir in dir.Subdirectories)
                {
                    PopulateDir(subdir);
                }
            }
            void WriteFiles(DirInfo dir, string path)
            {
                Console.WriteLine($"Writing {path}...");
                Directory.CreateDirectory(path);
                foreach (FileInfo file in dir.Files)
                {
                    (int start, int end) = fileOffsets[(int)file.Index];
                    Debug.Assert(start > 0 && end > start);
                    File.WriteAllBytes(Paths.Combine(path, file.Name), bytes[start..end]);
                }
                foreach (DirInfo subdir in dir.Subdirectories)
                {
                    WriteFiles(subdir, Paths.Combine(path, subdir.Name));
                }
            }
            var root = new DirInfo(rootName, index: 0);
            PopulateDir(root);
            WriteFiles(root, Paths.Combine("files", root.Name));
            if (hasArchives)
            {
                foreach (string path in Directory.EnumerateFiles(Paths.Combine("files", root.Name, "archives")))
                {
                    if (Path.GetExtension(path).ToLower() == ".arc")
                    {
                        Read.ExtractArchive(path);
                    }
                }
                Console.WriteLine("Converting sound_data.sdat...");
                string sdatDest = Paths.Combine("files", root.Name, "_seq");
                Directory.CreateDirectory(sdatDest);
                ConvertSdat(Paths.Combine("files", root.Name, "data", "sound", "sound_data.sdat"), sdatDest);
            }
            string ftcDir = Paths.Combine("files", root.Name, "ftc");
            Directory.CreateDirectory(ftcDir);
            byte[] WriteFile(string name, int offset, int size)
            {
                byte[] fileBytes = bytes[offset..(offset + size)];
                File.WriteAllBytes(Paths.Combine(ftcDir, name), fileBytes);
                return fileBytes;
            }
            WriteFile("arm9.bin", header.ARM9Offset, header.ARM9Size);
            WriteFile("arm7.bin", header.ARM7Offset, header.ARM7Size);
            WriteFile("fat.bin", (int)header.FatOffset, (int)header.FatSize);
            WriteFile("fnt.bin", (int)header.FntOffset, (int)header.FntSize);
            WriteFile("banner.bin", header.BannerOffset, 0x840);
            byte[] overlayInfo = WriteFile("y9.bin", header.Overlay9Offset, header.Overlay9Size);
            Debug.Assert(overlayInfo.Length % 32 == 0);
            for (int i = 0; i < overlayInfo.Length / 32; i++)
            {
                var items = new List<int>();
                for (int j = 0; j < 8; j++)
                {
                    int start = i * 32 + j * 4;
                    byte[] value = overlayInfo[start..(start + 4)];
                    items.Add(BitConverter.ToInt32(value));
                }
                int overlayId = items[0];
                int fileId = items[6];
                (int overlayStart, int overlayEnd) = fileOffsets[fileId];
                Debug.Assert(overlayStart > 0 && overlayEnd > overlayStart);
                File.WriteAllBytes(Paths.Combine(ftcDir, $"overlay9_{overlayId}"), bytes[overlayStart..overlayEnd]);
            }
            string ftcDest = Paths.Combine("files", root.Name, "_bin");
            Directory.CreateDirectory(ftcDest);
            foreach (string path in Directory.EnumerateFiles(ftcDir))
            {
                string filename = Path.GetFileName(path);
                if (filename == "arm9.bin" || filename.StartsWith("overlay9_"))
                {
                    Console.WriteLine($"Decompressing {filename}...");
                    LZBackward.Decompress(path, Paths.Combine(ftcDest, filename));
                }
            }
            Nop();
        }

        private static void ConvertSdat(string inputPath, string outputDir)
        {
            ReadOnlySpan<byte> sdatBytes = File.ReadAllBytes(inputPath);
            var finalSDAT = new SDAT();
            int sdatNumber = 1;
            var sdat = new SDAT();
            sdat.Read(sdatNumber.ToString(), sdatBytes);
            finalSDAT += sdat;
            finalSDAT.FixOffsetsAndSizes();
            using var memoryOwner = MemoryOwner<byte>.Allocate((int)finalSDAT.Size);
            finalSDAT.Write(memoryOwner.Span);
            var seqEntries = finalSDAT.INFOSection.SEQRecord.Entries;
            string ncsflibFilename = "mph.ncsflib";
            NCSFCommon.NCSF.MakeNCSF(Paths.Combine(outputDir, ncsflibFilename), [], memoryOwner.Span);
            NCSFCommon.TagList tags = [("_lib", ncsflibFilename), ("utf8", "1"), ("ncsfby", "MphRead")];
            AlbumGain albumGain = new();
            Dictionary<uint, NCSFCommon.TagList> fileTags = new(seqEntries.Length);
            for (uint i = 0, count = (uint)seqEntries.Length; i < count; ++i)
            {
                (uint offset, INFOEntrySEQ? entry) = seqEntries[(int)i];
                if (offset != 0 && entry is not null)
                {
                    if (entry.SSEQ!.Filename!.StartsWith("SSEQ"))
                    {
                        entry.SSEQ!.Filename = $"{i:X4} - {entry.SSEQ!.Filename}";
                    }
                    string minincsfFilename = $"{entry.SSEQ!.Filename}.minincsf";
                    var thisTags = tags.Clone();
                    string fullFilename = entry.FullFilename(sdatNumber > 1);
                    thisTags.AddOrReplace(("origFilename", entry.SSEQ.OriginalFilename!));
                    if (sdatNumber > 1)
                    {
                        thisTags.AddOrReplace(("origSDAT", entry.SDATNumber));
                    }
                    fileTags[i] = thisTags;
                }
            }
            for (uint i = 0, count = (uint)seqEntries.Length; i < count; ++i)
            {
                (uint offset, INFOEntrySEQ? entry) = seqEntries[(int)i];
                if (offset != 0 && entry is not null)
                {
                    string minincsfFilename = $"{entry.SSEQ!.Filename}.minincsf";
                    var thisTags = fileTags[i];
                    NCSFCommon.NCSF.MakeNCSF(Paths.Combine(outputDir, minincsfFilename), BitConverter.GetBytes(i), [], thisTags);
                }
            }
        }

        private static void PrintExit(string message)
        {
            Console.WriteLine(message);
            Console.WriteLine("Press any key to exit...");
            ConsoleSetup.PauseIfInteractive();
        }

        private static void Nop()
        {
        }

        public class DirInfo
        {
            public string Name { get; }
            public uint Index { get; }
            public List<DirInfo> Subdirectories { get; set; } = new List<DirInfo>();
            public List<FileInfo> Files { get; set; } = new List<FileInfo>();

            public DirInfo(string name, uint index)
            {
                Name = name;
                Index = index;
            }
        }

        public class FileInfo
        {
            public string Name { get; }
            public uint Index { get; }

            public FileInfo(string name, uint index)
            {
                Name = name;
                Index = index;
            }
        }

        public readonly struct DirTableEntry
        {
            public readonly uint Offset;
            public readonly ushort FirstFileIndex;
            public readonly ushort DirNum;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public readonly struct RomHeader
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 12)]
            public readonly char[] Title;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
            public readonly char[] GameCode;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
            public readonly char[] MakerCode;
            public readonly byte UnitCode;
            public readonly byte Seed;
            public readonly byte Capacity;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 7)]
            public readonly char[] Reserved15;
            public readonly byte Reserved16;
            public readonly byte Region;
            public readonly byte Version;
            public readonly byte AutoStart;
            public readonly int ARM9Offset;
            public readonly int ARM9EntryAddress;
            public readonly int ARM9RamAddress;
            public readonly int ARM9Size;
            public readonly int ARM7Offset;
            public readonly int ARM7EntryAddress;
            public readonly int ARM7RamAddress;
            public readonly int ARM7Size;
            public readonly uint FntOffset;
            public readonly uint FntSize;
            public readonly uint FatOffset;
            public readonly uint FatSize;
            public readonly int Overlay9Offset;
            public readonly int Overlay9Size;
            public readonly int Overlay7Offset;
            public readonly int Overlay7Size;
            public readonly uint ReadFlags;
            public readonly uint InitFlags;
            public readonly int BannerOffset;
        }

        public static void LoadRuntimeData()
        {
            if (!_romData.TryGetValue(Paths.MphKey, out RomData? data))
                return;

            string fontPath = Paths.Combine(Paths.FileSystem, "_bin", data.FontWidths.File);
            string beamPath = Paths.Combine(Paths.FileSystem, "_bin", data.BeamSfx.File);
            string platformPath = Paths.Combine(Paths.FileSystem, "_bin", data.PlatformSfx.File);
            string stamp = $"{Paths.MphKey}|{FileStamp(fontPath)}|{FileStamp(beamPath)}|{FileStamp(platformPath)}";

            lock (_runtimeDataGate)
            {
                // These tables are process-wide immutable data. A room change
                // used to reread the same multi-megabyte extracted binaries and
                // recopy all slices on every match/rematch. Keep them until the
                // selected ROM path or one of the source files actually changes.
                if (String.Equals(_runtimeDataStamp, stamp, StringComparison.Ordinal))
                    return;

                // arm9.bin
                byte[] bytes = File.ReadAllBytes(fontPath);
                byte[] widths = bytes[data.FontWidths.Offset..(data.FontWidths.Offset + data.FontWidths.Size)];
                byte[] offsets = bytes[data.FontOffsets.Offset..(data.FontOffsets.Offset + data.FontOffsets.Size)];
                byte[] chars = bytes[data.FontCharData.Offset..(data.FontCharData.Offset + data.FontCharData.Size)];
                byte[] enemyDamageSfx = bytes[data.EnemyDamageSfx.Offset..(data.EnemyDamageSfx.Offset + data.EnemyDamageSfx.Size)];
                byte[] enemyDeathSfx = bytes[data.EnemyDeathSfx.Offset..(data.EnemyDeathSfx.Offset + data.EnemyDeathSfx.Size)];
                Text.Font.Normal.SetData(widths, offsets, chars, minChar: 32);

                // overlay9_2
                bytes = File.ReadAllBytes(beamPath);
                byte[] terrainSfx = bytes[data.TerrianSfx.Offset..(data.TerrianSfx.Offset + data.TerrianSfx.Size)];
                byte[] beamSfx = bytes[data.BeamSfx.Offset..(data.BeamSfx.Offset + data.BeamSfx.Size)];
                byte[] hunterSfx = bytes[data.HunterSfx.Offset..(data.HunterSfx.Offset + data.HunterSfx.Size)];
                Metadata.SetTerrainSfxData(terrainSfx);
                Metadata.SetBeamSfxData(beamSfx);
                Metadata.SetHunterSfxData(hunterSfx);
                Metadata.SetEnemyDamageSfxData(enemyDamageSfx);
                Metadata.SetEnemyDeathSfxData(enemyDeathSfx);

                // overlay9_15 (or overlay9_12 for A76E0)
                bytes = File.ReadAllBytes(platformPath);
                byte[] platformSfx = bytes[data.PlatformSfx.Offset..(data.PlatformSfx.Offset + data.PlatformSfx.Size)];
                Metadata.SetPlatformSfxData(platformSfx);
                _runtimeDataStamp = stamp;
            }
        }

        private static string FileStamp(string path)
        {
            var info = new System.IO.FileInfo(path);
            return $"{Path.GetFullPath(path)}:{info.Length}:{info.LastWriteTimeUtc.Ticks}";
        }

        private static readonly FrozenDictionary<string, RomData> _romData = Frozen.Create<string, RomData>(
        [
            new(
                Ver.A76E0,
                new RomData()
                {
                    FontModel = new RomDataValues("arm9.bin", 0x9D528, 0x8284),
                    FontWidths = new RomDataValues("arm9.bin", 0x95C68, 480),
                    FontOffsets = new RomDataValues("arm9.bin", 0x95A88, 480),
                    FontCharData = new RomDataValues("arm9.bin", 0x96348, 0x4000),
                    TerrianSfx = new RomDataValues("overlay9_2", 0x1D828, 144),
                    BeamSfx = new RomDataValues("overlay9_2", 0x1D8B8, 180),
                    HunterSfx = new RomDataValues("overlay9_2", 0x1D96C, 272),
                    EnemyDamageSfx = new RomDataValues("arm9.bin", 0x9B574, 208),
                    EnemyDeathSfx = new RomDataValues("arm9.bin", 0x9B644, 208),
                    PlatformSfx = new RomDataValues("overlay9_12", 0x81E4, 360)
                }
            ),
            new (
                Ver.AMHE0,
                new RomData()
                {
                    FontModel = new RomDataValues("arm9.bin", 0xC76D4, 0x8284),
                    FontWidths = new RomDataValues("arm9.bin", 0xBF9B0, 480),
                    FontOffsets = new RomDataValues("arm9.bin", 0xBFB90, 480),
                    FontCharData = new RomDataValues("arm9.bin", 0xC0270, 0x4000),
                    TerrianSfx = new RomDataValues("overlay9_2", 0x1DA08, 144),
                    BeamSfx = new RomDataValues("overlay9_2", 0x1DA98, 180),
                    HunterSfx = new RomDataValues("overlay9_2", 0x1DB4C, 272),
                    EnemyDamageSfx = new RomDataValues("arm9.bin", 0xC54A8, 208),
                    EnemyDeathSfx = new RomDataValues("arm9.bin", 0xC5578, 208),
                    PlatformSfx = new RomDataValues("overlay9_15", 0x8284, 360)
                }
            ),
            new (
                Ver.AMHE1,
                new RomData()
                {
                    FontModel = new RomDataValues("arm9.bin", 0xC7F5C, 0x8284),
                    FontWidths = new RomDataValues("arm9.bin", 0xC020C, 480),
                    FontOffsets = new RomDataValues("arm9.bin", 0xC03EC, 480),
                    FontCharData = new RomDataValues("arm9.bin", 0xC0ACC, 0x4000),
                    TerrianSfx = new RomDataValues("overlay9_2", 0x1DA68, 144),
                    BeamSfx = new RomDataValues("overlay9_2", 0x1DAF8, 180),
                    HunterSfx = new RomDataValues("overlay9_2", 0x1DBAC, 272),
                    EnemyDamageSfx = new RomDataValues("arm9.bin", 0xC5D30, 208),
                    EnemyDeathSfx = new RomDataValues("arm9.bin", 0xC5E00, 208),
                    PlatformSfx = new RomDataValues("overlay9_15", 0x8284, 360)
                }
            ),
            new (
                Ver.AMHJ0,
                new RomData()
                {
                    FontModel = new RomDataValues("arm9.bin", 0xC9510, 0x8284),
                    FontWidths = new RomDataValues("arm9.bin", 0xC1754, 480),
                    FontOffsets = new RomDataValues("arm9.bin", 0xC1934, 480),
                    FontCharData = new RomDataValues("arm9.bin", 0xC2014, 0x4000),
                    TerrianSfx = new RomDataValues("overlay9_2", 0x1DA68, 144),
                    BeamSfx = new RomDataValues("overlay9_2", 0x1DAF8, 180),
                    HunterSfx = new RomDataValues("overlay9_2", 0x1DBAC, 272),
                    EnemyDamageSfx = new RomDataValues("arm9.bin", 0xC7278, 208),
                    EnemyDeathSfx = new RomDataValues("arm9.bin", 0xC7348, 208),
                    PlatformSfx = new RomDataValues("overlay9_15", 0x8284, 360)
                }
            ),
            new (
                Ver.AMHJ1,
                new RomData()
                {
                    FontModel = new RomDataValues("arm9.bin", 0xC94D0, 0x8284),
                    FontWidths = new RomDataValues("arm9.bin", 0xC1714, 480),
                    FontOffsets = new RomDataValues("arm9.bin", 0xC18F4, 480),
                    FontCharData = new RomDataValues("arm9.bin", 0xC1FD4, 0x4000),
                    TerrianSfx = new RomDataValues("overlay9_2", 0x1DA68, 144),
                    BeamSfx = new RomDataValues("overlay9_2", 0x1DAF8, 180),
                    HunterSfx = new RomDataValues("overlay9_2", 0x1DBAC, 272),
                    EnemyDamageSfx = new RomDataValues("arm9.bin", 0xC7238, 208),
                    EnemyDeathSfx = new RomDataValues("arm9.bin", 0xC7308, 208),
                    PlatformSfx = new RomDataValues("overlay9_15", 0x8284, 360)
                }
            ),
            new (
                Ver.AMHP0,
                new RomData()
                {
                    FontModel = new RomDataValues("arm9.bin", 0xC7F7C, 0x8284),
                    FontWidths = new RomDataValues("arm9.bin", 0xC022C, 480),
                    FontOffsets = new RomDataValues("arm9.bin", 0xC040C, 480),
                    FontCharData = new RomDataValues("arm9.bin", 0xC0AEC, 0x4000),
                    TerrianSfx = new RomDataValues("overlay9_2", 0x1DA08, 144),
                    BeamSfx = new RomDataValues("overlay9_2", 0x1DA98, 180),
                    HunterSfx = new RomDataValues("overlay9_2", 0x1DB4C, 272),
                    EnemyDamageSfx = new RomDataValues("arm9.bin", 0xC5D50, 208),
                    EnemyDeathSfx = new RomDataValues("arm9.bin", 0xC5E20, 208),
                    PlatformSfx = new RomDataValues("overlay9_15", 0x8284, 360)
                }
            ),
            new (
                Ver.AMHP1,
                new RomData()
                {
                    FontModel = new RomDataValues("arm9.bin", 0xC7FFC, 0x8284),
                    FontWidths = new RomDataValues("arm9.bin", 0xC02AC, 480),
                    FontOffsets = new RomDataValues("arm9.bin", 0xC048C, 480),
                    FontCharData = new RomDataValues("arm9.bin", 0xC0B6C, 0x4000),
                    TerrianSfx = new RomDataValues("overlay9_2", 0x1DA68, 144),
                    BeamSfx = new RomDataValues("overlay9_2", 0x1DAF8, 180),
                    HunterSfx = new RomDataValues("overlay9_2", 0x1DBAC, 272),
                    EnemyDamageSfx = new RomDataValues("arm9.bin", 0xC5DD0, 208),
                    EnemyDeathSfx = new RomDataValues("arm9.bin", 0xC5EA0, 208),
                    PlatformSfx = new RomDataValues("overlay9_15", 0x8284, 360)
                }
            ),
            new (
                Ver.AMHK0,
                new RomData()
                {
                    FontModel = new RomDataValues("arm9.bin", 0xC0D40, 0x8284),
                    FontWidths = new RomDataValues("arm9.bin", 0xBD580, 480),
                    FontOffsets = new RomDataValues("arm9.bin", 0xBD760, 480),
                    FontCharData = new RomDataValues("arm9.bin", 0xB9560, 0x4000),
                    TerrianSfx = new RomDataValues("overlay9_2", 0x1BDBA, 144),
                    BeamSfx = new RomDataValues("overlay9_2", 0x1BE4A, 180),
                    HunterSfx = new RomDataValues("overlay9_2", 0x1BEFE, 272),
                    EnemyDamageSfx = new RomDataValues("arm9.bin", 0xBE4DC, 208),
                    EnemyDeathSfx = new RomDataValues("arm9.bin", 0xBE5AC, 208),
                    PlatformSfx = new RomDataValues("overlay9_15", 0x7CC0, 360)
                }
            ),
            new (
                Ver.NTRJ0,
                new RomData()
                {
                    // todo: values
                    FontModel = new RomDataValues("arm9.bin", 0xED610, 0x8284),
                    FontWidths = new RomDataValues("arm9.bin", 0x1FC07C, 480),
                    FontOffsets = new RomDataValues("arm9.bin", 0x1FC25C, 480),
                    FontCharData = new RomDataValues("arm9.bin", 0x1FC93C, 0x4000)
                }
            )
        ]);
    }
}
