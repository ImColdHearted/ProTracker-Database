using Foot_Tracker.Models;
using System.Text.Json;
using Serilog;

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

        /// <summary>§193. The copy taken before every save. It has been
        /// written since long before this section and read by nothing until
        /// it.</summary>
        private static readonly string BackupFile =
            Path.Combine(
                StatsFolder,
                "lifetime-stats.json.bak"
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
            // Admin Client isolation (§101): every lifetime mutation - the
            // view model's encounter/catch/shiny paths AND the boss/PVP
            // trackers' own tallies - funnels through this one method, so
            // this single check guarantees no isolated-session activity can
            // touch the real lifetime file. Callers get the current on-disk
            // stats back unchanged, which every call site already handles (it
            // is the same shape as the lock-timeout fallback below).
            //
            // §249: IsolatedSession, not AdminModeService - the admin override
            // is one reason a session is isolated, and this file must refuse
            // for every reason there will ever be. See IsolatedSession.
            if (IsolatedSession.IsActive)
                return LoadFromDisk();

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

        // Boss battle tallies for the Lifetime Stats window (see
        // MIGRATION_GUIDE.md §91). won == null means the outcome is unknown
        // - the manual boss cooldown card click registers a fight without
        // knowing how it went, so only BossBattles moves.
        public static LifetimeStats AddBossBattle(
            bool? won)
        {
            return Update(stats =>
            {
                stats.BossBattles++;

                if (won == true)
                    stats.BossVictories++;
                else if (won == false)
                    stats.BossLosses++;
            });
        }

        // PVP result tally - called by Tracking/PvpTracker when the
        // "You won/lost the battle" text resolves a battle it identified.
        public static LifetimeStats AddPvpResult(
            bool won)
        {
            return Update(stats =>
            {
                if (won)
                    stats.PvpWins++;
                else
                    stats.PvpLosses++;
            });
        }

        // ------------------------------------------------------------
        // PRIVATE DISK METHODS
        //
        // These do NOT acquire the mutex themselves.
        // Their caller must own it when performing an update.
        // ------------------------------------------------------------

        /// <summary>
        /// §190. Reads the lifetime statistics, and survives a file that
        /// cannot be read.
        ///
        /// This was the one JSON-backed store in the app with no tolerance at
        /// all: no try, no catch, anywhere in its chain. Every other one -
        /// the session, UI preferences, client names, the catch log, the
        /// encounter history - catches and carries on with defaults, and each
        /// of them says so in a comment. This one threw, and because
        /// MainWindowViewModel reads it from a FIELD INITIALIZER it threw
        /// before the main window existed, which App.axaml.cs turns into the
        /// startup error window. One unreadable file, and the app would not
        /// open at all.
        ///
        /// A user's copy was a run of 0x00 bytes at the file's old length,
        /// which is what an unclean shutdown leaves behind: the file system
        /// kept the size and lost the contents. Their log was empty for the
        /// same reason, so the report they were told to send held nothing.
        ///
        /// Tolerance here is deliberately not the silent kind the other
        /// stores use. Lifetime statistics are months of totals, so the
        /// unreadable file is MOVED ASIDE rather than left to be overwritten
        /// by the next save, and what happened is written to the log with the
        /// file's size and whether it was all zeros. Starting fresh is a
        /// visible loss; being unable to start at all is a worse one, and
        /// quarantining keeps any chance of recovery alive.
        /// </summary>
        private static LifetimeStats LoadFromDisk()
        {
            if (!File.Exists(StatsFile))
                return new LifetimeStats();

            string json;

            try
            {
                json = File.ReadAllText(StatsFile);
            }
            catch (Exception ex)
            {
                // Locked, unreadable, or gone between the check and the read.
                // Nothing to quarantine here - the file was never opened, so
                // it may be perfectly fine and simply busy.
                Log.Warning(ex, "Lifetime statistics could not be read from {Path} - continuing with empty totals for now", StatsFile);
                return new LifetimeStats();
            }

            try
            {
                LifetimeStats? stats =
                    JsonSerializer.Deserialize<LifetimeStats>(
                        json,
                        JsonOptions
                    );

                if (stats != null)
                    return stats;

                QuarantineUnreadableFile("the file parsed as JSON null", json);
            }
            catch (Exception ex)
            {
                QuarantineUnreadableFile(ex.Message, json);
            }

            // §193: the file is gone, but the copy taken before the last save
            // is not. Recovering it costs at most one save's worth of totals
            // instead of every month of them, and the file has been sitting
            // there unread all along.
            return LoadBackup() ?? new LifetimeStats();
        }

        /// <summary>§193. Reads the pre-save copy, or null if there is not one
        /// or it is no better than the file it is standing in for. Never
        /// throws: it runs on a path that already failed once, and a second
        /// failure has to end in empty totals rather than in a crash.</summary>
        private static LifetimeStats? LoadBackup()
        {
            if (!File.Exists(BackupFile))
                return null;

            try
            {
                LifetimeStats? stats =
                    JsonSerializer.Deserialize<LifetimeStats>(
                        File.ReadAllText(BackupFile),
                        JsonOptions
                    );

                if (stats is null)
                    return null;

                Log.Warning(
                    "Lifetime statistics were recovered from {Backup}, the copy taken before the " +
                    "last save. At most one save's worth of totals is missing.",
                    BackupFile);

                return stats;
            }
            catch (Exception ex)
            {
                Log.Warning(ex,
                    "The lifetime statistics backup at {Backup} could not be read either - " +
                    "starting from empty totals",
                    BackupFile);

                return null;
            }
        }

        /// <summary>§190. Moves an unreadable statistics file out of the way
        /// so the next save cannot overwrite it and the next launch starts
        /// clean, and records enough about it to tell a corrupted file apart
        /// from a file the file system emptied.</summary>
        private static void QuarantineUnreadableFile(string reason, string content)
        {
            bool allZeros = content.Length > 0 && content.All(c => c == '\0');

            string quarantined = Path.Combine(
                StatsFolder,
                $"lifetime-stats.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}.json");

            try
            {
                File.Move(StatsFile, quarantined, overwrite: true);
            }
            catch (Exception ex)
            {
                // The totals start again either way; this only decides
                // whether the old file is still there to look at.
                Log.Warning(ex, "Unreadable lifetime statistics file could not be moved aside");
                quarantined = "(it could not be moved, so it is still at " + StatsFile + ")";
            }

            Log.Error(
                "Lifetime statistics could not be read and were reset: {Reason}. " +
                "The file held {Length} characters and {ZeroNote}. The old file is at {Quarantined}. " +
                "This is what an unclean shutdown leaves behind, and it usually means other files " +
                "written around the same time are empty too.",
                reason,
                content.Length,
                allZeros ? "every one of them was a zero byte" : "was not all zero bytes",
                quarantined);
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

            // §193: the previous file becomes the backup BEFORE the new one
            // lands, which is the order this always used - what changes is
            // that LoadFromDisk now actually reads it. This ran on every
            // save and nothing had ever opened it, so a user whose statistics
            // file was destroyed had months of totals sitting in .bak while
            // the app told them it was starting over.
            if (File.Exists(StatsFile))
            {
                File.Copy(
                    StatsFile,
                    BackupFile,
                    overwrite: true
                );
            }

            // §193: and the write itself is durable now - see DurableFile.
            // The old sequence wrote a temp file with File.WriteAllText and
            // renamed it, which looks atomic and is not: the rename is
            // journalled metadata, the contents were only in the operating
            // system's cache, and a machine that stops between them keeps the
            // rename and loses the data. That is how a file becomes the right
            // length and all zeros.
            DurableFile.WriteAllText(
                StatsFile,
                json
            );
        }
    }
    }
