using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace MphRead.Mods.MapGen
{
    public sealed record MapCompilation(BuiltMap? Map, MapValidationResult Validation);

    public static class MapCompiler
    {
        public static MapCompilation Compile(MapProject project, CancellationToken cancellation = default)
            => Compile(project.ToDefinition(), cancellation);

        public static MapCompilation Compile(MapDefinition definition, CancellationToken cancellation = default)
        {
            var result = MapValidator.Validate(definition);
            if (!result.IsValid) return new(null, result);
            cancellation.ThrowIfCancellationRequested();
            try
            {
                // Import can bake a missing texture pack, so fingerprint only after it completes.
                var snapshot = MapProjectSerializer.Clone(definition);
                BuiltMap map = snapshot.Import == null ? MapBuilder.Build(snapshot) : Q3Import.Build(snapshot, false);
                MapPacker.ApplyCollision(map, snapshot, verbose: false);
                map.SourceDefinition = definition;
                cancellation.ThrowIfCancellationRequested();
                MapBudgetValidator.Analyze(map, result);
                return new(result.IsValid ? map : null, result);
            }
            catch (MapAuthoringException ex) { result.Error(ex.Code, ex.Message); }
            catch (ProgramException ex) { result.Error("FP-MAP-019", ex.Message); }
            catch (IOException ex) { result.Error("FP-MAP-020", ex.Message); }
            catch (InvalidDataException ex) { result.Error("FP-MAP-020", ex.Message); }
            return new(null, result);
        }

        public static void ThrowIfInvalid(MapValidationResult result)
        {
            if (!result.IsValid) throw new MapAuthoringException("FP-MAP-019",
                string.Join(Environment.NewLine, result.Diagnostics.Where(d => d.Severity == MapDiagnosticSeverity.Error)
                    .Select(d => $"{d.Code}: {d.Message}")));
        }
    }
}
