using System;
using System.IO;
using System.Text.Json;
using Foot_Tracker.Models;

namespace Foot_Tracker.Services
{
    public static class AppearanceSettingsRepository
    {
        private static readonly string SettingsFolder =
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "PRO Tracker & Database");

        // Appearance is per-client (see SessionPersistenceService.ActiveClientNumber,
        // the same number current-session-client{N}.json is already keyed by) -
        // requested so someone running two instances (e.g. one account dedicated
        // to hunting, another to PVP) can give each its own look, whether that's
        // just to tell the windows apart at a glance or a genuine preference per
        // account. Uses AppearanceClientNumber rather than ActiveClientNumber
        // directly - defaults to client 1's look at startup (before this
        // instance's own client has been auto-detected/locked) instead of
        // resetting to a generic default for that brief window, then switches
        // to whichever client actually ends up locked once that happens (see
        // AppearanceClientNumber's remarks for why this is safe to default
        // even though the stricter hunt-data path isn't).
        private static string GetSettingsPath()
        {
            int clientNumber = SessionPersistenceService.AppearanceClientNumber;

            string fileName = clientNumber >= 1
                ? $"appearance-client{clientNumber}.json"
                : "appearance.json";

            return Path.Combine(SettingsFolder, fileName);
        }

        // Where this file used to live before per-client appearance existed -
        // there was only ever one shared save, so MigrateLegacySettingsIfNeeded
        // treats it as belonging to the default/first client rather than
        // guessing which client it "really" was for. The image file a legacy
        // CustomBackgroundPath points to (see SaveCustomBackground) isn't moved
        // or renamed by this migration - only the settings JSON is copied - so
        // that path keeps resolving correctly as-is.
        private static readonly string LegacySettingsPath =
            Path.Combine(
                SettingsFolder,
                "appearance.json");

        public static AppearanceSettings Load()
        {
            try
            {
                string settingsPath = GetSettingsPath();

                MigrateLegacySettingsIfNeeded(settingsPath);

                if (!File.Exists(settingsPath))
                    return new AppearanceSettings();

                string json = File.ReadAllText(settingsPath);

                AppearanceSettings? settings =
                    JsonSerializer.Deserialize<AppearanceSettings>(json);

                settings ??= new AppearanceSettings();

                MigrateLegacyTextShadowIfNeeded(settings, json);

                return settings;
            }
            catch
            {
                return new AppearanceSettings();
            }
        }

        // TextShadowEnabled (a bool) was replaced by TextShadowName (a string -
        // None/Small/Medium/Large, same shape as BorderShadowName) so Text
        // Shadow could offer more than just on/off. A settings file saved
        // before that change has "TextShadowEnabled": true but no
        // "TextShadowName" at all, so the deserializer above just leaves
        // TextShadowName at its "None" default - without this, anyone who'd
        // already turned Text Shadow on would silently find it back off.
        // "Medium" reproduces the exact blur/offset/opacity the old on/off
        // toggle always used (see ThemeManager.TextShadowCatalog), rather than
        // guessing at a level.
        private static void MigrateLegacyTextShadowIfNeeded(AppearanceSettings settings, string json)
        {
            try
            {
                if (!string.Equals(settings.TextShadowName, "None", StringComparison.OrdinalIgnoreCase))
                    return;

                using JsonDocument document = JsonDocument.Parse(json);

                if (document.RootElement.TryGetProperty("TextShadowEnabled", out JsonElement element) &&
                    element.ValueKind == JsonValueKind.True)
                {
                    settings.TextShadowName = "Medium";
                }
            }
            catch
            {
                // Best-effort only - worst case, Text Shadow just starts back at "None".
            }
        }

        // Only migrates into client 1 - see LegacySettingsPath's remarks.
        // Clients 2+ simply start with the app's default appearance, same as
        // any other newly-tracked client would.
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
                // case, this client starts with the app's default appearance.
            }
        }

        public static void Save(AppearanceSettings settings)
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

        public static string CustomBackgroundFolder =>
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "PRO Tracker & Database",
                "Backgrounds");

        // Saved per-client (see GetSettingsPath's remarks) so two clients each
        // set to a different custom background image don't stomp on one
        // shared "custom-background.*" file - without the client suffix, the
        // second client to save a background would delete the first client's
        // image file out from under it, even though each has its own
        // AppearanceSettings.CustomBackgroundPath pointing at it.
        public static string SaveCustomBackground(
            string sourcePath)
        {
            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException(
                    "The selected background image no longer exists.",
                    sourcePath);
            }

            Directory.CreateDirectory(CustomBackgroundFolder);

            string extension =
                Path.GetExtension(sourcePath).ToLowerInvariant();

            if (string.IsNullOrWhiteSpace(extension))
                extension = ".png";

            int clientNumber = SessionPersistenceService.AppearanceClientNumber;

            string baseFileName = clientNumber >= 1
                ? $"custom-background-client{clientNumber}"
                : "custom-background";

            string destinationPath =
                Path.Combine(
                    CustomBackgroundFolder,
                    $"{baseFileName}{extension}");

            foreach (string existingFile in
                     Directory.GetFiles(
                         CustomBackgroundFolder,
                         $"{baseFileName}.*"))
            {
                if (!string.Equals(
                        existingFile,
                        destinationPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(existingFile);
                }
            }

            File.Copy(
                sourcePath,
                destinationPath,
                overwrite: true);

            return destinationPath;
        }
    }
}
