#if MPHREAD_AVALONIA
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MphRead.Mods.Update;

namespace MphRead.Mods.Launcher
{
    /// <summary>
    /// Supabase identity + read model for the Hunter License surface.
    ///
    /// Every install begins with an anonymous Supabase identity so a license
    /// exists without an account-creation wall. Players can later attach an
    /// email/password or an OAuth identity to that same user. The UUID does not
    /// change when the identity is linked, so the existing Prime career rows
    /// remain the same license.
    ///
    /// Career totals are never accepted from this client. They still come from
    /// the server-accepted match pipeline in the <c>prime</c> schema.
    /// </summary>
    internal static class HunterLicenseClient
    {
        private const string DefaultUrl = "https://hwcjaygoistufktorbmf.supabase.co";
        private const string DefaultKey = "sb_publishable_EVT45OPl638kA_j8vZ0ebg_sw3aWaVz";

        private static readonly HttpClient Http = new()
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        private static readonly SemaphoreSlim Gate = new(1, 1);
        private static readonly JsonSerializerOptions Json = new()
        {
            PropertyNameCaseInsensitive = true
        };
        private static readonly HashSet<string> OAuthProviders =
            new(StringComparer.OrdinalIgnoreCase) { "google", "github", "discord" };

        private static string Url =>
            (Environment.GetEnvironmentVariable("PROJECT_PRIME_SUPABASE_URL") ?? DefaultUrl).TrimEnd('/');
        private static string Key =>
            Environment.GetEnvironmentVariable("PROJECT_PRIME_SUPABASE_KEY") ?? DefaultKey;
        private static string SessionPath =>
            Path.Combine(LauncherPrefs.Directory, "hunter-license.session.json");

        private static readonly object CareerTicketLock = new();
        private static string _careerTicket = "";
        private static uint _careerTicketClientId;
        private static DateTimeOffset _careerTicketExpiresAt;
        private static DateTimeOffset _careerTicketRetryAt;
        private static Task? _careerTicketTask;

        /// <summary>
        /// Non-blocking network identity lookup. Join/respawn paths call this
        /// once a second through SendIdentify: the first call starts HTTPS
        /// acquisition and a later call returns the cached two-hour ticket.
        /// No Supabase access token ever travels over the game's UDP socket.
        /// </summary>
        public static bool TryGetCareerTicket(uint clientId, out string ticket)
        {
            lock (CareerTicketLock)
            {
                if (_careerTicketClientId == clientId
                    && _careerTicket.Length > 0
                    && _careerTicketExpiresAt > DateTimeOffset.UtcNow.AddMinutes(5))
                {
                    ticket = _careerTicket;
                    return true;
                }

                ticket = "";
                if (DateTimeOffset.UtcNow < _careerTicketRetryAt)
                {
                    return false;
                }
                if (_careerTicketTask == null || _careerTicketTask.IsCompleted)
                {
                    _careerTicketTask = Task.Run(() => RefreshCareerTicketAsync(clientId));
                }
                return false;
            }
        }

        private static async Task RefreshCareerTicketAsync(uint clientId)
        {
            await Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                AuthSession session = await AuthenticateAsync(CancellationToken.None)
                    .ConfigureAwait(false);

                // A player does not have to open Hunter License before playing
                // online. Ensure the same prime profile/license rows the page
                // would have created, then ask for the narrow attribution token.
                string name = LauncherPrefs.PlayerName.Trim();
                if (name.Length == 0) name = "Player";
                Hunter preferred = Hunters.Resolve(LauncherPrefs.LastHunter);
                int hunter = Math.Clamp((int)preferred, 0, 6);
                _ = await RpcAsync<JsonElement>(
                    session.AccessToken,
                    "project_prime_hunter_license",
                    new Dictionary<string, object?>
                    {
                        ["p_display_name"] = name,
                        ["p_favorite_hunter"] = hunter
                    },
                    CancellationToken.None).ConfigureAwait(false);

                using var request = Request(HttpMethod.Post,
                    "/functions/v1/career-ticket", session.AccessToken,
                    new Dictionary<string, object?> { ["client_id"] = clientId });
                using HttpResponseMessage response = await Http.SendAsync(request)
                    .ConfigureAwait(false);
                string text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                EnsureSuccess(response, text, "Career identity service");
                CareerTicketResponse? result =
                    JsonSerializer.Deserialize<CareerTicketResponse>(text, Json);
                if (result == null || result.Ticket.Length == 0
                    || result.ExpiresAt <= DateTimeOffset.UtcNow)
                {
                    throw new InvalidOperationException(
                        "Career identity service returned no usable ticket.");
                }

                lock (CareerTicketLock)
                {
                    _careerTicket = result.Ticket;
                    _careerTicketClientId = clientId;
                    _careerTicketExpiresAt = result.ExpiresAt;
                    _careerTicketRetryAt = default;
                }
            }
            catch (Exception ex)
            {
                lock (CareerTicketLock)
                {
                    _careerTicketRetryAt = DateTimeOffset.UtcNow.AddSeconds(30);
                }
                Console.WriteLine($"[career] identity ticket unavailable: {FriendlyAction(ex)}");
            }
            finally
            {
                Gate.Release();
            }
        }

        private static void InvalidateCareerTicket()
        {
            lock (CareerTicketLock)
            {
                _careerTicket = "";
                _careerTicketClientId = 0;
                _careerTicketExpiresAt = default;
                _careerTicketRetryAt = default;
            }
        }

        public static HunterLicenseSnapshot LocalSnapshot()
        {
            string name = LauncherPrefs.PlayerName.Trim();
            if (name.Length == 0) name = "Player";
            Hunter preferred = Hunters.Resolve(LauncherPrefs.LastHunter);
            int hunter = Math.Clamp((int)preferred, 0, 6);
            return new HunterLicenseSnapshot
            {
                Connected = false,
                Status = "OFFLINE PROFILE",
                Account = new HunterLicenseAccount { IsAnonymous = true },
                Profile = new HunterLicenseProfile
                {
                    DisplayName = name,
                    FavoriteHunter = hunter
                }
            };
        }

        public static async Task<HunterLicenseSnapshot> LoadAsync(
            CancellationToken cancellationToken = default)
        {
            await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                AuthSession session = await AuthenticateAsync(cancellationToken).ConfigureAwait(false);
                AuthUser user = await GetUserAsync(session.AccessToken, cancellationToken)
                    .ConfigureAwait(false);

                string name = LauncherPrefs.PlayerName.Trim();
                if (name.Length == 0) name = "Player";
                Hunter preferred = Hunters.Resolve(LauncherPrefs.LastHunter);
                int hunter = Math.Clamp((int)preferred, 0, 6);

                HunterLicenseSnapshot snapshot = await RpcAsync<HunterLicenseSnapshot>(
                    session.AccessToken,
                    "project_prime_hunter_license",
                    new Dictionary<string, object?>
                    {
                        ["p_display_name"] = name,
                        ["p_favorite_hunter"] = hunter
                    },
                    cancellationToken).ConfigureAwait(false);

                snapshot.Connected = true;
                snapshot.Account = AccountOf(user);
                snapshot.Status = snapshot.Account.IsSecure
                    ? "SECURED LICENSE // SUPABASE CONNECTED"
                    : "GUEST LICENSE // SUPABASE CONNECTED";
                return snapshot;
            }
            catch (Exception ex)
            {
                HunterLicenseSnapshot fallback = LocalSnapshot();
                fallback.Status = FriendlyStatus(ex);
                return fallback;
            }
            finally
            {
                Gate.Release();
            }
        }

        /// <summary>
        /// Attach an email to the current identity. Supabase sends its configured
        /// email-change verification message; the password is deliberately not
        /// sent until the address has been verified.
        /// </summary>
        public static async Task<HunterLicenseActionResult> BeginEmailLinkAsync(
            string email, CancellationToken cancellationToken = default)
        {
            email = email.Trim();
            if (!LooksLikeEmail(email))
                return HunterLicenseActionResult.Fail("Enter a valid email address.");

            await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                AuthSession session = await AuthenticateAsync(cancellationToken).ConfigureAwait(false);
                AuthUser user = await GetUserAsync(session.AccessToken, cancellationToken)
                    .ConfigureAwait(false);
                if (email.Equals(user.Email ?? "", StringComparison.OrdinalIgnoreCase)
                    && !user.IsAnonymous)
                {
                    return HunterLicenseActionResult.Ok("That email is already linked to this license.");
                }

                await UpdateUserAsync(session.AccessToken,
                    new Dictionary<string, object?> { ["email"] = email },
                    cancellationToken).ConfigureAwait(false);
                return HunterLicenseActionResult.Ok(
                    $"Verification sent to {email}. Open the message, confirm the address, then return here.");
            }
            catch (Exception ex)
            {
                return HunterLicenseActionResult.Fail(FriendlyAction(ex));
            }
            finally
            {
                Gate.Release();
            }
        }

        /// <summary>
        /// Set or replace the password only after the user is no longer
        /// anonymous. This is the portable recovery path on a second device.
        /// </summary>
        public static async Task<HunterLicenseActionResult> SetPasswordAsync(
            string password, CancellationToken cancellationToken = default)
        {
            if (password.Length < 8)
                return HunterLicenseActionResult.Fail("Use at least 8 characters for the password.");

            await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                AuthSession session = await AuthenticateAsync(cancellationToken).ConfigureAwait(false);
                AuthUser user = await GetUserAsync(session.AccessToken, cancellationToken)
                    .ConfigureAwait(false);
                if (user.IsAnonymous)
                {
                    return HunterLicenseActionResult.Fail(
                        "The email is not verified yet. Confirm it from the message Supabase sent, then try again.");
                }
                if (String.IsNullOrWhiteSpace(user.Email))
                {
                    return HunterLicenseActionResult.Fail(
                        "Link an email before setting a recovery password.");
                }

                await UpdateUserAsync(session.AccessToken,
                    new Dictionary<string, object?> { ["password"] = password },
                    cancellationToken).ConfigureAwait(false);
                return HunterLicenseActionResult.Ok(
                    $"License secured. You can recover it on another device with {user.Email}.");
            }
            catch (Exception ex)
            {
                return HunterLicenseActionResult.Fail(FriendlyAction(ex));
            }
            finally
            {
                Gate.Release();
            }
        }

        /// <summary>
        /// Replace the local anonymous session only after a password sign-in
        /// succeeds. A failed recovery therefore cannot orphan the guest license.
        /// </summary>
        public static async Task<HunterLicenseActionResult> RecoverWithPasswordAsync(
            string email, string password, CancellationToken cancellationToken = default)
        {
            email = email.Trim();
            if (!LooksLikeEmail(email))
                return HunterLicenseActionResult.Fail("Enter the email attached to the license.");
            if (password.Length == 0)
                return HunterLicenseActionResult.Fail("Enter the license password.");

            await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                AuthSession session = await AuthRequestAsync(
                    "/auth/v1/token?grant_type=password",
                    new Dictionary<string, object?>
                    {
                        ["email"] = email,
                        ["password"] = password
                    },
                    cancellationToken).ConfigureAwait(false);

                AuthUser user = await GetUserAsync(session.AccessToken, cancellationToken)
                    .ConfigureAwait(false);
                if (user.IsAnonymous)
                    return HunterLicenseActionResult.Fail("That sign-in did not resolve to a secured license.");

                SaveStoredSession(session.RefreshToken);
                // Same process, different Supabase UUID. A ticket issued for
                // the empty guest must never follow the recovered license.
                InvalidateCareerTicket();
                return HunterLicenseActionResult.Ok(
                    $"Recovered {user.Email ?? email}. Loading its Hunter License now.");
            }
            catch (Exception ex)
            {
                return HunterLicenseActionResult.Fail(FriendlyAction(ex));
            }
            finally
            {
                Gate.Release();
            }
        }

        /// <summary>
        /// Start Supabase manual OAuth identity linking. The browser completes
        /// the provider flow; this process keeps its existing session and can
        /// simply refresh the user afterwards.
        /// </summary>
        public static async Task<HunterLicenseActionResult> StartOAuthLinkAsync(
            string provider, CancellationToken cancellationToken = default)
        {
            provider = provider.Trim().ToLowerInvariant();
            if (!OAuthProviders.Contains(provider))
                return HunterLicenseActionResult.Fail("That identity provider is not supported.");

            await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                AuthSession session = await AuthenticateAsync(cancellationToken).ConfigureAwait(false);
                AuthUser user = await GetUserAsync(session.AccessToken, cancellationToken)
                    .ConfigureAwait(false);
                if (user.Identities.Any(i =>
                    provider.Equals(i.Provider, StringComparison.OrdinalIgnoreCase)))
                {
                    return HunterLicenseActionResult.Ok(
                        $"{ProviderName(provider)} is already linked to this license.");
                }

                string path = "/auth/v1/user/identities/authorize?provider="
                    + Uri.EscapeDataString(provider) + "&skip_http_redirect=true";
                OAuthAuthorizeResponse link = await GetAsync<OAuthAuthorizeResponse>(
                    path, session.AccessToken, cancellationToken).ConfigureAwait(false);
                if (String.IsNullOrWhiteSpace(link.Url))
                    return HunterLicenseActionResult.Fail("Supabase did not return an authorization link.");

                if (!Updater.OpenLink(link.Url))
                    return HunterLicenseActionResult.Fail(
                        "Could not open the browser for identity linking.");

                return HunterLicenseActionResult.Ok(
                    $"{ProviderName(provider)} opened in your browser. Finish linking there, then choose REFRESH LINK STATUS.");
            }
            catch (Exception ex)
            {
                return HunterLicenseActionResult.Fail(FriendlyAction(ex));
            }
            finally
            {
                Gate.Release();
            }
        }

        private static async Task<AuthSession> AuthenticateAsync(CancellationToken cancellationToken)
        {
            StoredSession? stored = ReadStoredSession();
            if (stored?.RefreshToken is { Length: > 0 })
            {
                try
                {
                    AuthSession refreshed = await AuthRequestAsync(
                        "/auth/v1/token?grant_type=refresh_token",
                        new Dictionary<string, object?> { ["refresh_token"] = stored.RefreshToken },
                        cancellationToken).ConfigureAwait(false);
                    SaveStoredSession(refreshed.RefreshToken);
                    return refreshed;
                }
                catch (SupabaseHttpException ex) when (ex.StatusCode is 400 or 401 or 403)
                {
                    // An actually rejected refresh token can no longer identify
                    // this install. Network failures and rate limits never come
                    // through here: keeping the token is more important than
                    // manufacturing a new guest because the Wi-Fi blinked.
                    TryDeleteSession();
                }
            }

            AuthSession created = await AuthRequestAsync(
                "/auth/v1/signup",
                new Dictionary<string, object?> { ["data"] = new Dictionary<string, object?>() },
                cancellationToken).ConfigureAwait(false);
            SaveStoredSession(created.RefreshToken);
            return created;
        }

        private static async Task<AuthSession> AuthRequestAsync(
            string path, object body, CancellationToken cancellationToken)
        {
            using var request = Request(HttpMethod.Post, path, accessToken: null, body: body);
            using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken)
                .ConfigureAwait(false);
            string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            EnsureSuccess(response, text, "Supabase Auth");
            AuthSession? session = JsonSerializer.Deserialize<AuthSession>(text, Json);
            if (session == null || session.AccessToken.Length == 0 || session.RefreshToken.Length == 0)
                throw new InvalidOperationException("Supabase Auth returned no session.");
            return session;
        }

        private static async Task<AuthUser> GetUserAsync(
            string accessToken, CancellationToken cancellationToken)
            => await GetAsync<AuthUser>("/auth/v1/user", accessToken, cancellationToken)
                .ConfigureAwait(false);

        private static async Task<T> GetAsync<T>(
            string path, string accessToken, CancellationToken cancellationToken)
        {
            using var request = Request(HttpMethod.Get, path, accessToken, body: null);
            using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken)
                .ConfigureAwait(false);
            string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            EnsureSuccess(response, text, "Supabase Auth");
            return JsonSerializer.Deserialize<T>(text, Json)
                ?? throw new InvalidOperationException("Supabase Auth returned no data.");
        }

        private static async Task<AuthUser> UpdateUserAsync(
            string accessToken, object body, CancellationToken cancellationToken)
        {
            using var request = Request(HttpMethod.Put, "/auth/v1/user", accessToken, body);
            using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken)
                .ConfigureAwait(false);
            string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            EnsureSuccess(response, text, "Supabase Auth");
            return JsonSerializer.Deserialize<AuthUser>(text, Json)
                ?? throw new InvalidOperationException("Supabase Auth returned no user.");
        }

        private static async Task<T> RpcAsync<T>(
            string accessToken, string function, object body, CancellationToken cancellationToken)
        {
            using var request = Request(HttpMethod.Post,
                $"/rest/v1/rpc/{function}", accessToken, body);
            using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken)
                .ConfigureAwait(false);
            string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            EnsureSuccess(response, text, "Hunter License service");
            return JsonSerializer.Deserialize<T>(text, Json)
                ?? throw new InvalidOperationException("Hunter License service returned no data.");
        }

        private static HttpRequestMessage Request(
            HttpMethod method, string path, string? accessToken, object? body)
        {
            var request = new HttpRequestMessage(method, Url + path);
            request.Headers.TryAddWithoutValidation("apikey", Key);
            if (!String.IsNullOrWhiteSpace(accessToken))
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);
            if (body != null) request.Content = JsonBody(body);
            return request;
        }

        private static StringContent JsonBody(object value) => new(
            JsonSerializer.Serialize(value, Json), Encoding.UTF8, "application/json");

        private static void EnsureSuccess(
            HttpResponseMessage response, string body, string service)
        {
            if (response.IsSuccessStatusCode) return;
            int status = (int)response.StatusCode;
            string detail = ErrorMessage(body);
            throw new SupabaseHttpException(status,
                detail.Length == 0 ? $"{service} returned {status}." : detail);
        }

        private static string ErrorMessage(string body)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(body);
                JsonElement root = doc.RootElement;
                foreach (string key in new[] { "msg", "message", "error_description", "error" })
                {
                    if (root.TryGetProperty(key, out JsonElement value)
                        && value.ValueKind == JsonValueKind.String)
                    {
                        return value.GetString() ?? "";
                    }
                }
            }
            catch { }
            return "";
        }

        private static StoredSession? ReadStoredSession()
        {
            try
            {
                if (!File.Exists(SessionPath)) return null;
                return JsonSerializer.Deserialize<StoredSession>(File.ReadAllText(SessionPath), Json);
            }
            catch
            {
                return null;
            }
        }

        private static void SaveStoredSession(string refreshToken)
        {
            try
            {
                Directory.CreateDirectory(LauncherPrefs.Directory);
                string temp = SessionPath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(
                    new StoredSession { RefreshToken = refreshToken }, Json));
                File.Move(temp, SessionPath, overwrite: true);
            }
            catch
            {
                // A session that cannot be persisted still works for this run.
            }
        }

        private static void TryDeleteSession()
        {
            try
            {
                if (File.Exists(SessionPath)) File.Delete(SessionPath);
            }
            catch { }
        }

        private static HunterLicenseAccount AccountOf(AuthUser user)
        {
            var providers = user.Identities
                .Select(i => i.Provider.Trim().ToLowerInvariant())
                .Where(p => p.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return new HunterLicenseAccount
            {
                IsAnonymous = user.IsAnonymous,
                Email = user.Email ?? "",
                Providers = providers
            };
        }

        private static bool LooksLikeEmail(string value)
        {
            int at = value.IndexOf('@');
            return at > 0 && at < value.Length - 3
                && value.IndexOf('.', at + 2) > at + 1
                && value.Length <= 254;
        }

        private static string ProviderName(string provider)
            => provider.Length == 0 ? "Provider"
                : Char.ToUpperInvariant(provider[0]) + provider[1..];

        private static string FriendlyStatus(Exception ex)
        {
            if (ex is OperationCanceledException) return "PROFILE LOAD CANCELLED";
            if (ex is SupabaseHttpException http)
            {
                if (http.StatusCode is 400 or 422
                    && http.Message.Contains("anonymous", StringComparison.OrdinalIgnoreCase))
                    return "ENABLE SUPABASE ANONYMOUS SIGN-IN";
                if (http.StatusCode is 401 or 403) return "SUPABASE AUTH REQUIRED";
            }
            return "OFFLINE // RETRY LATER";
        }

        private static string FriendlyAction(Exception ex)
        {
            if (ex is OperationCanceledException) return "Action cancelled.";
            if (ex is SupabaseHttpException http)
            {
                if (http.Message.Contains("manual", StringComparison.OrdinalIgnoreCase)
                    && http.Message.Contains("link", StringComparison.OrdinalIgnoreCase))
                {
                    return "Enable Manual Linking in Supabase Auth settings, then try again.";
                }
                if (http.StatusCode == 429) return "Supabase rate limit reached. Wait a moment and try again.";
                return http.Message;
            }
            return ex.Message.Length == 0 ? "The identity service could not complete that action." : ex.Message;
        }

        private sealed class StoredSession
        {
            [JsonPropertyName("refresh_token")]
            public string RefreshToken { get; set; } = "";
        }

        private sealed class AuthSession
        {
            [JsonPropertyName("access_token")]
            public string AccessToken { get; set; } = "";
            [JsonPropertyName("refresh_token")]
            public string RefreshToken { get; set; } = "";
            [JsonPropertyName("user")]
            public AuthUser? User { get; set; }
        }

        private sealed class AuthUser
        {
            [JsonPropertyName("id")]
            public string Id { get; set; } = "";
            [JsonPropertyName("email")]
            public string? Email { get; set; }
            [JsonPropertyName("is_anonymous")]
            public bool IsAnonymous { get; set; } = true;
            [JsonPropertyName("identities")]
            public List<AuthIdentity> Identities { get; set; } = new();
        }

        private sealed class AuthIdentity
        {
            [JsonPropertyName("provider")]
            public string Provider { get; set; } = "";
        }

        private sealed class OAuthAuthorizeResponse
        {
            [JsonPropertyName("url")]
            public string Url { get; set; } = "";
        }

        private sealed class CareerTicketResponse
        {
            [JsonPropertyName("ticket")]
            public string Ticket { get; set; } = "";
            [JsonPropertyName("expires_at")]
            public DateTimeOffset ExpiresAt { get; set; }
        }

        private sealed class SupabaseHttpException : Exception
        {
            public int StatusCode { get; }
            public SupabaseHttpException(int statusCode, string message) : base(message)
                => StatusCode = statusCode;
        }
    }

    internal readonly record struct HunterLicenseActionResult(bool Success, string Message)
    {
        public static HunterLicenseActionResult Ok(string message) => new(true, message);
        public static HunterLicenseActionResult Fail(string message) => new(false, message);
    }

    internal sealed class HunterLicenseAccount
    {
        public bool IsAnonymous { get; set; } = true;
        public string Email { get; set; } = "";
        public List<string> Providers { get; set; } = new();
        public bool IsSecure => !IsAnonymous;
    }

    internal sealed class HunterLicenseSnapshot
    {
        [JsonIgnore]
        public bool Connected { get; set; }
        [JsonIgnore]
        public string Status { get; set; } = "";
        [JsonIgnore]
        public HunterLicenseAccount Account { get; set; } = new();
        [JsonPropertyName("profile")]
        public HunterLicenseProfile Profile { get; set; } = new();
        [JsonPropertyName("stats")]
        public HunterLicenseStats Stats { get; set; } = new();
        [JsonPropertyName("matches")]
        public List<HunterLicenseMatch> Matches { get; set; } = new();
        [JsonPropertyName("cosmetics")]
        public List<HunterLicenseCosmetic> Cosmetics { get; set; } = new();
    }

    internal sealed class HunterLicenseProfile
    {
        [JsonPropertyName("player_id")]
        public string PlayerId { get; set; } = "";
        [JsonPropertyName("display_name")]
        public string DisplayName { get; set; } = "Player";
        [JsonPropertyName("favorite_hunter")]
        public int FavoriteHunter { get; set; }
        [JsonPropertyName("created_at")]
        public DateTimeOffset? CreatedAt { get; set; }
        [JsonPropertyName("rating_points")]
        public int RatingPoints { get; set; }
        [JsonPropertyName("rating_tier")]
        public int? RatingTier { get; set; }
    }

    internal sealed class HunterLicenseStats
    {
        [JsonPropertyName("games_played")]
        public long GamesPlayed { get; set; }
        [JsonPropertyName("wins")]
        public long Wins { get; set; }
        [JsonPropertyName("ties")]
        public long Ties { get; set; }
        [JsonPropertyName("losses")]
        public long Losses { get; set; }
        [JsonPropertyName("kills")]
        public long Kills { get; set; }
        [JsonPropertyName("deaths")]
        public long Deaths { get; set; }
        [JsonPropertyName("assists")]
        public long Assists { get; set; }
        [JsonPropertyName("damage")]
        public long Damage { get; set; }
        [JsonPropertyName("played_ticks")]
        public long PlayedTicks { get; set; }
        [JsonPropertyName("headshots")]
        public long Headshots { get; set; }
        [JsonPropertyName("longest_kill_streak")]
        public long LongestKillStreak { get; set; }
        [JsonPropertyName("current_win_streak")]
        public long CurrentWinStreak { get; set; }
        [JsonPropertyName("longest_win_streak")]
        public long LongestWinStreak { get; set; }
        [JsonPropertyName("octolith_scores")]
        public long OctolithScores { get; set; }
        [JsonPropertyName("nodes_captured")]
        public long NodesCaptured { get; set; }
        [JsonPropertyName("kills_as_prime")]
        public long KillsAsPrime { get; set; }
    }

    internal sealed class HunterLicenseMatch
    {
        [JsonPropertyName("match_id")]
        public string MatchId { get; set; } = "";
        [JsonPropertyName("played_at")]
        public DateTimeOffset? PlayedAt { get; set; }
        [JsonPropertyName("room_key")]
        public string RoomKey { get; set; } = "";
        [JsonPropertyName("mode")]
        public int Mode { get; set; }
        [JsonPropertyName("trust_class")]
        public int TrustClass { get; set; }
        [JsonPropertyName("career_eligible")]
        public bool CareerEligible { get; set; }
        [JsonPropertyName("rating_status")]
        public string RatingStatus { get; set; } = "";
        [JsonPropertyName("eligible")]
        public bool Eligible { get; set; }
        [JsonPropertyName("won")]
        public bool Won { get; set; }
        [JsonPropertyName("tied")]
        public bool Tied { get; set; }
        [JsonPropertyName("played_ticks")]
        public long PlayedTicks { get; set; }
        [JsonPropertyName("kills")]
        public long Kills { get; set; }
        [JsonPropertyName("deaths")]
        public long Deaths { get; set; }
        [JsonPropertyName("assists")]
        public long Assists { get; set; }
        [JsonPropertyName("damage")]
        public long Damage { get; set; }
    }

    internal sealed class HunterLicenseCosmetic
    {
        [JsonPropertyName("hunter")]
        public int Hunter { get; set; }
        [JsonPropertyName("skin_key")]
        public string SkinKey { get; set; } = "";
        [JsonPropertyName("armor_effect_key")]
        public string ArmorEffectKey { get; set; } = "";
        [JsonPropertyName("death_effect_key")]
        public string DeathEffectKey { get; set; } = "";
    }
}
#endif
