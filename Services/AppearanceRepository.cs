using System;
using System.Collections.Generic;
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

                NormalizeRemovedPresetBackground(settings);

                return settings;
            }
            catch
            {
                return new AppearanceSettings();
            }
        }

        // §104 removed the six preset backgrounds (Midnight/Blood/Slate/
        // Pride/Pink/Violet) in favor of one built-in look, Slate. A settings
        // file saved before that change can still name any of the other five,
        // which would now resolve to a background image that is no longer
        // offered anywhere in the UI - and, for the four that never shipped an
        // image at all, to a flat color with no way left to change it. Coerced
        // to Slate on load instead, so an existing install simply lands on the
        // new default.
        //
        // Deliberately narrow: this only touches the PRESET id, and only when
        // a preset is what the file actually selects. A custom image, a custom
        // color or a custom gradient is the user's own choice rather than a
        // removed preset, so all three are left exactly as they were (the two
        // flags are checked first for precisely that reason). An older file's
        // now-unknown keys - TextShadowName, TextStrokeColorArgb and the rest
        // of the §104 removals - need no handling at all: System.Text.Json
        // ignores properties the model no longer declares.
        private static void NormalizeRemovedPresetBackground(AppearanceSettings settings)
        {
            if (settings.UseCustomBackground || settings.UseCustomGradient)
                return;

            if (string.Equals(settings.BackgroundId, AppearanceSettings.DefaultBackgroundId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            settings.BackgroundId = AppearanceSettings.DefaultBackgroundId;
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

            DurableFile.WriteAllText(settingsPath, json);
        }

        // ------------------------------------------------- §209 theme files

        /// <summary>§209. The extension the Save/Load buttons in the
        /// Appearance window use. A plain .json so anyone can look at what
        /// they were sent before opening it.</summary>
        public const string ThemeFileExtension = ".protheme.json";

        /// <summary>§209. What one shared theme file holds.
        ///
        /// A DTO rather than AppearanceSettings itself, deliberately. The
        /// settings model carries things that are about THIS machine and this
        /// install - which client's file it is, the absolute path of a
        /// background image sitting in this user's AppData - and a file meant
        /// to be pasted into Discord should not carry any of that. Listing the
        /// portable fields explicitly also means adding a machine-local
        /// setting to AppearanceSettings later cannot silently start leaking
        /// it into shared files.
        ///
        /// The picture background is the one thing deliberately left out: the
        /// image lives on the sender's disk and the path is meaningless on the
        /// receiver's. A gradient IS included, because a gradient is just
        /// colours and a direction, which travel perfectly.</summary>
        public sealed class ThemeFile
        {
            /// <summary>Bumped only if the shape changes incompatibly. Import
            /// reads anything it recognises and ignores the rest, so a file
            /// from a newer build loads what it can rather than refusing.</summary>
            public int Version { get; set; } = 1;

            public string App { get; set; } = "PRO Tracker & Database";

            public string? Name { get; set; }

            public string? SavedUtc { get; set; }

            public int TextColorArgb { get; set; }
            public int BorderColorArgb { get; set; }
            public int SpriteBoxBackgroundColorArgb { get; set; }
            public int EncountersBackgroundColorArgb { get; set; }
            public int HeaderBackgroundColorArgb { get; set; }
            public int StatsBackgroundColorArgb { get; set; }
            public int ButtonColorArgb { get; set; }
            public int CustomBackgroundColorArgb { get; set; }

            public bool UseCustomGradient { get; set; }
            public List<int> CustomGradientColorArgbs { get; set; } = new();
            public string CustomGradientDirection { get; set; } = string.Empty;

            public string FontFamilyName { get; set; } = string.Empty;
            public string FontSizeName { get; set; } = string.Empty;
        }

        /// <summary>§209. Writes the portable subset of these settings to a
        /// file the user picked. Throws on failure - the caller shows the
        /// message.</summary>
        public static void ExportTheme(AppearanceSettings settings, string path, string? name = null)
        {
            var file = new ThemeFile
            {
                Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
                SavedUtc = DateTime.UtcNow.ToString("o"),

                TextColorArgb = settings.TextColorArgb,
                BorderColorArgb = settings.BorderColorArgb,
                SpriteBoxBackgroundColorArgb = settings.SpriteBoxBackgroundColorArgb,
                EncountersBackgroundColorArgb = settings.EncountersBackgroundColorArgb,
                HeaderBackgroundColorArgb = settings.HeaderBackgroundColorArgb,
                StatsBackgroundColorArgb = settings.StatsBackgroundColorArgb,
                ButtonColorArgb = settings.ButtonColorArgb,
                CustomBackgroundColorArgb = settings.CustomBackgroundColorArgb,

                UseCustomGradient = settings.UseCustomGradient,
                CustomGradientColorArgbs = new List<int>(settings.CustomGradientColorArgbs),
                CustomGradientDirection = settings.CustomGradientDirection,

                FontFamilyName = settings.FontFamilyName,
                FontSizeName = settings.FontSizeName
            };

            string json = JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true });

            // Not DurableFile: that one is for the app's own state, where a
            // half-written file would break the next start. This is an export
            // to wherever the user pointed the picker, which may be a network
            // share or a removable drive, and a plain write that fails there
            // fails loudly rather than leaving a temp file behind.
            File.WriteAllText(path, json);
        }

        /// <summary>§209. Reads a shared theme file onto a COPY of the current
        /// settings, so everything the file does not carry - which client this
        /// is, the picture background already set up on this machine - is kept
        /// exactly as it was. Throws with a plain sentence if the file is not
        /// one of ours.</summary>
        public static AppearanceSettings ImportTheme(string path, AppearanceSettings current)
        {
            ThemeFile? file;

            try
            {
                file = JsonSerializer.Deserialize<ThemeFile>(File.ReadAllText(path));
            }
            catch (JsonException)
            {
                throw new InvalidDataException("That file is not a Pro Tracker theme - it is not readable as JSON.");
            }

            if (file is null || file.Version <= 0)
                throw new InvalidDataException("That file is not a Pro Tracker theme.");

            AppearanceSettings merged = Clone(current);

            merged.TextColorArgb = file.TextColorArgb;
            merged.BorderColorArgb = file.BorderColorArgb;
            merged.SpriteBoxBackgroundColorArgb = file.SpriteBoxBackgroundColorArgb;
            merged.EncountersBackgroundColorArgb = file.EncountersBackgroundColorArgb;
            merged.HeaderBackgroundColorArgb = file.HeaderBackgroundColorArgb;
            merged.StatsBackgroundColorArgb = file.StatsBackgroundColorArgb;
            merged.ButtonColorArgb = file.ButtonColorArgb;
            merged.CustomBackgroundColorArgb = file.CustomBackgroundColorArgb;

            // A gradient in the file replaces whatever background mode is set,
            // because a gradient is the background. A file with no gradient
            // leaves this machine's background alone rather than blanking it -
            // the file never claimed to describe one.
            if (file.UseCustomGradient && file.CustomGradientColorArgbs.Count >= 2)
            {
                merged.UseCustomGradient = true;
                merged.UseCustomBackground = false;
                merged.CustomBackgroundPath = string.Empty;
                merged.CustomGradientColorArgbs = new List<int>(file.CustomGradientColorArgbs);

                if (!string.IsNullOrWhiteSpace(file.CustomGradientDirection))
                    merged.CustomGradientDirection = file.CustomGradientDirection;
            }

            if (!string.IsNullOrWhiteSpace(file.FontFamilyName))
                merged.FontFamilyName = file.FontFamilyName;

            if (!string.IsNullOrWhiteSpace(file.FontSizeName))
                merged.FontSizeName = file.FontSizeName;

            return merged;
        }

        /// <summary>§209. A copy through the same serializer the settings file
        /// uses, so a field added to AppearanceSettings is carried without this
        /// method having to be remembered.</summary>
        private static AppearanceSettings Clone(AppearanceSettings source) =>
            JsonSerializer.Deserialize<AppearanceSettings>(JsonSerializer.Serialize(source))
            ?? new AppearanceSettings();

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
