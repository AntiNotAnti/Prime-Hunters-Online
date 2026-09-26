using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace MphRead.Mods.MapGen
{
    public sealed record MapCompilation(BuiltMap? Map, MapValidationResult Validation);

    public static class MapCompiler
    {
        internal static object ContentReadLock { get; } = new();
        public static MapCompilation Compile(MapProject project, CancellationToken cancellation = default)
            => Compile(project.ToDefinition(), cancellation);

        public static MapCompilation Compile(MapDefinition definition, CancellationToken cancellation = default)
        {
            cancellation.ThrowIfCancellationRequested();
            var result = MapValidator.Validate(definition);
            if (!result.IsValid) return new(null, result);
            cancellation.ThrowIfCancellationRequested();
            try
            {
                // Import can bake a missing texture pack, so fingerprint only after it completes.
                var snapshot = MapProjectSerializer.Clone(definition);
                BuiltMap map;
                if (snapshot.NativeRoom != null)
                    lock (ContentReadLock) map = NativeRoomImport.Build(snapshot, cancellation);
                else if (snapshot.Import == null) map = MapBuilder.Build(snapshot, cancellation);
                else lock (ContentReadLock) map = Q3Import.Build(snapshot, false, cancellation);
                MapPacker.ApplyCollision(map, snapshot, verbose: false);
                map.SourceDefinition = definition;
                cancellation.ThrowIfCancellationRequested();
                MapBudgetValidator.Analyze(map, result);
                // Keep successfully compiled geometry available to editor
                // analysis even when a runtime budget is exceeded. Build and
                // package paths still reject the validation below; this only
                // lets an author see and repair an oversized map.
                return new(map, result);
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
