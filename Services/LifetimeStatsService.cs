using Foot_Tracker.Models;
using System.Text.Json;

namespace Foot_Tracker.Services
{
    public static class LifetimeStatsService
    {
        private static readonly string StatsFolder =
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData
                ),
                "ProTracker",
                "Database"
            );

        private static readonly string StatsFile =
            Path.Combine(
                StatsFolder,
                "lifetime-stats.json"
            );

        private static readonly JsonSerializerOptions JsonOptions =
            new()
            {
                WriteIndented = true
            };

        // This mutex is shared between separate Pro Tracker processes.
        private static readonly Mutex StatsMutex =
            new(
                false,
                @"Local\ProTracker_LifetimeStats"
            );

        // ------------------------------------------------------------
        // LOAD
        // ------------------------------------------------------------

        public static LifetimeStats Load()
        {
            bool lockTaken = false;

            try
            {
                try
                {
                    lockTaken =
                        StatsMutex.WaitOne(
                            TimeSpan.FromSeconds(5)
                        );
                }
                catch (AbandonedMutexException)
                {
                    lockTaken = true;
                }

                if (!lockTaken)
                {
                    throw new IOException(
                        "Could not obtain the lifetime statistics lock."
                    );
                }

                return LoadFromDisk();
            }
            finally
            {
                if (lockTaken)
                    StatsMutex.ReleaseMutex();
            }
        }

        // ------------------------------------------------------------
        // LEGACY FULL SAVE
        // ------------------------------------------------------------

        public static void Save(
            LifetimeStats stats)
        {
            bool lockTaken = false;

            try
            {
                lockTaken =
                    StatsMutex.WaitOne(
                        TimeSpan.FromSeconds(5)
                    );

                if (!lockTaken)
                    return;

                SaveToDisk(stats);
            }
            catch (AbandonedMutexException)
            {
                lockTaken = true;

                SaveToDisk(stats);
            }
            finally
            {
                if (lockTaken)
                    StatsMutex.ReleaseMutex();
            }
        }

        // ------------------------------------------------------------
        // SHARED UPDATE
        // ------------------------------------------------------------

        public static LifetimeStats Update(
            Action<LifetimeStats> updateAction)
        {
            bool lockTaken = false;

            try
            {
                try
                {
                    lockTaken =
                        StatsMutex.WaitOne(
                            TimeSpan.FromSeconds(5)
                        );
                }
                catch (AbandonedMutexException)
                {
                    // We acquired ownership when the other
                    // process terminated unexpectedly.
                    lockTaken = true;
                }

                if (!lockTaken)
                    return LoadFromDisk();

                // CRITICAL:
                // Reload AFTER obtaining the mutex.
                LifetimeStats stats =
                    LoadFromDisk();

                updateAction(stats);

                SaveToDisk(stats);

                return stats;
            }
            finally
            {
                if (lockTaken)
                    StatsMutex.ReleaseMutex();
            }
        }

        // ------------------------------------------------------------
        // CONVENIENCE METHODS
        // ------------------------------------------------------------

        public static LifetimeStats AddEncounter(
            string pokemonName)
        {
            return Update(stats =>
            {
                stats.TotalEncounters++;

                if (!string.IsNullOrWhiteSpace(
                        pokemonName))
                {
                    if (!stats.PokemonEncounters.TryGetValue(
                            pokemonName,
                            out long count))
                    {
                        count = 0;
                    }

                    stats.PokemonEncounters[pokemonName] =
                        count + 1;
                }
            });
        }

        public static LifetimeStats AddSuccessfulCatch()
        {
            return Update(stats =>
            {
                stats.SuccessfulCatches++;
            });
        }

        public static LifetimeStats AddFailedCatch()
        {
            return Update(stats =>
            {
                stats.FailedCatches++;
            });
        }

        public static LifetimeStats AddShinyEncounter()
        {
            return Update(stats =>
            {
                stats.ShinyEncounters++;
            });
        }

        public static LifetimeStats AddFormEncounter()
        {
            return Update(stats =>
            {
                stats.FormEncounters++;
            });
        }

        public static LifetimeStats AddHuntingTime(
            TimeSpan amount)
        {
            return Update(stats =>
            {
                stats.TotalHuntingTime += amount;
            });
        }

        // Called by Services/PvpOpponentService.RegisterBattle for every
        // detected PVP battle - the returned LifetimeStats lets the caller read
        // back the updated lifetime count for opponentName immediately (needed
        // to stamp that count onto the battle-log entry being created), without
        // a second Update/lock round trip.
        public static LifetimeStats AddPvpBattle(
            string opponentName)
        {
            return Update(stats =>
            {
                if (!string.IsNullOrWhiteSpace(
                        opponentName))
                {
                    if (!stats.PvpOpponentBattleCounts.TryGetValue(
                            opponentName,
                            out long count))
                    {
                        count = 0;
                    }

                    stats.PvpOpponentBattleCounts[opponentName] =
                        count + 1;
                }
            });
        }

        // ------------------------------------------------------------
        // PRIVATE DISK METHODS
        //
        // These do NOT acquire the mutex themselves.
        // Their caller must own it when performing an update.
        // ------------------------------------------------------------

        private static LifetimeStats LoadFromDisk()
        {
            if (!File.Exists(StatsFile))
                return new LifetimeStats();

            string json =
                File.ReadAllText(StatsFile);

            LifetimeStats? stats =
                JsonSerializer.Deserialize<LifetimeStats>(
                    json,
                    JsonOptions
                );

            if (stats == null)
            {
                throw new InvalidDataException(
                    "Lifetime stats could not be read."
                );
            }

            return stats;
        }

        private static void SaveToDisk(
            LifetimeStats stats)
        {
            Directory.CreateDirectory(
                StatsFolder
            );

            string json =
                JsonSerializer.Serialize(
                    stats,
                    JsonOptions
                );

            string tempFile =
                StatsFile + ".tmp";

            string backupFile =
                StatsFile + ".bak";

            File.WriteAllText(
                tempFile,
                json
            );

            // Preserve the previous known file before replacing it.
            if (File.Exists(StatsFile))
            {
                File.Copy(
                    StatsFile,
                    backupFile,
                    overwrite: true
                );
            }

            File.Move(
                tempFile,
                StatsFile,
                true
            );
        }
    }
    }
