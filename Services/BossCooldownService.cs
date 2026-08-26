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
        // Same shared folder LifetimeStatsService/SessionPersistenceService save
        // to (%LocalAppData%\ProTracker\Database) rather than a "Data" folder next
        // to the built executable - that way cooldown history survives a rebuild/
        // republish (which wipes the bin output folder) the same way lifetime
        // stats and session saves already do.
        private static readonly string SaveFolder =
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData
                ),
                "ProTracker",
                "Database"
            );

        // Boss cooldowns are specific to whichever PRO ACCOUNT is logged into
        // the client this app instance is tracking (see
        // SessionPersistenceService.ActiveClientNumber, set from the client
        // picker/Assign Client - the same number current-session-client{N}.json
        // is already keyed by) - defeating a boss on one account does not put
        // the same boss on cooldown for a different account being tracked by
        // another instance. Without a per-client file, two accounts fighting
        // the same boss around the same time would stomp on a single shared
        // cooldown entry. Falls back to a client-less shared file only if
        // somehow no client number is set yet - shouldn't normally happen,
        // since SessionPersistenceService defaults to client 1.
        private static string GetSavePath()
        {
            int clientNumber = SessionPersistenceService.ActiveClientNumber;

            string fileName = clientNumber >= 1
                ? $"boss-cooldowns-client{clientNumber}.json"
                : "boss-cooldowns.json";

            return Path.Combine(SaveFolder, fileName);
        }

        // Two prior save locations, oldest first - both from before per-client
        // files existed, so there's no "which client" to preserve; see
        // MigrateLegacySaveIfNeeded for why only client 1 inherits either one.
        private static readonly string LegacyNextToExePath =
            Path.Combine(
                AppContext.BaseDirectory,
                "Data",
                "boss-cooldowns.json"
            );

        private static readonly string LegacySharedPath =
            Path.Combine(
                SaveFolder,
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

            // PropertyNameCaseInsensitive is required here - boss JSON files aren't
            // consistently cased (e.g. ThePumpkinKing.json uses "BossID" while most
            // others use "bossId"), and without this, deserialization would silently
            // leave BossId/Name empty for any file whose casing doesn't exactly match
            // the C# property names. This was a latent bug present before automatic
            // boss detection existed - RegisterBossDefeat (used by the manual boss
            // cooldown card click too) could have been affected by it already.
            return JsonSerializer.Deserialize<BossCooldownDefinition>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            );
        }

        /// <summary>
        /// All known (bossId, name) pairs from DataFiles/Bosses/*.json - used by
        /// Tracking/BossBattleDetector.cs to match OCR'd battle titles against real
        /// boss names.
        /// </summary>
        public static IReadOnlyList<(string BossId, string Name)> GetAllBossNames()
        {
            var results = new List<(string, string)>();

            string bossesFolder = Path.Combine(AppContext.BaseDirectory, "DataFiles", "Bosses");

            if (!Directory.Exists(bossesFolder))
                return results;

            foreach (string file in Directory.GetFiles(bossesFolder, "*.json"))
            {
                try
                {
                    string json = File.ReadAllText(file);

                    var definition = JsonSerializer.Deserialize<BossCooldownDefinition>(
                        json,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                    );

                    if (definition != null &&
                        !string.IsNullOrWhiteSpace(definition.BossId) &&
                        !string.IsNullOrWhiteSpace(definition.Name))
                    {
                        results.Add((definition.BossId, definition.Name));
                    }
                }
                catch
                {
                    // Skip malformed boss files, same as elsewhere this folder is scanned.
                }
            }

            return results;
        }

        private static readonly List<BossCooldownEntry>
            cooldowns = new();

        public static IReadOnlyList<BossCooldownEntry>
            Cooldowns => cooldowns;

        // Called at app startup (see App.axaml.cs), and again by
        // MainWindowViewModel.AssignTrackerClient every time the active client
        // changes - reloading here (instead of only ever loading once at
        // startup) is what makes switching clients actually swap in that
        // client's own cooldown data instead of continuing to show/save over
        // whichever client was active before.
        public static void Load()
        {
            cooldowns.Clear();

            string savePath = GetSavePath();

            MigrateLegacySaveIfNeeded(savePath);

            if (!File.Exists(savePath))
                return;

            try
            {
                string json =
                    File.ReadAllText(savePath);

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

        // One-time copy from a prior save location into the current client's
        // per-client file - covers both the original next-to-the-exe location
        // and the shared (not-yet-per-client) ProTracker/Database file it later
        // moved to, so a rebuild/republish or this per-client upgrade doesn't
        // silently reset boss cooldown history back to empty. Only migrates
        // into client 1: before per-client files existed there was only ever
        // one shared save, so it's treated as belonging to the default/first
        // client rather than guessing which client it "really" tracked -
        // clients 2+ simply start with an empty cooldown list, same as any
        // other newly-tracked client would.
        private static void MigrateLegacySaveIfNeeded(string savePath)
        {
            try
            {
                if (File.Exists(savePath))
                    return;

                if (SessionPersistenceService.ActiveClientNumber != 1)
                    return;

                string? legacySource =
                    File.Exists(LegacySharedPath) ? LegacySharedPath :
                    File.Exists(LegacyNextToExePath) ? LegacyNextToExePath :
                    null;

                if (legacySource is null)
                    return;

                Directory.CreateDirectory(SaveFolder);

                File.Copy(
                    legacySource,
                    savePath,
                    overwrite: false
                );
            }
            catch
            {
                // Migration failure must never stop the application - worst
                // case, cooldown history starts fresh in the new location.
            }
        }

        public static void Save()
        {
            string savePath = GetSavePath();

            string? folder =
                Path.GetDirectoryName(savePath);

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
                savePath,
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