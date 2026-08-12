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

        private static readonly string SettingsFile =
            Path.Combine(
                SettingsFolder,
                "appearance.json");

        public static AppearanceSettings Load()
        {
            try
            {
                if (!File.Exists(SettingsFile))
                    return new AppearanceSettings();

                string json = File.ReadAllText(SettingsFile);

                AppearanceSettings? settings =
                    JsonSerializer.Deserialize<AppearanceSettings>(json);

                return settings ?? new AppearanceSettings();
            }
            catch
            {
                return new AppearanceSettings();
            }
        }

        public static void Save(AppearanceSettings settings)
        {
            Directory.CreateDirectory(SettingsFolder);

            JsonSerializerOptions options = new()
            {
                WriteIndented = true
            };

            string json =
                JsonSerializer.Serialize(settings, options);

            File.WriteAllText(SettingsFile, json);
        }
        public static string CustomBackgroundFolder =>
    Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData),
        "PRO Tracker & Database",
        "Backgrounds");
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

            string destinationPath =
                Path.Combine(
                    CustomBackgroundFolder,
                    $"custom-background{extension}");

            foreach (string existingFile in
                     Directory.GetFiles(
                         CustomBackgroundFolder,
                         "custom-background.*"))
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