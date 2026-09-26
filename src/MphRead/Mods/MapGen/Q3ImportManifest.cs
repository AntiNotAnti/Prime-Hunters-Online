using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MphRead.Mods.MapGen;

/// <summary>
/// Local authoring provenance for a Q3 import. This is deliberately a sidecar
/// rather than part of MapDefinition: absolute source paths and archive mtimes
/// are useful to the editor but must not change runtime fingerprints or leak
/// into portable .ppmap packages.
/// </summary>
public sealed class Q3ImportManifest
{
    public int Version { get; set; } = 1;
    public string Source { get; set; } = "";
    public string MapName { get; set; } = "";
    public float UnitsPerUnit { get; set; }
    public List<Archive> Archives { get; set; } = new();
    public List<MapTextureBake.Resolution> Textures { get; set; } = new();

    public sealed record Archive(string Path,long Length,long ModifiedUtcTicks);

    public static string FileName => ".q3-import.json";

    public static Q3ImportManifest From(Q3ImportService.Analysis analysis,float unitsPerUnit)
    {
        var manifest=new Q3ImportManifest
        {
            Source=Path.GetFullPath(analysis.Source),
            MapName=analysis.MapName,
            UnitsPerUnit=unitsPerUnit,
            Textures=analysis.Textures.Resolutions.ToList()
        };
        foreach(string path in analysis.Textures.Archives.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var info=new FileInfo(path);
                manifest.Archives.Add(new(Path.GetFullPath(path),info.Exists?info.Length:0,
                    info.Exists?info.LastWriteTimeUtc.Ticks:0));
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                manifest.Archives.Add(new(path,0,0));
            }
        }
        return manifest;
    }

    public static Q3ImportManifest? Load(string projectDirectory)
    {
        string path=Path.Combine(projectDirectory,FileName);
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Q3ImportManifest>(File.ReadAllBytes(path))
                : null;
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void Save(string projectDirectory)
    {
        Directory.CreateDirectory(projectDirectory);
        AtomicFile.Write(Path.Combine(projectDirectory,FileName),
            JsonSerializer.SerializeToUtf8Bytes(this,new JsonSerializerOptions{WriteIndented=true}));
    }

    public string Summary()
    {
        int resolved=Textures.Count(t=>!t.Fallback),fallback=Textures.Count(t=>t.Fallback);
        var byArchive=Textures.Where(t=>!t.Fallback&&!String.IsNullOrEmpty(t.Archive))
            .GroupBy(t=>Path.GetFileName(t.Archive!),StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g=>g.Count()).Select(g=>$"{g.Key}: {g.Count()}").ToArray();
        return $"{resolved}/{Textures.Count} textures resolved · {fallback} fallback"
            +(byArchive.Length==0?"":"\n"+String.Join("\n",byArchive));
    }
}
