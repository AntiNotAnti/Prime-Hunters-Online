using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MphRead.Mods.Replay;

internal static class ReplayEncoderCapabilities
{
    private static readonly string[] Allowed = { "libx264", "libx265", "h264_nvenc", "hevc_nvenc",
        "h264_qsv", "hevc_qsv", "h264_videotoolbox", "hevc_videotoolbox", "h264_amf", "hevc_amf" };
    private static readonly Lazy<Task<IReadOnlyList<string>>> Cached = new(() => Task.Run(Probe));
    internal static Task<IReadOnlyList<string>> Available => Cached.Value;
    private static async Task<IReadOnlyList<string>> Probe()
    {
        try
        {
            using var process = new Process { StartInfo = new("ffmpeg", "-hide_banner -encoders")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
            if (!process.Start()) return new[] { "libx264" };
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { process.Kill(true); await process.WaitForExitAsync().ConfigureAwait(false); }
            string output = await stdout.ConfigureAwait(false); await stderr.ConfigureAwait(false);
            var supported = output.Split('\n').Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Where(parts => parts.Length > 1).Select(parts => parts[1]).ToHashSet(StringComparer.Ordinal);
            return Allowed.Where(name => supported.Contains(name) || name == "libx264").ToArray();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or System.IO.IOException or NotSupportedException)
        { return new[] { "libx264" }; }
    }
    internal static ReplayVideoExportManifest Apply(ReplayVideoExportManifest job, string encoder)
    {
        if (!Allowed.Contains(encoder)) throw new ArgumentException("Unsupported encoder.", nameof(encoder));
        string options = encoder is "libx264" or "libx265" ? $"-c:v {encoder} -preset slow -crf 18"
            : $"-c:v {encoder} -b:v 20M";
        return job with { FfmpegArguments = job.FfmpegArguments.Replace("-c:v libx264 -preset slow -crf 18", options, StringComparison.Ordinal) };
    }
}
