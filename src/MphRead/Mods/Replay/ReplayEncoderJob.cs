using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MphRead.Mods.Replay;

internal sealed record ReplayEncodingResult(bool Cancelled, int? ExitCode, string? Error, string Stderr);

/// <summary>One owned encoder. Completion includes process exit and both redirected pipes.</summary>
internal sealed class ReplayEncoderJob : IDisposable
{
    private readonly CancellationTokenSource _cancel = new();
    private int _frames;
    internal int Frames => Volatile.Read(ref _frames);
    internal Task<ReplayEncodingResult> Completion { get; }
    internal ReplayEncoderJob(string executable, string arguments, string directory)
        => Completion = Task.Run(() => Run(executable, arguments, directory));
    internal void Cancel() => _cancel.Cancel();
    private async Task<ReplayEncodingResult> Run(string executable, string arguments, string directory)
    {
        var tail = new StringBuilder();
        try
        {
            _cancel.Token.ThrowIfCancellationRequested();
            await File.WriteAllTextAsync(Path.Combine(directory, "encode.txt"), executable + " " + arguments, _cancel.Token).ConfigureAwait(false);
            using var process = new Process { StartInfo = new ProcessStartInfo(executable, arguments)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
                RedirectStandardOutput = true, WorkingDirectory = directory } };
            if (!process.Start()) return new(false, null, "Encoder did not start.", "");
            // Cancellation only signals from the render thread. Kill/wait occur here.
            Task stdout = ReadProgress(process.StandardOutput);
            Task stderr = ReadError(process.StandardError, tail);
            string? terminationError = null;
            try { await process.WaitForExitAsync(_cancel.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception ex)
                { terminationError = "Could not terminate encoder: " + ex.Message + "\n"; }
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            string log = terminationError + tail.ToString();
            try { await File.WriteAllTextAsync(Path.Combine(directory, "encode.log"), log).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log += "\nLog: " + ex.Message; }
            return new(_cancel.IsCancellationRequested, process.ExitCode,
                process.ExitCode == 0 ? null : $"FFmpeg exited with code {process.ExitCode}. {log}", log);
        }
        catch (OperationCanceledException) { return new(true, null, null, tail.ToString()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        { return new(_cancel.IsCancellationRequested, null, ex.Message, tail.ToString()); }
    }
    private async Task ReadProgress(StreamReader reader)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            if (line.StartsWith("frame=", StringComparison.Ordinal)
                && int.TryParse(line.AsSpan(6), NumberStyles.Integer, CultureInfo.InvariantCulture, out int frame))
                Volatile.Write(ref _frames, frame);
    }
    private static async Task ReadError(StreamReader reader, StringBuilder tail)
    {
        char[] buffer = new char[2048];
        int count;
        while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            tail.Append(buffer, 0, count);
            if (tail.Length > 16384) tail.Remove(0, tail.Length - 16384);
        }
    }
    public void Dispose()
    {
        Cancel();
        _ = Completion.ContinueWith(_ => _cancel.Dispose(), TaskScheduler.Default);
    }
}
