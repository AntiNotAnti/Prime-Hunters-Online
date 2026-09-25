using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace MphRead.Mods.Network.Telemetry;

/// <summary>Only aggregate files are uploaded. Worker-thread, bounded work per match,
/// persistent exponential retry schedule, no player credential or raw-event upload.</summary>
public static class NetTelemetryUpload
{
    private sealed record Retry(int Attempts, DateTime Next);
    public static void Run(NetTelemetryConfig config, string summary, Action failure)
    {
        if (!Uri.TryCreate(config.Endpoint, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("https" or "http")) { failure(); return; }
        string directory = Path.Combine(config.Directory, "pending");
        Directory.CreateDirectory(directory);
        File.Copy(summary, Path.Combine(directory, Path.GetFileName(summary)), true);
        var files = new DirectoryInfo(directory).GetFiles("match-*.summary.json");
        Array.Sort(files, (a, b) => a.CreationTimeUtc.CompareTo(b.CreationTimeUtc));
        long bytes = 0; foreach (var file in files) bytes += file.Length;
        int remaining = files.Length;
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Clamp(config.UploadTimeoutSeconds, 1, 10)) };
        string? token = Environment.GetEnvironmentVariable(config.TokenEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(token)) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        int attempts = 0;
        foreach (var file in files)
        {
            string retryPath = file.FullName + ".retry";
            if (remaining > Math.Clamp(config.MaximumPendingUploads, 1, 128) || bytes > Math.Clamp(config.MaximumUploadBytes, 1024, 64 * 1024 * 1024))
            { bytes -= file.Length; remaining--; file.Delete(); File.Delete(retryPath); failure(); continue; }
            int prior = 0;
            if (File.Exists(retryPath))
            {
                string[] fields = File.ReadAllText(retryPath).Split('|');
                if (fields.Length == 2 && int.TryParse(fields[0], out prior) && long.TryParse(fields[1], out long next)
                    && DateTime.UtcNow.Ticks < next) continue;
            }
            if (attempts++ >= 4) break;
            try
            {
                using var body = new StringContent(File.ReadAllText(file.FullName), Encoding.UTF8, "application/json");
                using var response = client.PostAsync(endpoint, body).GetAwaiter().GetResult();
                response.EnsureSuccessStatusCode(); file.Delete(); File.Delete(retryPath);
            }
            catch (Exception)
            {
                failure(); prior = Math.Clamp(prior + 1, 1, 12);
                File.WriteAllText(retryPath, prior + "|" + DateTime.UtcNow.AddSeconds(Math.Min(3600, 5 * Math.Pow(2, prior))).Ticks);
            }
        }
    }
}
