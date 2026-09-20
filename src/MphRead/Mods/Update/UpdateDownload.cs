using System;
using System.IO;
using System.Net.Http;
using System.Threading;

namespace MphRead.Mods.Update
{
    /// <summary>
    /// Fetch a release asset to a file, reporting how far along it is.
    ///
    /// Only ever the address GitHub itself answered with -- see
    /// <see cref="UpdateInfo.AssetUrl"/>, which is copied out of the release
    /// rather than built from the tag -- and only ever over https. Both are
    /// checked again here, because this is the one place in the program that
    /// writes a file somebody else chose the contents of.
    /// </summary>
    public static class UpdateDownload
    {
        /// <summary>GitHub's asset downloads redirect to this.</summary>
        private const string _assetHost = "objects.githubusercontent.com";

        private const string _releaseHost = "github.com";

        /// <summary>
        /// Long enough for a 45 MB APK on a bad connection, and bounded so a
        /// stalled socket does not leave a screen saying "downloading" for the
        /// rest of the session.
        /// </summary>
        private static readonly TimeSpan _timeout = TimeSpan.FromMinutes(10);

        /// <summary>Why the last attempt produced nothing.</summary>
        public static string? LastError { get; private set; }

        /// <summary>
        /// Fetch <paramref name="url"/> to <paramref name="path"/>, and return
        /// whether the file is now there and complete.
        ///
        /// Written to a neighbouring ".part" and renamed only once the whole
        /// body has arrived, so a download interrupted half way can never be
        /// mistaken for a package: the installer is handed a path that either
        /// does not exist or is whole.
        /// </summary>
        /// <param name="progress">0 to 1, or -1 while the length is unknown.</param>
        public static bool Fetch(string url, string path, long expectedBytes = 0,
            Action<float>? progress = null, CancellationToken cancel = default)
        {
            LastError = null;
            if (!IsAllowed(url))
            {
                LastError = "that download address is not GitHub's";
                return false;
            }
            string partial = path + ".part";
            try
            {
                string? directory = Path.GetDirectoryName(path);
                if (!String.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                using var client = new HttpClient { Timeout = _timeout };
                client.DefaultRequestHeaders.Add("User-Agent",
                    $"{Mods.Branding.FileName}/{BuildVersion.Display}");
                // SyncHttp rather than client.Send: the synchronous one does
                // not exist on Android's handler, which is the one platform
                // that fetches the package itself. See SyncHttp.
                using HttpResponseMessage response = SyncHttp.Send(client,
                    new HttpRequestMessage(HttpMethod.Get, url),
                    HttpCompletionOption.ResponseHeadersRead, cancel);
                if (!response.IsSuccessStatusCode)
                {
                    LastError = $"GitHub answered {(int)response.StatusCode}";
                    return false;
                }
                long total = response.Content.Headers.ContentLength ?? expectedBytes;
                // ReadAsStreamAsync for the same reason as the send above:
                // the synchronous reads on HttpContent are the ones a handler
                // may not have implemented.
                using (Stream source = response.Content
                    .ReadAsStreamAsync(cancel).GetAwaiter().GetResult())
                using (var target = new FileStream(partial, FileMode.Create,
                    FileAccess.Write, FileShare.None))
                {
                    byte[] buffer = new byte[64 * 1024];
                    long done = 0;
                    int read;
                    while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        cancel.ThrowIfCancellationRequested();
                        target.Write(buffer, 0, read);
                        done += read;
                        progress?.Invoke(total > 0 ? Math.Min(1f, (float)(done / (double)total)) : -1f);
                    }
                    if (total > 0 && done != total)
                    {
                        LastError = "the download ended early";
                        return false;
                    }
                }
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                File.Move(partial, path);
                progress?.Invoke(1f);
                return true;
            }
            catch (OperationCanceledException)
            {
                LastError = "cancelled";
                return false;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return false;
            }
            finally
            {
                try
                {
                    if (File.Exists(partial))
                    {
                        File.Delete(partial);
                    }
                }
                catch (IOException)
                {
                    // A leftover .part is litter, not a failure.
                }
            }
        }

        /// <summary>
        /// https, and one of GitHub's own hosts.
        ///
        /// The address always comes from a release GitHub just answered with,
        /// so this cannot normally fail -- which is exactly why it is worth
        /// having: the one thing that would make it fail is a response that
        /// did not come from where it claimed to.
        /// </summary>
        private static bool IsAllowed(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed)
                || parsed.Scheme != Uri.UriSchemeHttps)
            {
                return false;
            }
            string host = parsed.Host;
            return host.Equals(_releaseHost, StringComparison.OrdinalIgnoreCase)
                || host.Equals(_assetHost, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith("." + _releaseHost, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
        }
    }
}
