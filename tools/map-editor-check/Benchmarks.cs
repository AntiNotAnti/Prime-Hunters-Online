using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
        var cache = new MapViewportCache(); cache.Invalidate(source, new(MapChangeDomain.All));
        Measure("legacy selection geometry rebuild", 20, () => MapViewportScene.Create(source));
        Measure("selection invalidation", 1000, () => cache.Invalidate(source, new(MapChangeDomain.Selection)));
        Measure("full CPU geometry rebuild", 20, () => cache.Invalidate(source, new(MapChangeDomain.Geometry)));
        Measure("one-object CPU geometry rebuild", 100, () => cache.Invalidate(source, new(MapChangeDomain.Geometry, new[] { id })));
        Measure("delta undo and redo", 1000, () => { current.History.Undo(); current.History.Redo(); });
        string directory = Path.Combine(Path.GetTempPath(), "prime-map-benchmark-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Measure("save 1000 objects", 10, () => current.Save(Path.Combine(directory, "map.json")));
            // Synthetic texture and traversable floor avoid any cartridge inputs.
            using (var texture = new BinaryWriter(File.Create(Path.Combine(directory, "test.tex"))))
            {
                texture.Write(System.Text.Encoding.ASCII.GetBytes("FPTX")); texture.Write((ushort)1); texture.Write((ushort)1);
                texture.Write((ushort)0); texture.Write((ushort)8); texture.Write((ushort)8); texture.Write((ushort)1); texture.Write((ushort)0);
                texture.Write((ushort)32767); texture.Write(new byte[64]);
            }
            var runtime = new MapDefinition { Name = "BENCHMARK_RUNTIME", BaseDirectory = directory };
            runtime.Materials.Add(new() { Texture = "test.tex" }); runtime.Assets.Add(new() { Path = "test.tex" });
            runtime.Geometry.Add(new MapBox { Transform = new() { Position = new[] { 0f, -1, 0 }, Scale = new[] { 80f, 1, 80 } } });
            runtime.Spawns.Add(new() { Position = new[] { 0f, 2, 0 } });
            var snapshot = MapBuildSnapshot.Capture(runtime);
            var scheduler = new MapBuildScheduler(Path.Combine(directory, "cache"));
            var cold = scheduler.BuildAsync(snapshot).GetAwaiter().GetResult();
            if (!cold.Succeeded) throw new InvalidOperationException(string.Join(";", cold.Diagnostics.Select(d => d.Message)));
            var hot = scheduler.BuildAsync(snapshot).GetAwaiter().GetResult();
            Console.WriteLine($"BENCH runtime compile: miss {cold.Milliseconds:0.000} ms; hit {hot.Milliseconds:0.000} ms; reused={hot.CacheHit}");
            var timer = Stopwatch.StartNew(); var navigation = scheduler.AnalyzeAsync(snapshot, navigation: true).GetAwaiter().GetResult(); timer.Stop();
            Console.WriteLine($"BENCH navigation 80x80 floor: {timer.Elapsed.TotalMilliseconds:0.000} ms; {navigation.CreateNavigation()?.Positions.Length} nodes");
        }
        finally { Directory.Delete(directory, recursive: true); }
        GC.KeepAlive(dirty); GC.KeepAlive(history);
    }
}
