using System;
using System.IO;
using System.Text.Json;
using Foot_Tracker.Models;

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
            ("TargetedEncountersFound", "Targeted Encounters Found"),
            ("SinceShiny", "Since Shiny"),
            ("SinceForm", "Since Form"),
            ("SuccessfulCatches", "Successful Catches"),
            ("PokemonBrokenFree", "Pokémon Broken Free"),
            ("CatchRate", "Catch Rate"),
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

            File.WriteAllText(settingsPath, json);
        }
    }
}
