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
        }

        /// <summary>Sum of EncounterCounts for every currently-targeted Pokemon -
        /// used for the "Targeted Encounters" stat.</summary>
        public int GetTargetedEncounterCount()
        {
            int total = 0;

            foreach (string target in TargetPokemons)
            {
                if (EncounterCounts.TryGetValue(target, out int count))
                    total += count;
            }

            return total;
        }

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
            EncountersSinceForm = 0;
            SuccessfulCatches = 0;
            FailedCatches = 0;

            TotalEncounters = 0;
            EncountersSinceShiny = 0;

            // SinceFormPaused is intentionally NOT reset here - see its own comment.

            ElapsedTime = TimeSpan.Zero;

            runningSince = null;
            IsRunning = false;
            EncounterCounts.Clear();
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