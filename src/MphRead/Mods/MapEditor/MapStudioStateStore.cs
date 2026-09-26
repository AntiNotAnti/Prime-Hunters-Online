using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MphRead.Mods.MapGen;

namespace MphRead.Mods.MapEditor;

public sealed class MapStudioState
{
    public Dictionary<string,Guid[]> SelectionSets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> FavoriteMaterials { get; set; } = new();
    public List<string> RecentPrefabs { get; set; } = new();
}

public static class MapStudioStateStore
{
    private static readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    public static string PathFor(MapDefinition definition)
    {
        string id = definition.MapId == Guid.Empty
            ? Safe(definition.Name)
            : definition.MapId.ToString("N");
        return Path.Combine(CustomRooms.MapDirectory,".studio",id+".json");
    }

    public static MapStudioState Load(MapDefinition definition)
    {
        string path=PathFor(definition);
        try
        {
            if(!File.Exists(path))return new();
            return JsonSerializer.Deserialize<MapStudioState>(File.ReadAllBytes(path),_json)??new();
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    public static void Save(MapDefinition definition,MapStudioState state)
    {
        string path=PathFor(definition);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.Write(path,JsonSerializer.SerializeToUtf8Bytes(state,_json));
    }

    public static void Prune(MapDefinition definition,MapStudioState state)
    {
        var ids=MapObjects.All(definition).Select(o=>o.Id).ToHashSet();
        foreach(string key in state.SelectionSets.Keys.ToArray())
        {
            Guid[] kept=state.SelectionSets[key].Where(ids.Contains).Distinct().ToArray();
            if(kept.Length==0)state.SelectionSets.Remove(key);
            else state.SelectionSets[key]=kept;
        }
    }

    private static string Safe(string value)
    {
        string result=new(value.Select(c=>char.IsLetterOrDigit(c)||c is '-' or '_'?c:'_').ToArray());
        return String.IsNullOrWhiteSpace(result)?"map":result;
    }
}
