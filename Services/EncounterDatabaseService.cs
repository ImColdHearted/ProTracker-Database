using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Serilog;

namespace Foot_Tracker.Services
{
    /// <summary>One row of the permanent encounter log, as the All time view
    /// shows it. Distinct from Models.SessionEncounterRecord: that one is a
    /// live, mutable record of the CURRENT hunt with no timestamp, this one is
    /// an immutable snapshot read back out of the database with the moment it
    /// happened, which is the whole reason the permanent log is worth
    /// keeping.</summary>
    public sealed record EncounterLogRow(
        long Id,
        DateTime WhenUtc,
        string Species,
        int? Level,
        string Location,
        string? Gender = null,
        string? RareType = null)
    {
        public string LevelDisplay => Level is int value ? $"Lv. {value}" : "Lv. ?";

        /// <summary>§124. Blank, not "Unknown", for a row from before
        /// these columns existed or an encounter whose gender never resolved.
        /// A history list with thousands of rows should not be a wall of the
        /// word Unknown - an empty cell reads as "not recorded" on its own.</summary>
        public string GenderDisplay => Gender switch
        {
            "Male" => "M",
            "Female" => "F",
            "Genderless" => "-",
            _ => string.Empty
        };

        /// <summary>Shiny or Form, blank for an ordinary encounter. Same
        /// reasoning as GenderDisplay: the interesting rows are the ones
        /// that say something.</summary>
        public string RareDisplay =>
            string.IsNullOrWhiteSpace(RareType) || RareType == "None"
                ? string.Empty
                : RareType;

        /// <summary>Stored in UTC, shown in the player's own time - the log is
        /// meant to be read by the person who made it.</summary>
        public string WhenDisplay => WhenUtc.ToLocalTime().ToString("d MMM yyyy  HH:mm");
    }

    /// <summary>
    /// The permanent encounter log (MIGRATION_GUIDE.md §112). Every detected
    /// wild encounter, forever, in one SQLite database - as opposed to
    /// SessionEncounterHistoryService, which holds the CURRENT hunt in memory
    /// and clears on Reset.
    ///
    /// Why a database and not another JSON file: the session store rewrites
    /// its whole file on every debounced save, which is fine for a few
    /// thousand rows and untenable for a permanent log. At the tester's
    /// measured 230 encounters per hunting hour, a year of four hours a day
    /// is ~336,000 rows; three years at eight hours a day is around two
    /// million. Measured on the real schema at that size: 191 MB on disk,
    /// 0.19 ms per insert, and 0.35-0.62 ms to fetch a page of the All time
    /// view at ANY depth using keyset paging. It does not degrade.
    ///
    /// Three deliberate properties:
    ///
    /// FAIL-SAFE. Hunting must never break because a log sink did. Every
    /// entry point swallows its own failure, logs once, and goes dormant -
    /// after which the app behaves exactly as it did before §112 existed.
    /// A missing native SQLite library, a locked file, a full disk and a
    /// corrupt database all land in the same place: nothing is recorded, and
    /// nothing else notices.
    ///
    /// NO I/O ON THE ENCOUNTER PATH. SessionEncounterHistoryService.Append
    /// promises it does no I/O of its own, and this must not break that
    /// promise. So Append here only stashes the encounter in memory; the row
    /// is written when the NEXT encounter arrives, or on an explicit flush.
    /// That deferral also removes UPDATEs entirely: late level and location
    /// refinements (§96, §102) target the encounter currently on screen, and
    /// the app already guarantees a refinement never crosses an encounter
    /// boundary, so by the time a row is written its values are final. One
    /// INSERT per encounter, no UPDATE, no row-id bookkeeping.
    ///
    /// ONE DATABASE, CLIENT COLUMN. Unlike every per-client JSON store, this
    /// is a single file with a client number on each row. Queries default to
    /// the active profile, but a cross-profile total is one query rather than
    /// four connections - which is half the point of using a database.
    /// Encounters recorded while no client is bound (§105 step-down, before
    /// any assignment) are skipped, matching every other per-client store.
    /// </summary>
    public static class EncounterDatabaseService
    {
        /// <summary>Bumped only when the table shape changes; stored in the
        /// database's own PRAGMA user_version so a future migration can tell
        /// what it is looking at without a metadata table.</summary>
        private const int SchemaVersion = 2;

        /// <summary>Rows per page of the All time view. Paging is keyset
        /// ("id &lt; the last one I showed"), not OFFSET: measured on two
        /// million rows, keyset stays at 0.13 ms at any depth while OFFSET
        /// degrades to 8.6 ms by page 500.
        ///
        /// §128 brought this down from 200 to 100, to match the front
        /// encounter table and the agreed maximum for both. The keyset
        /// property is unaffected - it is a LIMIT, not an offset - so this
        /// is purely how much arrives at once.</summary>
        public const int PageSize = 100;

        private static readonly string SaveFolder =
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ProTracker",
                "Database"
            );

        private static readonly string DatabasePath =
            Path.Combine(SaveFolder, "encounters.db");

        // One connection for the app's life, guarded by this. SQLite handles
        // concurrent readers fine in WAL mode, but a single ADO.NET connection
        // object is not thread-safe, and writes come from a background task
        // while reads come from the UI thread.
        private static readonly object gate = new();

        private static SqliteConnection? connection;
        private static bool openAttempted;

        /// <summary>Set by the first failure of any kind. Once true nothing is
        /// tried again for the rest of the run - a sink that cannot work
        /// should be silent, not a warning every fifteen seconds.</summary>
        private static bool dormant;

        // The encounter on screen, not yet written. See the class doc's
        // "no I/O on the encounter path".
        private static int pendingClient;
        private static DateTime pendingWhenUtc;
        private static string? pendingSpecies;
        private static int? pendingLevel;
        private static string pendingLocation = "Unknown";

        // §124. Gender and shiny/form arrive AFTER the encounter is
        // appended - both need a detection window the first frame cannot
        // give - which is exactly the case the pending-row design already
        // exists for. They refine the row in memory and cost nothing,
        // the same way level and location do.
        private static string? pendingGender;
        private static string? pendingRare;

        /// <summary>False when the log is unavailable - a missing native
        /// library, an unwritable folder, a damaged file. The All time view
        /// reads this to explain itself rather than showing an empty list
        /// that looks like "you have never seen this Pokemon".</summary>
        public static bool IsAvailable
        {
            get
            {
                lock (gate)
                {
                    return EnsureOpen() is not null;
                }
            }
        }

        private static SqliteConnection? EnsureOpen()
        {
            if (dormant)
                return null;

            if (openAttempted)
                return connection;

            openAttempted = true;

            try
            {
                Directory.CreateDirectory(SaveFolder);

                var builder = new SqliteConnectionStringBuilder
                {
                    DataSource = DatabasePath
                };

                var opened = new SqliteConnection(builder.ToString());
                opened.Open();

                using (SqliteCommand setup = opened.CreateCommand())
                {
                    // WAL so a reader (the All time view) never blocks the
                    // writer, and NORMAL so a commit does not wait on a disk
                    // flush - the authoritative encounter COUNTS are saved
                    // separately per encounter by SessionPersistenceService,
                    // so the worst a hard power loss costs here is the last
                    // few rows of history detail.
                    setup.CommandText =
                        "PRAGMA journal_mode=WAL;" +
                        "PRAGMA synchronous=NORMAL;" +
                        // §124 added gender and rare. A database created
                        // from here on gets them in the CREATE; one that
                        // already exists is brought forward by
                        // AddMissingColumns below, which is why this shape
                        // and that list have to agree.
                        "CREATE TABLE IF NOT EXISTS encounters (" +
                        "  id       INTEGER PRIMARY KEY AUTOINCREMENT," +
                        "  client   INTEGER NOT NULL," +
                        "  utc      TEXT    NOT NULL," +
                        "  species  TEXT    NOT NULL," +
                        "  level    INTEGER," +
                        "  location TEXT    NOT NULL," +
                        "  gender   TEXT," +
                        "  rare     TEXT);" +
                        // The one index the feature needs. A second index on
                        // (client, id) was measured and dropped: it cost 26
                        // bytes a row - 52 MB at two million - and no query
                        // this feature makes uses it. AUTOINCREMENT is kept
                        // deliberately: ordering is by id, and without it a
                        // deleted tail could let a new row reuse a lower id
                        // and sort into the wrong place.
                        "CREATE INDEX IF NOT EXISTS ix_encounters_species_client" +
                        "  ON encounters (species, client, id DESC);" +
                        $"PRAGMA user_version={SchemaVersion};";

                    setup.ExecuteNonQuery();
                }

                // §124. CREATE TABLE IF NOT EXISTS does nothing to a table
                // that is already there, so an existing log keeps the v1
                // shape and every read of the two new columns would throw.
                AddMissingColumns(opened);
                RenameLegacySpecies(opened);

                connection = opened;
                Log.Information("Encounter log opened at {Path}", DatabasePath);

                return connection;
            }
            catch (Exception ex)
            {
                // The single most likely cause is the native SQLite library
                // not being beside the executable. Everything else in the app
                // carries on regardless - that is the point of going dormant
                // rather than throwing.
                Log.Warning(ex, "Encounter log unavailable - it will stay off for this run");

                dormant = true;
                connection = null;

                return null;
            }
        }

        /// <summary>
        /// §124. Brings a database created under an older schema up to the
        /// current one, by asking the table what columns it actually has and
        /// adding any that are missing.
        ///
        /// Driven off PRAGMA table_info rather than off user_version. The
        /// version number says what SHOULD be there; table_info says what IS,
        /// and only the second one is safe to act on. A database whose
        /// user_version was written but whose ALTER did not land - a crash
        /// between the two, an older build that set the version without
        /// migrating - would be skipped forever by a version check and
        /// repaired on the next open by this one. It is also idempotent, so
        /// running it on every open costs one cheap query and can never
        /// double-add.
        ///
        /// SQLite's ALTER TABLE ADD COLUMN is a metadata-only change: it does
        /// not rewrite the table, so this stays instant at two million rows.
        /// Existing rows read NULL for the new columns, which is exactly
        /// right - those encounters happened before anything recorded a
        /// gender, and a blank cell says so honestly where a backfilled
        /// "Unknown" would look like a reading that failed.
        /// </summary>
        /// <summary>§405. Rows logged under the old spelling of the two
        /// Nidoran - "Nidoran♀"/"Nidoran♂" - become "Nidoran F"/"Nidoran M",
        /// the library's spelling now, so the count and the history for a
        /// species are one whole. Two indexed lookups; nothing to do on
        /// every run after the first.</summary>
        private static void RenameLegacySpecies(SqliteConnection open)
        {
            using SqliteCommand rename = open.CreateCommand();

            rename.CommandText =
                "UPDATE encounters SET species = $f WHERE species = $legacyF;" +
                "UPDATE encounters SET species = $m WHERE species = $legacyM;";

            rename.Parameters.AddWithValue("$f", "Nidoran F");
            rename.Parameters.AddWithValue("$legacyF", "Nidoran\u2640");
            rename.Parameters.AddWithValue("$m", "Nidoran M");
            rename.Parameters.AddWithValue("$legacyM", "Nidoran\u2642");

            int changed = rename.ExecuteNonQuery();

            if (changed > 0)
                Log.Information("Encounter log: {Count} Nidoran rows renamed to the F/M spelling.", changed);
        }

        private static void AddMissingColumns(SqliteConnection open)
        {
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                using (SqliteCommand info = open.CreateCommand())
                {
                    info.CommandText = "PRAGMA table_info(encounters);";

                    using SqliteDataReader reader = info.ExecuteReader();

                    while (reader.Read())
                        present.Add(reader.GetString(1));   // 1 = column name
                }

                foreach ((string name, string type) in
                         new[] { ("gender", "TEXT"), ("rare", "TEXT") })
                {
                    if (present.Contains(name))
                        continue;

                    using SqliteCommand alter = open.CreateCommand();

                    // The column names are literals from the array above, not
                    // anything a caller supplies, so there is nothing here to
                    // parameterize - ALTER TABLE takes no parameters anyway.
                    alter.CommandText =
                        $"ALTER TABLE encounters ADD COLUMN {name} {type};";

                    alter.ExecuteNonQuery();

                    Log.Information(
                        "Encounter log migrated: added column {Column}", name);
                }
            }
            catch (Exception ex)
            {
                // A log that cannot be migrated is still readable for
                // everything it held before, so this does not go dormant -
                // but the new columns will not resolve, so say so once.
                Log.Warning(ex, "Encounter log schema migration failed");
            }
        }

        /// <summary>
        /// Records that an encounter happened, WITHOUT touching the disk. The
        /// previous encounter - now final, since refinements never cross an
        /// encounter boundary - is written on a background task.
        /// </summary>
        public static void Append(string species, int? level, string? location)
        {
            if (dormant || string.IsNullOrWhiteSpace(species))
                return;

            int client = SessionPersistenceService.ActiveClientNumber;

            // No bound profile means no per-client store writes anything -
            // same rule the session, catch log, PVP and cooldown stores follow.
            if (client < 1)
                return;

            FlushPending();

            pendingClient = client;
            pendingWhenUtc = DateTime.UtcNow;
            pendingSpecies = species;
            pendingLevel = level;
            pendingLocation = string.IsNullOrWhiteSpace(location) ? "Unknown" : location;

            // Cleared per encounter, not carried forward: a Pokemon with no
            // gender reading must not inherit the previous one's.
            pendingGender = null;
            pendingRare = null;
        }

        /// <summary>Late level refinement (§96) for the encounter on screen.
        /// Free: the row has not been written yet.</summary>
        public static void RefineCurrentLevel(int level)
        {
            if (pendingSpecies is not null)
                pendingLevel = level;
        }

        /// <summary>Late location refinement (§102) for the encounter on
        /// screen. Free, for the same reason.</summary>
        public static void RefineCurrentLocation(string location)
        {
            if (pendingSpecies is not null && !string.IsNullOrWhiteSpace(location))
                pendingLocation = location;
        }

        /// <summary>§124. Gender for the encounter on screen. Free, for the
        /// same reason as the two refinements above - the row has not been
        /// written yet.</summary>
        public static void RefineCurrentGender(string? gender)
        {
            if (pendingSpecies is not null && !string.IsNullOrWhiteSpace(gender))
                pendingGender = gender;
        }

        /// <summary>§124. Shiny/Form for the encounter on screen. "None"
        /// is treated as no reading rather than stored, so an ordinary
        /// encounter leaves the column null and the display blank.</summary>
        public static void RefineCurrentRareType(string? rareType)
        {
            if (pendingSpecies is not null &&
                !string.IsNullOrWhiteSpace(rareType) &&
                rareType != "None")
            {
                pendingRare = rareType;
            }
        }

        /// <summary>Writes the encounter still held in memory, if any. Called
        /// when the next encounter arrives, and by the session store's own
        /// flush points (stop, reset, client switch, app exit) so the last
        /// encounter of a hunt is not left unwritten.</summary>
        public static void FlushPending()
        {
            if (pendingSpecies is null)
                return;

            int client = pendingClient;
            DateTime whenUtc = pendingWhenUtc;
            string species = pendingSpecies;
            int? level = pendingLevel;
            string location = pendingLocation;
            string? gender = pendingGender;
            string? rare = pendingRare;

            pendingSpecies = null;
            pendingLevel = null;
            pendingLocation = "Unknown";
            pendingGender = null;
            pendingRare = null;

            Task.Run(() => Insert(client, whenUtc, species, level, location, gender, rare));
        }

        /// <summary>Drops the unwritten encounter without recording it - used
        /// where the session itself is being discarded rather than ended.</summary>
        public static void ForgetPending()
        {
            pendingSpecies = null;
            pendingLevel = null;
            pendingLocation = "Unknown";
        }

        private static void Insert(
            int client, DateTime whenUtc, string species, int? level, string location,
            string? gender, string? rare)
        {
            lock (gate)
            {
                SqliteConnection? open = EnsureOpen();

                if (open is null)
                    return;

                try
                {
                    using SqliteCommand command = open.CreateCommand();

                    command.CommandText =
                        "INSERT INTO encounters (client, utc, species, level, location, gender, rare) " +
                        "VALUES ($client, $utc, $species, $level, $location, $gender, $rare);";

                    command.Parameters.AddWithValue("$client", client);
                    command.Parameters.AddWithValue("$utc", whenUtc.ToString("O"));
                    command.Parameters.AddWithValue("$species", species);
                    command.Parameters.AddWithValue("$level", (object?)level ?? DBNull.Value);
                    command.Parameters.AddWithValue("$location", location);
                    command.Parameters.AddWithValue("$gender", (object?)gender ?? DBNull.Value);
                    command.Parameters.AddWithValue("$rare", (object?)rare ?? DBNull.Value);

                    command.ExecuteNonQuery();
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Encounter log write failed - the log is now off for this run");
                    dormant = true;
                }
            }
        }

        /// <summary>
        /// One page of the All time view, newest first. <paramref
        /// name="beforeId"/> is the id of the last row already shown - keyset
        /// paging, so page five hundred costs what page one costs. Null for
        /// the first page. An unavailable log returns an empty list rather
        /// than throwing; the caller distinguishes the two through
        /// IsAvailable.
        /// </summary>
        public static IReadOnlyList<EncounterLogRow> Query(
            string species, int client, long? beforeId, int limit)
        {
            var results = new List<EncounterLogRow>();

            if (string.IsNullOrWhiteSpace(species))
                return results;

            lock (gate)
            {
                SqliteConnection? open = EnsureOpen();

                if (open is null)
                    return results;

                try
                {
                    using SqliteCommand command = open.CreateCommand();

                    command.CommandText =
                        "SELECT id, utc, species, level, location, gender, rare FROM encounters " +
                        "WHERE species = $species AND client = $client " +
                        (beforeId is null ? string.Empty : "AND id < $beforeId ") +
                        "ORDER BY id DESC LIMIT $limit;";

                    command.Parameters.AddWithValue("$species", species);
                    command.Parameters.AddWithValue("$client", client);
                    command.Parameters.AddWithValue("$limit", limit);

                    if (beforeId is long before)
                        command.Parameters.AddWithValue("$beforeId", before);

                    using SqliteDataReader reader = command.ExecuteReader();

                    while (reader.Read())
                        results.Add(ReadRow(reader));
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Encounter log read failed");
                }
            }

            return results;
        }

        /// <summary>§273. The seven selected columns, in the one order both
        /// readers ask for them. Was inline in Query; lifted out when
        /// QueryRank arrived, because two copies of a column-index mapping is
        /// how one of them ends up off by one.</summary>
        private static EncounterLogRow ReadRow(SqliteDataReader reader)
        {
            DateTime whenUtc = DateTime.TryParse(
                reader.GetString(1),
                null,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out DateTime parsed)
                ? parsed
                : DateTime.UtcNow;

            return new EncounterLogRow(
                reader.GetInt64(0),
                whenUtc,
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3),
                reader.GetString(4),
                // Null on every row written before §124, and on any encounter
                // whose reading never resolved.
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6));
        }

        /// <summary>
        /// §273. How a row ranks when the history is sorted rare-first: forms
        /// before shinies before everything else, which is the order the
        /// Shiny/Form header asks for.
        ///
        /// Written once, as SQL, because it is used in two places that must
        /// agree - the filter that selects one rank and the ORDER BY inside
        /// it - and because it has to match RareEncounterType's own spelling.
        /// The column holds "Shiny", "Form", or "&lt;Event&gt; Form" (see
        /// MainWindowViewModel's two RefineCurrentRareType calls), so "not
        /// shiny and not empty" is what makes a form, rather than a list of
        /// event names this file would have to be told about.
        /// </summary>
        private const string RareRankExpression =
            "CASE WHEN rare IS NULL OR rare = '' OR rare = 'None' THEN 2 " +
            "     WHEN rare = 'Shiny' THEN 1 " +
            "     ELSE 0 END";

        /// <summary>Ranks, named. Anything outside 0-2 is not a rank.</summary>
        public const int RankForm = 0;
        public const int RankShiny = 1;
        public const int RankOrdinary = 2;

        /// <summary>§273. One page of the rare-first order, and where the next
        /// one starts. <see cref="NextRank"/> past <see cref="RankOrdinary"/>
        /// means the log is exhausted.</summary>
        public sealed record RankedPage(
            IReadOnlyList<EncounterLogRow> Rows,
            int NextRank,
            long? NextBeforeId);

        /// <summary>
        /// §273. One page of the log with forms first, then shinies, then the
        /// rest - each group newest first.
        ///
        /// NOT one query with an ORDER BY over the computed rank. That would
        /// make SQLite materialise and sort every row of the species on every
        /// page turn, which is exactly the cost §112 built keyset paging to
        /// avoid - and the reason this is worth doing carefully is that an
        /// event can mark THOUSANDS of encounters as a form, so "the rare rows
        /// are few, just load them all" is not true either.
        ///
        /// Instead each rank is paged on its own, in rank order, by the same
        /// keyset the ordinary view uses: ask rank 0 for rows older than the
        /// cursor, and when that rank runs dry inside a page, carry on into
        /// rank 1 from its top. Every individual query is the indexed
        /// "species, client, id DESC" lookup with a LIMIT, so a page costs
        /// what a page has always cost however deep it is.
        ///
        /// The cursor is therefore a PAIR - which rank, and which id within it
        /// - and the caller keeps it the same way it already keeps the plain
        /// one (§128's stack).
        /// </summary>
        public static RankedPage QueryRareFirst(
            string species, int client, int startRank, long? beforeId, int limit)
        {
            var results = new List<EncounterLogRow>();

            int rank = Math.Clamp(startRank, RankForm, RankOrdinary);
            long? cursor = beforeId;

            if (string.IsNullOrWhiteSpace(species) || limit <= 0)
                return new RankedPage(results, rank, cursor);

            while (rank <= RankOrdinary && results.Count < limit)
            {
                IReadOnlyList<EncounterLogRow> slice =
                    QueryRank(species, client, rank, cursor, limit - results.Count);

                results.AddRange(slice);

                if (results.Count >= limit)
                {
                    // Full page. The next one continues inside whichever rank
                    // the last row belongs to, after that row.
                    EncounterLogRow last = results[results.Count - 1];

                    return new RankedPage(results, RankOf(last), last.Id);
                }

                // That rank is exhausted - the next starts at the top of the
                // next one, which is why the cursor is dropped here rather
                // than carried across.
                rank++;
                cursor = null;
            }

            return new RankedPage(results, rank, null);
        }

        /// <summary>The same rank rule as <see cref="RareRankExpression"/>,
        /// in C#, so a row already read can be placed without asking the
        /// database again. The two are one rule in two languages and have to
        /// keep saying the same thing.</summary>
        public static int RankOf(EncounterLogRow row) =>
            string.IsNullOrWhiteSpace(row.RareType) || row.RareType == "None"
                ? RankOrdinary
                : row.RareType == "Shiny"
                    ? RankShiny
                    : RankForm;

        /// <summary>One rank's own keyset page - the ordinary query with the
        /// rank filter added, so the index still drives it.</summary>
        private static IReadOnlyList<EncounterLogRow> QueryRank(
            string species, int client, int rank, long? beforeId, int limit)
        {
            var results = new List<EncounterLogRow>();

            lock (gate)
            {
                SqliteConnection? open = EnsureOpen();

                if (open is null)
                    return results;

                try
                {
                    using SqliteCommand command = open.CreateCommand();

                    command.CommandText =
                        "SELECT id, utc, species, level, location, gender, rare FROM encounters " +
                        "WHERE species = $species AND client = $client " +
                        $"AND {RareRankExpression} = $rank " +
                        (beforeId is null ? string.Empty : "AND id < $beforeId ") +
                        "ORDER BY id DESC LIMIT $limit;";

                    command.Parameters.AddWithValue("$species", species);
                    command.Parameters.AddWithValue("$client", client);
                    command.Parameters.AddWithValue("$rank", rank);
                    command.Parameters.AddWithValue("$limit", limit);

                    if (beforeId is long before)
                        command.Parameters.AddWithValue("$beforeId", before);

                    using SqliteDataReader reader = command.ExecuteReader();

                    while (reader.Read())
                        results.Add(ReadRow(reader));
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Encounter log rare-first read failed");
                }
            }

            return results;
        }

        /// <summary>Lifetime count for one species on one profile. Answered
        /// from the index alone - measured at 11 ms over half a million
        /// rows.</summary>
        public static long CountFor(string species, int client)
        {
            if (string.IsNullOrWhiteSpace(species))
                return 0;

            lock (gate)
            {
                SqliteConnection? open = EnsureOpen();

                if (open is null)
                    return 0;

                try
                {
                    using SqliteCommand command = open.CreateCommand();

                    command.CommandText =
                        "SELECT COUNT(*) FROM encounters WHERE species = $species AND client = $client;";

                    command.Parameters.AddWithValue("$species", species);
                    command.Parameters.AddWithValue("$client", client);

                    object? scalar = command.ExecuteScalar();

                    return scalar is long count ? count : 0;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Encounter log count failed");
                    return 0;
                }
            }
        }
    }
}
