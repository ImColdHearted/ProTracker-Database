using System;
using System.IO;
using System.Text.Json;
using Foot_Tracker.Models;

namespace Foot_Tracker.Services
{
    public static class EventSettingsService
    {
        // The list shown in the Event dropdown - add/rename/remove entries here as
        // PRO's actual event calendar changes. "None" should always stay first,
        // representing "no event currently active".
        public static readonly string[] CurrentEventOptions =
        {
            "None",
            "Summer",
            "Halloween",
            "Christmas",
            "Valentine's",
            "Easter",
            "May 4th",
            "Bidoof Day",
            "Pikachu World Quest",
            "April Fool's"
        };

        private static readonly string SettingsFolder =
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "PRO Tracker & Database");

        private static readonly string SettingsFile =
            Path.Combine(
                SettingsFolder,
                "event.json");

        public static EventSettings Load()
        {
            try
            {
                if (!File.Exists(SettingsFile))
                    return new EventSettings();

                string json = File.ReadAllText(SettingsFile);

                EventSettings? settings =
                    JsonSerializer.Deserialize<EventSettings>(json);

                return settings ?? new EventSettings();
            }
            catch
            {
                return new EventSettings();
            }
        }

        public static void Save(EventSettings settings)
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
    }
}