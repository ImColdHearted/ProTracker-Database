using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Foot_Tracker.Models;
using Serilog;

namespace Foot_Tracker.Services
{
    /// <summary>Anything the events server said no to, or the failure to
    /// reach it at all. Message is safe to show on a status line as it is.</summary>
    public sealed class EventsSyncException : Exception
    {
        public int StatusCode { get; }

        public bool Unauthorized => StatusCode == 401;

        public EventsSyncException(string message, int statusCode = 0, Exception? inner = null)
            : base(message, inner)
        {
            StatusCode = statusCode;
        }
    }

    /// <summary>§151. What the admin route answers: the live count and, when
    /// the Worker has any, a week of ten-minute samples summarised. Only
    /// numbers - see the Worker's presence remarks.</summary>
    public sealed record PresenceSummary(
        int ActiveTrackers,
        int WindowMinutes,
        PresenceHistory? History,
        IReadOnlyList<PresenceVersion> Versions);

    /// <summary>§231. How many of the trackers counted above are running a
    /// given build. "unknown" covers every client older than §231, which
    /// sent no version at all - and, until §231 set one, every client would
    /// have said 1.0.0.0 regardless.</summary>
    public sealed record PresenceVersion(string Version, int Trackers);

    /// <param name="Days">The span the Worker summarises (7).</param>
    /// <param name="SampleMinutes">Bucket length (10).</param>
    /// <param name="SampleCount">Samples in the span.</param>
    /// <param name="CoveredDays">Earliest sample to now, capped at Days - how much of the week the numbers rest on.</param>
    /// <param name="CoveredBuckets">Ten-minute buckets in that span.</param>
    /// <param name="ActiveBuckets">Buckets in which at least one tracker was hunting.</param>
    /// <param name="PeakTrackers">Most trackers in any one bucket, null with no samples.</param>
    /// <param name="PeakAtUtc">When that was.</param>
    /// <param name="AverageWhenActive">Mean over the active buckets only.</param>
    /// <param name="ByHourUtc">Mean trackers per hour of the day, UTC, missing buckets counted as zero.</param>
    public sealed record PresenceHistory(
        int Days, int SampleMinutes, int SampleCount, double CoveredDays, int CoveredBuckets, int ActiveBuckets,
        int? PeakTrackers, DateTime? PeakAtUtc, double AverageWhenActive, IReadOnlyList<double> ByHourUtc);

    /// <summary>§234. What the Worker made when the console asked for a test
    /// quest: the species and goal it stored, and the two Mysterious Ticket
    /// tiers it worked out from that goal (0.5% and 3%). Taken from the
    /// Worker's answer rather than recomputed here, so the console can never
    /// show tiers the server disagrees with.</summary>
    public sealed record TestWorldQuest(
        string Pokemon,
        int TotalIvs,
        int FirstTierIvs,
        int SecondTierIvs,
        DateTime? EndsUtc);

    /// <summary>§153. One delegated admin login as the Worker lists it -
    /// name and permission only, never a password or a digest.</summary>
    public sealed record AdminLoginInfo(long Id, string Username, bool CanViewStatus, DateTime CreatedUtc, DateTime? LastUsedUtc, bool Revoked);

    /// <summary>
    /// §143. The Events board's connection to the shared events server - the
    /// Cloudflare Worker in Backend/EventsWorker. GuildEventService and
    /// GuildEventEntryService stay exactly what they were, the local copy;
    /// this is the layer that fills them from the server and pushes posts and
    /// entries up to it. With no server address configured (see
    /// events-backend.json), IsOnline is false and every window behaves as
    /// the local-only prototype did.
    ///
    /// Credentials, none of which is ever written by this class:
    ///
    /// The ADMIN token (since §153 the MASTER credential) is the Worker's
    /// own secret. The admin types it into the tracker when an admin action
    /// first needs it and it stays in a static field, memory only, until the
    /// tracker closes. It is never saved, never logged, and never shipped: a
    /// copy of this app cannot post to anyone's board.
    ///
    /// ADMIN LOGINS (§153) are the delegated alternative: a username plus a
    /// verifier derived from the password on this machine
    /// (DeriveLoginVerifier - the password itself never reaches this class,
    /// the wire, or the server). Held in memory exactly like the master
    /// token; the one deliberate exception is the opt-in remembered login,
    /// which AdminLoginStore keeps encrypted to this Windows account and
    /// which is loaded here once per run on first need.
    ///
    /// The INSTALL token (submitting and removing this tracker's own entries)
    /// is 32 random bytes generated once per installation and kept in the
    /// app's data folder. The server stores only its SHA-256 hash. It grants
    /// nothing except "this is the same tracker that submitted that entry".
    /// </summary>
    public static class EventsSyncService
    {
        /// <summary>The shipped configuration file (copied to the output by
        /// the csproj's SharedPokemonLibrary\Data glob) - one key, baseUrl.
        /// A file of the same name in the app's data folder overrides it, so
        /// a test server can be pointed at without rebuilding.</summary>
        public static readonly string ShippedConfigPath = Path.Combine(
            AppContext.BaseDirectory, "SharedPokemonLibrary", "Data", "Events", "events-backend.json");

        private static readonly string DataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ProTracker",
            "Database");

        public static readonly string OverrideConfigPath = Path.Combine(DataFolder, "events-backend.json");

        private static readonly string InstallTokenPath = Path.Combine(DataFolder, "events-install-token.txt");

        private static readonly HttpClient http = CreateClient();

        private static readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web);

        private static readonly Lazy<string> baseUrl = new(LoadBaseUrl);

        private static readonly Lazy<string> installToken = new(LoadOrCreateInstallToken);

        // Memory only - see the class remarks.
        private static string? adminToken;

        // §153: the delegated login this run signed in with, when the master
        // token is not in use. The verifier is the derived key, not the
        // password. Memory only; the remembered copy is AdminLoginStore's.
        private static string? adminLoginUser;
        private static string? adminLoginVerifier;
        private static bool storedLoginTried;

        // §150: the presence heartbeat's only content - 16 random bytes made
        // when this process started and forgotten when it exits. Not the
        // install token, not derived from it, not saved: two hunts by the same
        // player on two days cannot be connected through it.
        private static readonly string RunId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

        public static string BaseUrl => baseUrl.Value;

        public static bool IsOnline => BaseUrl.Length > 0;

        /// <summary>§226. The per-install identity, for the one other
        /// caller that needs it: BugReportUploadService, which posts a
        /// Report a Problem bundle to the same server and is rate limited
        /// by the same token the events board uses. Internal rather than
        /// public - this is an identifier the server hashes, and nothing
        /// outside this assembly has any business reading it.</summary>
        internal static string InstallToken => installToken.Value;

        /// <summary>§229. Where this assembly's small per-machine files
        /// live, so BugReportUploadService can remember when it last sent
        /// without inventing a second folder for one line of text.</summary>
        internal static string LocalDataFolder => DataFolder;

        /// <summary>§153. True when this run can make admin requests - the
        /// master token, a signed-in login, or a login remembered on this
        /// machine (loaded here, once, the first time anything asks).</summary>
        public static bool HasAdminCredentials
        {
            get
            {
                LoadRememberedLoginOnce();
                return adminToken is not null || adminLoginVerifier is not null;
            }
        }

        public static void SetAdminToken(string? token)
        {
            adminToken = string.IsNullOrWhiteSpace(token) ? null : token.Trim();

            if (adminToken is not null)
            {
                adminLoginUser = null;
                adminLoginVerifier = null;
            }
        }

        /// <summary>§153. Sign this run in with a delegated login. Takes the
        /// derived verifier, never the password - the sign-in window runs
        /// DeriveLoginVerifier first.</summary>
        public static void SetAdminLogin(string username, string verifierHex)
        {
            adminLoginUser = username.Trim();
            adminLoginVerifier = verifierHex;
            adminToken = null;
        }

        /// <summary>Drops whatever admin credential this run holds, and the
        /// remembered login with it - a revoked or reset login must not keep
        /// signing itself back in from disk forever.</summary>
        public static void ForgetAdminCredentials()
        {
            adminToken = null;
            adminLoginUser = null;
            adminLoginVerifier = null;
            storedLoginTried = true;
            AdminLoginStore.Forget();
        }

        private static void LoadRememberedLoginOnce()
        {
            if (storedLoginTried)
                return;

            storedLoginTried = true;

            if (adminToken is not null || adminLoginVerifier is not null)
                return;

            if (AdminLoginStore.TryLoad(out string username, out string verifier))
            {
                adminLoginUser = username;
                adminLoginVerifier = verifier;
                Log.Information("Events server: a remembered admin login was found on this machine and will be used for this run.");
            }
        }

        /// <summary>§153. The slow half of a login, run on this machine:
        /// PBKDF2-HMAC-SHA256 at 210,000 iterations - the same standard the
        /// local admin login set - salted with a digest of the app's own
        /// domain string and the lowercased username, so equal passwords
        /// still derive apart and a rainbow table for another service is
        /// useless here. The Worker stores a digest of this result and never
        /// sees a password; a guess against a stolen database costs the full
        /// derivation. Takes about a tenth of a second - callers run it off
        /// the UI thread (Task.Run).</summary>
        public static string DeriveLoginVerifier(string username, string password)
        {
            byte[] salt = SHA256.HashData(Encoding.UTF8.GetBytes("ProTracker events admin login|" + username.Trim().ToLowerInvariant()));
            byte[] verifier = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, 210_000, HashAlgorithmName.SHA256, 32);

            return Convert.ToHexString(verifier).ToLowerInvariant();
        }

        // ------------------------------------------------------------ reads

        public static async Task<IReadOnlyList<GuildEvent>> FetchEventsAsync(CancellationToken cancellationToken = default)
        {
            EventListDto list = await SendAsync<EventListDto>(HttpMethod.Get, "/v1/events", null, admin: false, cancellationToken);

            return list.Events.Select(ToModel).ToList();
        }

        public static async Task<IReadOnlyList<EventEntry>> FetchEntriesAsync(string eventId, CancellationToken cancellationToken = default)
        {
            EntryListDto list = await SendAsync<EntryListDto>(HttpMethod.Get, $"/v1/events/{Uri.EscapeDataString(eventId)}/entries", null, admin: false, cancellationToken);

            return list.Entries.Select(ToModel).ToList();
        }

        /// <summary>§207. Which counterpart events the game is currently
        /// running, as the admin published them. Public - every tracker asks
        /// for this, it is a notice board rather than a secret. The returned
        /// list is already trimmed, de-duplicated and capped by the server.
        /// </summary>
        public static async Task<ActiveEvents> FetchActiveEventsAsync(CancellationToken cancellationToken = default)
        {
            ActiveEventsDto dto = await SendAsync<ActiveEventsDto>(
                HttpMethod.Get, "/v1/active-events", null, admin: false, cancellationToken);

            return new ActiveEvents(dto.Events, dto.UpdatedUtc, dto.UpdatedBy ?? string.Empty);
        }

        /// <summary>§233. The World Quests the Worker has read out of PRO's
        /// own Discord announcement channel, newest first. Public and
        /// unauthenticated for the same reason as the active-event list: a
        /// quest PRO announced in its own server is a notice board, not a
        /// secret. An empty list is the normal answer - quests run about once
        /// a month.
        ///
        /// A quest the Worker could not parse comes back with Parsed false and
        /// the fields it did get; the caller shows nothing rather than
        /// half a quest.</summary>
        public static async Task<IReadOnlyList<WorldQuest>> FetchWorldQuestsAsync(CancellationToken cancellationToken = default)
        {
            WorldQuestListDto list = await SendAsync<WorldQuestListDto>(
                HttpMethod.Get, "/v1/world-quests", null, admin: false, cancellationToken);

            var quests = new List<WorldQuest>();

            foreach (WorldQuestDto dto in list.Recent ?? new List<WorldQuestDto>())
            {
                if (string.IsNullOrWhiteSpace(dto.MessageId))
                    continue;

                quests.Add(new WorldQuest(
                    dto.MessageId,
                    dto.Pokemon ?? string.Empty,
                    dto.TotalIvs,
                    dto.SingleIvs,
                    dto.AverageSubmissions,
                    dto.LowestTier ?? string.Empty,
                    dto.Reward ?? string.Empty,
                    dto.Duration ?? string.Empty,
                    dto.EndTimeText ?? string.Empty,
                    ParseUtc(dto.StartedUtc),
                    // Null, not "now": a quest with no end is one whose
                    // duration the Worker could not read, and a countdown
                    // started from now would be a lie the window then shows
                    // ticking down.
                    string.IsNullOrWhiteSpace(dto.EndsUtc) ? null : ParseUtc(dto.EndsUtc),
                    dto.Parsed));
            }

            return quests;
        }

        /// <summary>§234. Admin. Starts a World Quest of the admin's own so the
        /// World Quest window can be exercised on a day PRO is not running one.
        /// The Worker stores it exactly where a real quest goes, so nothing
        /// downstream can tell the difference; only its message id marks it.
        /// Returns the two reward tiers as the Worker worked them out, rather
        /// than recomputing them here and risking disagreeing about them.</summary>
        public static async Task<TestWorldQuest> StartTestWorldQuestAsync(
            string pokemon, int totalIvs, CancellationToken cancellationToken = default)
        {
            var body = new TestQuestRequestDto { Pokemon = pokemon, TotalIvs = totalIvs };

            TestQuestResponseDto created = await SendAsync<TestQuestResponseDto>(
                HttpMethod.Post, "/v1/admin/world-quests/test", body, admin: true, cancellationToken);

            return new TestWorldQuest(
                created.Quest?.Pokemon ?? pokemon,
                created.Quest?.TotalIvs ?? totalIvs,
                created.FirstTierIvs,
                created.SecondTierIvs,
                string.IsNullOrWhiteSpace(created.Quest?.EndsUtc) ? null : ParseUtc(created.Quest.EndsUtc));
        }

        /// <summary>§234. Admin. Removes every test quest, not only the running
        /// one - a quest lasts a day and testing makes several. Returns how
        /// many rows went. Cannot touch a quest PRO announced.</summary>
        public static async Task<int> ClearTestWorldQuestsAsync(CancellationToken cancellationToken = default)
        {
            TestQuestClearedDto cleared = await SendAsync<TestQuestClearedDto>(
                HttpMethod.Delete, "/v1/admin/world-quests/test", null, admin: true, cancellationToken);

            return cleared.Removed;
        }

        /// <summary>§239. What PRO announced, newest first, as the Worker read
        /// it out of the followed Discord channel. Public and unauthenticated,
        /// the same reasoning as the quests and the active-event list.</summary>
        public static async Task<IReadOnlyList<Announcement>> FetchAnnouncementsAsync(
            CancellationToken cancellationToken = default)
        {
            AnnouncementListDto list = await SendAsync<AnnouncementListDto>(
                HttpMethod.Get, "/v1/announcements", null, admin: false, cancellationToken);

            var announcements = new List<Announcement>();

            foreach (AnnouncementDto dto in list.Announcements ?? new List<AnnouncementDto>())
            {
                if (string.IsNullOrWhiteSpace(dto.Id))
                    continue;

                announcements.Add(new Announcement
                {
                    Id = dto.Id,
                    Author = dto.Author ?? string.Empty,
                    Body = dto.Body ?? string.Empty,
                    ImageUrl = dto.ImageUrl ?? string.Empty,
                    Published = string.IsNullOrWhiteSpace(dto.PostedUtc)
                        ? null
                        : new DateTimeOffset(ParseUtc(dto.PostedUtc), TimeSpan.Zero),
                });
            }

            return announcements;
        }

        // ----------------------------------------------------------- writes

        /// <summary>§207. Admin. Publishes the active-event list to every
        /// tracker; the server caps it at three and hands back what it
        /// actually stored, which is what the console then shows.</summary>
        public static async Task<ActiveEvents> SaveActiveEventsAsync(
            IEnumerable<string> events, CancellationToken cancellationToken = default)
        {
            var body = new ActiveEventsDto { Events = events.ToList() };

            ActiveEventsDto saved = await SendAsync<ActiveEventsDto>(
                HttpMethod.Put, "/v1/active-events", body, admin: true, cancellationToken);

            return new ActiveEvents(saved.Events, saved.UpdatedUtc, saved.UpdatedBy ?? string.Empty);
        }

        /// <summary>Admin. Posts the draft and returns the server's copy -
        /// its id and its clock, which is what every other tracker will see.</summary>
        public static async Task<GuildEvent> PostEventAsync(GuildEvent draft, CancellationToken cancellationToken = default)
        {
            var body = new EventDto
            {
                Type = draft.Type.ToString(),
                Title = draft.Title,
                Message = draft.Message,
                PostedBy = draft.PostedBy,
                PokemonName = draft.PokemonName,
                ItemReward = draft.ItemReward,
                PokemonReward = draft.PokemonReward,
                PokeDollars = draft.PokeDollars,
                AllowPokemonSubmissions = draft.AllowPokemonSubmissions,
                AllowViewEntries = draft.AllowViewEntries
            };

            EventEnvelopeDto envelope = await SendAsync<EventEnvelopeDto>(HttpMethod.Post, "/v1/events", body, admin: true, cancellationToken);

            if (envelope.Event is null)
                throw new EventsSyncException("The events server accepted the post but sent nothing back.");

            return ToModel(envelope.Event);
        }

        /// <summary>Admin. True when the event was removed, false when the
        /// server no longer had it (already gone counts as done).</summary>
        public static async Task<bool> DeleteEventAsync(string eventId, CancellationToken cancellationToken = default)
        {
            try
            {
                await SendAsync<DeletedDto>(HttpMethod.Delete, $"/v1/events/{Uri.EscapeDataString(eventId)}", null, admin: true, cancellationToken);
                return true;
            }
            catch (EventsSyncException ex) when (ex.StatusCode == 404)
            {
                return false;
            }
        }

        /// <summary>Player. Submits an entry under this installation's token
        /// and returns the server's copy of it.</summary>
        public static async Task<EventEntry> SubmitEntryAsync(string eventId, string username, string pokemonName, CancellationToken cancellationToken = default)
        {
            var body = new EntryDto { Username = username, PokemonName = pokemonName };

            EntryEnvelopeDto envelope = await SendAsync<EntryEnvelopeDto>(
                HttpMethod.Post, $"/v1/events/{Uri.EscapeDataString(eventId)}/entries", body, admin: false, cancellationToken);

            if (envelope.Entry is null)
                throw new EventsSyncException("The events server accepted the entry but sent nothing back.");

            return ToModel(envelope.Entry);
        }

        // --------------------------------------------------------- presence

        /// <summary>§150. Player, while a hunt runs: "one tracker is hunting",
        /// and nothing else. The request deliberately carries no install
        /// token (identify: false) and logs at Debug on success (quiet: true)
        /// - it repeats every five minutes and is not news.</summary>
        public static Task SendPresenceAsync(CancellationToken cancellationToken = default) =>
            SendAsync<AckDto>(
                HttpMethod.Post,
                "/v1/presence",
                // §231: the build, beside the run id. Still nothing that
                // could name a person - a version string is the same for
                // everyone running it, which is the entire point of counting
                // them.
                new PresenceDto { RunId = RunId, Version = AppVersion.Current },
                admin: false,
                cancellationToken,
                identify: false,
                quiet: true);

        /// <summary>§150. Admin. How many trackers sent a heartbeat inside the
        /// server's window (ten minutes), and (§151) the week's history when
        /// the Worker has samples to summarise - null from a Worker older than
        /// §151, which the console reads as "no history yet".</summary>
        public static async Task<PresenceSummary> FetchActiveTrackersAsync(CancellationToken cancellationToken = default)
        {
            PresenceCountDto count = await SendAsync<PresenceCountDto>(HttpMethod.Get, "/v1/admin/presence", null, admin: true, cancellationToken);

            PresenceHistory? history = count.History is null ? null : new PresenceHistory(
                count.History.Days,
                count.History.SampleMinutes,
                count.History.SampleCount,
                count.History.CoveredDays,
                count.History.CoveredBuckets,
                count.History.ActiveBuckets,
                count.History.Peak?.Trackers,
                count.History.Peak is null ? null : ParseUtc(count.History.Peak.AtUtc),
                count.History.AverageWhenActive,
                count.History.ByHourUtc ?? Array.Empty<double>());

            // §231.
            var versions = new List<PresenceVersion>();

            foreach (PresenceVersionDto v in count.Versions ?? new List<PresenceVersionDto>())
            {
                string name = string.IsNullOrWhiteSpace(v.Version) ? "unknown" : v.Version.Trim();

                if (v.Trackers > 0)
                    versions.Add(new PresenceVersion(name, v.Trackers));
            }

            return new PresenceSummary(count.ActiveTrackers, count.WindowMinutes, history, versions);
        }

        // ---------------------------------------------- admin logins (§153)

        /// <summary>§153. Master token only. The delegated logins - names,
        /// permissions and use, never a digest.</summary>
        public static async Task<IReadOnlyList<AdminLoginInfo>> FetchAdminLoginsAsync(CancellationToken cancellationToken = default)
        {
            AdminLoginListDto list = await SendAsync<AdminLoginListDto>(HttpMethod.Get, "/v1/admin/logins", null, admin: true, cancellationToken);

            return list.Logins.Select(ToModel).ToList();
        }

        /// <summary>§153. Master token only. Creates the login - or, posting
        /// an existing name, resets its password and permission and lifts a
        /// revocation.</summary>
        public static async Task<AdminLoginInfo> SaveAdminLoginAsync(string username, string verifierHex, bool canViewStatus, CancellationToken cancellationToken = default)
        {
            var body = new AdminLoginSaveDto { Username = username, Verifier = verifierHex, CanViewStatus = canViewStatus };

            AdminLoginEnvelopeDto envelope = await SendAsync<AdminLoginEnvelopeDto>(HttpMethod.Post, "/v1/admin/logins", body, admin: true, cancellationToken);

            if (envelope.Login is null)
                throw new EventsSyncException("The events server accepted the login but sent nothing back.");

            return ToModel(envelope.Login);
        }

        /// <summary>§153. Master token only. The login stops working with
        /// its next request.</summary>
        public static async Task<AdminLoginInfo> RevokeAdminLoginAsync(long id, CancellationToken cancellationToken = default)
        {
            AdminLoginEnvelopeDto envelope = await SendAsync<AdminLoginEnvelopeDto>(HttpMethod.Post, $"/v1/admin/logins/{id}/revoke", null, admin: true, cancellationToken);

            if (envelope.Login is null)
                throw new EventsSyncException("The events server revoked the login but sent nothing back.");

            return ToModel(envelope.Login);
        }

        // --------------------------------------------------------- transport

        private static async Task<T> SendAsync<T>(
            HttpMethod method, string path, object? body, bool admin, CancellationToken cancellationToken,
            bool identify = true, bool quiet = false)
            where T : class, new()
        {
            if (!IsOnline)
                throw new EventsSyncException("No events server is configured.");

            using var request = new HttpRequestMessage(method, BaseUrl + path);

            // The install token identifies this tracker to the server (only
            // its hash is stored there); harmless on reads, required on
            // submissions. §150's presence heartbeat is the one request that
            // leaves it out on purpose - that call is anonymous by design.
            if (identify)
                request.Headers.TryAddWithoutValidation("X-Install-Token", installToken.Value);

            if (admin)
            {
                LoadRememberedLoginOnce();

                if (adminToken is not null)
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
                }
                else if (adminLoginUser is not null && adminLoginVerifier is not null)
                {
                    // §153: a delegated login travels as two headers. The
                    // verifier is already the derived key; the Worker stores
                    // and compares digests of it, constant-time.
                    request.Headers.TryAddWithoutValidation("X-Admin-User", adminLoginUser);
                    request.Headers.TryAddWithoutValidation("X-Admin-Verifier", adminLoginVerifier);
                }
                else
                {
                    throw new EventsSyncException("This action needs an events-server admin sign-in.", 401);
                }
            }

            if (body is not null)
            {
                request.Content = new StringContent(JsonSerializer.Serialize(body, body.GetType(), json), Encoding.UTF8, "application/json");
            }

            HttpResponseMessage response;
            string text;

            try
            {
                response = await http.SendAsync(request, cancellationToken);
                text = await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Log.Warning("Events server: {Method} {Path} timed out", method, path);
                throw new EventsSyncException("the events server did not answer in time");
            }
            catch (HttpRequestException ex)
            {
                Log.Warning(ex, "Events server: {Method} {Path} could not be reached", method, path);
                throw new EventsSyncException("the events server could not be reached", 0, ex);
            }

            using (response)
            {
                int status = (int)response.StatusCode;

                // Method, path and status only - never a header, never a body
                // that could carry a token. Quiet callers (the §150 heartbeat)
                // log at Debug so a repeating call does not fill the file.
                if (quiet)
                    Log.Debug("Events server: {Method} {Path} -> {Status}", method, path, status);
                else
                    Log.Information("Events server: {Method} {Path} -> {Status}", method, path, status);

                if (!response.IsSuccessStatusCode)
                    throw new EventsSyncException(ErrorMessage(text, status), status);

                if (string.IsNullOrWhiteSpace(text))
                    return new T();

                try
                {
                    return JsonSerializer.Deserialize<T>(text, json) ?? new T();
                }
                catch (JsonException ex)
                {
                    throw new EventsSyncException("the events server sent a reply this tracker could not read", status, ex);
                }
            }
        }

        private static string ErrorMessage(string text, int status)
        {
            try
            {
                ErrorDto? error = JsonSerializer.Deserialize<ErrorDto>(text, json);

                if (!string.IsNullOrWhiteSpace(error?.Error))
                    return error.Error;
            }
            catch (JsonException)
            {
                // A non-JSON error page - fall through to the status.
            }

            return status switch
            {
                401 => "the events server rejected the admin sign-in",
                403 => "the events server refused this action",
                404 => "the events server has no such event",
                429 => "the events server is asking this tracker to slow down",
                503 => "the events server is not set up yet",
                _ => $"the events server answered with HTTP {status}"
            };
        }

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ProTracker/1.0");
            return client;
        }

        // ------------------------------------------------------------ config

        private static string LoadBaseUrl()
        {
            foreach (string path in new[] { OverrideConfigPath, ShippedConfigPath })
            {
                if (!File.Exists(path))
                    continue;

                try
                {
                    BackendConfigDto? config = JsonSerializer.Deserialize<BackendConfigDto>(File.ReadAllText(path), json);
                    string url = (config?.BaseUrl ?? string.Empty).Trim().TrimEnd('/');

                    if (url.Length == 0)
                        continue;

                    if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
                        (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
                    {
                        Log.Warning("Events server: ignoring baseUrl in {Path} - it must be an https:// address", path);
                        continue;
                    }

                    Log.Information("Events server: {Url} (from {Path})", url, path);
                    return url;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Events server: could not read {Path}", path);
                }
            }

            Log.Information("Events server: none configured - the Events board is local to this machine.");
            return string.Empty;
        }

        private static string LoadOrCreateInstallToken()
        {
            try
            {
                if (File.Exists(InstallTokenPath))
                {
                    string existing = File.ReadAllText(InstallTokenPath).Trim();

                    if (existing.Length >= 32 && existing.Length <= 128)
                        return existing;
                }

                string fresh = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

                Directory.CreateDirectory(DataFolder);
                File.WriteAllText(InstallTokenPath, fresh);

                return fresh;
            }
            catch (Exception ex)
            {
                // No data folder to write to: a per-run token still works for
                // submitting; only "delete my own entry later" is lost.
                Log.Warning(ex, "Events server: the install token could not be saved - using one for this run only");
                return Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            }
        }

        // ----------------------------------------------------------- mapping

        private static GuildEvent ToModel(EventDto dto) => new()
        {
            Id = dto.Id,
            Type = Enum.TryParse(dto.Type, ignoreCase: true, out GuildEventType type) ? type : GuildEventType.Announcement,
            Title = dto.Title,
            Message = dto.Message,
            PostedBy = dto.PostedBy,
            PostedAtUtc = ParseUtc(dto.PostedAtUtc),
            PokemonName = dto.PokemonName,
            ItemReward = dto.ItemReward,
            PokemonReward = dto.PokemonReward,
            PokeDollars = dto.PokeDollars,
            AllowPokemonSubmissions = dto.AllowPokemonSubmissions,
            AllowViewEntries = dto.AllowViewEntries,
            EntryCount = dto.EntryCount
        };

        /// <summary>§153.</summary>
        private static AdminLoginInfo ToModel(AdminLoginDto dto) => new(
            dto.Id,
            dto.Username,
            dto.CanViewStatus,
            ParseUtc(dto.CreatedUtc),
            string.IsNullOrEmpty(dto.LastUsedUtc) ? null : ParseUtc(dto.LastUsedUtc),
            dto.Revoked);

        private static EventEntry ToModel(EntryDto dto) => new()
        {
            Id = dto.Id,
            EventId = dto.EventId,
            Username = dto.Username,
            PokemonName = dto.PokemonName,
            SubmittedAtUtc = ParseUtc(dto.SubmittedAtUtc)
        };

        private static DateTime ParseUtc(string? iso) =>
            DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset value)
                ? value.UtcDateTime
                : DateTime.UtcNow;

        // -------------------------------------------------------------- DTOs
        // Property names are the Worker's JSON keys (camelCased by
        // JsonSerializerDefaults.Web on the way out and matched
        // case-insensitively on the way in).

        private sealed class ActiveEventsDto
        {
            public List<string> Events { get; set; } = new();
            public string? UpdatedUtc { get; set; }
            public string? UpdatedBy { get; set; }
        }

        private sealed class BackendConfigDto
        {
            public string? BaseUrl { get; set; }
        }

        private sealed class EventDto
        {
            public string Id { get; set; } = string.Empty;
            public string Type { get; set; } = string.Empty;
            public string Title { get; set; } = string.Empty;
            public string Message { get; set; } = string.Empty;
            public string PostedBy { get; set; } = string.Empty;
            public string? PostedAtUtc { get; set; }
            public string PokemonName { get; set; } = string.Empty;
            public string ItemReward { get; set; } = string.Empty;
            public string PokemonReward { get; set; } = string.Empty;
            public long PokeDollars { get; set; }
            public bool AllowPokemonSubmissions { get; set; } = true;
            public bool AllowViewEntries { get; set; } = true;
            public int EntryCount { get; set; }
        }

        private sealed class EventListDto
        {
            public List<EventDto> Events { get; set; } = new();
        }

        private sealed class EventEnvelopeDto
        {
            public EventDto? Event { get; set; }
        }

        private sealed class EntryDto
        {
            public string Id { get; set; } = string.Empty;
            public string EventId { get; set; } = string.Empty;
            public string Username { get; set; } = string.Empty;
            public string PokemonName { get; set; } = string.Empty;
            public string? SubmittedAtUtc { get; set; }
            public bool Mine { get; set; }
        }

        private sealed class EntryListDto
        {
            public List<EntryDto> Entries { get; set; } = new();
        }

        private sealed class EntryEnvelopeDto
        {
            public EntryDto? Entry { get; set; }
        }

        private sealed class DeletedDto
        {
            public bool Deleted { get; set; }
        }

        private sealed class AckDto
        {
            public bool Ok { get; set; }
        }

        private sealed class PresenceDto
        {
            public string RunId { get; set; } = string.Empty;

            // §231. A Worker older than §231 ignores it.
            public string Version { get; set; } = string.Empty;
        }

        private sealed class PresenceCountDto
        {
            public int ActiveTrackers { get; set; }
            public int WindowMinutes { get; set; } = 10;
            public PresenceHistoryDto? History { get; set; }

            // §231. Null from a Worker older than §231, which the console
            // reads as "no breakdown yet" rather than as nobody running
            // anything.
            public List<PresenceVersionDto>? Versions { get; set; }
        }

        private sealed class PresenceVersionDto
        {
            public string Version { get; set; } = "";
            public int Trackers { get; set; }
        }

        private sealed class PresenceHistoryDto
        {
            public int Days { get; set; } = 7;
            public int SampleMinutes { get; set; } = 10;
            public int SampleCount { get; set; }
            public double CoveredDays { get; set; }
            public int CoveredBuckets { get; set; }
            public int ActiveBuckets { get; set; }
            public PresencePeakDto? Peak { get; set; }
            public double AverageWhenActive { get; set; }
            public double[]? ByHourUtc { get; set; }
        }

        private sealed class PresencePeakDto
        {
            public int Trackers { get; set; }
            public string? AtUtc { get; set; }
        }

        private sealed class AdminLoginDto
        {
            public long Id { get; set; }
            public string Username { get; set; } = string.Empty;
            public bool CanViewStatus { get; set; }
            public string? CreatedUtc { get; set; }
            public string? LastUsedUtc { get; set; }
            public bool Revoked { get; set; }
        }

        private sealed class AdminLoginListDto
        {
            public List<AdminLoginDto> Logins { get; set; } = new();
        }

        private sealed class AdminLoginEnvelopeDto
        {
            public AdminLoginDto? Login { get; set; }
        }

        private sealed class AdminLoginSaveDto
        {
            public string Username { get; set; } = string.Empty;
            public string Verifier { get; set; } = string.Empty;
            public bool CanViewStatus { get; set; }
        }

        private sealed class WorldQuestListDto
        {
            public WorldQuestDto? Active { get; set; }
            public List<WorldQuestDto>? Recent { get; set; }
            public string? AsOfUtc { get; set; }
        }

        private sealed class WorldQuestDto
        {
            public string? MessageId { get; set; }
            public string? Pokemon { get; set; }
            public int TotalIvs { get; set; }
            public int SingleIvs { get; set; }
            public int AverageSubmissions { get; set; }
            public string? LowestTier { get; set; }
            public string? Reward { get; set; }
            public string? Duration { get; set; }
            public string? EndTimeText { get; set; }
            public string? StartedUtc { get; set; }
            public string? EndsUtc { get; set; }
            public bool Parsed { get; set; }
        }

        private sealed class TestQuestRequestDto
        {
            public string Pokemon { get; set; } = string.Empty;
            public int TotalIvs { get; set; }
        }

        private sealed class TestQuestResponseDto
        {
            public WorldQuestDto? Quest { get; set; }
            public int FirstTierIvs { get; set; }
            public int SecondTierIvs { get; set; }
        }

        private sealed class TestQuestClearedDto
        {
            public int Removed { get; set; }
        }

        private sealed class AnnouncementListDto
        {
            public List<AnnouncementDto>? Announcements { get; set; }
            public string? AsOfUtc { get; set; }
        }

        private sealed class AnnouncementDto
        {
            public string? Id { get; set; }
            public string? Author { get; set; }
            public string? Body { get; set; }
            public string? ImageUrl { get; set; }
            public string? Link { get; set; }
            public string? PostedUtc { get; set; }
            public string? Source { get; set; }
        }

        private sealed class ErrorDto
        {
            public string? Error { get; set; }
        }
    }
}
