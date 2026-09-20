using System.Net.Http;
using System.Threading;

namespace MphRead.Mods.Update
{
    /// <summary>
    /// A blocking HTTP request that also works on a phone.
    ///
    /// <c>HttpClient.Send</c> -- the synchronous one -- is not something every
    /// platform has. .NET for Android defaults <c>UseNativeHttpHandler</c> to
    /// true, which puts <c>Xamarin.Android.Net.AndroidMessageHandler</c>
    /// (Java's HttpURLConnection underneath) behind every HttpClient, and that
    /// handler implements <c>SendAsync</c> and nothing else: the synchronous
    /// call falls through to <c>HttpMessageHandler.Send</c>, whose whole body
    /// is a throw.
    ///
    /// So the update check on Android never reached GitHub at all. It caught
    /// the NotSupportedException, wrote "could not reach GitHub" into
    /// <see cref="UpdateCheck.LastReason"/>, and the front screen -- which
    /// only ever shows the corner when a release was found -- said nothing.
    /// A phone stayed on the version it had, with no way to tell that from
    /// being up to date. The desktop was unaffected because SocketsHttpHandler
    /// does implement the synchronous path.
    ///
    /// Both callers are already on a background thread and want the answer
    /// before they continue, so waiting on the async call is the whole fix;
    /// nothing here awaits back onto a captured context, so there is no
    /// deadlock to have.
    /// </summary>
    internal static class SyncHttp
    {
        public static HttpResponseMessage Send(HttpClient client,
            HttpRequestMessage request,
            HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead,
            CancellationToken cancel = default)
        {
            return client.SendAsync(request, completion, cancel).GetAwaiter().GetResult();
        }
    }
}
