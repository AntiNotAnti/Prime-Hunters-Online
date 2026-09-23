using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using OpenTK.Mathematics;

namespace MphRead.Mods.MapGen;

public sealed record MapPreviewFace(ImmutableArray<Vector3> Points, float Shade, int Material);

/// <summary>Immutable analysis shared between waiters. Mutable navigation views are copied.</summary>
public sealed class MapAnalysisResult
{
    public string Fingerprint { get; }
    public IReadOnlyList<MapDiagnostic> Diagnostics { get; }
    public IReadOnlyList<MapBudget> Budgets { get; }
    public ImmutableArray<MapPreviewFace> Faces { get; }
    public ImmutableArray<MapPreviewFace> CollisionFaces { get; }
    private readonly MapNodePacker.NavigationGraph? _navigation;
    public bool Succeeded => Diagnostics.All(d => d.Severity != MapDiagnosticSeverity.Error);
    internal MapAnalysisResult(string key, MapCompilation compilation, bool navigation)
    {
        Fingerprint = key;
        var validation = new MapValidationResult();
        validation.Diagnostics.AddRange(compilation.Validation.Diagnostics);
        validation.Budgets.AddRange(compilation.Validation.Budgets);
        Faces = compilation.Map?.Faces.Select(f => new MapPreviewFace(f.Points.ToImmutableArray(), f.Shade, f.Material))
            .ToImmutableArray() ?? ImmutableArray<MapPreviewFace>.Empty;
        CollisionFaces = compilation.Map?.Solid.Select(f => new MapPreviewFace(f.Points.ToImmutableArray(), f.Shade, f.Material))
            .ToImmutableArray() ?? ImmutableArray<MapPreviewFace>.Empty;
        if (navigation && compilation.Map is BuiltMap map)
        {
            try
            {
                _navigation = MapNodePacker.Analyze(map.Solid, map.Definition.NavigationLinks);
                MapBudgetValidator.Add(validation, "Navigation nodes", _navigation.Positions.Length, MapNodePacker.MaxNodes);
                MapBudgetValidator.Add(validation, "Navigation edges", _navigation.Edges);
                int regions = _navigation.Components.Distinct().Count();
                if (regions > 1) validation.Warning("FP-MAP-007", $"Navigation contains {regions} disconnected regions.");
            }
            catch (MapAuthoringException ex) { validation.Error(ex.Code, ex.Message); }
        }
        Diagnostics = Array.AsReadOnly(validation.Diagnostics.ToArray());
        Budgets = Array.AsReadOnly(validation.Budgets.ToArray());
    }
    public MapValidationResult Validation()
    {
        var result = new MapValidationResult();
        result.Diagnostics.AddRange(Diagnostics); result.Budgets.AddRange(Budgets); return result;
    }
    public MapNodePacker.NavigationGraph? CreateNavigation() => _navigation is not { } graph ? null : new(
        (byte[])graph.Bytes.Clone(), (Vector3[])graph.Positions.Clone(),
        graph.Neighbours.Select(n => (int[])n.Clone()).ToArray(), (int[])graph.Components.Clone(), graph.Edges);
}
