using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace MphRead.Mods
{
    /// <summary>
    /// Runs thumbnail captures several at a time.
    ///
    /// Parallel across processes, not threads: GLFW requires its windows to
    /// be created and pumped on the main thread, so several GL windows in
    /// one process is not possible. Each worker is a fresh instance of this
    /// executable invoked with a share of the rooms, which also isolates a
    /// crash on one room from the rest of the batch.
    /// </summary>
    public static class ThumbnailBatch
    {
        /// <summary>
        /// How many workers to run at once.
        ///
        /// The cores, rather than the flat ten this used to be. Ten was the
        /// right shape when a worker was one room and spent a fifth of its
        /// life asleep between 60 Hz frames -- oversubscribing hid the
        /// waiting. A worker is now a share of rooms and never waits, so
        /// anything past the cores only adds context switches, and each of
        /// them is also a GL context and a room's textures: the machines that
        /// refuse those allocations (see Run) are exactly the ones with few
        /// cores. Measured on an 8-core box rendering 28 rooms: 5.2 s at ten,
        /// 4.1 s at eight.
        /// </summary>
        // Mac previews use one background app process for the entire batch.
        public static int DefaultParallelism => OperatingSystem.IsMacOS()
            ? 1 : Math.Clamp(Environment.ProcessorCount, 2, 10);

        private static readonly object _batchLock = new();
        private static bool _workerFailed;
        private static readonly object _processLock = new();
        private static readonly HashSet<Process> _activeWorkers = new();
        private static bool _exiting;

        static ThumbnailBatch()
        {
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                Process[] active;
                lock (_processLock)
                {
                    _exiting = true;
                    active = new Process[_activeWorkers.Count];
                    _activeWorkers.CopyTo(active);
                }
                foreach (Process worker in active) StopWorker(worker);
            };
        }

        /// <summary>
        /// Whether previews can be rendered at all here. Every worker is a
        /// fresh instance of this executable, and Android has no executable to
        /// start -- so a phone shows map names and no pictures rather than
        /// stalling on a batch that can never produce one.
        /// </summary>
        public static bool CanRun => !OperatingSystem.IsAndroid()
            && Environment.ProcessPath != null;

        public static int Run(IReadOnlyList<string> rooms, int parallelism,
                              int width, int height, Action<string>? report = null,
                              TimeSpan? workerTimeout = null)
        {
            // Setup and the front screen can request previews concurrently.
            // Recheck the cache after waiting rather than starting duplicate workers.
            lock (_batchLock)
            {
                if (_workerFailed)
                {
                    report?.Invoke("Preview generation stopped after a worker failure; restart the app to retry.");
                    return 0;
                }
                var missing = new List<string>();
                foreach (string room in rooms)
                    if (!ThumbnailGenerator.Exists(room)) missing.Add(room);
                if (missing.Count == 0) return 0;
                ThumbnailLog.Begin(missing.Count);
                parallelism = OperatingSystem.IsMacOS() ? 1 : Math.Clamp(parallelism, 1, 16);
                TimeSpan timeout = workerTimeout ?? TimeSpan.FromMinutes(5);
                if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(workerTimeout));
                string? exePath = Environment.ProcessPath;
                if (exePath == null)
                    return RunSerial(missing, width, height, report);
                int written = RunWorkers(missing, parallelism, width, height, exePath,
                    timeout, report, out List<string> failed, out bool abnormalExit);
                if (abnormalExit)
                {
                    _workerFailed = true;
                    string note = "[thumbnails] worker failed; stopping previews without automatic retries";
                    report?.Invoke(note);
                    ThumbnailLog.Write(note);
                }
                else if (failed.Count > 0 && parallelism > 1)
                {
                    // Retry graceful capture failures at lower GPU pressure, but
                    // never turn a native crash into another wave of app launches.
                    string note = $"[thumbnails] {failed.Count} preview(s) missing; retrying in one worker";
                    report?.Invoke(note);
                    ThumbnailLog.Write(note);
                    written += RunWorkers(failed, 1, width, height, exePath, timeout,
                        report, out _, out _workerFailed);
                }
                return written;
            }
        }

        private static int RunWorkers(IReadOnlyList<string> rooms, int parallelism,
                                      int width, int height, string exePath, TimeSpan timeout,
                                      Action<string>? report, out List<string> failedRooms,
                                      out bool abnormalExit)
        {
            failedRooms = new List<string>();
            abnormalExit = false;
            var running = new List<Process>();
            var pending = new HashSet<string>(rooms, StringComparer.OrdinalIgnoreCase);
            int written = 0;
            var clock = Stopwatch.StartNew();
            try
            {
                foreach (IReadOnlyList<string> share in Shares(rooms, parallelism))
                {
                    Process? proc = StartWorker(exePath, share, width, height);
                    if (proc == null) { abnormalExit = true; break; }
                    running.Add(proc);
                }
                while (!abnormalExit)
                {
                    bool allExited = true;
                    foreach (Process proc in running)
                    {
                        if (!proc.HasExited) { allExited = false; continue; }
                        if (proc.ExitCode != 0)
                        {
                            ThumbnailLog.Write($"worker {proc.Id} exited with code {proc.ExitCode}");
                            abnormalExit = true;
                        }
                    }
                    if (allExited || abnormalExit) break;
                    if (clock.Elapsed >= timeout)
                    {
                        ThumbnailLog.Write($"worker timeout after {timeout.TotalSeconds:0} seconds");
                        abnormalExit = true;
                        break;
                    }
                    ReportCompleted();
                    Thread.Sleep(50);
                }
            }
            finally
            {
                // Bound every worker lifetime, including monitor/report failures.
                foreach (Process proc in running) StopWorker(proc);
            }
            ReportCompleted();
            foreach (string room in pending)
            {
                failedRooms.Add(room);
                string line = $"[thumbnails] FAILED {room}";
                report?.Invoke(line);
                ThumbnailLog.Write(line);
            }
            return written;

            void ReportCompleted()
            {
                foreach (string room in rooms)
                {
                    if (!pending.Contains(room) || !ThumbnailGenerator.Exists(room)) continue;
                    pending.Remove(room);
                    written++;
                    report?.Invoke($"[thumbnails] {written}/{rooms.Count} ok {room}");
                }
            }
        }

        private static void StopWorker(Process proc)
        {
            try
            {
                if (!proc.HasExited) proc.Kill(entireProcessTree: true);
                // The asynchronous readers drain both pipes while it exits.
                if (proc.WaitForExit(5000)) proc.WaitForExit();
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception ex)
            {
                ThumbnailLog.Write($"could not stop preview worker: {ex.Message}");
            }
            finally
            {
                lock (_processLock) _activeWorkers.Remove(proc);
                proc.Dispose();
            }
        }

        /// <summary>
        /// Deal the rooms out to that many workers, round robin.
        ///
        /// Round robin rather than contiguous blocks: the rooms arrive sorted
        /// by name and their sizes are not, so blocks would hand one worker
        /// every big room. This is the split Android already uses.
        ///
        /// A room photographed after another in the same worker is caught with
        /// its pickups at a different point in their spin -- they are animated,
        /// and the phase is one of the few things a fresh Scene does not put
        /// back (ThumbnailCapture.CaptureRoom resets the rest). Nothing appears
        /// or disappears; the geometry, the camera and the lighting are
        /// identical to the pixel. The cost of avoiding it is a process per
        /// room, which is a third of the batch.
        /// </summary>
        private static List<List<string>> Shares(IReadOnlyList<string> rooms, int parallelism)
        {
            int workers = Math.Min(parallelism, rooms.Count);
            var shares = new List<List<string>>(workers);
            for (int i = 0; i < workers; i++)
            {
                shares.Add(new List<string>());
            }
            for (int i = 0; i < rooms.Count; i++)
            {
                shares[i % workers].Add(rooms[i]);
            }
            return shares;
        }

        private static Process? StartWorker(string exePath, IReadOnlyList<string> share,
                                            int width, int height)
        {
            var info = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                // Workers find paths.txt through the working directory, the
                // same way a normal launch does.
                WorkingDirectory = Directory.GetCurrentDirectory(),
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            if (Path.GetFileNameWithoutExtension(exePath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
#pragma warning disable IL3000 // Only a framework-dependent dotnet launch reaches this branch.
                string? assembly = System.Reflection.Assembly.GetEntryAssembly()?.Location;
#pragma warning restore IL3000
                if (String.IsNullOrEmpty(assembly)) return null;
                info.ArgumentList.Add(assembly);
            }
            for (int i = 0; i < share.Count; i++)
            {
                info.ArgumentList.Add("-thumbnail");
                info.ArgumentList.Add(share[i]);
            }
            info.ArgumentList.Add("-size");
            info.ArgumentList.Add($"{width}x{height}");
            Process? proc = null;
            try
            {
                lock (_processLock)
                {
                    if (_exiting) return null;
                    proc = Process.Start(info);
                    if (proc != null) _activeWorkers.Add(proc);
                }
                if (proc == null)
                {
                    Console.WriteLine($"[thumbnails] could not start a worker for "
                        + $"{share.Count} room(s)");
                }
                if (proc != null)
                {
                    int lines = 0;
                    void Drain(object sender, DataReceivedEventArgs e)
                    {
                        // Keep draining after the log cap: otherwise a verbose
                        // renderer fills its redirected pipe and never exits.
                        if (e.Data == null || Interlocked.Increment(ref lines) > 64) return;
                        string line = e.Data.Length > 2048 ? e.Data[..2048] : e.Data;
                        ThumbnailLog.Write(line);
                    }
                    proc.OutputDataReceived += Drain;
                    proc.ErrorDataReceived += Drain;
                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();
                }
                return proc;
            }
            catch (Exception ex)
            {
                if (proc != null) StopWorker(proc);
                ThumbnailLog.Write($"worker failed to start: {ex.Message}");
                return null;
            }
        }

        private static int RunSerial(IReadOnlyList<string> rooms, int width, int height,
                                      Action<string>? report)
        {
            int written = 0;
            for (int i = 0; i < rooms.Count; i++)
            {
                if (ThumbnailGenerator.Exists(rooms[i]))
                {
                    continue;
                }
                if (ThumbnailCapture.CaptureRoom(rooms[i], width, height))
                {
                    written++;
                }
                string line = $"[thumbnails] {i + 1}/{rooms.Count}  {rooms[i]}";
                Console.WriteLine(line);
                report?.Invoke(line);
            }
            return written;
        }
    }
}
