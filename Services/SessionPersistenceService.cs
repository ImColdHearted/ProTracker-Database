using System.Text.Json;
using Foot_Tracker.Models;

namespace Foot_Tracker.Services
{
    public static class SessionPersistenceService
    {
        private static readonly string SessionFolder =
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData
                ),
                "ProTracker",
                "Database"
            );

        private static readonly string SessionPath =
            Path.Combine(
                SessionFolder,
                "current-session.json"
            );

        private static readonly string LegacySessionPath =
    Path.Combine(
        AppContext.BaseDirectory,
        "DataFiles",
        "current-session.json"
    );

        private static void MigrateLegacySession()
        {
            try
            {
                // New location already exists.
                if (File.Exists(SessionPath))
                    return;

                // Nothing from an older version to migrate.
                if (!File.Exists(LegacySessionPath))
                    return;

                Directory.CreateDirectory(
                    Path.GetDirectoryName(SessionPath)!
                );

                File.Copy(
                    LegacySessionPath,
                    SessionPath,
                    overwrite: false
                );
            }
            catch
            {
                // Migration failure should never prevent
                // the tracker from opening.
            }
        }
        public static void Save(HuntSession session)
        {
            Directory.CreateDirectory(
                Path.GetDirectoryName(SessionPath)!
            );

            var data = new HuntSessionSaveData
            {
                TargetPokemon = session.TargetPokemon,
                CurrentEncounter = session.CurrentEncounter,
                PreviousEncounter = session.PreviousEncounter,

                TotalEncounters = session.TotalEncounters,
                EncountersSinceShiny = session.EncountersSinceShiny,
                EncountersSinceForm = session.EncountersSinceForm,

                SuccessfulCatches = session.SuccessfulCatches,
                FailedCatches = session.FailedCatches,

                ElapsedTime =
                    session.GetCurrentElapsedTime(),

                EncounterCounts =
                    new Dictionary<string, int>(
                        session.EncounterCounts,
                        StringComparer.OrdinalIgnoreCase
                    )
            };

            string json =
                JsonSerializer.Serialize(
                    data,
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
                    }
                );

            File.WriteAllText(
                SessionPath,
                json
            );
        }

        public static HuntSessionSaveData? Load()
        {
            MigrateLegacySession();
            if (!File.Exists(SessionPath))
                return null;

            try
            {
                string json =
                    File.ReadAllText(SessionPath);

                return JsonSerializer.Deserialize<HuntSessionSaveData>(
                    json,
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    }
                );
            }
            catch
            {
                // A damaged session file should not prevent
                // ProTrackerandDatabase from opening.
                return null;
            }
        }

        public static void Delete()
        {
            if (File.Exists(SessionPath))
            {
                File.Delete(SessionPath);
            }
        }
    }

    public class HuntSessionSaveData
    {
        public string TargetPokemon { get; set; } =
            string.Empty;

        public string CurrentEncounter { get; set; } =
            string.Empty;

        public string PreviousEncounter { get; set; } =
            string.Empty;

        public int TotalEncounters { get; set; }

        public int EncountersSinceShiny { get; set; }

        public int EncountersSinceForm { get; set; }

        public int SuccessfulCatches { get; set; }

        public int FailedCatches { get; set; }

        public TimeSpan ElapsedTime { get; set; }

        public Dictionary<string, int>
            EncounterCounts
        { get; set; } =
                new(StringComparer.OrdinalIgnoreCase);
    }
}