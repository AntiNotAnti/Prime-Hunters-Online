using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace MphRead.Mods.MapGen;

/// <summary>
/// GUI/CLI-neutral Quake 3 import workflow. Analysis is read-only; import is
/// transactional and never leaves a half-created map folder behind.
/// </summary>
public static class Q3ImportService
{
    public sealed record Options(
        string Source,
        string? MapName,
        string RoomName,
        string DestinationDirectory,
        float? UnitsPerUnit = null,
        bool KeepClip = true,
        bool KeepItems = true,
        bool KeepSky = true,
        bool KeepSpawns = true,
        int PatchLevel = 3,
        int TextureSize = MapTextureBake.DefaultSize,
        IReadOnlyList<string>? Dependencies = null);

    public sealed record Analysis(
        string Source,
        string MapName,
        IReadOnlyList<string> Maps,
        float AutoScale,
        float Width,
        float Height,
        float Depth,
        int Surfaces,
        int Patches,
        int Brushes,
        int PlayerClips,
        int Spawns,
        int Pickups,
        MapTextureBake.Coverage Textures);

    public enum Severity { Info, Warning, Error }
    public sealed record Diagnostic(Severity Severity, string Message);
    public sealed record Result(bool Succeeded, string? ProjectPath, Analysis? Analysis,
        IReadOnlyList<Diagnostic> Diagnostics);

    public static Analysis Analyze(string source, string? mapName = null,
        IReadOnlyList<string>? dependencies = null, float? scale = null,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!File.Exists(source)) throw new FileNotFoundException("Quake 3 source was not found.", source);
        var maps = Q3Bsp.ListMaps(source);
        if (maps.Count == 0) throw new ProgramException($"{Path.GetFileName(source)} contains no BSP levels.");
        string selected = mapName ?? maps[0];
        if (!maps.Any(m => m.Equals(selected, StringComparison.OrdinalIgnoreCase)))
            throw new ProgramException($"{Path.GetFileName(source)} has no map {selected}.");
        var bsp = Q3Bsp.Load(source, selected, cancellation);
        float auto = Q3Convert.AutoScale(Q3Convert.WidestExtent(bsp));
        float unit = scale ?? auto;
        Q3Convert.Bounds(bsp, out float[] min, out float[] max, sky: false);
        var archives = MapTextureBake.DiscoverArchives(source, dependencies);
        var coverage = MapTextureBake.Analyze(bsp, archives);
        int clips = bsp.Brushes.Count(b =>
            (bsp.Textures[b.Texture].Contents & Q3Bsp.ContentsSolid) == 0
            && (bsp.Textures[b.Texture].Contents & Q3Bsp.ContentsPlayerClip) != 0);
        int spawns = bsp.Entities.Count(e => e.TryGetValue("classname", out string? name)
            && (name.Equals("info_player_start", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("info_player_deathmatch", StringComparison.OrdinalIgnoreCase)));
        int patches = bsp.Faces.Count(f => f.Type == 2);
        int pickups = Q3Import.Pickups(bsp, unit).Count();
        return new(source, selected, maps, auto,
            (max[0] - min[0]) / unit,
            (max[2] - min[2]) / unit,
            (max[1] - min[1]) / unit,
            bsp.Faces.Count, patches, bsp.Brushes.Count, clips, spawns, pickups, coverage);
    }

    public static Result Import(Options options, CancellationToken cancellation = default)
    {
        var diagnostics = new List<Diagnostic>();
        string destination = Path.GetFullPath(options.DestinationDirectory);
        string parent = Path.GetDirectoryName(destination)
            ?? throw new IOException("Import destination has no parent directory.");
        Directory.CreateDirectory(parent);
        string staging = Path.Combine(parent, "." + Path.GetFileName(destination)
            + ".import-" + Guid.NewGuid().ToString("N"));
        try
        {
            MapValidator.RequireRuntimeName(options.RoomName);
            if (Directory.Exists(destination))
                throw new IOException("A map folder already has this name. Choose a new name or use Reimport.");
            if (options.PatchLevel is < 1 or > 8)
                throw new ArgumentOutOfRangeException(nameof(options.PatchLevel), "Patch detail must be 1-8.");

            Analysis analysis = Analyze(options.Source, options.MapName, options.Dependencies,
                options.UnitsPerUnit, cancellation);
            foreach (string missing in analysis.Textures.Missing)
                diagnostics.Add(new(Severity.Warning, $"Missing texture {missing}; a visible fallback will be used."));

            Directory.CreateDirectory(staging);
            var lines = new List<string>();
            IReadOnlyList<string> archives = MapTextureBake.DiscoverArchives(options.Source, options.Dependencies);
            int status = Q3Convert.Run(options.Source, analysis.MapName, options.RoomName, staging,
                dropClip: !options.KeepClip, dropItems: !options.KeepItems,
                forcedScale: options.UnitsPerUnit, textureSize: options.TextureSize,
                cancellation: cancellation, textureArchives: archives, log: lines.Add);
            foreach (string line in lines) diagnostics.Add(new(Severity.Info, line));
            if (status != 0) throw new IOException("Quake 3 conversion failed.");

            string projectPath = Directory.EnumerateFiles(staging, "*.json").Single();
            var definition = MapDefinition.Load(projectPath);
            definition.FormatVersion = 2;
            if (definition.MapId == Guid.Empty) definition.MapId = Guid.NewGuid();
            if (definition.Import != null)
            {
                definition.Import.KeepSky = options.KeepSky;
                definition.Import.KeepClip = options.KeepClip;
                definition.Import.PatchLevel = options.PatchLevel;
                if (!options.KeepSpawns)
                {
                    definition.Import.KeepSpawns = false;
                    definition.Spawns.Clear();
                }
            }
            definition.Save(projectPath);
            cancellation.ThrowIfCancellationRequested();

            Directory.Move(staging, destination);
            string finalProject = Path.Combine(destination, Path.GetFileName(projectPath));
            return new(true, finalProject, analysis, diagnostics.AsReadOnly());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ProgramException
            or ArgumentException or UnauthorizedAccessException)
        {
            diagnostics.Add(new(Severity.Error, ex.Message));
            return new(false, null, null, diagnostics.AsReadOnly());
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                try { Directory.Delete(staging, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>
    /// Reimport architecture and art while preserving Project Prime-authored
    /// gameplay/editor data. Source-specific scale/preview are refreshed;
    /// authored entities, metadata, assets, audio, capabilities and geometry
    /// remain with the project.
    /// </summary>
    public static Result Reimport(MapDefinition existing, Options options,
        string projectPath, CancellationToken cancellation = default)
    {
        string root = Path.GetDirectoryName(Path.GetFullPath(projectPath))
            ?? throw new IOException("Project path has no directory.");
        string tempDestination = Path.Combine(Path.GetTempPath(), "ProjectPrime-reimport-"
            + Guid.NewGuid().ToString("N"));
        var request = options with { DestinationDirectory = tempDestination, RoomName = existing.Name };
        Result imported = Import(request, cancellation);
        if (!imported.Succeeded || imported.ProjectPath == null) return imported;
        try
        {
            var fresh = MapDefinition.Load(imported.ProjectPath);
            var merged = MapProjectSerializer.Clone(existing);
            var previousImport = existing.Import;
            merged.Import = fresh.Import;
            if (merged.Import != null && previousImport != null)
            {
                merged.Import.KeepSky = previousImport.KeepSky;
                merged.Import.KeepClip = previousImport.KeepClip;
                merged.Import.KeepSpawns = previousImport.KeepSpawns;
                merged.Import.KeepItems = previousImport.KeepItems;
                merged.Import.PatchLevel = previousImport.PatchLevel;
                merged.Import.TexScale = previousImport.TexScale;
                merged.Import.DefaultMaterial = previousImport.DefaultMaterial;
                merged.Import.ShaderMaterials = new Dictionary<string, int>(previousImport.ShaderMaterials,
                    StringComparer.OrdinalIgnoreCase);
            }
            merged.ScaleFactor = fresh.ScaleFactor;
            merged.KillHeight = fresh.KillHeight;
            merged.FarClip = fresh.FarClip;
            if (merged.Preview == null) merged.Preview = fresh.Preview;

            string freshRoot = Path.GetDirectoryName(imported.ProjectPath)!;
            if (fresh.Import != null)
            {
                string? level = fresh.Import.Resolve();
                string? textures = fresh.Import.ResolveTextures();
                if (level != null)
                {
                    string target = Path.Combine(root, Path.GetFileName(level));
                    AtomicFile.Write(target, File.ReadAllBytes(level));
                    merged.Import!.Source = Path.GetFileName(target);
                }
                if (textures != null)
                {
                    string target = Path.Combine(root, Path.GetFileName(textures));
                    AtomicFile.Write(target, File.ReadAllBytes(textures));
                    merged.Import!.Textures = Path.GetFileName(target);
                }
                merged.Import!.BaseDirectory = root;
                merged.Import.BundlePath = null;
            }
            merged.BaseDirectory = root;
            merged.SourcePath = Path.GetFullPath(projectPath);
            merged.BundlePath = null;
            merged.Save(projectPath);
            return imported with { ProjectPath = Path.GetFullPath(projectPath) };
        }
        finally
        {
            try { if (Directory.Exists(tempDestination)) Directory.Delete(tempDestination, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
