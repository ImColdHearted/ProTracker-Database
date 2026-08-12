using Foot_Tracker.Services;

namespace Foot_Tracker.Models
{
    public class HuntSession
    {
        public string TargetPokemon { get; set; } = string.Empty;

        public string CurrentEncounter { get; set; } = string.Empty;

        public string PreviousEncounter { get; set; } = string.Empty;

        public int TotalEncounters { get; set; }

        public int EncountersSinceShiny { get; set; }

        public int EncountersSinceForm { get; set; }

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

        public TimeSpan ElapsedTime { get; private set; } =
            TimeSpan.Zero;

        public bool IsRunning { get; private set; }

        private DateTime? runningSince;

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

        public void Reset()
        {
            TargetPokemon = string.Empty;
            CurrentEncounter = string.Empty;
            PreviousEncounter = string.Empty;
            EncountersSinceForm = 0;
            SuccessfulCatches = 0;
            FailedCatches = 0;

            TotalEncounters = 0;
            EncountersSinceShiny = 0;

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
            TargetPokemon =
                data.TargetPokemon ?? string.Empty;

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
    }
}