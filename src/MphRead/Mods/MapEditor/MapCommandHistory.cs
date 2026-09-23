using System;
using System.Collections.Generic;

namespace MphRead.Mods.MapEditor;

public readonly record struct DocumentStateId(ulong Value);
[Flags]
public enum MapChangeDomain
{
    None = 0, Geometry = 1, Transform = 2, Material = 4, Entity = 8,
    Selection = 16, Overlay = 32, Environment = 64, Navigation = 128,
    Metadata = 256, Import = 512, All = 1023
}
public sealed record MapDocumentChange(MapChangeDomain Domains, IReadOnlyList<Guid>? ObjectIds = null);
public interface IMapEditCommand
{
    string Label { get; }
    long ApproximateBytes { get; }
    MapDocumentChange Change { get; }
    void Execute();
    void Undo();
    bool TryMerge(IMapEditCommand next) => false;
}

/// <summary>State identity is independent of stack depth, pruning and branch history.</summary>
public sealed class MapCommandHistory
{
    private sealed record Entry(IMapEditCommand Command, DocumentStateId Before, DocumentStateId After, object? Transaction);
    private readonly List<Entry> _undo = new(), _redo = new();
    private readonly int _maximumCommands;
    private readonly long _maximumBytes;
    private ulong _nextState;
    private DocumentStateId? _savedState;
    public DocumentStateId CurrentStateId { get; private set; }
    public bool CanUndo => _undo.Count != 0;
    public bool CanRedo => _redo.Count != 0;
    public int CommandCount => _undo.Count + _redo.Count;
    public long ApproximateBytes { get; private set; }
    public event Action<MapDocumentChange>? Changed;
    public MapCommandHistory(int maximumCommands = 500, long maximumBytes = 256L * 1024 * 1024)
    {
        if (maximumCommands < 1 || maximumBytes < 1) throw new ArgumentOutOfRangeException();
        _maximumCommands = maximumCommands; _maximumBytes = maximumBytes;
    }
    public void MarkSaved() => _savedState = CurrentStateId;
    public void Execute(IMapEditCommand command, object? transaction = null)
    {
        if (command.ApproximateBytes < 0) throw new ArgumentOutOfRangeException(nameof(command));
        command.Execute();
        foreach (var entry in _redo) ApproximateBytes -= entry.Command.ApproximateBytes;
        _redo.Clear();
        var before = CurrentStateId;
        CurrentStateId = new(++_nextState);
        bool merged = false;
        if (transaction != null && _undo.Count > 0 && before != _savedState)
        {
            var last = _undo[^1];
            long oldBytes = last.Command.ApproximateBytes;
            if (Equals(last.Transaction, transaction) && last.Command.TryMerge(command))
            {
                _undo[^1] = last with { After = CurrentStateId };
                ApproximateBytes += last.Command.ApproximateBytes - oldBytes;
                merged = true;
            }
        }
        if (!merged)
        {
            _undo.Add(new(command, before, CurrentStateId, transaction));
            ApproximateBytes += command.ApproximateBytes;
        }
        while (_undo.Count > 0 && (CommandCount > _maximumCommands || ApproximateBytes > _maximumBytes))
        { ApproximateBytes -= _undo[0].Command.ApproximateBytes; _undo.RemoveAt(0); }
        Changed?.Invoke(command.Change);
    }
    public void Undo()
    {
        if (!CanUndo) return;
        var entry = _undo[^1]; entry.Command.Undo();
        _undo.RemoveAt(_undo.Count - 1); _redo.Add(entry);
        CurrentStateId = entry.Before; Changed?.Invoke(entry.Command.Change);
    }
    public void Redo()
    {
        if (!CanRedo) return;
        var entry = _redo[^1]; entry.Command.Execute();
        _redo.RemoveAt(_redo.Count - 1); _undo.Add(entry);
        CurrentStateId = entry.After; Changed?.Invoke(entry.Command.Change);
    }
}
