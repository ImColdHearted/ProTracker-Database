using System;
using System.IO;
using System.Text.Json;
using Foot_Tracker.Models;

namespace Foot_Tracker.Services
{
    public static class BossRepository
    {
        public static BossData Load(string bossId)
        {
            if (string.IsNullOrWhiteSpace(bossId))
            {
                throw new ArgumentException(
                    "A boss ID must be provided.",
                    nameof(bossId));
            }

            string fileName = $"{bossId}.json";

            string filePath = Path.Combine(
                AppContext.BaseDirectory,
                "DataFiles",
                "Bosses",
                fileName);

            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException(
                    $"The boss file could not be found:\n{filePath}",
                    filePath);
            }

            string json = File.ReadAllText(filePath);

            JsonSerializerOptions options = new()
            {
                PropertyNameCaseInsensitive = true
            };

            BossData? boss = JsonSerializer.Deserialize<BossData>(
                json,
                options);

            if (boss == null)
            {
                throw new InvalidDataException(
                    $"The boss file '{fileName}' could not be read.");
            }

            return boss;
        }
    }
}