#if MPHREAD_AVALONIA
using System;
using System.Net;
using Avalonia;
using Avalonia.Media;

namespace MphRead.Mods.Launcher.Gui
{
    /// <summary>
    /// The little slab at the head of a server row.
    ///
    /// <para>
    /// The country comes from the address, through <see cref="GeoCountry"/>'s
    /// shipped table -- no request, so no third party learns which servers you
    /// browse and no row arrives late. A private address has no country to
    /// find and gets the network badge instead, which is the true answer
    /// rather than a missing one; an address that resolves to a country
    /// without a drawn flag gets its two-letter code.
    /// </para>
    ///
    /// <para>
    /// Drawn as rows of whole pixels on a 16x11 grid, the same way the heart
    /// is: at this size an anti-aliased glyph is a smudge, and the rest of the
    /// screen is aliased.
    /// </para>
    /// </summary>
    internal static class ServerBadge
    {
        public enum Kind
        {
            /// <summary>Out on the internet: the ordinary case.</summary>
            Internet,
            /// <summary>A private address: this network, this house.</summary>
            Lan,
            /// <summary>Loopback: this machine, hosting for itself.</summary>
            Local,
            /// <summary>Asked and never answered.</summary>
            Silent
        }

        public const double Width = Flags.Width;
        public const double Height = Flags.Height;

        /// <summary>
        /// What the endpoint says about where the server is. Anything that is
        /// not parseable is treated as out there, because a name that has to
        /// be resolved is not a name on this network.
        /// </summary>
        /// <summary>
        /// The country code for a row, or an empty string when the address has
        /// none to give: a LAN server, a loopback one, or a name this build's
        /// table does not cover.
        /// </summary>
        public static string CountryOf(string endpoint)
        {
            string host = endpoint;
            int colon = host.LastIndexOf(':');
            if (colon > 0)
            {
                host = host[..colon];
            }
            return IPAddress.TryParse(host, out IPAddress? ip) ? GeoCountry.Of(ip) : "";
        }

        public static Kind Of(string endpoint, bool answered)
        {
            if (!answered)
            {
                return Kind.Silent;
            }
            string host = endpoint;
            int colon = host.LastIndexOf(':');
            if (colon > 0)
            {
                host = host[..colon];
            }
            if (!IPAddress.TryParse(host, out IPAddress? ip))
            {
                return Kind.Internet;
            }
            if (IPAddress.IsLoopback(ip))
            {
                return Kind.Local;
            }
            byte[] b = ip.GetAddressBytes();
            if (b.Length == 4 && (b[0] == 10
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 169 && b[1] == 254)))
            {
                return Kind.Lan;
            }
            return Kind.Internet;
        }

        /// <summary>
        /// The flag if the address has a country, the network badge if it is
        /// this network's, the barred slab if it never answered.
        /// </summary>
        public static void Draw(DrawingContext context, string endpoint, bool answered,
            double x, double y)
        {
            Kind kind = Of(endpoint, answered);
            if (kind == Kind.Internet && CountryOf(endpoint) is { Length: 2 } code)
            {
                Flags.Draw(context, code, x, y);
                return;
            }
            Draw(context, kind, x, y);
        }

        public static void Draw(DrawingContext context, Kind kind, double x, double y)
        {
            var frame = new Rect(Math.Round(x), Math.Round(y), Width, Height);
            Color ground = kind switch
            {
                Kind.Lan => Color.FromRgb(0x23, 0x2a, 0x36),
                Kind.Local => Color.FromRgb(0x1d, 0x2b, 0x27),
                Kind.Silent => Color.FromRgb(0x1a, 0x1f, 0x29),
                _ => Color.FromRgb(0x1b, 0x27, 0x36)
            };
            context.FillRectangle(new SolidColorBrush(ground), frame, 2);

            double cx = frame.X, cy = frame.Y;
            var ink = new SolidColorBrush(kind switch
            {
                Kind.Lan => Color.FromRgb(0x2c, 0x5a, 0x4e),
                Kind.Local => Color.FromRgb(0x5f, 0x9e, 0x72),
                Kind.Silent => Color.FromRgb(0x3a, 0x43, 0x53),
                _ => Color.FromRgb(0x3f, 0x7f, 0xa8)
            });

            if (kind == Kind.Silent)
            {
                // A barred slab: asked, and nothing came back.
                context.FillRectangle(ink, new Rect(cx + 4, cy + 7, 14, 2));
                return;
            }
            if (kind == Kind.Lan || kind == Kind.Local)
            {
                // A screen on a stand: this network.
                context.FillRectangle(ink, new Rect(cx + 4, cy + 3, 14, 7));
                context.FillRectangle(new SolidColorBrush(Color.FromRgb(0x8a, 0x93, 0xa6)),
                    new Rect(cx + 10, cy + 10, 2, 2));
                context.FillRectangle(new SolidColorBrush(Color.FromRgb(0x8a, 0x93, 0xa6)),
                    new Rect(cx + 7, cy + 12, 8, 2));
                return;
            }
            // Three bands round a sphere: out there.
            context.FillRectangle(ink, new Rect(cx + 4, cy + 3, 14, 9));
            var pale = new SolidColorBrush(Color.FromRgb(0x8d, 0xc4, 0xe8));
            context.FillRectangle(pale, new Rect(cx + 4, cy + 6, 14, 1));
            context.FillRectangle(pale, new Rect(cx + 10, cy + 3, 2, 9));
            context.FillRectangle(pale, new Rect(cx + 6, cy + 4, 1, 7));
            context.FillRectangle(pale, new Rect(cx + 15, cy + 4, 1, 7));
        }
    }
}
#endif
