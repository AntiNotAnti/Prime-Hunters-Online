using System;
using System.Collections.Generic;
using System.Linq;
using MphRead.Mods.MapGen;

namespace MphRead.Mods.MapEditor;

/// <summary>CPU authoring caches. Camera movement never enters this invalidation path.</summary>
public sealed record MapViewportRepair(MapCollisionRepairKind Kind,float Confidence,string Detail,
    System.Numerics.Vector3[] Points);

public sealed class MapViewportCache
{
    private readonly Dictionary<Guid, MapViewportFace[]> _native = new();
    private readonly Dictionary<Guid, MapViewportMesh> _meshes = new();
    public IReadOnlyList<MapViewportMesh> Meshes { get; private set; } = Array.Empty<MapViewportMesh>();
    private IReadOnlyList<MapViewportChunk> _importedChunks = Array.Empty<MapViewportChunk>();
    public IReadOnlyList<MapViewportChunk> ImportedChunks => _importedChunks;
    public MapFaceSpatialIndex ImportedSpatial { get; private set; } = new(Array.Empty<MapViewportFace>());
    public MapFaceSpatialIndex ImportedCollisionSpatial { get; private set; } = new(Array.Empty<MapViewportFace>());
    public IReadOnlyList<MapCollisionHeatCell> CollisionHeat { get; private set; } = Array.Empty<MapCollisionHeatCell>();
    public IReadOnlyList<MapViewportRepair> CollisionRepairs { get; private set; } = Array.Empty<MapViewportRepair>();
    public MapCollisionHealthSnapshot? CollisionHealth { get; private set; }
    public IReadOnlyList<MapViewportFace> NativeFaces { get; private set; } = Array.Empty<MapViewportFace>();
    public IReadOnlyList<MapViewportFace> ImportedFaces { get; private set; } = Array.Empty<MapViewportFace>();
    public IReadOnlyList<MapViewportFace> ImportedCollisionFaces { get; private set; } = Array.Empty<MapViewportFace>();
    public IReadOnlyList<MapObject> Entities { get; private set; } = Array.Empty<MapObject>();
    public int GeometryRebuildCount { get; private set; }
    public int GeometryObjectsRebuilt { get; private set; }
    public int EntityRebuildCount { get; private set; }
    public int SelectionRebuildCount { get; private set; }
    public int ImportedRebuildCount { get; private set; }
    public int CollisionRebuildCount { get; private set; }
    public int NavigationInvalidationCount { get; private set; }
    public int OverlayInvalidationCount { get; private set; }

    public void Invalidate(MapDefinition definition, MapDocumentChange change)
    {
        var domains = change.Domains;
        bool geometry = (domains & (MapChangeDomain.Geometry | MapChangeDomain.Material)) != 0;
        if (geometry)
        {
            var ids = change.ObjectIds?.ToHashSet();
            if (ids == null) { _native.Clear(); _meshes.Clear(); }
            else foreach (var id in ids) { _native.Remove(id); _meshes.Remove(id); }
            var scene = MapViewportScene.Create(definition, ids);
            foreach (var group in scene.Faces.GroupBy(f => f.ObjectId))
            {
                var faces = group.ToArray();
                _native[group.Key] = faces;
                _meshes[group.Key] = new(group.Key, Array.AsReadOnly(faces));
            }
            NativeFaces = Array.AsReadOnly(_native.Values.SelectMany(f => f).ToArray());
            GeometryRebuildCount++;
            CollisionRebuildCount++;
            GeometryObjectsRebuilt += definition.Geometry.Count(g => ids == null || ids.Contains(g.Id))
                + definition.Brushes.Count(b => ids == null || ids.Contains(b.Id));
        }
        bool navigationEntity = domains.HasFlag(MapChangeDomain.Navigation)
            && (change.ObjectIds == null || definition.NavigationLinks.Any(n => change.ObjectIds.Contains(n.Id))
                || Entities.Any(o => o.Value is MapNavigationLink && change.ObjectIds.Contains(o.Id)));
        if (domains.HasFlag(MapChangeDomain.Entity) || navigationEntity)
        {
            Entities = Array.AsReadOnly(MapObjects.All(definition)
                .Where(o => o.Value is not MapGeometry and not MapBrush).ToArray());
            EntityRebuildCount++;
        }
        if (domains.HasFlag(MapChangeDomain.Selection)) SelectionRebuildCount++;
        if (domains.HasFlag(MapChangeDomain.Navigation)) NavigationInvalidationCount++;
        if (domains.HasFlag(MapChangeDomain.Overlay)) OverlayInvalidationCount++;
        if (domains.HasFlag(MapChangeDomain.Import))
        {
            ImportedFaces = ImportedCollisionFaces = Array.Empty<MapViewportFace>();
            _importedChunks = Array.Empty<MapViewportChunk>();
            ImportedSpatial = new(Array.Empty<MapViewportFace>());
            ImportedCollisionSpatial = new(Array.Empty<MapViewportFace>());
            CollisionHeat = Array.Empty<MapCollisionHeatCell>();
            CollisionRepairs = Array.Empty<MapViewportRepair>();
            CollisionHealth = null;
        }
        if (geometry || domains.HasFlag(MapChangeDomain.Import)) UpdateMeshes();
    }

    public void SetImported(BuiltMap map)
    {
        ImportedFaces = Array.AsReadOnly(map.Faces.Take(map.ImportedFaceCount).Select(face => new MapViewportFace(Guid.Empty,
            face.Points.Select(p => new System.Numerics.Vector3(p.X, p.Y, p.Z)).ToArray(),
            face.Shade, face.Material, true)).ToArray());
        ImportedRebuildCount++;
        ImportedCollisionFaces = Array.AsReadOnly(map.Solid.Take(map.ImportedCollisionFaceCount).Select(face => new MapViewportFace(Guid.Empty,
            face.Points.Select(p => new System.Numerics.Vector3(p.X, p.Y, p.Z)).ToArray(), face.Shade, face.Material, true)).ToArray());
        CollisionRebuildCount++;
        CollisionRepairs = Array.AsReadOnly(map.CollisionRepairs.Select(r => new MapViewportRepair(
            r.Kind,r.Confidence,r.Detail,r.Points.Select(p=>new System.Numerics.Vector3(p.X,p.Y,p.Z)).ToArray())).ToArray());
        CollisionHealth = map.CollisionHealth is { } h
            ? new MapCollisionHealthSnapshot(h.InputFaces,h.OutputFaces,h.CanonicalizedFaces,h.ConvexifiedFaces,
                h.StitchedVertices,h.TJunctions,h.RestoredBuriedFaces,h.FloorProxies,h.PhantomFacesRemoved,
                h.SpawnsMoved,h.ItemsMoved,h.NavigationNodes,h.ReachableNodes,h.NavigationComponents,
                h.ReachableComponents,h.ProbeCount,h.ProbeFailures,h.SweepCount,h.SweepFailures,h.Confidence)
            : null;
        RebuildImported();
    }

    public void SetImported(MapAnalysisResult analysis)
    {
        ImportedFaces = Array.AsReadOnly(analysis.Faces.Take(analysis.ImportedFaceCount).Select(face => new MapViewportFace(Guid.Empty,
            face.Points.Select(p => new System.Numerics.Vector3(p.X, p.Y, p.Z)).ToArray(),
            face.Shade, face.Material, true)).ToArray());
        ImportedRebuildCount++;
        ImportedCollisionFaces = Array.AsReadOnly(analysis.CollisionFaces.Take(analysis.ImportedCollisionFaceCount).Select(face => new MapViewportFace(Guid.Empty,
            face.Points.Select(p => new System.Numerics.Vector3(p.X, p.Y, p.Z)).ToArray(), face.Shade, face.Material, true)).ToArray());
        CollisionRebuildCount++;
        CollisionRepairs = Array.AsReadOnly(analysis.CollisionRepairs.Select(r => new MapViewportRepair(
            r.Kind,r.Confidence,r.Detail,r.Points.Select(p=>new System.Numerics.Vector3(p.X,p.Y,p.Z)).ToArray())).ToArray());
        CollisionHealth = analysis.CollisionHealth;
        RebuildImported();
    }

    private void RebuildImported()
    {
        ImportedSpatial = new(ImportedFaces);
        ImportedCollisionSpatial = new(ImportedCollisionFaces);
        _importedChunks = MapViewportChunker.Create(ImportedFaces, ImportedCollisionFaces);
        CollisionHeat = MapCollisionHeatmap.Build(ImportedCollisionFaces);
        UpdateMeshes();
    }

    public IReadOnlyList<MapViewportMesh> VisibleMeshes(MapViewportCamera camera, MapViewportLayout layout)
        => Array.AsReadOnly(_meshes.Values.Concat(_importedChunks.Where(c => c.Visible(camera, layout)).Select(c => c.Mesh)).ToArray());

    public IReadOnlyList<MapViewportFace> VisibleImportedFaces(MapViewportCamera camera, MapViewportLayout layout, bool collision)
        => Array.AsReadOnly(_importedChunks.Where(c => c.Visible(camera, layout))
            .SelectMany(c => collision ? c.CollisionFaces : c.Faces).ToArray());

    public IReadOnlyList<MapViewportFace> CollisionNear(System.Numerics.Vector3 point)
        => Array.AsReadOnly(NativeFaces.Where(f => f.Solid)
            .Concat(ImportedCollisionSpatial.Column(point)).ToArray());

    private void UpdateMeshes() => Meshes = Array.AsReadOnly(_meshes.Values
        .Concat(_importedChunks.Select(c => c.Mesh)).ToArray());
}

/// <summary>One logical/pixel rectangle contract for rendering, picking and capture.</summary>
public readonly record struct MapViewportLayout(double Width, double Height, double RenderScale = 1,
    int PixelX = 0, int PixelY = 0)
{
    public bool IsValid => double.IsFinite(Width) && double.IsFinite(Height) && double.IsFinite(RenderScale)
        && Width > 0 && Height > 0 && RenderScale > 0;
    public double AspectRatio => IsValid ? Width / Height : 1;
    public int PixelWidth => IsValid ? (int)Math.Clamp(Math.Round(Width * RenderScale), 1, int.MaxValue) : 0;
    public int PixelHeight => IsValid ? (int)Math.Clamp(Math.Round(Height * RenderScale), 1, int.MaxValue) : 0;
    public (double X, double Y) Normalize(double logicalX, double logicalY)
        => IsValid ? (logicalX / Width * 2 - 1, 1 - logicalY / Height * 2) : (0, 0);
}
