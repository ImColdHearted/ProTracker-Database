using Foot_Tracker.Models;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Foot_Tracker.Services
{
    /// <summary>
    /// §277. How each boss has gone: wins, losses, and attempts whose result
    /// nobody saw.
    ///
    /// The outcome was already being read. BossCooldownTracker has watched for
    /// PRO's "You won/lost the battle" line since the start, and since §91 that
    /// Won/Lost has fed the Lifetime Stats window's boss tallies - as ONE
    /// number for every boss together. Which boss it was is right there in the
    /// same method, and was thrown away. This keeps it.
    ///
    /// RECORDED PER ATTEMPT, NOT PER BATTLE. The tracker's lifetime tally
    /// counts each battle end, deliberately, so that each NPC of a two-NPC boss
    /// (Shary and Shaui, Medusa and Eldir) counts as its own fight. A win/loss
    /// RECORD wants the other granularity: beating both halves of one boss is
    /// one win, not two. So this is written where the cooldown is - the point
    /// the whole attempt resolves, which by construction happens once per
    /// attempt for single and multi-NPC bosses alike, and with the outcome of
    /// the battle that decided it (a loss to either NPC ends the attempt
    /// immediately; a win only reaches that point once every required NPC is
    /// beaten).
    ///
    /// Per client, like the cooldowns themselves and for the same reason: a
    /// record belongs to the PRO account that earned it.
    /// </summary>
    public static class BossRecordService
    {
        // Same folder every other per-client store uses - see
        // BossCooldownService.SaveFolder for why it is not next to the exe.
        private static readonly string SaveFolder =
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData
                ),
                "ProTracker",
                "Database"
            );

        private static string GetSavePath()
        {
            int clientNumber = SessionPersistenceService.ActiveClientNumber;

            string fileName = clientNumber >= 1
                ? $"boss-records-client{clientNumber}.json"
                : "boss-records.json";

            return Path.Combine(SaveFolder, fileName);
        }

        // No legacy migration: this store is new in §277, so there is no older
        // location for it to inherit from. A profile that has been playing for
        // months starts at 0-0 and fills from its next boss fight, which is
        // the honest answer - the per-boss outcomes were never written down
        // before this, so there is nothing to recover.

        private static readonly Dictionary<string, BossRecordEntry> records =
            new(StringComparer.OrdinalIgnoreCase);

        private static bool loaded;

        /// <summary>Raised after a record changes, so an open Boss Database or
        /// boss detail window can re-read rather than needing a refresh
        /// button. Raised from BossCooldownTracker's background loop, so
        /// subscribers must marshal to the UI thread themselves - the same
        /// contract PvpOpponentService.OpponentsChanged carries.</summary>
        public static event Action? RecordsChanged;

        /// <summary>Loads from disk on first access, like
        /// PvpOpponentService.Opponents - this is read by the UI and written by
        /// the tracker, so a lazy load beats a second startup wiring
        /// point.</summary>
        public static BossRecordEntry? Get(string? bossId)
        {
            if (string.IsNullOrWhiteSpace(bossId))
                return null;

            EnsureLoaded();

            return records.TryGetValue(bossId, out BossRecordEntry? entry) ? entry : null;
        }

        public static void Load()
        {
            loaded = true;

            LoadFromDisk();
        }

        /// <summary>Re-reads the now-active client's own file - called
        /// alongside the other per-client reloads when the tracked client
        /// changes, so a record does not follow the player onto another
        /// account.</summary>
        public static void ReloadForActiveClient()
        {
            Load();

            RecordsChanged?.Invoke();
        }

        private static void EnsureLoaded()
        {
            if (loaded)
                return;

            Load();
        }

        private static void LoadFromDisk()
        {
            records.Clear();

            string savePath = GetSavePath();

            if (!File.Exists(savePath))
                return;

            try
            {
                List<BossRecordEntry>? saved =
                    JsonSerializer.Deserialize<List<BossRecordEntry>>(File.ReadAllText(savePath));

                if (saved is null)
                    return;

                foreach (BossRecordEntry entry in saved)
                {
                    if (string.IsNullOrWhiteSpace(entry.BossId))
                        continue;

                    records[entry.BossId] = entry;
                }
            }
            catch (Exception ex)
            {
                // An unreadable file leaves an empty record rather than
                // stopping the app, the same as every other JSON store here.
                Log.Warning(ex, "Boss records could not be read from {Path}", savePath);
            }
        }

        /// <summary>
        /// §277. Records one resolved attempt against this boss.
        ///
        /// <paramref name="won"/> is null when the result was not read - the
        /// manual "start the cooldown" click on a Boss Database card, which has
        /// always registered a fight without knowing how it went. That counts
        /// as an attempt and as neither a win nor a loss, exactly as it already
        /// does for the lifetime tally.
        /// </summary>
        public static void Record(string? bossId, bool? won)
        {
            // Admin Client isolation (§101) - the same gate RegisterBossDefeat
            // applies, and for the same reason: admin-client play is not the
            // player's record.
            if (AdminModeService.IsActive)
                return;

            if (string.IsNullOrWhiteSpace(bossId))
                return;

            EnsureLoaded();

            if (!records.TryGetValue(bossId, out BossRecordEntry? entry))
            {
                entry = new BossRecordEntry { BossId = bossId };
                records[bossId] = entry;
            }

            if (won == true)
                entry.Wins++;
            else if (won == false)
                entry.Losses++;
            else
                entry.Unknown++;

            entry.LastAtUtc = DateTime.UtcNow;

            Save();

            RecordsChanged?.Invoke();
        }

        private static void Save()
        {
            string savePath = GetSavePath();

            string? folder = Path.GetDirectoryName(savePath);

            if (!string.IsNullOrWhiteSpace(folder))
                Directory.CreateDirectory(folder);

            var ordered = new List<BossRecordEntry>(records.Values);

            // §193: durable - see DurableFile.
            DurableFile.WriteAllText(
                savePath,
                JsonSerializer.Serialize(ordered, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
