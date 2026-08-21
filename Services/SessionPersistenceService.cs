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

        // Old single-client file.
        private static readonly string LegacySessionPath =
            Path.Combine(
                SessionFolder,
                "current-session.json"
            );

        private static readonly string VeryOldSessionPath =
            Path.Combine(
                AppContext.BaseDirectory,
                "DataFiles",
                "current-session.json"
            );

        // Remembers which client was last active across app restarts, so the
        // previous hunt's data shows up immediately on launch instead of only
        // after Play assigns a client. Just a plain text file with one number.
        private static readonly string LastActiveClientPath =
            Path.Combine(
                SessionFolder,
                "last-active-client.txt"
            );

        private static int activeClientNumber =
            LoadLastActiveClientNumber();

        private static int LoadLastActiveClientNumber()
        {
            try
            {
                if (File.Exists(LastActiveClientPath))
                {
                    string text =
                        File.ReadAllText(LastActiveClientPath).Trim();

                    if (int.TryParse(text, out int saved) && saved >= 1)
                        return saved;
                }
            }
            catch
            {
                // Fall through to the default below.
            }

            // Default to client 1 - the common single-client case - rather
            // than 0/"no client", so a fresh install still shows *something*
            // once a session has actually been saved once.
            return 1;
        }

        // ============================================================
        // CLIENT ASSIGNMENT
        // ============================================================

        public static void SetActiveClient(
            int clientNumber)
        {
            if (clientNumber < 1)
            {
                activeClientNumber = 0;
                return;
            }

            activeClientNumber =
                clientNumber;

            try
            {
                Directory.CreateDirectory(SessionFolder);
                File.WriteAllText(LastActiveClientPath, clientNumber.ToString());
            }
            catch
            {
                // Non-critical - worst case, the next launch falls back to
                // whatever client number was last successfully remembered.
            }

            MigrateLegacySessionIfNeeded();
        }

        public static int ActiveClientNumber =>
            activeClientNumber;

        // ============================================================
        // PATH
        // ============================================================

        private static string? GetSessionPath()
        {
            if (activeClientNumber <= 0)
                return null;

            return Path.Combine(
                SessionFolder,
                $"current-session-client{activeClientNumber}.json"
            );
        }

        // ============================================================
        // LEGACY MIGRATION
        // ============================================================

        private static void MigrateLegacySessionIfNeeded()
        {
            try
            {
                Directory.CreateDirectory(
                    SessionFolder
                );

                string? newPath =
                    GetSessionPath();

                if (newPath == null)
                    return;

                // This client already has its own save.
                if (File.Exists(newPath))
                    return;

                // First try the existing AppData single-client save.
                if (File.Exists(LegacySessionPath))
                {
                    File.Copy(
                        LegacySessionPath,
                        newPath,
                        overwrite: false
                    );

                    return;
                }

                // Then try the very old application-folder save.
                if (File.Exists(VeryOldSessionPath))
                {
                    File.Copy(
                        VeryOldSessionPath,
                        newPath,
                        overwrite: false
                    );
                }
            }
            catch
            {
                // Migration failure must never stop the application.
            }
        }

        // ============================================================
        // SAVE
        // ============================================================

        public static void Save(
            HuntSession session)
        {
            string? sessionPath =
                GetSessionPath();

            // No client has been selected yet.
            //
            // Do not write a shared session file because two
            // tracker processes could overwrite one another.
            if (sessionPath == null)
                return;

            Directory.CreateDirectory(
                SessionFolder
            );

            var data =
                new HuntSessionSaveData
                {
                    TargetPokemon =
                        session.TargetPokemon,

                    CurrentEncounter =
                        session.CurrentEncounter,

                    PreviousEncounter =
                        session.PreviousEncounter,

                    TotalEncounters =
                        session.TotalEncounters,

                    EncountersSinceShiny =
                        session.EncountersSinceShiny,

                    EncountersSinceForm =
                        session.EncountersSinceForm,

                    SuccessfulCatches =
                        session.SuccessfulCatches,

                    FailedCatches =
                        session.FailedCatches,

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

            string tempPath =
                sessionPath + ".tmp";

            File.WriteAllText(
                tempPath,
                json
            );

            File.Move(
                tempPath,
                sessionPath,
                overwrite: true
            );
        }

        // ============================================================
        // LOAD
        // ============================================================

        public static HuntSessionSaveData? Load()
        {
            string? sessionPath =
                GetSessionPath();

            if (sessionPath == null)
                return null;

            MigrateLegacySessionIfNeeded();

            if (!File.Exists(sessionPath))
                return null;

            try
            {
                string json =
                    File.ReadAllText(
                        sessionPath
                    );

                return
                    JsonSerializer.Deserialize<HuntSessionSaveData>(
                        json,
                        new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        }
                    );
            }
            catch
            {
                return null;
            }
        }

        // ============================================================
        // DELETE
        // ============================================================

        public static void Delete()
        {
            string? sessionPath =
                GetSessionPath();

            if (sessionPath == null)
                return;

            if (File.Exists(sessionPath))
            {
                File.Delete(
                    sessionPath
                );
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