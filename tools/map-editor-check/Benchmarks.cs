using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using MphRead.Mods.MapEditor;
using MphRead.Mods.MapGen;

static class Benchmarks
{
    public static void Run()
    {
        var source = new MapDefinition { Name = "BENCHMARK" };
        source.Materials.Add(new()); source.Spawns.Add(new());
        for (int i = 0; i < 1000; i++) source.Geometry.Add(new MapBox { Label = "Box " + i });
        var current = new MapDocument(new MapProject(source), "benchmark.json");
        var legacy = new MapProject(MapProjectSerializer.Clone(source));
        var history = new Queue<(MapDefinition Before, MapDefinition After)>();
        string saved = legacy.Definition.Serialize();
        Guid id = source.Geometry[0].Id;
        bool dirty = false;
        void Measure(string name, int iterations, Action action)
        {
            action(); // warm up JIT and serializers before measuring
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            var timer = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++) action();
            timer.Stop();
            Console.WriteLine($"BENCH {name}: {timer.Elapsed.TotalMilliseconds / iterations:0.000} ms/op; "
                + $"{(GC.GetAllocatedBytesForCurrentThread() - allocated) / iterations:N0} bytes/op ({iterations} iterations)");
        }
        // Exact old dirty-check expression and editing algorithm from target main.
        Measure("legacy dirty check", 100, () => dirty = legacy.Definition.Serialize() != saved);
        Measure("state-ID dirty check", 100, () => dirty = current.IsDirty);
        Measure("legacy transform", 20, () =>
        {
            var before = legacy.ToDefinition(); var after = legacy.ToDefinition();
            after.Geometry[0].Transform.Position[0]++;
            if (before.Serialize() == after.Serialize()) return;
            history.Enqueue((before, after)); if (history.Count > 50) history.Dequeue();
            legacy = new(MapProjectSerializer.Clone(after));
        });
        Measure("delta transform", 20, () => current.TransformSelection(new[] { id }, "Move", Vector3.UnitX, 0, 1, false));
        GC.KeepAlive(dirty); GC.KeepAlive(history);
    }
}
