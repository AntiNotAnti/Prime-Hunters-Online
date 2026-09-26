using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using MphRead.Editor;
using MphRead.Formats.Collision;
using MphRead.Utility;
using OpenTK.Mathematics;

namespace MphRead.Mods.MapGen
{
    /// <summary>
    /// Writes model, animation, collision, entities and navigation, then the
    /// build manifest that marks the complete output set.
    /// </summary>
    public static class MapPacker
    {
        public static void Generate(BuiltMap map, string archiveDir, string entityDir, string nodeDir,
            bool verbose = true, CancellationToken cancellation = default)
        {
            cancellation.ThrowIfCancellationRequested();
            MapDefinition def = map.Definition;
            if(Metadata.IsBuiltInRoom(def.Name))throw new MapAuthoringException("FP-MAP-010","A custom map cannot replace a built-in room.");
            var validation = MapValidator.Validate(def);
            MapBudgetValidator.Analyze(map, validation);
            MapCompiler.ThrowIfInvalid(validation);
            MapOutputSet outputs = MapOutputSet.Create(def, archiveDir, entityDir, nodeDir);
            Directory.CreateDirectory(archiveDir);
            Directory.CreateDirectory(entityDir);
            byte[] model; int vertices;
            lock (MapCompiler.ContentReadLock) (model, vertices) = BuildModel(map);
            cancellation.ThrowIfCancellationRequested();
            byte[] collision = BuildCollision(map);
            byte[] entities = Repack.PackEntities(map.Entities);
            (byte[] nodes, int nodeCount, int edges) = MapNodePacker.Pack(map.Solid,def.NavigationLinks,cancellation);
            // Build every byte before replacing any output. The manifest is the
            // commit marker: an interrupted publication is rebuilt next launch.
            cancellation.ThrowIfCancellationRequested();
            if (File.Exists(outputs.Manifest)) File.Delete(outputs.Manifest);
            AtomicFile.Write(outputs.Model, model);
            AtomicFile.Write(outputs.Animation, new byte[24]);
            AtomicFile.Write(outputs.Collision, collision);
            AtomicFile.Write(outputs.Entities, entities);
            AtomicFile.Write(outputs.Nodes, nodes);
            MapBuildManifest.Write(map.SourceDefinition ?? def, outputs);
            if (verbose)
            {
                Console.WriteLine($"{def.Name}: {map.Faces.Count} polygons ({vertices} vertices), "
                    + $"{map.Solid.Count} collision faces, {map.Entities.Count} entities");
                Console.WriteLine($"  {nodeCount} bot waypoints, {edges} routes between them");
                Console.WriteLine($"  model {model.Length:N0} B, collision {collision.Length:N0} B, "
                    + $"entities {entities.Length:N0} B, nodes {nodes.Length:N0} B");
            }
        }

        public static void Generate(MapDefinition def, string archiveDir, string entityDir, string nodeDir,
            bool verbose = true)
        {
            var result = MapBuildScheduler.Shared.BuildAsync(MapBuildSnapshot.Capture(def)).GetAwaiter().GetResult();
            MapCompiler.ThrowIfInvalid(result.Validation());
            MapBuildScheduler.Install(result, def, archiveDir, entityDir, nodeDir);
            if (verbose) Console.WriteLine($"{def.Name}: {(result.CacheHit ? "cached" : "compiled")} runtime files ({result.Milliseconds:0} ms)");
        }

        /// <summary>
        /// Swap the room's collision for the one in the map's .obj, if it
        /// names one.
        ///
        /// Here rather than inside either builder because it is the same
        /// answer to both: collision and drawn geometry are already separate
        /// lists, so a map may be drawn from a converted level and blocked by
        /// a hand-edited mesh, which is the arrangement this exists for.
        /// `MapNodePacker` reads the same list, so the bots' waypoints follow
        /// the edit with nothing else to do.
        /// </summary>
        public static void ApplyCollision(BuiltMap map, MapDefinition def, bool verbose)
        {
            if (def.Collision == null) return;
            if (string.IsNullOrWhiteSpace(def.Collision.Source))
                throw new ProgramException("Collision mesh source must be a nonempty path.");
            byte[]? bytes = def.Collision.ReadBytes();
            if (bytes == null)
            {
                throw new ProgramException(
                    $"{def.Name} says its collision is {def.Collision.Source}, which is not beside "
                    + $"the map file, in {CustomRooms.MapDirectory}, or with the game files. "
                    + "Write one with tools/collision-to-obj.py, or take the \"collision\" key out "
                    + "to go back to the collision the geometry makes.");
            }
            CollisionObj.Result read = CollisionObj.Read(bytes, def.Collision.Source, def.Collision.ZUp);
            int replaced = map.Solid.Count;
            map.Solid.Clear();
            map.Solid.AddRange(read.Faces);
            // Replacement collision becomes the static architecture collision
            // for an imported map. Keep the viewport boundary accurate after
            // the source BSP collision has been replaced.
            map.ImportedCollisionFaceCount = def.Import != null ? map.Solid.Count : 0;
            if (verbose)
            {
                Console.WriteLine($"  collision from {def.Collision.Source}: {read.Faces.Count} faces"
                    + $" over {read.Vertices} vertices, in place of the geometry's {replaced}"
                    + (read.Degenerate > 0 ? $" ({read.Degenerate} enclosing no area, skipped)" : ""));
            }
        }

        private static (byte[], int) BuildModel(BuiltMap map)
        {
            MapDefinition def = map.Definition;
            MapTexturePack? own = def.Import?.LoadTexturePack();
            if (own != null)
            {
                return BuildModel(map, own);
            }
            Model? source = def.Materials.Any(m=>m.Texture==null) ? Read.GetRoomModelInstance(def.TextureSource).Model : null;
            Recolor? recolor = source?.Recolors[0];
            // copy only the textures the map asks for, remapping the IDs as we
            // go -- the texture and its palette are copied as a pair, so a
            // material can never end up wearing someone else's colours
            var textures = new List<Repack.TextureInfo>();
            var palettes = new List<Repack.PaletteInfo>();
            var textureMap = new Dictionary<int, int>();
            var paletteMap = new Dictionary<int, int>();
            var materials = new List<Material>();
            foreach (MapMaterial mapMaterial in def.Materials)
            {
                if(mapMaterial.Texture!=null)
                {
                    MapTexturePack pack=MapTexturePack.Load(MapAssets.Read(def,mapMaterial.Texture),mapMaterial.Texture);
                    if(pack.Entries.Count!=1)throw new MapAuthoringException("FP-MAP-001","A native material texture pack must contain one texture.");
                    var entry=pack.Entries[0];int ownTexture=textures.Count,ownPalette=palettes.Count;
                    textures.Add(new Repack.TextureInfo(TextureFormat.Palette8Bit,opaque:true,entry.Height,entry.Width,entry.Pixels));
                    palettes.Add(new Repack.PaletteInfo(entry.Palette));
                    materials.Add(RawStructs.MakeMaterial(mapMaterial.Name,ownTexture,ownPalette,RepeatMode.Repeat,RepeatMode.Repeat,lighting:false,
                        diffuse:new ColorRgb(31,31,31),ambient:new ColorRgb(0,0,0)));
                    continue;
                }
                if(source==null||recolor==null)throw new MapAuthoringException("FP-MAP-001","Missing source material.");
                if (mapMaterial.SourceMaterial < 0 || mapMaterial.SourceMaterial >= source.Materials.Count)
                {
                    throw new ProgramException($"{def.TextureSource} has no material {mapMaterial.SourceMaterial}.");
                }
                Material srcMaterial = source.Materials[mapMaterial.SourceMaterial];
                if (srcMaterial.TextureId < 0 || srcMaterial.PaletteId < 0)
                {
                    throw new ProgramException(
                        $"Material {mapMaterial.SourceMaterial} of {def.TextureSource} has no texture.");
                }
                if (!textureMap.TryGetValue(srcMaterial.TextureId, out int textureId))
                {
                    textureId = textures.Count;
                    textures.Add(Repack.ConvertData(recolor.Textures[srcMaterial.TextureId],
                        recolor.TextureData[srcMaterial.TextureId]));
                    textureMap.Add(srcMaterial.TextureId, textureId);
                }
                if (!paletteMap.TryGetValue(srcMaterial.PaletteId, out int paletteId))
                {
                    paletteId = palettes.Count;
                    palettes.Add(new Repack.PaletteInfo(recolor.PaletteData[srcMaterial.PaletteId]
                        .Select(d => d.Data).ToList()));
                    paletteMap.Add(srcMaterial.PaletteId, paletteId);
                }
                materials.Add(RawStructs.MakeMaterial(mapMaterial.Name, textureId, paletteId,
                    RepeatMode.Repeat, RepeatMode.Repeat, lighting: false,
                    diffuse: new ColorRgb(31, 31, 31), ambient: new ColorRgb(0, 0, 0)));
            }
            if (materials.Count == 0)
            {
                throw new ProgramException("A map needs at least one material.");
            }
            return Assemble(map, def, materials, textures, palettes);
        }

        /// <summary>
        /// Geometry into a model file, once the materials are decided: the
        /// part that does not care where the textures came from.
        /// </summary>
        private static (byte[], int) Assemble(BuiltMap map, MapDefinition def, List<Material> materials,
            List<Repack.TextureInfo> textures, List<Repack.PaletteInfo> palettes)
        {
            float scale = MathF.Pow(2, def.ScaleFactor);
            var renders = new List<IReadOnlyList<RenderInstruction>>();
            var meshes = new List<Mesh>();
            var nodeMeshes = new List<(int First,int Count)>();
            int vertexCount = 0;

            // A single material-sized display list is fine for cartridge-scale
            // rooms, but imported PC maps can put hundreds of thousands of
            // triangles behind one shader. Split large maps spatially first,
            // then cap each display list's vertex load. This preserves one
            // logical room part while giving the renderer bounded child nodes
            // with useful culling bounds.
            IEnumerable<(int X,int Y,int Z,IReadOnlyList<BuiltFace> Faces)> chunks;
            if(map.Faces.Count<RenderPartitionThreshold)
                chunks=new[]{(0,0,0,(IReadOnlyList<BuiltFace>)map.Faces)};
            else
                chunks=map.Faces.GroupBy(FaceCell).OrderBy(g=>g.Key.X).ThenBy(g=>g.Key.Y).ThenBy(g=>g.Key.Z)
                    .Select(g=>(g.Key.X,g.Key.Y,g.Key.Z,(IReadOnlyList<BuiltFace>)g.ToArray()));

            foreach(var chunk in chunks)
            {
                int firstMesh=meshes.Count;
                foreach(var materialGroup in chunk.Faces.GroupBy(f=>f.Material).OrderBy(g=>g.Key))
                {
                    if(materialGroup.Key<0||materialGroup.Key>=materials.Count)
                        throw new MapAuthoringException("FP-MAP-001",$"Geometry references material {materialGroup.Key}, but only {materials.Count} exist.");
                    var normalized=materialGroup.SelectMany(face=>face.Points.Length<=4?new[]{face}:Fan(face)).ToArray();
                    foreach(var batch in RenderBatches(normalized,MaxRenderVerticesPerList))
                    {
                        if(renders.Count>=UInt16.MaxValue)
                            throw new MapAuthoringException("FP-MAP-003","Render display-list budget exceeded; increase spatial partition size or simplify geometry.");
                        var instructions=new List<RenderInstruction>();
                        vertexCount+=EmitPrimitives(instructions,batch.Where(f=>f.Points.Length==3),0,scale);
                        vertexCount+=EmitPrimitives(instructions,batch.Where(f=>f.Points.Length==4),1,scale);
                        while(instructions.Count%4!=0)instructions.Add(new RenderInstruction(InstructionCode.NOP));
                        meshes.Add(RawStructs.MakeMesh(materialGroup.Key,renders.Count));
                        renders.Add(instructions);
                    }
                }
                int meshCount=meshes.Count-firstMesh;
                if(meshCount>0)
                {
                    if(firstMesh>UInt16.MaxValue/2)
                        throw new MapAuthoringException("FP-MAP-003","Render mesh offset exceeds the native room format's 16-bit byte offset.");
                    nodeMeshes.Add((firstMesh,meshCount));
                }
            }

            if(nodeMeshes.Count==0)throw new MapAuthoringException("FP-MAP-013","A map needs visible geometry.");
            if(nodeMeshes.Count>=Int16.MaxValue)throw new MapAuthoringException("FP-MAP-003","Render partition node budget exceeded.");
            var nodes=new List<Node>
            {
                RawStructs.MakeNode("rmMain",meshCount:0,firstMeshId:0,child:1)
            };
            for(int i=0;i<nodeMeshes.Count;i++)
            {
                var part=nodeMeshes[i];
                nodes.Add(RawStructs.MakeNode($"geo{i+1:D4}",part.Count,part.First,parent:0,
                    next:i+1<nodeMeshes.Count?i+2:-1));
            }
            var dlists = new DisplayList[renders.Count];
            var options = new Repack.RepackOptions()
            {
                IsRoom = true,
                Texture = Repack.RepackTexture.Inline,
                ComputeBounds = Repack.ComputeBounds.Capped
            };
            (byte[] bytes, _) = Repack.PackModel((int)scale, Array.Empty<int>(), Array.Empty<int>(),
                materials, textures, palettes, nodes, meshes, renders, dlists, options);
            return (bytes, vertexCount);
        }

        internal const int RenderPartitionThreshold=8192;
        internal const int MaxRenderVerticesPerList=60000;
        internal const float RenderCellSize=64f;

        internal readonly record struct RenderLayoutEstimate(int Partitions,int Meshes,int Vertices,long CommandBytes);

        internal static RenderLayoutEstimate EstimateRenderLayout(BuiltMap map)
        {
            IEnumerable<IGrouping<(int X,int Y,int Z),BuiltFace>> groups =
                map.Faces.GroupBy(face=>map.Faces.Count<RenderPartitionThreshold?(0,0,0):FaceCell(face));
            int partitions=0,meshes=0,vertices=0;long bytes=0;
            foreach(var chunk in groups)
            {
                bool any=false;
                foreach(var material in chunk.GroupBy(f=>f.Material))
                {
                    var normalized=material.SelectMany(face=>face.Points.Length<=4?new[]{face}:Fan(face)).ToArray();
                    foreach(var batch in RenderBatches(normalized,MaxRenderVerticesPerList))
                    {
                        any=true;meshes++;
                        int batchVertices=batch.Sum(f=>f.Points.Length);
                        int instructions=2; // BEGIN/END
                        long arguments=1; // BEGIN primitive argument
                        foreach(var face in batch)
                        {
                            instructions+=2+2*face.Points.Length;
                            arguments+=2+3L*face.Points.Length;
                        }
                        instructions=(instructions+3)/4*4;
                        bytes+=(instructions/4)*4+arguments*4;
                        vertices+=batchVertices;
                    }
                }
                if(any)partitions++;
            }
            return new(partitions,meshes,vertices,bytes);
        }
        private static (int X,int Y,int Z) FaceCell(BuiltFace face)
        {
            if(face.Points.Length==0)return(0,0,0);
            Vector3 centre=Vector3.Zero;foreach(var point in face.Points)centre+=point;centre/=face.Points.Length;
            return((int)MathF.Floor(centre.X/RenderCellSize),(int)MathF.Floor(centre.Y/RenderCellSize),(int)MathF.Floor(centre.Z/RenderCellSize));
        }

        private static IEnumerable<IReadOnlyList<BuiltFace>> RenderBatches(
            IReadOnlyList<BuiltFace> faces,int maxVertices)
        {
            var batch=new List<BuiltFace>();int vertices=0;
            foreach(var face in faces)
            {
                int count=face.Points.Length;
                if(batch.Count>0&&vertices+count>maxVertices)
                {yield return batch.ToArray();batch.Clear();vertices=0;}
                batch.Add(face);vertices+=count;
            }
            if(batch.Count>0)yield return batch.ToArray();
        }

        /// <summary>
        /// The same model, wearing the level's own textures.
        ///
        /// Nothing is borrowed from a shipped room here, so nothing that came
        /// off the cartridge ends up in the file: one material per shader, and
        /// each one's image and palette straight out of the pack.
        /// </summary>
        private static (byte[], int) BuildModel(BuiltMap map, MapTexturePack pack)
        {
            MapDefinition def = map.Definition;
            var textures = new List<Repack.TextureInfo>();
            var palettes = new List<Repack.PaletteInfo>();
            var materials = new List<Material>();
            foreach (MapTexturePack.Entry entry in pack.Entries)
            {
                textures.Add(new Repack.TextureInfo(TextureFormat.Palette8Bit, opaque: true,
                    entry.Height, entry.Width, (IReadOnlyList<byte>)entry.Pixels));
                palettes.Add(new Repack.PaletteInfo(entry.Palette));
                // The shader name is longer than a material name may be, and
                // the tail is the part that identifies it.
                string name = entry.Name.Length <= 30 ? entry.Name : entry.Name[^30..];
                materials.Add(RawStructs.MakeMaterial(name, textures.Count - 1, palettes.Count - 1,
                    RepeatMode.Repeat, RepeatMode.Repeat, lighting: false,
                    diffuse: new ColorRgb(31, 31, 31), ambient: new ColorRgb(0, 0, 0)));
            }

            // Imported architecture occupies [0, pack.Count). Authored hybrid
            // geometry is compiled with an offset and uses these materials
            // appended after the BSP set.
            Model? source = def.Materials.Any(m => m.Texture == null)
                ? Read.GetRoomModelInstance(def.TextureSource).Model : null;
            Recolor? recolor = source?.Recolors[0];
            var sourceTextures = new Dictionary<int, int>();
            var sourcePalettes = new Dictionary<int, int>();
            foreach (MapMaterial mapMaterial in def.Materials)
            {
                if (mapMaterial.Texture != null)
                {
                    MapTexturePack own = MapTexturePack.Load(MapAssets.Read(def, mapMaterial.Texture), mapMaterial.Texture);
                    if (own.Entries.Count != 1)
                        throw new MapAuthoringException("FP-MAP-001", "A native material texture pack must contain one texture.");
                    var entry = own.Entries[0];
                    int textureId = textures.Count, paletteId = palettes.Count;
                    textures.Add(new Repack.TextureInfo(TextureFormat.Palette8Bit, opaque: true,
                        entry.Height, entry.Width, entry.Pixels));
                    palettes.Add(new Repack.PaletteInfo(entry.Palette));
                    materials.Add(RawStructs.MakeMaterial(mapMaterial.Name, textureId, paletteId,
                        RepeatMode.Repeat, RepeatMode.Repeat, lighting: false,
                        diffuse: new ColorRgb(31, 31, 31), ambient: new ColorRgb(0, 0, 0)));
                    continue;
                }
                if (source == null || recolor == null)
                    throw new MapAuthoringException("FP-MAP-001", "Missing source material.");
                if (mapMaterial.SourceMaterial < 0 || mapMaterial.SourceMaterial >= source.Materials.Count)
                    throw new ProgramException($"{def.TextureSource} has no material {mapMaterial.SourceMaterial}.");
                Material srcMaterial = source.Materials[mapMaterial.SourceMaterial];
                if (srcMaterial.TextureId < 0 || srcMaterial.PaletteId < 0)
                    throw new ProgramException($"Material {mapMaterial.SourceMaterial} of {def.TextureSource} has no texture.");
                if (!sourceTextures.TryGetValue(srcMaterial.TextureId, out int textureId2))
                {
                    textureId2 = textures.Count;
                    textures.Add(Repack.ConvertData(recolor.Textures[srcMaterial.TextureId],
                        recolor.TextureData[srcMaterial.TextureId]));
                    sourceTextures.Add(srcMaterial.TextureId, textureId2);
                }
                if (!sourcePalettes.TryGetValue(srcMaterial.PaletteId, out int paletteId2))
                {
                    paletteId2 = palettes.Count;
                    palettes.Add(new Repack.PaletteInfo(recolor.PaletteData[srcMaterial.PaletteId]
                        .Select(d => d.Data).ToList()));
                    sourcePalettes.Add(srcMaterial.PaletteId, paletteId2);
                }
                materials.Add(RawStructs.MakeMaterial(mapMaterial.Name, textureId2, paletteId2,
                    RepeatMode.Repeat, RepeatMode.Repeat, lighting: false,
                    diffuse: new ColorRgb(31, 31, 31), ambient: new ColorRgb(0, 0, 0)));
            }
            if (materials.Count == 0)
            {
                throw new ProgramException("The texture pack is empty.");
            }
            return Assemble(map, def, materials, textures, palettes);
        }

        /// <summary>Splits a polygon with more than four sides into a triangle fan.</summary>
        private static IEnumerable<BuiltFace> Fan(BuiltFace face)
        {
            for (int i = 1; i < face.Points.Length - 1; i++)
            {
                yield return new BuiltFace(
                    new[] { face.Points[0], face.Points[i], face.Points[i + 1] },
                    new[] { face.Texcoords[0], face.Texcoords[i], face.Texcoords[i + 1] },
                    face.Normal, face.Material, face.Shade);
            }
        }

        private static int EmitPrimitives(List<RenderInstruction> instructions, IEnumerable<BuiltFace> faces,
            uint primitiveType, float scale)
        {
            List<BuiltFace> list = faces.ToList();
            if (list.Count == 0)
            {
                return 0;
            }
            int vertexCount = 0;
            instructions.Add(new RenderInstruction(InstructionCode.BEGIN_VTXS, primitiveType));
            foreach (BuiltFace face in list)
            {
                instructions.Add(new RenderInstruction(InstructionCode.COLOR, PackColor(face.Shade)));
                instructions.Add(new RenderInstruction(InstructionCode.NORMAL, PackNormal(face.Normal)));
                for (int i = 0; i < face.Points.Length; i++)
                {
                    instructions.Add(new RenderInstruction(InstructionCode.TEXCOORD,
                        PackTexcoord(face.Texcoords[i].X, face.Texcoords[i].Y)));
                    instructions.Add(PackVertex(face.Points[i], scale));
                    vertexCount++;
                }
            }
            instructions.Add(new RenderInstruction(InstructionCode.END_VTXS));
            return vertexCount;
        }

        private static uint PackColor(float shade)
        {
            uint value = (uint)Math.Clamp((int)MathF.Round(31 * shade), 0, 31);
            return value | (value << 5) | (value << 10);
        }

        private static uint PackNormal(Vector3 normal)
        {
            static uint Component(float value)
            {
                int packed = Math.Clamp((int)MathF.Round(value * 512), -512, 511);
                return (uint)packed & 0x3FF;
            }
            return Component(normal.X) | (Component(normal.Y) << 10) | (Component(normal.Z) << 20);
        }

        private static uint PackTexcoord(float u, float v)
        {
            static uint Component(float value)
            {
                int packed = Math.Clamp((int)MathF.Round(value * 16), Int16.MinValue, Int16.MaxValue);
                return (uint)packed & 0xFFFF;
            }
            return Component(u) | (Component(v) << 16);
        }

        private static RenderInstruction PackVertex(Vector3 point, float scale)
        {
            static uint Component(float value, float scale)
            {
                int packed = Fixed.ToInt(value / scale);
                if (packed < Int16.MinValue || packed > Int16.MaxValue)
                {
                    throw new ProgramException(
                        $"Vertex {value} does not fit at scale {scale}; raise the map's scaleFactor.");
                }
                return (uint)packed & 0xFFFF;
            }
            uint x = Component(point.X, scale);
            uint y = Component(point.Y, scale);
            uint z = Component(point.Z, scale);
            return new RenderInstruction(InstructionCode.VTX_16, x | (y << 16), z);
        }

        internal static IEnumerable<BuiltFace> CollisionParts(BuiltFace face)
            => face.Points.Length <= 10 ? new[] { face } : Fan(face);

        private static byte[] BuildCollision(BuiltMap map)
        {
            var editors = new List<CollisionDataEditor>();
            foreach (BuiltFace face in map.Solid)
            {
                // the collision format takes at most ten points per face
                foreach (BuiltFace part in CollisionParts(face))
                {
                    var editor = new CollisionDataEditor()
                    {
                        // bit 2 means the face is in every layer; the low two
                        // bits are the normal's primary axis, which the
                        // point-on-face test reads to pick its projection
                        LayerMask = (ushort)(4 | GetPrimaryAxis(part.Normal)),
                        Plane = new Vector4(part.Normal, Vector3.Dot(part.Normal, part.Points[0])),
                        Damaging = face.Damaging,
                        Terrain = face.Terrain,
                        Slipperiness = face.Slipperiness,
                        Reflect = face.ReflectBeams,
                        // the editor states these the other way round: it asks
                        // whether a face is there for players, beams and the
                        // scan visor, and the file stores whether to ignore it
                        Players = !face.IgnorePlayers,
                        Beams = !face.IgnoreBeams,
                        Scan = !face.IgnoreScan
                    };
                    editor.Points.AddRange(part.Points);
                    editors.Add(editor);
                }
            }
            if (editors.Count == 0)
            {
                throw new ProgramException("A map needs at least one solid face.");
            }
            return MapCollisionPacker.Pack(editors);
        }

        public static int GetPrimaryAxis(Vector3 normal)
        {
            float x = MathF.Abs(normal.X);
            float y = MathF.Abs(normal.Y);
            float z = MathF.Abs(normal.Z);
            if (y > x && y >= z)
            {
                return 1;
            }
            if (z > x && z > y)
            {
                return 2;
            }
            return 0;
        }
    }
}
