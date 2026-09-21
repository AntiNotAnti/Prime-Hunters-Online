using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MphRead.Entities;
using OpenTK.Mathematics;

namespace MphRead.Mods.MapGen
{
    public static class MapValidator
    {
        public static bool ValidRuntimeName(string? name) => name != null && name.Length <= 40
            && Regex.IsMatch(name, @"\A[A-Za-z0-9][A-Za-z0-9 _-]*\z") && name.Trim() == name
            && !Regex.IsMatch(name, @"\A(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])\z", RegexOptions.IgnoreCase);

        public static void RequireRuntimeName(string? name)
        {
            if (!ValidRuntimeName(name)) throw new MapAuthoringException("FP-MAP-009",
                "Runtime name must be 1–40 letters, digits, spaces, underscores or hyphens and cannot be a device name.");
        }

        public static bool Vector(float[]? v) => v?.Length == 3 && v.All(float.IsFinite);
        private static bool Position(float[]? v)=>Vector(v)&&v!.All(x=>Math.Abs(x)<524288);
        private static bool Color(int[]? c) => c?.Length == 3 && c.All(n => n >= 0 && n <= 31);

        public static MapValidationResult Validate(MapDefinition d, bool checkSources = true)
        {
            var r = new MapValidationResult();
            if (d.FormatVersion is < 1 or > 2) r.Error("FP-MAP-008", "Unsupported source format.");
            if (!ValidRuntimeName(d.Name)) r.Error("FP-MAP-009", "Invalid runtime name.");
            if(d.Version?.Length>64||d.Author?.Length>256||d.InGameName?.Length>256||d.Description?.Length>8192)
                r.Error("FP-MAP-008","Map metadata exceeds the supported length.");
            if (d.FormatVersion == 2 && d.MapId == Guid.Empty) r.Error("FP-MAP-010", "Version 2 maps require a persistent ID.");
            if (d.ScaleFactor is < 0 or > 16) r.Error("FP-MAP-004", "Scale factor must be between 0 and 16.");
            if (!float.IsFinite(d.KillHeight) || Math.Abs(d.KillHeight) >= 524288)
                r.Error("FP-MAP-011", "Kill height must fit the runtime fixed-point range.");
            if (!float.IsFinite(d.FarClip) || d.FarClip <= 0 || d.FarClip >= 524288)
                r.Error("FP-MAP-011", "Far clip must be positive and fit the runtime fixed-point range.");
            if (!Color(d.FogColor) || !Color(d.Light1Color) || !Color(d.Light2Color))
                r.Error("FP-MAP-012", "Colors require three channels from 0 to 31.");
            if (!Vector(d.Light1Vector) || !Vector(d.Light2Vector)
                || d.Light1Vector.All(x => x == 0) || d.Light2Vector.All(x => x == 0))
                r.Error("FP-MAP-012", "Lighting vectors must be finite and nonzero.");
            if (d.FogSlope is < 0 or > 10 || d.FogOffset is < 0 or > 65535)
                r.Error("FP-MAP-012", "Fog slope or offset is outside the runtime range.");
            if (d.Preview != null && (!Vector(d.Preview.Position) || !Vector(d.Preview.Target)))
                r.Error("FP-MAP-011", "Preview camera needs finite position and target vectors.");
            if (d.Materials == null || d.Brushes == null || d.Geometry == null || d.Spawns == null || d.Items == null || d.JumpPads == null)
            {
                r.Error("FP-MAP-008", "Map collections cannot be null.");
                return r;
            }
            var ids = new HashSet<Guid>();
            if (d.Geometry.Count + d.Brushes.Count > 10000 || d.Spawns.Count + d.Items.Count + d.JumpPads.Count > 32767)
            { r.Error("FP-MAP-003", "Map object count exceeds the compiler budget."); return r; }
            void Id(Guid id)
            {
                if (id != Guid.Empty && !ids.Add(id)) r.Error("FP-MAP-010", "Duplicate object ID.", id);
            }
            foreach (var m in d.Materials)
            {
                if (m == null) { r.Error("FP-MAP-001", "Null material."); continue; }
                Id(m.Id);
                if (m.SourceMaterial < 0 || !float.IsFinite(m.TexScale) || m.TexScale <= 0)
                    r.Error("FP-MAP-001", "Material requires a nonnegative source index and positive UV scale.", m.Id);
            }
            if (d.Import == null && d.Materials.Count == 0) r.Error("FP-MAP-001", "At least one material is required.");
            foreach (var b in d.Brushes)
            {
                if (b == null) { r.Error("FP-MAP-013", "Null brush."); continue; }
                Id(b.Id);
                if (!Vector(b.Min) || !Vector(b.Max)) { r.Error("FP-MAP-013", "Brush coordinates must be finite triples.", b.Id); continue; }
                if (Enumerable.Range(0, 3).Any(i => b.Min[i] == b.Max[i])) r.Error("FP-MAP-013", "Brush dimensions cannot be zero.", b.Id);
                float bound = 8 * MathF.Pow(2, Math.Clamp(d.ScaleFactor, 0, 16));
                if (b.Min.Concat(b.Max).Any(x => x < -bound || x >= bound || Math.Abs(x) >= 524288))
                    r.Error("FP-MAP-004", "Brush exceeds fixed-point vertex range; increase scale factor or reduce extent.", b.Id);
                if (b.Material < 0 || b.Material >= d.Materials.Count) r.Error("FP-MAP-001", "Brush references a missing material.", b.Id);
                if (!float.IsFinite(b.Shade) || b.Shade is < 0 or > 1) r.Error("FP-MAP-013", "Shade must be between zero and one.", b.Id);
                if (b.Terrain != null && (!Enum.TryParse<Terrain>(b.Terrain, true, out var terrain) || !Enum.IsDefined(terrain)))
                    r.Error("FP-MAP-013", "Unknown collision terrain.", b.Id);
            }
            bool safeBrushes = d.Brushes.All(b => b != null && Vector(b.Min) && Vector(b.Max));
            var solids=new List<IReadOnlyList<BuiltFace>>();
            foreach (var geometry in d.Geometry)
            {
                if (geometry == null) { r.Error("FP-MAP-013", "Null geometry object."); continue; }
                Id(geometry.Id);
                if (geometry.Material < 0 || geometry.Material >= d.Materials.Count || d.Materials[geometry.Material] == null)
                { r.Error("FP-MAP-001", "Geometry references a missing material.", geometry.Id); continue; }
                if(!float.IsFinite(geometry.Shade)||geometry.Shade is <0 or >1||!Enum.TryParse<Terrain>(geometry.Terrain,true,out var ground)||!Enum.IsDefined(ground))
                    r.Error("FP-MAP-013","Geometry requires shade 0–1 and a valid terrain.",geometry.Id);
                try
                {
                    var faces=GeometryCompiler.Compile(geometry, d.Materials[geometry.Material].TexScale);if(geometry.Solid)solids.Add(faces);
                    float bound=8*MathF.Pow(2,Math.Clamp(d.ScaleFactor,0,16));
                    if(faces.SelectMany(f=>f.Points).Any(p=>!float.IsFinite(p.LengthSquared)||Enumerable.Range(0,3).Any(i=>p[i]<-bound||p[i]>=bound||Math.Abs(p[i])>=524288)))
                        r.Error("FP-MAP-004","Geometry exceeds fixed-point range; increase scale factor or reduce extent.",geometry.Id);
                }
                catch (MapAuthoringException ex) { r.Error(ex.Code, ex.Message, geometry.Id); }
            }
            if (d.Spawns.Count == 0 && d.Import?.KeepSpawns != true) r.Error("FP-MAP-014", "At least one player spawn is required.");
            foreach (var s in d.Spawns)
            {
                if (s == null) { r.Error("FP-MAP-014", "Null spawn."); continue; }
                Id(s.Id);
                if (!Position(s.Position) || !float.IsFinite(s.Yaw)) { r.Error("FP-MAP-014", "Spawn requires position within fixed-point range and finite yaw.", s.Id); continue; }
                if (s.Team is < -1 or > 3) r.Error("FP-MAP-014", "Spawn team must be neutral (-1) or 0–3.", s.Id);
                if (s.Position[1] <= d.KillHeight) r.Error("FP-MAP-014", "Spawn is below the kill plane.", s.Id);
                var position=MapBuilder.ToVector(s.Position);
                bool InSolid(Vector3 p)=>solids.Any(faces=>faces.All(f=>Vector3.Dot(f.Normal,p-f.Points[0])<-.01f));
                if(InSolid(position))r.Error("FP-MAP-002","Spawn is inside solid geometry.",s.Id);
                else if(InSolid(position+Vector3.UnitY*1.8f))r.Warning("FP-MAP-002","Spawn has insufficient headroom.",s.Id);
                if (safeBrushes)
                {
                    if (d.Brushes.Any(b => b.Solid && Inside(s.Position, b, 0)))
                        r.Error("FP-MAP-002", "Spawn is inside solid geometry.", s.Id);
                    else if (d.Brushes.Any(b => b.Solid && Inside(new[] { s.Position[0], s.Position[1] + 1.8f, s.Position[2] }, b, 0.3f)))
                        r.Warning("FP-MAP-002", "Spawn has insufficient headroom.", s.Id);
                    if (d.Import == null && d.Geometry.Count == 0 && !d.Brushes.Any(b => b.Solid && Supported(s.Position, b)))
                        r.Warning("FP-MAP-014", "Spawn has no nearby supporting floor.", s.Id);
                }
            }
            foreach (var item in d.Items)
            {
                if (item == null) { r.Error("FP-MAP-015", "Null pickup."); continue; }
                Id(item.Id);
                if (!Position(item.Position) || !Enum.TryParse<ItemType>(item.Type, true, out var type) || !MapBuilder.MultiplayerItems.Contains(type))
                    r.Error("FP-MAP-015", "Pickup needs a finite position and a supported multiplayer item type.", item.Id);
                if (item.SpawnInterval == 0) r.Warning("FP-MAP-015", "Pickup respawn interval is zero.", item.Id);
            }
            foreach (var pad in d.JumpPads)
            {
                if (pad == null) { r.Error("FP-MAP-016", "Null jump pad."); continue; }
                Id(pad.Id);
                bool valid = Position(pad.Position) && Position(pad.Size) && pad.Size.All(x => x > 0)
                    && ((pad.Target != null) != (pad.Vector != null))
                    && (pad.Target == null || Position(pad.Target))
                    && (pad.Vector == null || (Vector(pad.Vector) && pad.Vector.Any(x => x != 0) && float.IsFinite(pad.Speed) && pad.Speed > 0));
                if (!valid) { r.Error("FP-MAP-016", "Jump pad requires positive trigger dimensions and exactly one of target or nonzero vector plus positive speed.", pad.Id); continue; }
                var (direction, speed) = MapBuilder.SolveJumpPad(pad);
                if (!float.IsFinite(speed) || speed <= 0) r.Error("FP-MAP-016", "Jump pad trajectory cannot be solved.", pad.Id);
                else if (safeBrushes)
                {
                    var velocity = direction * speed;
                    float duration = pad.Target == null ? 90 : (velocity.Y + MathF.Sqrt(MathF.Max(0,
                        velocity.Y * velocity.Y - 2 * (77 / 4096f) * (pad.Target[1] - pad.Position[1])))) / (77 / 4096f);
                    for (int i = 1; i < 60; i++)
                    {
                        float t = duration * i / 60;
                        var p = MapBuilder.ToVector(pad.Position) + velocity * t - Vector3.UnitY * (0.5f * (77 / 4096f) * t * t);
                        if (d.Brushes.Any(b => b.Solid && Inside(new[] { p.X, p.Y, p.Z }, b, 0)))
                        { r.Warning("FP-MAP-016", "Jump trajectory crosses solid geometry.", pad.Id); break; }
                    }
                }
            }
            if (d.Import is { } import)
            {
                if(string.IsNullOrEmpty(import.Source)||import.ShaderMaterials==null){r.Error("FP-MAP-005","Import source and shader mappings are required.");return r;}
                if(d.Geometry.Count!=0||d.Brushes.Count!=0)r.Error("FP-MAP-013","Imported architecture is read-only; additional primitives require a native project.");
                if (!float.IsFinite(import.UnitsPerUnit) || import.UnitsPerUnit <= 0 || !float.IsFinite(import.TexScale) || import.TexScale <= 0
                    || import.PatchLevel is < 1 or > 8) r.Error("FP-MAP-017", "Import scale/UV scale must be positive and patch level 1–8.");
                if (checkSources)
                {
                    string? source = import.Resolve();
                    if (source == null) r.Error("FP-MAP-005", "Imported level could not be resolved.");
                    else if (!Path.GetExtension(source).Equals(".bsp", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            var maps = Q3Bsp.ListMaps(source);
                            if (import.MapName != null && !maps.Any(m => m.Equals(import.MapName, StringComparison.OrdinalIgnoreCase)))
                                r.Error("FP-MAP-005", $"Archive does not contain {import.MapName}.");
                            else if (!maps.Any()) r.Error("FP-MAP-005", "Archive contains no BSP levels.");
                        }
                        catch (Exception ex) when (ex is InvalidDataException or IOException or ProgramException) { r.Error("FP-MAP-005", ex.Message); }
                    }
                    if (!string.IsNullOrEmpty(import.Textures) && import.BundlePath == null && import.ResolveTextures() == null)
                    {
                        if (source != null && Path.GetExtension(source).Equals(".pk3", StringComparison.OrdinalIgnoreCase))
                            r.Warning("FP-MAP-006", "Texture pack is missing; the compiler will attempt to bake it from the PK3.");
                        else r.Error("FP-MAP-006", "Texture pack is missing and cannot be baked from this source.");
                    }
                }
            }
            MapAssets.Validate(d,r,checkSources);
            MapModeValidator.Validate(d,r);
            if(d.NavigationLinks==null)r.Error("FP-MAP-007","Navigation links cannot be null.");
            else if(d.NavigationLinks.Count>4096)r.Error("FP-MAP-007","Too many navigation links.");
            else foreach(var link in d.NavigationLinks)
                if(link==null||!Position(link.From)||!Position(link.To)||!Enum.IsDefined(link.Kind))r.Error("FP-MAP-007","Invalid navigation link.");
                else Id(link.Id);
            return r;
        }

        private static bool Inside(float[] p, MapBrush b, float radius) => Enumerable.Range(0, 3)
            .All(i => p[i] > Math.Min(b.Min[i], b.Max[i]) - radius + 0.01f && p[i] < Math.Max(b.Min[i], b.Max[i]) + radius - 0.01f);

        private static bool Supported(float[] p, MapBrush b) => p[0] >= Math.Min(b.Min[0], b.Max[0])
            && p[0] <= Math.Max(b.Min[0], b.Max[0]) && p[2] >= Math.Min(b.Min[2], b.Max[2])
            && p[2] <= Math.Max(b.Min[2], b.Max[2]) && p[1] >= Math.Max(b.Min[1], b.Max[1])
            && p[1] - Math.Max(b.Min[1], b.Max[1]) <= 3;
    }
}
