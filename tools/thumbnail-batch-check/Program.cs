using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MphRead.Mods;

if (args.Contains("-thumbnail"))
{
    string root = ThumbnailGenerator.Root;
    File.WriteAllText(Path.Combine(root, $"started-{Environment.ProcessId}"), "started");
    string mode = Environment.GetEnvironmentVariable("FRUITY_PREVIEW_CASE")!;
    if (mode == "crash") return 139;
    if (mode is "timeout" or "parent-exit") Thread.Sleep(TimeSpan.FromSeconds(30));
    if (mode == "concurrent") Thread.Sleep(300);
    // Exceed OS pipe capacity on both streams; an undrained worker deadlocks.
    for (int i = 0; i < 10000; i++)
    {
        Console.WriteLine(new string('o', 100));
        Console.Error.WriteLine(new string('e', 100));
    }
    for (int i = 0; i + 1 < args.Length; i++)
        if (args[i] == "-thumbnail") File.WriteAllText(Path.Combine(root, "result-" + args[++i]), "complete");
    return 0;
}

if (args.Length == 2 && args[0] == "--case")
{
    string fixture = Environment.GetEnvironmentVariable("FRUITY_PARENT_EXIT_FIXTURE")
        ?? Directory.CreateTempSubdirectory("fruity-preview-check-").FullName;
    Environment.SetEnvironmentVariable("FRUITY_PREVIEW_FIXTURE", fixture);
    Environment.SetEnvironmentVariable("FRUITY_PREVIEW_CASE", args[1]);
    var clock = Stopwatch.StartNew();
    try
    {
        string[] rooms = { "one", "two", "three" };
        int parallelism = args[1] == "success" ? 16 : 1;
        TimeSpan timeout = TimeSpan.FromSeconds(args[1] == "timeout" ? 2 : 10);
        int Run() => ThumbnailBatch.Run(rooms, parallelism, 64, 64, workerTimeout: timeout);
        if (args[1] == "parent-exit")
        {
            _ = Task.Run(Run);
            while (Directory.GetFiles(fixture, "started-*").Length == 0)
            {
                if (clock.Elapsed > TimeSpan.FromSeconds(10)) throw new Exception("Child did not start.");
                Thread.Sleep(20);
            }
            Environment.Exit(0); // ProcessExit must stop the live child.
        }
        int count;
        if (args[1] == "concurrent")
        {
            var first = Task.Run(Run);
            var second = Task.Run(Run);
            Task.WaitAll(first, second);
            count = first.Result + second.Result;
        }
        else count = Run();
        bool failure = args[1] is "crash" or "timeout";
        if (count != (failure ? 0 : 3)) throw new Exception($"Unexpected capture count {count}.");
        if (failure && Run() != 0) throw new Exception("A failed worker restarted in the same session.");
        string[] starts = Directory.GetFiles(fixture, "started-*");
        int expected = args[1] == "success" && !OperatingSystem.IsMacOS() ? 3 : 1;
        if (starts.Length != expected) throw new Exception($"Expected {expected} worker(s), got {starts.Length}.");
        foreach (string file in starts)
        {
            int pid = int.Parse(Path.GetFileName(file)["started-".Length..]);
            try
            {
                using var child = Process.GetProcessById(pid);
                if (!child.HasExited) throw new Exception($"Worker {pid} was left running.");
            }
            catch (ArgumentException) { }
        }
        if (clock.Elapsed > TimeSpan.FromSeconds(12)) throw new Exception("Worker lifetime was not bounded.");
        if (ThumbnailLog.Lines.Count > 200) throw new Exception("Worker output logging was not capped.");
        Console.WriteLine($"PASS {args[1]}: {starts.Length} worker(s), {count} captures, no orphan processes");
        return 0;
    }
    finally { Directory.Delete(fixture, recursive: true); }
}

foreach (string mode in new[] { "success", "crash", "timeout", "concurrent", "parent-exit" })
{
    var start = new ProcessStartInfo(Environment.ProcessPath!)
    { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    start.ArgumentList.Add("--case"); start.ArgumentList.Add(mode);
    string? parentFixture = mode == "parent-exit"
        ? Directory.CreateTempSubdirectory("fruity-parent-exit-").FullName : null;
    if (parentFixture != null) start.Environment["FRUITY_PARENT_EXIT_FIXTURE"] = parentFixture;
    using var process = Process.Start(start)!;
    var output = process.StandardOutput.ReadToEndAsync();
    var errors = process.StandardError.ReadToEndAsync();
    if (!process.WaitForExit(20000))
    {
        process.Kill(entireProcessTree: true);
        throw new Exception($"Case {mode} timed out.");
    }
    Console.Write(output.GetAwaiter().GetResult());
    Console.Error.Write(errors.GetAwaiter().GetResult());
    if (process.ExitCode != 0) return 1;
    if (parentFixture != null)
    {
        try
        {
            string marker = Directory.GetFiles(parentFixture, "started-*").Single();
            int pid = int.Parse(Path.GetFileName(marker)["started-".Length..]);
            try
            {
                using var child = Process.GetProcessById(pid);
                if (!child.HasExited) throw new Exception("Parent exit orphaned the worker.");
            }
            catch (ArgumentException) { }
            Console.WriteLine("PASS parent-exit: live worker stopped with its parent");
        }
        finally { Directory.Delete(parentFixture, recursive: true); }
    }
}
return 0;
