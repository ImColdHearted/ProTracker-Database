using Foot_Tracker.Services;

namespace Foot_Tracker.Models
{
    public class HuntSession
    {
        // Kept only so an old saved session (single-target) still loads correctly -
        // see Restore() below. New code should use TargetPokemons instead.
        [Obsolete("Use TargetPokemons instead - kept only for old-save migration.")]
        public string TargetPokemon
        {
            get => TargetPokemons.Count > 0 ? TargetPokemons[0] : string.Empty;
            set
            {
                TargetPokemons.Clear();
                if (!string.IsNullOrWhiteSpace(value))
                    TargetPokemons.Add(value);
            }
        }

        // Up to 4 simultaneous targets - enforced by the caller (MainWindowViewModel/
        // PokemonSelectorViewModel), not this list itself.
        public List<string> TargetPokemons { get; set; } = new();

        public string CurrentEncounter { get; set; } = string.Empty;

        public string PreviousEncounter { get; set; } = string.Empty;

        // §139. What the Current/Previous Encounter cards show beyond the
        // species name: empty for an ordinary encounter, ShinyForm once the
        // shiny check confirmed one, UnidentifiedForm while a special form
        // is known but not yet named, otherwise the event CounterpartMatcher
        // named ("Summer", "Pinkan", ...). The image is the catalog-relative
        // counterpart file for that event, or empty when there is none to
        // show. Both move Current -> Previous together with the names
        // (MainWindowViewModel.RegisterEncounter), so the rare form is still
        // on the Previous card when the next encounter is an ordinary one -
        // which is exactly the moment a hunter wants to see what they had.
        public const string ShinyForm = "Shiny";
        public const string UnidentifiedForm = "Form";

        public string CurrentEncounterForm { get; set; } = string.Empty;

        public string CurrentEncounterFormImage { get; set; } = string.Empty;

        public string PreviousEncounterForm { get; set; } = string.Empty;

        public string PreviousEncounterFormImage { get; set; } = string.Empty;

        public int TotalEncounters { get; set; }

        public int EncountersSinceShiny { get; set; }

        public int EncountersSinceForm { get; set; }

        // Deliberately NOT reset by Reset() below - meant to stay set across
        // hunt resets/target changes for as long as an event is inactive (e.g. the
        // whole gap between a summer event ending and the next seasonal event
        // starting), not just for one hunt session.
        public bool SinceFormPaused { get; set; }

        public int SuccessfulCatches { get; set; }

        public int FailedCatches { get; set; }
        public Dictionary<string, int> EncounterCounts { get; } =
    new(StringComparer.OrdinalIgnoreCase);

        // §123: what actually HAPPENED to each species, alongside how
        // often it turned up. The front encounter table shows Amount Caught
        // and Ran From per row, and neither number existed anywhere before -
        // SuccessfulCatches and FailedCatches are session-wide totals, and
        // the encounter log records catches only.
        //
        // These deliberately do NOT sum to EncounterCounts, and that is not
        // a bug to be fixed by making them: an encounter can also end in a
        // knockout, or in the Pokemon breaking free and the player leaving,
        // or simply still be in progress. Ran From counts runs the game
        // announced - "You have run away from the wild Pokemon." - and
        // nothing else. Inferring the rest by subtraction would produce a
        // number that looks precise and is not.
        public Dictionary<string, int> CaughtCounts { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, int> RanFromCounts { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>When each species was last encountered, for the front
        /// table's Last Encountered column. UTC so it survives a save and a
        /// reload across a daylight-saving boundary without shifting.</summary>
        public Dictionary<string, DateTime> LastEncounteredUtc { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public void RegisterPokemonEncounter(string pokemonName)
        {
            if (string.IsNullOrWhiteSpace(pokemonName))
                return;

            if (EncounterCounts.ContainsKey(pokemonName))
            {
                EncounterCounts[pokemonName]++;
            }
            else
            {
                EncounterCounts[pokemonName] = 1;
            }

            LastEncounteredUtc[pokemonName] = DateTime.UtcNow;
        }

        /// <summary>§123. One species-keyed tally, used by both counters
        /// below - the two differ only in which dictionary they land in.</summary>
        private static void Increment(
            Dictionary<string, int> counts,
            string pokemonName)
        {
            if (string.IsNullOrWhiteSpace(pokemonName))
                return;

            counts[pokemonName] =
                counts.TryGetValue(pokemonName, out int existing)
                    ? existing + 1
                    : 1;
        }

        public void RegisterCatch(string pokemonName) =>
            Increment(CaughtCounts, pokemonName);

        public void RegisterRunAway(string pokemonName) =>
            Increment(RanFromCounts, pokemonName);

        /// <summary>§364. The one piece of arithmetic behind all three
        /// target stats: add up a species-keyed tally over the DISTINCT
        /// species currently targeted.
        ///
        /// §361 is why "distinct" is the whole point, and it applies to
        /// every tally alike. The same Pokemon can hold more than one target
        /// slot - one drawn as its form, one as its shiny - and those slots
        /// share a tally, because an event is counted once under the species
        /// and the form is a picture. Walking the target list as it stands
        /// would count the same catch twice for two slots and three times for
        /// three, and the number would grow by hunting nothing at all.
        ///
        /// Compared the way every other species key in this file is compared,
        /// so "Mareanie" and "mareanie" are one species and not two.</summary>
        private int SumOverDistinctTargets(
            Dictionary<string, int> counts)
        {
            int total = 0;

            var counted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string target in TargetPokemons)
            {
                if (!counted.Add(target))
                    continue;

                if (counts.TryGetValue(target, out int count))
                    total += count;
            }

            return total;
        }

        /// <summary>How many of the encounters so far were a targeted
        /// species - the "Target Pokemon Found" stat.</summary>
        public int GetTargetedEncounterCount() =>
            SumOverDistinctTargets(EncounterCounts);

        /// <summary>§364. How many of those were caught - the "Target Pokemon
        /// Caught" stat. Deliberately NOT the same number as
        /// SuccessfulCatches, which counts every catch in the session: a
        /// hunter catches things he is not hunting, and the user who asked
        /// for this said so in as many words. Both stats stay, side by side,
        /// counting different things.</summary>
        public int GetTargetedCaughtCount() =>
            SumOverDistinctTargets(CaughtCounts);

        /// <summary>§364. How many were fled from - the "Target Pokemon
        /// Fled" stat, and the same quantity the encounter table's Fled
        /// column shows, narrowed to the targets. It means what RanFromCounts
        /// means and nothing wider: runs the game announced, "You have run
        /// away from the wild Pokemon." See that dictionary's remarks for why
        /// these numbers deliberately do not sum to the encounter count.</summary>
        public int GetTargetedRanFromCount() =>
            SumOverDistinctTargets(RanFromCounts);

        public TimeSpan ElapsedTime { get; private set; } =
            TimeSpan.Zero;

        public bool IsRunning { get; private set; }

        private DateTime? runningSince;

        /// <summary>True only while the Time Hunting clock is actually ticking -
        /// false whenever IsRunning is false (Play hasn't been pressed / Stop was
        /// pressed) AND while paused for a boss battle via PauseTimeAccrual().
        /// MainWindowViewModel.HuntTimer_Tick uses this (not IsRunning alone) to
        /// decide whether to add to lifetime hunting stats, so a boss battle's
        /// duration is excluded from both the on-screen Time Hunting stat and
        /// lifetime stats consistently.</summary>
        public bool IsAccruingTime =>
            IsRunning && runningSince.HasValue;

        public void Start()
        {
            if (IsRunning)
                return;

            runningSince = DateTime.Now;
            IsRunning = true;
        }

        public void Pause()
        {
            if (!IsRunning)
                return;

            if (runningSince.HasValue)
            {
                ElapsedTime +=
                    DateTime.Now - runningSince.Value;
            }

            runningSince = null;
            IsRunning = false;
        }

        /// <summary>
        /// Freezes the Time Hunting clock without leaving the "hunting" state -
        /// IsRunning stays true, so this doesn't touch the Play/Stop button
        /// enablement (CanStart/CanStop) the way Pause() would. Used to stop the
        /// clock for the duration of a boss battle (see MainWindowViewModel's
        /// BossBattleActiveChanged handler) while keeping the hunt itself "active"
        /// from the user's perspective. A no-op if not currently hunting, or if
        /// already paused - safe to call unconditionally.
        /// </summary>
        public void PauseTimeAccrual()
        {
            if (!IsAccruingTime)
                return;

            ElapsedTime +=
                DateTime.Now - runningSince!.Value;

            runningSince = null;
        }

        /// <summary>Undoes PauseTimeAccrual() - resumes the Time Hunting clock from
        /// where it left off. A no-op if not currently hunting (Stop was pressed
        /// while paused - don't resurrect the clock) or if already accruing.</summary>
        public void ResumeTimeAccrual()
        {
            if (!IsRunning || runningSince.HasValue)
                return;

            runningSince = DateTime.Now;
        }

        public void Reset()
        {
            TargetPokemons.Clear();
            CurrentEncounter = string.Empty;
            PreviousEncounter = string.Empty;
            CurrentEncounterForm = string.Empty;
            CurrentEncounterFormImage = string.Empty;
            PreviousEncounterForm = string.Empty;
            PreviousEncounterFormImage = string.Empty;
            SuccessfulCatches = 0;
            FailedCatches = 0;

            TotalEncounters = 0;

            // §360: EncountersSinceShiny and EncountersSinceForm are NOT reset
            // here any more, and that is the point of this section.
            //
            // Reset is what you press between hunts - 200 encounters on one
            // thing, reset, 300 on the next. Zeroing the two "since" counters
            // with the hunt made them count since the last RESET rather than
            // since the last shiny, which is not what they are for and not
            // what their labels say. A hunter who resets four times a session
            // could never see the real drought.
            //
            // They now move for exactly one reason each: a shiny sets Since
            // Shiny to zero, a form sets Since Form to zero
            // (MainWindowViewModel's rare-encounter switch). That is the same
            // rule SinceFormPaused has followed since it was added, one line
            // below, and for the same reason - it outlives the hunt because
            // what it is counting outlives the hunt.
            //
            // A brand-new session still starts at zero: these are 0 on a fresh
            // HuntSession and Restore() takes them from the save file, so
            // nothing about a first run or a client switch changes.
            //
            // SinceFormPaused is intentionally NOT reset here - see its own comment.

            ElapsedTime = TimeSpan.Zero;

            runningSince = null;
            IsRunning = false;
            EncounterCounts.Clear();

            // §123: these are per-hunt exactly as EncounterCounts is, so
            // they clear with it. Missing one here would leave a species
            // showing catches from a hunt that no longer exists.
            CaughtCounts.Clear();
            RanFromCounts.Clear();
            LastEncounteredUtc.Clear();
        }

        public TimeSpan GetCurrentElapsedTime()
        {
            if (!IsRunning ||
                !runningSince.HasValue)
            {
                return ElapsedTime;
            }

            return ElapsedTime +
                   (DateTime.Now - runningSince.Value);
        }

        public void Restore(
    HuntSessionSaveData data)
        {
            // Migration: an old save file only ever had the single TargetPokemon
            // field. A newer one has TargetPokemons populated directly, in which
            // case that takes priority.
            if (data.TargetPokemons is { Count: > 0 })
            {
                TargetPokemons = new List<string>(data.TargetPokemons);
            }
            else
            {
                TargetPokemons = string.IsNullOrWhiteSpace(data.TargetPokemon)
                    ? new List<string>()
                    : new List<string> { data.TargetPokemon };
            }

            CurrentEncounter =
                data.CurrentEncounter ?? string.Empty;

            PreviousEncounter =
                data.PreviousEncounter ?? string.Empty;

            // §139. Absent from a save written before the cards showed
            // forms - System.Text.Json leaves these null, and an empty form
            // is exactly "ordinary encounter".
            CurrentEncounterForm =
                data.CurrentEncounterForm ?? string.Empty;

            CurrentEncounterFormImage =
                data.CurrentEncounterFormImage ?? string.Empty;

            PreviousEncounterForm =
                data.PreviousEncounterForm ?? string.Empty;

            PreviousEncounterFormImage =
                data.PreviousEncounterFormImage ?? string.Empty;

            TotalEncounters =
                data.TotalEncounters;

            EncountersSinceShiny =
                data.EncountersSinceShiny;

            EncountersSinceForm =
                data.EncountersSinceForm;

            SinceFormPaused =
                data.SinceFormPaused;

            SuccessfulCatches =
                data.SuccessfulCatches;

            FailedCatches =
                data.FailedCatches;

            ElapsedTime =
                data.ElapsedTime;

            EncounterCounts.Clear();

            foreach (var encounter
                     in data.EncounterCounts)
            {
                EncounterCounts[encounter.Key] =
                    encounter.Value;
            }

            // §123. A save written before this existed has no such
            // entries, so these come back empty and the new columns read
            // zero for that hunt. Nothing else about the session is
            // affected - the counts it already had are untouched - which is
            // the whole reason for restoring them separately rather than
            // rebuilding them from anything.
            CaughtCounts.Clear();

            if (data.CaughtCounts is not null)
            {
                foreach (var caught in data.CaughtCounts)
                    CaughtCounts[caught.Key] = caught.Value;
            }

            RanFromCounts.Clear();

            if (data.RanFromCounts is not null)
            {
                foreach (var ran in data.RanFromCounts)
                    RanFromCounts[ran.Key] = ran.Value;
            }

            LastEncounteredUtc.Clear();

            if (data.LastEncounteredUtc is not null)
            {
                foreach (var seen in data.LastEncounteredUtc)
                    LastEncounteredUtc[seen.Key] = seen.Value;
            }

            // Restored sessions always start paused.
            runningSince = null;
            IsRunning = false;
        }

        /// <summary>
        /// Additive counterpart to Restore() above - used by the Import "Add to
        /// Current" mode (see MainWindowViewModel.ImportHuntData/
        /// ImportModeDialogWindow) to combine a second export's numbers into this
        /// session instead of wiping it out entirely. Meant for merging two
        /// multi-client hunts of the same target (see the per-client Assign
        /// Client work) back into one combined total without losing either
        /// side's progress.
        ///
        /// Only the genuinely cumulative counters are summed: EncounterCounts
        /// (per-species), TotalEncounters, SuccessfulCatches, FailedCatches, and
        /// ElapsedTime. Everything else - TargetPokemons, CurrentEncounter/
        /// PreviousEncounter, EncountersSinceShiny/EncountersSinceForm - is left
        /// exactly as this session already has it. Those are "current state",
        /// not running totals, and adding two independent "encounters since a
        /// shiny/form last appeared" streaks together wouldn't mean anything.
        /// </summary>
        public void MergeFrom(
            HuntSessionSaveData data)
        {
            TotalEncounters +=
                data.TotalEncounters;

            SuccessfulCatches +=
                data.SuccessfulCatches;

            FailedCatches +=
                data.FailedCatches;

            ElapsedTime +=
                data.ElapsedTime;

            foreach (var encounter
                     in data.EncounterCounts)
            {
                if (EncounterCounts.TryGetValue(encounter.Key, out int existing))
                {
                    EncounterCounts[encounter.Key] =
                        existing + encounter.Value;
                }
                else
                {
                    EncounterCounts[encounter.Key] =
                        encounter.Value;
                }
            }

            // Merged sessions always start paused, same as Restore().
            runningSince = null;
            IsRunning = false;
        }
    }
}