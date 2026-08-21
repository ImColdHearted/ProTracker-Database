using Foot_Tracker.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Foot_Tracker.Services
{
    public static class BossCooldownService
    {
        private static readonly string SavePath =
            Path.Combine(
                AppContext.BaseDirectory,
                "Data",
                "boss-cooldowns.json"
            );

        private static readonly Dictionary<string, int>
            CustomCooldownDays =
                new(StringComparer.OrdinalIgnoreCase)
                {
                    // Add the one special boss here later.
                    // Example:
                    // ["Special Boss Name"] = 6
                };

        private static BossCooldownDefinition? LoadBossDefinition(
    string bossId)
        {
            string path =
                Path.Combine(
                    AppContext.BaseDirectory,
                    "DataFiles",
                    "Bosses",
                    $"{bossId}.json"
                );

            if (!File.Exists(path))
                return null;

            string json =
                File.ReadAllText(path);

            return JsonSerializer.Deserialize<
                BossCooldownDefinition
            >(json);
        }

        private static readonly List<BossCooldownEntry>
            cooldowns = new();

        public static IReadOnlyList<BossCooldownEntry>
            Cooldowns => cooldowns;

        public static void Load()
        {
            cooldowns.Clear();

            if (!File.Exists(SavePath))
                return;

            try
            {
                string json =
                    File.ReadAllText(SavePath);

                List<BossCooldownEntry>? loaded =
                    JsonSerializer.Deserialize<
                        List<BossCooldownEntry>
                    >(json);

                if (loaded != null)
                {
                    cooldowns.AddRange(loaded);
                }
            }
            catch
            {
                // Keep empty cooldown list if file is damaged.
            }
        }

        public static void Save()
        {
            string? folder =
                Path.GetDirectoryName(SavePath);

            if (!string.IsNullOrWhiteSpace(folder))
            {
                Directory.CreateDirectory(folder);
            }

            string json =
                JsonSerializer.Serialize(
                    cooldowns,
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
                    }
                );

            File.WriteAllText(
                SavePath,
                json
            );
        }
        public static void RegisterBossDefeat(
            string bossId)
        {
            BossCooldownDefinition? definition =
                LoadBossDefinition(bossId);

            if (definition == null)
                return;

            DateTime now =
                DateTime.Now;

            BossCooldownEntry? existing =
                cooldowns.FirstOrDefault(
                    x => x.BossName.Equals(
                        definition.Name,
                        StringComparison.OrdinalIgnoreCase
                    )
                );

            if (existing == null)
            {
                existing =
                    new BossCooldownEntry
                    {
                        BossName =
                            definition.Name
                    };

                cooldowns.Add(existing);
            }

            existing.LastDefeated =
                now;

            existing.ReadyAt =
                now.AddHours(
                    definition.BossCooldown
                );

            Save();
        }

        public static BossCooldownEntry?
            GetCooldown(string bossName)
        {
            return cooldowns.FirstOrDefault(
                x => x.BossName.Equals(
                    bossName,
                    StringComparison.OrdinalIgnoreCase
                )
            );
        }

        private static int GetCooldownDays(
            string bossName)
        {
            if (CustomCooldownDays.TryGetValue(
                    bossName,
                    out int days))
            {
                return days;
            }

            return 12;
        }

    }
}