using System.Globalization;
using System.Text.Json;
using Serilog;

namespace Foot_Tracker.Services
{
    /// <summary>§233. One World Quest as the Worker parsed it out of PRO's
    /// own Discord announcement - see Backend/EventsWorker/worker.js §232 and
    /// GET /v1/world-quests.
    ///
    /// EndsUtc is the Worker's own arithmetic (start plus the announced
    /// duration), NOT the date printed in the announcement: the announcement
    /// repeats a stale date - the September post still read "Sunday, August
    /// 16" - so EndTimeText is kept only to show what the post said, and every
    /// countdown runs off EndsUtc.
    ///
    /// §298. EndedUtc is set when the admin declared the quest over because
    /// both servers had met the goal - the half of "24 hours OR the goal,
    /// whichever comes first" that nothing on the wire can see. It is null
    /// for every quest nobody has ended. The Worker also reports EndsUtc as
    /// the EARLIER of the two, so a countdown that knows nothing about
    /// EndedUtc still stops; EndedUtc is what lets the panel say the goal was
    /// met rather than that the day simply ran out.</summary>
    public sealed record WorldQuest(
        string MessageId,
        string Pokemon,
        int TotalIvs,
        int SingleIvs,
        int AverageSubmissions,
        string LowestTier,
        string Reward,
        string Duration,
        string EndTimeText,
        DateTime StartedUtc,
        DateTime? EndsUtc,
        DateTime? EndedUtc,
        bool Parsed);

    /// <summary>§233. One catch counted toward the player's own total.</summary>
    public sealed record WorldQuestSubmission(
        int Total,
        string Species,
        DateTime AtUtc,
        bool Automatic);

    /// <summary>§233. The player's own running contribution to one quest.</summary>
    public sealed record WorldQuestProgress(
        string QuestId,
        IReadOnlyList<WorldQuestSubmission> Submissions)
    {
        public int Collected
        {
            get
            {
                int total = 0;

                foreach (WorldQuestSubmission submission in Submissions)
                    total += submission.Total;

                return total;
            }
        }

        public int Count => Submissions.Count;
    }

    /// <summary>
    /// §233. The World Quest window's data: what quest is running (from the
    /// events server) and how much the player has personally submitted to it
    /// (from this machine, and only this machine).
    ///
    /// WHY THE PROGRESS NEVER LEAVES THE MACHINE. The tracker already knows how
    /// to send things to the events server, so storing a contribution total
    /// there would have been the shorter road. It is not taken: a per-player
    /// running total of what someone caught, when, is exactly the kind of
    /// hunting history the observer is forbidden to keep (see the Worker's own
    /// remarks and MIGRATION_GUIDE.md §150). The quest itself is a public
    /// notice board and is fetched; what the player did about it is theirs and
    /// stays in their own data folder.
    ///
    /// The file is keyed by the quest's Discord message id, so a new quest
    /// starts a new count without erasing the last one - a player who reopens
    /// the window after a quest ends still sees what they did.
    /// </summary>
    public static class WorldQuestService
    {
        /// <summary>Beside the install token and the backend override, in the
        /// tracker's own data folder - not in Documents, not beside the
        /// executable.</summary>
        private static string ProgressPath =>
            Path.Combine(EventsSyncService.LocalDataFolder, "world-quest-progress.json");

        /// <summary>How many quests' worth of progress is kept. Quests run
        /// about monthly, so this is roughly a year of history in a file of a
        /// few kilobytes.</summary>
        private const int KeepQuests = 12;

        /// <summary>An IV total is six IVs of 0-31.</summary>
        public const int MaxSubmissionTotal = 186;

        private static readonly JsonSerializerOptions Json =
            new(JsonSerializerDefaults.Web) { WriteIndented = true };

        private static readonly object Gate = new();

        // ------------------------------------------------------------ quest

        /// <summary>The quest running right now, or null when there is none
        /// (which is the normal state - quests run about once a month). Throws
        /// EventsSyncException when the server cannot be reached, which the
        /// caller shows on the status line.</summary>
        public static async Task<WorldQuest?> FetchActiveAsync(CancellationToken cancellationToken = default) =>
            (await FetchActiveAndAsync(null, cancellationToken)).Active;

        /// <summary>§298. The running quest AND one named quest, from a single
        /// fetch. The main window needs both on the same poll: which quest is
        /// running decides the menu colour, and what has become of the quest
        /// the player is actually IN decides whether their countdown should
        /// still be running - a quest someone ended is over for them too, and
        /// they are the person it matters most to. One call rather than two,
        /// so the two answers cannot come from different minutes.
        ///
        /// <paramref name="messageId"/> null, or a quest the server no longer
        /// lists, gives Named null. Active is null when nothing is running.</summary>
        public static async Task<(WorldQuest? Active, WorldQuest? Named)> FetchActiveAndAsync(
            string? messageId, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<WorldQuest> quests = await EventsSyncService.FetchWorldQuestsAsync(cancellationToken);

            DateTime now = DateTime.UtcNow;

            WorldQuest? active = null;
            WorldQuest? named = null;

            foreach (WorldQuest quest in quests)
            {
                // §298: a quest that was ended is not running, whatever its
                // derived end time says. The Worker already reports EndsUtc as
                // the earlier of the two, so the date test alone would do it;
                // the rule is written out anyway, here as in the Worker,
                // because it IS the rule rather than a consequence of one.
                if (active is null
                    && quest.Parsed
                    && quest.EndedUtc is null
                    && quest.EndsUtc is not null
                    && quest.EndsUtc > now
                    && quest.StartedUtc <= now)
                {
                    active = quest;
                }

                if (named is null
                    && messageId is not null
                    && string.Equals(quest.MessageId, messageId, StringComparison.Ordinal))
                {
                    named = quest;
                }
            }

            return (active, named);
        }

        /// <summary>§251. The quest with this id, running or not, or null when
        /// the server no longer lists it. World Quest mode is entered for ONE
        /// quest and stays on it across a restart (WorldQuestMode's marker);
        /// the figures shown for it after that restart must be that quest's,
        /// not whichever quest happens to be running by then. Same exception
        /// as FetchActiveAsync when the server cannot be reached.</summary>
        public static async Task<WorldQuest?> FetchAsync(string messageId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(messageId))
                return null;

            IReadOnlyList<WorldQuest> quests = await EventsSyncService.FetchWorldQuestsAsync(cancellationToken);

            foreach (WorldQuest quest in quests)
            {
                if (string.Equals(quest.MessageId, messageId, StringComparison.Ordinal))
                    return quest;
            }

            return null;
        }

        // --------------------------------------------------------- progress

        public static WorldQuestProgress Load(string questId)
        {
            if (string.IsNullOrWhiteSpace(questId))
                return new WorldQuestProgress(string.Empty, Array.Empty<WorldQuestSubmission>());

            lock (Gate)
            {
                Dictionary<string, List<StoredSubmission>> all = ReadFile();

                if (!all.TryGetValue(questId, out List<StoredSubmission>? stored))
                    return new WorldQuestProgress(questId, Array.Empty<WorldQuestSubmission>());

                var submissions = new List<WorldQuestSubmission>(stored.Count);

                foreach (StoredSubmission entry in stored)
                {
                    submissions.Add(new WorldQuestSubmission(
                        entry.Total,
                        entry.Species ?? string.Empty,
                        ParseUtc(entry.AtUtc),
                        entry.Automatic));
                }

                return new WorldQuestProgress(questId, submissions);
            }
        }

        /// <summary>Adds one catch and returns the progress including it. A
        /// total outside 0-186 is refused rather than stored, so a bad manual
        /// entry cannot corrupt the count.</summary>
        public static WorldQuestProgress Add(string questId, int total, string species, bool automatic)
        {
            if (string.IsNullOrWhiteSpace(questId))
                throw new ArgumentException("A World Quest id is required.", nameof(questId));

            if (total < 0 || total > MaxSubmissionTotal)
                throw new ArgumentOutOfRangeException(nameof(total), total, "An IV total is 0 to 186.");

            lock (Gate)
            {
                Dictionary<string, List<StoredSubmission>> all = ReadFile();

                if (!all.TryGetValue(questId, out List<StoredSubmission>? stored))
                {
                    stored = new List<StoredSubmission>();
                    all[questId] = stored;
                }

                stored.Add(new StoredSubmission
                {
                    Total = total,
                    Species = species ?? string.Empty,
                    AtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    Automatic = automatic,
                });

                WriteFile(all);

                return Load(questId);
            }
        }

        /// <summary>§254. Removes one specific catch - the first stored entry
        /// whose time and total both match - and returns the progress without
        /// it, with whether anything was actually removed. Matched on the two
        /// values rather than on a position in the list, because Auto Detect
        /// can count a catch between the list being shown and the click,
        /// which moves every position by one; the time is stored to a tenth
        /// of a microsecond, so two catches sharing it AND a total are the
        /// same catch for every purpose here. Nothing is written when nothing
        /// matched.</summary>
        public static (WorldQuestProgress Progress, bool Removed) Remove(string questId, DateTime atUtc, int total)
        {
            if (string.IsNullOrWhiteSpace(questId))
                return (new WorldQuestProgress(string.Empty, Array.Empty<WorldQuestSubmission>()), false);

            lock (Gate)
            {
                Dictionary<string, List<StoredSubmission>> all = ReadFile();
                bool removed = false;

                if (all.TryGetValue(questId, out List<StoredSubmission>? stored))
                {
                    for (int i = 0; i < stored.Count; i++)
                    {
                        if (stored[i].Total != total || ParseUtc(stored[i].AtUtc) != atUtc)
                            continue;

                        stored.RemoveAt(i);
                        removed = true;
                        break;
                    }

                    if (removed)
                        WriteFile(all);
                }

                return (Load(questId), removed);
            }
        }

        // -------------------------------------------------------------- file

        private static Dictionary<string, List<StoredSubmission>> ReadFile()
        {
            try
            {
                if (!File.Exists(ProgressPath))
                    return new Dictionary<string, List<StoredSubmission>>(StringComparer.Ordinal);

                string text = File.ReadAllText(ProgressPath);

                StoredFile? file = JsonSerializer.Deserialize<StoredFile>(text, Json);

                var all = new Dictionary<string, List<StoredSubmission>>(StringComparer.Ordinal);

                foreach (StoredQuest quest in file?.Quests ?? new List<StoredQuest>())
                {
                    if (!string.IsNullOrWhiteSpace(quest.QuestId))
                        all[quest.QuestId] = quest.Submissions ?? new List<StoredSubmission>();
                }

                return all;
            }
            catch (Exception ex)
            {
                // A progress file that cannot be read must not stop the window
                // opening - the player can still submit by hand, and the next
                // save replaces the unreadable file.
                Log.Warning(ex, "World Quest: the local progress file could not be read - starting from an empty count.");
                return new Dictionary<string, List<StoredSubmission>>(StringComparer.Ordinal);
            }
        }

        private static void WriteFile(Dictionary<string, List<StoredSubmission>> all)
        {
            try
            {
                var quests = new List<StoredQuest>();

                foreach (KeyValuePair<string, List<StoredSubmission>> pair in all)
                    quests.Add(new StoredQuest { QuestId = pair.Key, Submissions = pair.Value });

                // Newest last-touched first, then trimmed - a quest with no
                // submissions left is dropped entirely rather than kept as an
                // empty row.
                quests.RemoveAll(quest => quest.Submissions is null || quest.Submissions.Count == 0);
                quests.Sort(static (a, b) => string.CompareOrdinal(
                    b.Submissions![^1].AtUtc, a.Submissions![^1].AtUtc));

                if (quests.Count > KeepQuests)
                    quests.RemoveRange(KeepQuests, quests.Count - KeepQuests);

                DurableFile.WriteAllText(
                    ProgressPath,
                    JsonSerializer.Serialize(new StoredFile { Quests = quests }, Json));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "World Quest: the local progress file could not be saved.");
            }
        }

        private static DateTime ParseUtc(string? iso) =>
            DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset value)
                ? value.UtcDateTime
                : DateTime.UtcNow;

        private sealed class StoredFile
        {
            public List<StoredQuest> Quests { get; set; } = new();
        }

        private sealed class StoredQuest
        {
            public string QuestId { get; set; } = string.Empty;
            public List<StoredSubmission>? Submissions { get; set; }
        }

        private sealed class StoredSubmission
        {
            public int Total { get; set; }
            public string? Species { get; set; }
            public string? AtUtc { get; set; }
            public bool Automatic { get; set; }
        }
    }
}
