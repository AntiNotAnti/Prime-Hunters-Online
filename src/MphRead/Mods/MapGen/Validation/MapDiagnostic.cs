using System;
using System.Collections.Generic;
using System.Linq;

namespace MphRead.Mods.MapGen
{
    public enum MapDiagnosticSeverity { Info, Warning, Error }

    public sealed record MapDiagnostic(string Code, MapDiagnosticSeverity Severity, string Message, Guid? ObjectId = null);

    public sealed record MapBudget(string Name, long Used, long? Limit = null)
    {
        public double? Percent => Limit > 0 ? 100.0 * Used / Limit : null;
    }

    public sealed class MapValidationResult
    {
        public List<MapDiagnostic> Diagnostics { get; } = new();
        public List<MapBudget> Budgets { get; } = new();
        public bool IsValid => Diagnostics.All(d => d.Severity != MapDiagnosticSeverity.Error);
        public void Error(string code, string message, Guid? id = null)
            => Diagnostics.Add(new(code, MapDiagnosticSeverity.Error, message, id));
        public void Warning(string code, string message, Guid? id = null)
            => Diagnostics.Add(new(code, MapDiagnosticSeverity.Warning, message, id));
    }

    public sealed class MapAuthoringException : ProgramException
    {
        public string Code { get; }
        public MapAuthoringException(string code, string message) : base(message) { Code = code; }
    }
}
