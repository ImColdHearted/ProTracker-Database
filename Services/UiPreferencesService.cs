using System;
using System.IO;
using System.Text.Json;
using Foot_Tracker.Models;
using Serilog;

namespace Foot_Tracker.Services
{
    public static class UiPreferencesService
    {
        // Every excludable stat's storage key, paired with the label shown in
        // the Exclude Stats window (Stats menu) and used as the source list for
        // ExcludeStatsViewModel's checkboxes. Add an entry here - plus a
        // matching Show<Stat> property/case in MainWindowViewModel.ApplyExcludedStats
        // and an IsVisible binding in MainWindow.axaml - to make another stat
        // excludable. "Pause Since Form" and the "Current Event" selector are
        // deliberately left out - they're controls the user interacts with, not
        // pure stat readouts, so hiding them would remove functionality rather
        // than just decluttering the display.
        public static readonly (string Key, string DisplayName)[] ExcludableStats =
        {
            ("TimeHunting", "Time Hunting"),
            ("TotalEncounters", "Total Encounters"),
            // §364: the label matches the panel's own wording now. The KEY is
            // deliberately left as it was - it is what a saved preferences
            // file contains, and renaming it would un-hide this stat for
            // everyone who had hidden it.
            ("TargetedEncountersFound", "Target Pokémon Found"),
            ("TargetedPokemonCaught", "Target Pokémon Caught"),
            ("TargetedPokemonFled", "Target Pokémon Fled"),
            ("SinceShiny", "Since Shiny"),
            ("SinceForm", "Since Form"),
            // §364: Successful Catches and Pokémon Broken Free count EVERY
            // catch and every break-out, target or not, which is why they
            // stay beside the two target-only stats above rather than being
            // replaced by them. A hunter catches things he is not hunting.
            ("SuccessfulCatches", "Successful Catches"),
            ("PokemonBrokenFree", "Pokémon Broken Free"),
            // §364: Catch Rate is gone from the panel, so it is gone from
            // here. A preferences file that still lists the key is harmless -
            // nothing reads it, and the next Save rewrites the list from this
            // catalogue.
        };

        /// <summary>§126. The encounter table's own columns, offered in the
        /// same window as the stats above but kept as a separate list and a
        /// separate persisted key - they hide different things, and one list
        /// would make a stat key and a column key collide the moment two of
        /// them wanted the same name.
        ///
        /// The Pokemon name is deliberately absent. A row with its name
        /// hidden is a row of numbers about nothing, so it is not offered
        /// rather than offered and refused.</summary>
        public static readonly (string Key, string DisplayName)[] ExcludableTableColumns =
        {
            ("Encounters", "Encounters"),
            ("AmountCaught", "Amount Caught"),
            ("RanFrom", "Ran From"),
            ("LastEncountered", "Last Encountered"),
            ("Rate", "Rate"),
        };

        private static readonly string SettingsFolder =
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "PRO Tracker & Database");

        // Per-client (see SessionPersistenceService.ActiveClientNumber, the same
        // number current-session-client{N}.json is already keyed by). This
        // actually completes StatsPanelOnRight's own original intent - its doc
        // comment says it exists so multi-client hunters running several
        // instances side by side can dock each instance's stats column toward
        // the middle of the screen, but a single shared file meant every
        // instance was forced to the same side anyway. Excluded stats are
        // per-client for the same reason ExcludeStats was requested: someone
        // dedicating one account to hunting and another to PVP may want a
        // different stat set visible on each. Uses AppearanceClientNumber
        // rather than ActiveClientNumber directly, same reasoning as
        // AppearanceSettingsRepository: defaults to client 1's layout at
        // startup instead of resetting to the app's defaults for the brief
        // window before this instance's own client has been locked.
        private static string GetSettingsPath()
        {
            int clientNumber = SessionPersistenceService.AppearanceClientNumber;

            string fileName = clientNumber >= 1
                ? $"ui-preferences-client{clientNumber}.json"
                : "ui-preferences.json";

            return Path.Combine(SettingsFolder, fileName);
        }

        // Where this file used to live before per-client preferences existed -
        // there was only ever one shared save, so MigrateLegacySettingsIfNeeded
        // treats it as belonging to the default/first client rather than
        // guessing which client it "really" was for.
        private static readonly string LegacySettingsPath =
            Path.Combine(
                SettingsFolder,
                "ui-preferences.json");

        public static UiPreferences Load()
        {
            try
            {
                string settingsPath = GetSettingsPath();

                MigrateLegacySettingsIfNeeded(settingsPath);

                if (!File.Exists(settingsPath))
                    return new UiPreferences();

                string json = File.ReadAllText(settingsPath);

                UiPreferences? settings =
                    JsonSerializer.Deserialize<UiPreferences>(json);

                return settings ?? new UiPreferences();
            }
            catch
            {
                return new UiPreferences();
            }
        }

        // Only migrates into client 1 - see LegacySettingsPath's remarks.
        // Clients 2+ simply start with the app's default stats-panel side/set
        // of shown stats, same as any other newly-tracked client would.
        private static void MigrateLegacySettingsIfNeeded(string settingsPath)
        {
            try
            {
                if (File.Exists(settingsPath))
                    return;

                if (SessionPersistenceService.AppearanceClientNumber != 1)
                    return;

                if (!File.Exists(LegacySettingsPath))
                    return;

                Directory.CreateDirectory(SettingsFolder);

                File.Copy(
                    LegacySettingsPath,
                    settingsPath,
                    overwrite: false);
            }
            catch
            {
                // Migration failure must never stop the application - worst
                // case, this client starts with the app's default preferences.
            }
        }

        public static void Save(UiPreferences settings)
        {
            string settingsPath = GetSettingsPath();

            Directory.CreateDirectory(SettingsFolder);

            JsonSerializerOptions options = new()
            {
                WriteIndented = true
            };

            string json =
                JsonSerializer.Serialize(settings, options);

            DurableFile.WriteAllText(settingsPath, json);
        }

        /// <summary>§146. Writes the two sound choices (and, since §152, their
        /// volumes, and since §389 the output device) into every client's
        /// preference file that exists (and the one for the client in use,
        /// whether or not it exists yet), so a sound picked once plays on
        /// whichever client the hunt runs on. Everything else in each file is
        /// left exactly as it was. Returns how many files were written; a
        /// file that cannot be read or written is logged and skipped rather
        /// than stopping the rest.</summary>
        public static int SaveSoundsForEveryClient(
            string sinceFormSound, string sinceShinySound, int sinceFormSoundVolume, int sinceShinySoundVolume,
            string soundOutputDevice, string soundOutputDeviceLabel, int maxClients)
        {
            int written = 0;
            int activeClient = SessionPersistenceService.AppearanceClientNumber;

            JsonSerializerOptions options = new()
            {
                WriteIndented = true
            };

            for (int clientNumber = 1; clientNumber <= maxClients; clientNumber++)
            {
                string path = Path.Combine(SettingsFolder, $"ui-preferences-client{clientNumber}.json");

                if (clientNumber != activeClient && !File.Exists(path))
                    continue;

                try
                {
                    UiPreferences settings = File.Exists(path)
                        ? JsonSerializer.Deserialize<UiPreferences>(File.ReadAllText(path)) ?? new UiPreferences()
                        : new UiPreferences();

                    settings.SinceFormSound = sinceFormSound;
                    settings.SinceShinySound = sinceShinySound;
                    settings.SinceFormSoundVolume = sinceFormSoundVolume;
                    settings.SinceShinySoundVolume = sinceShinySoundVolume;
                    settings.SoundOutputDevice = soundOutputDevice;
                    settings.SoundOutputDeviceLabel = soundOutputDeviceLabel;

                    Directory.CreateDirectory(SettingsFolder);
                    DurableFile.WriteAllText(path, JsonSerializer.Serialize(settings, options));
                    written++;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Sound preferences could not be written for client {Client} ({Path}).", clientNumber, path);
                }
            }

            return written;
        }
    }
}
