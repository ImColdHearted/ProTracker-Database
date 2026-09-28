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

                // §349. One Text Color, Border Color and Font became four of
                // each. This copies the old values into the new fields so an
                // existing appearance looks identical after the update - the
                // new controls simply start where the old ones left off.
                //
                // Here rather than in the deserializer because it has to run
                // for a file that HAS no version key, which is every file
                // written before §349.
                settings.MigrateGlobalsToSections();

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

            // §349. Legacy: one font for the window. Still written and still
            // read, because a theme file shared by a pre-§349 build carries
            // only these - see ImportTheme, which seeds the four sections
            // from them when the per-section fields are absent.
            public string FontFamilyName { get; set; } = string.Empty;
            public string FontSizeName { get; set; } = string.Empty;

            // §349. Four sections, each with its own border, text and font.
            // Version stays 1: these are ADDITIVE, so an older build reads
            // the file, finds the fields it knows, ignores these and lands on
            // a theme that is simply less specific - which is what §209's
            // "reads anything it recognises and ignores the rest" was for.
            public int SpriteBoxBorderColorArgb { get; set; }
            public int SpriteBoxTextColorArgb { get; set; }
            public string SpriteBoxFontFamilyName { get; set; } = string.Empty;
            public string SpriteBoxFontSizeName { get; set; } = string.Empty;

            public int EncountersBorderColorArgb { get; set; }
            public int EncountersTextColorArgb { get; set; }
            public string EncountersFontFamilyName { get; set; } = string.Empty;
            public string EncountersFontSizeName { get; set; } = string.Empty;

            public int StatsBorderColorArgb { get; set; }
            public int StatsTextColorArgb { get; set; }
            public string StatsFontFamilyName { get; set; } = string.Empty;
            public string StatsFontSizeName { get; set; } = string.Empty;

            public int ButtonBorderColorArgb { get; set; }
            public int ButtonTextColorArgb { get; set; }
            public string ButtonFontFamilyName { get; set; } = string.Empty;
            public string ButtonFontSizeName { get; set; } = string.Empty;

            // §380. Nullable: a file from before §380 carries no such field,
            // and every theme made then had bold headings, so absent reads
            // as true in ImportTheme. Additive like §349, Version stays 1.
            public bool? BoldHeadings { get; set; }

            // §384. Border width and corner radius per section - numbers, so
            // they travel, unlike the frame pictures. Nullable for the same
            // reason as BoldHeadings: absent reads as the pre-§384 look.
            public double? SpriteBoxBorderWidth { get; set; }
            public double? SpriteBoxCornerRadius { get; set; }
            public double? EncountersBorderWidth { get; set; }
            public double? EncountersCornerRadius { get; set; }
            public double? StatsBorderWidth { get; set; }
            public double? StatsCornerRadius { get; set; }
            public double? ButtonBorderWidth { get; set; }
            public double? ButtonCornerRadius { get; set; }

            // §387. The sprite row panel: two colours (0, transparent, when
            // absent - which is also its default) and two numbers.
            public int SpriteRowBackgroundColorArgb { get; set; }
            public int SpriteRowBorderColorArgb { get; set; }

            // §393. The menu bar's colours; absent reads as 0, automatic.
            public int MenuTextColorArgb { get; set; }
            public int MenuHighlightColorArgb { get; set; }

            // §394. The menu bar's font; absent or empty follows Statistics.
            public string MenuFontFamilyName { get; set; } = string.Empty;
            public string MenuFontSizeName { get; set; } = string.Empty;
            public double? SpriteRowBorderWidth { get; set; }
            public double? SpriteRowCornerRadius { get; set; }
        }

        /// <summary>§345. The portable subset of these settings as the DTO,
        /// without writing it anywhere.
        ///
        /// Split out of ExportTheme because §345 needs the same object to
        /// POST rather than to save. Sharing a theme to the community
        /// gallery and saving one to a file must describe the identical
        /// appearance, and the only way to guarantee that is for both to
        /// come from here - a second field list would drift the first time
        /// one of them gained a colour the other did not.</summary>
        public static ThemeFile BuildThemeFile(AppearanceSettings settings, string? name = null)
        {
            return new ThemeFile
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
                FontSizeName = settings.FontSizeName,

                SpriteBoxBorderColorArgb = settings.SpriteBoxBorderColorArgb,
                SpriteBoxTextColorArgb = settings.SpriteBoxTextColorArgb,
                SpriteBoxFontFamilyName = settings.SpriteBoxFontFamilyName,
                SpriteBoxFontSizeName = settings.SpriteBoxFontSizeName,

                EncountersBorderColorArgb = settings.EncountersBorderColorArgb,
                EncountersTextColorArgb = settings.EncountersTextColorArgb,
                EncountersFontFamilyName = settings.EncountersFontFamilyName,
                EncountersFontSizeName = settings.EncountersFontSizeName,

                StatsBorderColorArgb = settings.StatsBorderColorArgb,
                StatsTextColorArgb = settings.StatsTextColorArgb,
                StatsFontFamilyName = settings.StatsFontFamilyName,
                StatsFontSizeName = settings.StatsFontSizeName,

                ButtonBorderColorArgb = settings.ButtonBorderColorArgb,
                ButtonTextColorArgb = settings.ButtonTextColorArgb,
                ButtonFontFamilyName = settings.ButtonFontFamilyName,
                ButtonFontSizeName = settings.ButtonFontSizeName,

                BoldHeadings = settings.BoldHeadings,

                SpriteBoxBorderWidth = settings.SpriteBoxBorderWidth,
                SpriteBoxCornerRadius = settings.SpriteBoxCornerRadius,
                EncountersBorderWidth = settings.EncountersBorderWidth,
                EncountersCornerRadius = settings.EncountersCornerRadius,
                StatsBorderWidth = settings.StatsBorderWidth,
                StatsCornerRadius = settings.StatsCornerRadius,
                ButtonBorderWidth = settings.ButtonBorderWidth,
                ButtonCornerRadius = settings.ButtonCornerRadius,

                SpriteRowBackgroundColorArgb = settings.SpriteRowBackgroundColorArgb,
                SpriteRowBorderColorArgb = settings.SpriteRowBorderColorArgb,
                MenuTextColorArgb = settings.MenuTextColorArgb,
                MenuHighlightColorArgb = settings.MenuHighlightColorArgb,
                MenuFontFamilyName = settings.MenuFontFamilyName,
                MenuFontSizeName = settings.MenuFontSizeName,
                SpriteRowBorderWidth = settings.SpriteRowBorderWidth,
                SpriteRowCornerRadius = settings.SpriteRowCornerRadius
            };
        }

        /// <summary>§209. Writes the portable subset of these settings to a
        /// file the user picked. Throws on failure - the caller shows the
        /// message.</summary>
        public static void ExportTheme(AppearanceSettings settings, string path, string? name = null)
        {
            ThemeFile file = BuildThemeFile(settings, name);

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

            return ImportTheme(file, current);
        }

        /// <summary>§345. The same merge, from a theme already in memory.
        ///
        /// A community appearance arrives over HTTP rather than as a file on
        /// disk, and it must land on the current settings by exactly the
        /// rules a downloaded .protheme.json does - a gradient replacing the
        /// background, an absent font leaving this machine's alone. So the
        /// merge lives here and the file-reading overload above calls it,
        /// rather than the gallery growing its own copy that would quietly
        /// disagree the first time these rules changed.</summary>
        public static AppearanceSettings ImportTheme(ThemeFile file, AppearanceSettings current)
        {
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

            // §349. The per-section values, with the pre-§349 globals as the
            // fallback for each.
            //
            // A file written before §349 carries no section fields at all:
            // every colour reads 0, which is transparent, and applying that
            // would turn someone's text invisible. So a section colour is
            // taken only when the file actually claims one, and otherwise
            // comes from the single global that file DID carry - which is
            // exactly what that theme meant when it was made.
            merged.SpriteBoxBorderColorArgb =
                file.SpriteBoxBorderColorArgb != 0 ? file.SpriteBoxBorderColorArgb : file.BorderColorArgb;
            merged.SpriteBoxTextColorArgb =
                file.SpriteBoxTextColorArgb != 0 ? file.SpriteBoxTextColorArgb : file.TextColorArgb;

            if (!string.IsNullOrWhiteSpace(file.SpriteBoxFontFamilyName))
                merged.SpriteBoxFontFamilyName = file.SpriteBoxFontFamilyName;
            else if (!string.IsNullOrWhiteSpace(file.FontFamilyName))
                merged.SpriteBoxFontFamilyName = file.FontFamilyName;

            if (!string.IsNullOrWhiteSpace(file.SpriteBoxFontSizeName))
                merged.SpriteBoxFontSizeName = file.SpriteBoxFontSizeName;
            else if (!string.IsNullOrWhiteSpace(file.FontSizeName))
                merged.SpriteBoxFontSizeName = file.FontSizeName;

            merged.EncountersBorderColorArgb =
                file.EncountersBorderColorArgb != 0 ? file.EncountersBorderColorArgb : file.BorderColorArgb;
            merged.EncountersTextColorArgb =
                file.EncountersTextColorArgb != 0 ? file.EncountersTextColorArgb : file.TextColorArgb;

            if (!string.IsNullOrWhiteSpace(file.EncountersFontFamilyName))
                merged.EncountersFontFamilyName = file.EncountersFontFamilyName;
            else if (!string.IsNullOrWhiteSpace(file.FontFamilyName))
                merged.EncountersFontFamilyName = file.FontFamilyName;

            if (!string.IsNullOrWhiteSpace(file.EncountersFontSizeName))
                merged.EncountersFontSizeName = file.EncountersFontSizeName;
            else if (!string.IsNullOrWhiteSpace(file.FontSizeName))
                merged.EncountersFontSizeName = file.FontSizeName;

            merged.StatsBorderColorArgb =
                file.StatsBorderColorArgb != 0 ? file.StatsBorderColorArgb : file.BorderColorArgb;
            merged.StatsTextColorArgb =
                file.StatsTextColorArgb != 0 ? file.StatsTextColorArgb : file.TextColorArgb;

            if (!string.IsNullOrWhiteSpace(file.StatsFontFamilyName))
                merged.StatsFontFamilyName = file.StatsFontFamilyName;
            else if (!string.IsNullOrWhiteSpace(file.FontFamilyName))
                merged.StatsFontFamilyName = file.FontFamilyName;

            if (!string.IsNullOrWhiteSpace(file.StatsFontSizeName))
                merged.StatsFontSizeName = file.StatsFontSizeName;
            else if (!string.IsNullOrWhiteSpace(file.FontSizeName))
                merged.StatsFontSizeName = file.FontSizeName;

            merged.ButtonBorderColorArgb =
                file.ButtonBorderColorArgb != 0 ? file.ButtonBorderColorArgb : file.BorderColorArgb;
            merged.ButtonTextColorArgb =
                file.ButtonTextColorArgb != 0 ? file.ButtonTextColorArgb : file.TextColorArgb;

            if (!string.IsNullOrWhiteSpace(file.ButtonFontFamilyName))
                merged.ButtonFontFamilyName = file.ButtonFontFamilyName;
            else if (!string.IsNullOrWhiteSpace(file.FontFamilyName))
                merged.ButtonFontFamilyName = file.FontFamilyName;

            if (!string.IsNullOrWhiteSpace(file.ButtonFontSizeName))
                merged.ButtonFontSizeName = file.ButtonFontSizeName;
            else if (!string.IsNullOrWhiteSpace(file.FontSizeName))
                merged.ButtonFontSizeName = file.FontSizeName;

            // §380. A theme describes the whole look, so a file that says
            // nothing about its headings meant them bold - every theme made
            // before the flag existed had them so.
            merged.BoldHeadings = file.BoldHeadings ?? true;

            // §384. Absent means the look every theme had before the setting
            // existed; present, clamped like anything else that reaches the
            // window, so a hand-edited file cannot draw a 900px border.
            var defaults = new AppearanceSettings();
            merged.SpriteBoxBorderWidth = ThemeManager.ClampBorderWidth(file.SpriteBoxBorderWidth ?? defaults.SpriteBoxBorderWidth);
            merged.SpriteBoxCornerRadius = ThemeManager.ClampCornerRadius(file.SpriteBoxCornerRadius ?? defaults.SpriteBoxCornerRadius);
            merged.EncountersBorderWidth = ThemeManager.ClampBorderWidth(file.EncountersBorderWidth ?? defaults.EncountersBorderWidth);
            merged.EncountersCornerRadius = ThemeManager.ClampCornerRadius(file.EncountersCornerRadius ?? defaults.EncountersCornerRadius);
            merged.StatsBorderWidth = ThemeManager.ClampBorderWidth(file.StatsBorderWidth ?? defaults.StatsBorderWidth);
            merged.StatsCornerRadius = ThemeManager.ClampCornerRadius(file.StatsCornerRadius ?? defaults.StatsCornerRadius);
            merged.ButtonBorderWidth = ThemeManager.ClampBorderWidth(file.ButtonBorderWidth ?? defaults.ButtonBorderWidth);
            merged.ButtonCornerRadius = ThemeManager.ClampCornerRadius(file.ButtonCornerRadius ?? defaults.ButtonCornerRadius);

            // §387. Transparent when absent is the panel's own default, so an
            // older file lands on no panel, as it always meant.
            merged.SpriteRowBackgroundColorArgb = file.SpriteRowBackgroundColorArgb;
            merged.SpriteRowBorderColorArgb = file.SpriteRowBorderColorArgb;
            merged.MenuTextColorArgb = file.MenuTextColorArgb;
            merged.MenuHighlightColorArgb = file.MenuHighlightColorArgb;

            // §394: no fall-back to the global here - a theme that names no
            // menu font means the menu follows Statistics, which is the
            // same thing the global would have given it.
            merged.MenuFontFamilyName = file.MenuFontFamilyName ?? string.Empty;
            merged.MenuFontSizeName = file.MenuFontSizeName ?? string.Empty;
            merged.SpriteRowBorderWidth = ThemeManager.ClampBorderWidth(file.SpriteRowBorderWidth ?? defaults.SpriteRowBorderWidth);
            merged.SpriteRowCornerRadius = ThemeManager.ClampCornerRadius(file.SpriteRowCornerRadius ?? defaults.SpriteRowCornerRadius);

            // The receiving settings are already version 2 (Load migrates
            // before anything can import), so nothing here needs to migrate
            // again - and the flag must not be cleared, or the next Load
            // would seed the sections from the legacy globals and undo this.
            merged.SettingsVersion = 2;

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
        /// <summary>§382. The panel pictures, stored the way the window
        /// background is: one copy per client per panel in the same folder
        /// (panel-stats-client1.png, panel-table-client1.jpg), the previous
        /// copy under another extension deleted. <paramref name="slot"/> is
        /// one of <see cref="PanelImageSlots"/>; anything else is refused so
        /// a path can never be built from user text.</summary>
        /// <summary>§383. The per-client copies a picture can be saved as:
        /// the statistics panel, the encounter table, the sprite boxes.</summary>
        public static readonly IReadOnlyList<string> PanelImageSlots =
            new[] { "stats", "table", "sprites", "stats-frame", "table-frame", "sprites-frame", "sprite-row", "sprite-row-frame" };

        public static string SavePanelImage(string sourcePath, string slot)
        {
            if (!PanelImageSlots.Contains(slot))
                throw new ArgumentException("Unknown panel slot.", nameof(slot));

            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException(
                    "The selected panel picture no longer exists.",
                    sourcePath);
            }

            Directory.CreateDirectory(CustomBackgroundFolder);

            string extension = Path.GetExtension(sourcePath).ToLowerInvariant();

            if (string.IsNullOrWhiteSpace(extension))
                extension = ".png";

            int clientNumber = SessionPersistenceService.AppearanceClientNumber;

            string baseFileName = clientNumber >= 1
                ? $"panel-{slot}-client{clientNumber}"
                : $"panel-{slot}";

            string destinationPath = Path.Combine(CustomBackgroundFolder, $"{baseFileName}{extension}");

            // The source may BE the destination (Apply pressed twice with
            // nothing changed); copying a file onto itself would truncate it.
            if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(destinationPath), StringComparison.OrdinalIgnoreCase))
                return destinationPath;

            foreach (string existingFile in Directory.GetFiles(CustomBackgroundFolder, $"{baseFileName}.*"))
            {
                if (!string.Equals(existingFile, destinationPath, StringComparison.OrdinalIgnoreCase))
                    File.Delete(existingFile);
            }

            File.Copy(sourcePath, destinationPath, overwrite: true);

            return destinationPath;
        }

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
