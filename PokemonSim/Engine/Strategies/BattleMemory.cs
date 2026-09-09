using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using PokemonSim.Models;

namespace PokemonSim.Engine.Strategies
{
    /// <summary>
    /// Section 183. What the opponent remembers between battles.
    ///
    /// Everything before this section decided each battle from nothing. Two
    /// identical positions a week apart produced identical play, which is
    /// the one thing a human opponent never does - and against a fixed boss
    /// team a player learns the script in half a dozen attempts.
    ///
    /// So the brain now keeps a small book on each MATCHUP - its active
    /// species against yours - holding, per action, how often it has taken
    /// that action there and how often the battle went its way afterwards.
    /// Two things come out of that book at choice time. It shades DOWN what
    /// it has been repeating, so a line used eight times in ten gives way to
    /// its second choice; and it shades TOWARD what has actually been
    /// winning, weighted low, because one win-or-lose bit spread across
    /// forty decisions is a noisy teacher and a heavy hand there buys
    /// superstition rather than skill.
    ///
    /// What it stores is deliberately thin: two species names, an action
    /// index, and two counts. No teams, no items, no turn order, no times,
    /// nothing that identifies a player and nothing that could reconstruct a
    /// battle, a session or a hunt. It is a book on positions, not on people.
    ///
    /// It is also strictly a PLAY-TIME feature and never runs while the
    /// Battle Lab is collecting. The observation vector has no feature for
    /// any of this, so a teacher whose choices depended on it would be
    /// labelling states with an answer the student cannot see, and no amount
    /// of training would recover the missing reason. LabBattleRunner
    /// therefore never builds one.
    /// </summary>
    public sealed class BattleMemory
    {
        /// <summary>Counts are halved once a matchup has this many entries,
        /// which is what makes the book about RECENT battles rather than
        /// all of them. Twenty is the "last twenty games" the feature was
        /// asked for; halving rather than dropping keeps the shape of what
        /// came before instead of forgetting it on a cliff edge.</summary>
        public const double RecencyWindow = 20;

        /// <summary>How hard a repeated action is shaded down, on the same
        /// scale as Monte Carlo's own switch penalty (0.08) - enough to
        /// break a tie or a near-tie, never enough to make it play a move
        /// it thinks is bad.</summary>
        public const double NoveltyWeight = 0.09;

        /// <summary>How hard a winning action is shaded up. Deliberately
        /// smaller: the outcome of one battle is a single bit, and every
        /// decision in it gets the same credit whether it was the reason or
        /// merely present.</summary>
        public const double OutcomeWeight = 0.05;

        /// <summary>Uses before the outcome term is trusted at full weight.
        /// Below it the term is damped, so one lucky battle does not become
        /// a conviction.</summary>
        public const double ConfidenceUses = 6;

        /// <summary>A matchup is dropped once the book holds this many, so
        /// a long-lived file cannot grow without bound. The least used go
        /// first.</summary>
        public const int MaxSituations = 4000;

        public const int FileVersion = 1;

        readonly object gate = new();
        readonly Dictionary<string, Dictionary<int, Entry>> book = new(StringComparer.Ordinal);

        sealed class Entry
        {
            public double Uses;
            public double Wins;
        }

        /// <summary>How many matchups the book holds - for the panel, and
        /// for a test to see that clearing worked.</summary>
        public int SituationCount
        {
            get { lock (gate) return book.Count; }
        }

        /// <summary>
        /// The matchup key: who is standing where. Species only, folded to
        /// lower case so a data-file rename of casing does not split the
        /// book in two.
        /// </summary>
        public static string Situation(PokemonState self, PokemonState opponent) =>
            (self?.Species ?? "?").ToLowerInvariant() + "|" +
            (opponent?.Species ?? "?").ToLowerInvariant();

        /// <summary>Section 183. The key for "who comes in against this".
        /// Only the Pokemon standing opposite matters: what just fainted is
        /// gone, and it is not what the choice is about.</summary>
        public static string ReplacementSituation(PokemonState opponent) =>
            "replace|" + (opponent?.Species ?? "?").ToLowerInvariant();

        /// <summary>
        /// What to add to an action's score in this matchup. Zero when the
        /// book has never seen it, which is every matchup on a fresh
        /// install - the feature costs nothing until it has something to
        /// say.
        /// </summary>
        public double Adjust(string situation, int action, int legalCount)
        {
            if (action < 0 || legalCount <= 1)
                return 0;

            lock (gate)
            {
                if (!book.TryGetValue(situation, out Dictionary<int, Entry>? entries))
                    return 0;

                double total = entries.Values.Sum(e => e.Uses);

                if (total <= 0)
                    return 0;

                entries.TryGetValue(action, out Entry? entry);

                double uses = entry?.Uses ?? 0;

                // Novelty is measured against an even spread rather than
                // against zero, so an action used its fair share is neither
                // rewarded nor punished and only genuine repetition moves.
                double share = uses / total;
                double even = 1.0 / legalCount;
                double novelty = -NoveltyWeight * (share - even);

                double outcome = 0;

                if (uses > 0 && entry != null)
                {
                    double winRate = entry.Wins / uses;
                    double confidence = uses / (uses + ConfidenceUses);

                    outcome = OutcomeWeight * (winRate - 0.5) * 2 * confidence;
                }

                return novelty + outcome;
            }
        }

        /// <summary>Start a battle's worth of notes. The recall is used by
        /// one battle on one thread and folded back in when that battle
        /// ends; nothing reaches the shared book until then, so a battle
        /// never learns from itself mid-way.</summary>
        public BattleRecall Begin() => new BattleRecall(this);

        internal void Fold(List<(string Situation, int Action)> notes, bool won)
        {
            if (notes.Count == 0)
                return;

            lock (gate)
            {
                foreach ((string situation, int action) in notes)
                {
                    if (!book.TryGetValue(situation, out Dictionary<int, Entry>? entries))
                    {
                        entries = new Dictionary<int, Entry>();
                        book[situation] = entries;
                    }

                    if (!entries.TryGetValue(action, out Entry? entry))
                    {
                        entry = new Entry();
                        entries[action] = entry;
                    }

                    entry.Uses += 1;

                    if (won)
                        entry.Wins += 1;
                }

                foreach (Dictionary<int, Entry> entries in book.Values)
                    Decay(entries);

                Trim();
            }
        }

        /// <summary>Halve a matchup once it is full, which turns the counts
        /// into a rolling average over roughly the last RecencyWindow
        /// battles rather than a tally since installation.</summary>
        static void Decay(Dictionary<int, Entry> entries)
        {
            double total = entries.Values.Sum(e => e.Uses);

            if (total <= RecencyWindow)
                return;

            foreach (Entry entry in entries.Values)
            {
                entry.Uses /= 2;
                entry.Wins /= 2;
            }
        }

        void Trim()
        {
            if (book.Count <= MaxSituations)
                return;

            foreach (string key in book
                .OrderBy(pair => pair.Value.Values.Sum(e => e.Uses))
                .Take(book.Count - MaxSituations)
                .Select(pair => pair.Key)
                .ToList())
            {
                book.Remove(key);
            }
        }

        public void Clear()
        {
            lock (gate) book.Clear();
        }

        // ---- persistence -------------------------------------------------

        /// <summary>
        /// The whole book as one small json object: version, then matchup
        /// to action to "uses,wins". Written by the caller that knows where
        /// application data belongs - this class is given a path and has no
        /// opinion about it, which is what keeps the engine free of the
        /// tracker.
        /// </summary>
        public string ToJson()
        {
            lock (gate)
            {
                var rows = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

                foreach ((string situation, Dictionary<int, Entry> entries) in book)
                {
                    var actions = new Dictionary<string, string>(StringComparer.Ordinal);

                    foreach ((int action, Entry entry) in entries)
                    {
                        if (entry.Uses <= 0)
                            continue;

                        actions[action.ToString(CultureInfo.InvariantCulture)] =
                            entry.Uses.ToString("0.###", CultureInfo.InvariantCulture) + "," +
                            entry.Wins.ToString("0.###", CultureInfo.InvariantCulture);
                    }

                    if (actions.Count > 0)
                        rows[situation] = actions;
                }

                return JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["version"] = FileVersion,
                    ["matchups"] = rows
                });
            }
        }

        /// <summary>Reads a book back. A file that is missing, empty,
        /// truncated, from a future version or simply nonsense yields an
        /// EMPTY memory rather than an exception - the worst this feature
        /// may ever do to a battle is have nothing to say.</summary>
        public static BattleMemory FromJson(string? json)
        {
            var memory = new BattleMemory();

            if (string.IsNullOrWhiteSpace(json))
                return memory;

            try
            {
                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement root = document.RootElement;

                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("version", out JsonElement version) ||
                    version.GetInt32() != FileVersion ||
                    !root.TryGetProperty("matchups", out JsonElement matchups) ||
                    matchups.ValueKind != JsonValueKind.Object)
                {
                    return memory;
                }

                foreach (JsonProperty situation in matchups.EnumerateObject())
                {
                    if (situation.Value.ValueKind != JsonValueKind.Object)
                        continue;

                    var entries = new Dictionary<int, Entry>();

                    foreach (JsonProperty action in situation.Value.EnumerateObject())
                    {
                        if (!int.TryParse(action.Name, NumberStyles.Integer,
                                          CultureInfo.InvariantCulture, out int index))
                        {
                            continue;
                        }

                        string[] parts = (action.Value.GetString() ?? "").Split(',');

                        if (parts.Length != 2 ||
                            !double.TryParse(parts[0], NumberStyles.Float,
                                             CultureInfo.InvariantCulture, out double uses) ||
                            !double.TryParse(parts[1], NumberStyles.Float,
                                             CultureInfo.InvariantCulture, out double wins))
                        {
                            continue;
                        }

                        if (!double.IsFinite(uses) || !double.IsFinite(wins) || uses <= 0)
                            continue;

                        entries[index] = new Entry
                        {
                            Uses = uses,
                            Wins = Math.Clamp(wins, 0, uses)
                        };
                    }

                    if (entries.Count > 0)
                        memory.book[situation.Name] = entries;
                }

                memory.Trim();
            }
            catch (Exception)
            {
                return new BattleMemory();
            }

            return memory;
        }
    }

    /// <summary>
    /// Section 183. One battle's notes, held aside until the battle ends.
    ///
    /// Nothing is written to the shared book while a battle is in progress.
    /// That is not tidiness: a brain that learned from its own choices
    /// mid-battle would start shading against a move the moment it used it,
    /// which is a different and much worse feature than the one asked for.
    /// </summary>
    public sealed class BattleRecall
    {
        readonly BattleMemory memory;
        readonly List<(string Situation, int Action)> notes = new();

        internal BattleRecall(BattleMemory memory) => this.memory = memory;

        public int NoteCount => notes.Count;
        public bool Settled { get; private set; }

        public double Adjust(string situation, int action, int legalCount) =>
            memory.Adjust(situation, action, legalCount);

        public void Note(string situation, int action)
        {
            if (!Settled && action >= 0)
                notes.Add((situation, action));
        }

        /// <summary>Fold this battle into the book. Called once, by whoever
        /// owns the battle - a battle abandoned without this simply teaches
        /// nothing, which is the right outcome for one nobody finished.
        /// </summary>
        public void Settle(bool won)
        {
            if (Settled)
                return;

            Settled = true;
            memory.Fold(notes, won);
            notes.Clear();
        }
    }
}