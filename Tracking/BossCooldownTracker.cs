using System;
using System.Collections.Generic;
using System.Linq;
using Serilog;
using SkiaSharp;
using System.Threading;
using System.Threading.Tasks;
using Foot_Tracker.Services;
using Foot_Tracker.Tracking.Capture;

namespace Foot_Tracker.Tracking
{
    /// <summary>
    /// Watches for boss battles and automatically starts a boss's cooldown when one
    /// ends - independent of EncounterTracker (wild-encounter hunting). This runs
    /// whenever a PRO client is assigned, whether or not the user has pressed Play
    /// to hunt a specific Pokemon - see MainWindowViewModel.AssignTrackerClient.
    ///
    /// Deliberately a separate tracker rather than folded into EncounterTracker:
    /// an earlier version did exactly that, but it meant boss-title OCR only ever
    /// ran while hunting was active, and wasted OCR calls scanning for a boss title
    /// during every ordinary wild encounter too. This tracker only ever does boss
    /// detection - see BossBattleDetector.cs for the actual OCR.
    /// </summary>
    public sealed class BossCooldownTracker : IDisposable
    {
        private CancellationTokenSource? cancellationTokenSource;
        private Task? trackingTask;

        private string? currentBossId;
        private string? currentSubNpc;
        private bool bossCooldownRegisteredForThisBattle;
        private int bossDetectionAttempts;

        // Tracks, per multi-NPC bossId (see BossBattleDetector.MultiNpcRequiresAll),
        // which of its required NPCs have been beaten so far this cooldown cycle.
        // Deliberately NOT cleared by the "battle window closed" reset in ScanOnce -
        // unlike currentBossId, this has to survive the gap between one NPC's
        // battle window closing and the next NPC's battle window opening, since a
        // dual-NPC boss like Shary & Shaui is fought as two separate battle
        // sessions, not one continuous battle. Cleared once every required NPC for
        // a bossId has been seen and its cooldown is actually registered, or on
        // Start(), same as this tracker's other per-cycle state. In-memory only,
        // same as everything else here - a boss beaten halfway through right as
        // the app is closed simply starts its pair over on the next launch.
        private readonly Dictionary<string, HashSet<string>> defeatedSubNpcsByBossId =
            new(StringComparer.OrdinalIgnoreCase);

        // Raised from 5 (2.5s worth of tries at ScanDelayMs=500) - that budget was
        // getting fully burned through during a boss battle's intro animation
        // (the title text renders gradually/garbled for the first couple of
        // seconds), after which this tracker gave up silently for the rest of the
        // battle and only got another chance if BattleWindowLocator happened to
        // lose and re-find the battle window on its own (which resets
        // bossDetectionAttempts back to 0). That's exactly what a real tester's
        // log showed: "VS. Erika" didn't OCR cleanly until ~35 seconds after the
        // battle actually started, and detection only succeeded because of one of
        // those incidental resets. 40 attempts (~20s) comfortably covers the intro
        // animation without relying on that coincidence. This higher budget still
        // only matters for genuine boss battles or unreadable OCR - see
        // confirmedWild below, which makes an ordinary wild encounter bail out
        // after a single attempt instead of spending this whole budget on a
        // battle that was never going to match a boss name.
        private const int MaxBossDetectionAttempts = 40;

        // Lighter polling cadence than EncounterTracker's - boss fights run for a
        // while, so there's no need to poll as aggressively as catch-result
        // detection does, and this may run continuously for the whole app session.
        private const int ScanDelayMs = 500;

        private static readonly IWindowCaptureService captureService = WindowCaptureServiceFactory.Instance;

        public bool IsRunning =>
            cancellationTokenSource != null &&
            !cancellationTokenSource.IsCancellationRequested;

        /// <summary>True from the moment a boss battle's name is confirmed until that
        /// battle's window disappears. MainWindowViewModel reads this once, right
        /// after EncounterTracker.Start(), to cover the edge case where hunting is
        /// started mid-boss-fight (BossCooldownActiveChanged only fires on a
        /// transition, so a tracker created after the transition already happened
        /// would otherwise never learn about it).</summary>
        public bool IsBossBattleActive => currentBossId is not null;

        public event Action<string>? StatusChanged;

        /// <summary>Raised with the bossId once a boss's cooldown is automatically started.</summary>
        public event Action<string>? BossCooldownRegistered;

        /// <summary>
        /// Raised true the moment a boss battle's name is confirmed, and false once
        /// that battle's window disappears. EncounterTracker (wild-encounter
        /// hunting) subscribes to this via MainWindowViewModel so it stops trying
        /// to identify/track a "wild encounter" during a boss fight - both
        /// trackers otherwise see the same battle window and, before this, raced
        /// each other: EncounterTracker would OCR the boss's active Pokemon as if
        /// it were a wild encounter (confirmed via a real tester's log) while this
        /// tracker was still only supposed to be watching for the battle's Win/
        /// Loss text.
        /// </summary>
        public event Action<bool>? BossBattleActiveChanged;

        public void Start()
        {
            if (IsRunning)
                return;

            currentBossId = null;
            currentSubNpc = null;
            bossCooldownRegisteredForThisBattle = false;
            bossDetectionAttempts = 0;
            defeatedSubNpcsByBossId.Clear();

            cancellationTokenSource = new CancellationTokenSource();
            trackingTask = Task.Run(() => TrackingLoopAsync(cancellationTokenSource.Token));

            Log.Information("Boss cooldown tracking started.");
        }

        public async Task StopAsync()
        {
            if (cancellationTokenSource == null)
                return;

            cancellationTokenSource.Cancel();

            try
            {
                if (trackingTask != null)
                    await trackingTask;
            }
            catch (OperationCanceledException)
            {
                // Expected when stopping.
            }

            cancellationTokenSource.Dispose();
            cancellationTokenSource = null;
            trackingTask = null;

            if (currentBossId is not null)
            {
                // Safety valve: if this tracker gets stopped mid-boss-fight,
                // don't leave EncounterTracker permanently paused.
                currentBossId = null;
                BossBattleActiveChanged?.Invoke(false);
            }

            Log.Information("Boss cooldown tracking stopped.");
        }

        private async Task TrackingLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    ScanOnce();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Boss cooldown tracking loop error");
                }

                await Task.Delay(ScanDelayMs, cancellationToken);
            }
        }

        private static SKBitmap? CaptureScreenshot()
        {
            byte[]? pngBytes = captureService.CaptureSelectedWindowPng();
            return pngBytes is null ? null : ImageOps.DecodePng(pngBytes);
        }

        private void ScanOnce()
        {
            using SKBitmap? screenshot = CaptureScreenshot();

            // No status message here for a missing screenshot - EncounterTracker
            // (if also running, e.g. the user is hunting too) already reports
            // "Waiting for PROClient..."; this tracker doesn't need to duplicate it.
            if (screenshot == null)
                return;

            bool battleExists = BattleWindowLocator.TryLocate(screenshot, out SKRectI battleBounds);

            if (!battleExists)
            {
                if (currentBossId is not null)
                {
                    // The boss battle we were tracking just closed - let
                    // EncounterTracker resume normal wild-encounter detection.
                    BossBattleActiveChanged?.Invoke(false);
                }

                currentBossId = null;
                currentSubNpc = null;
                bossCooldownRegisteredForThisBattle = false;
                bossDetectionAttempts = 0;
                return;
            }

            if (currentBossId is null)
            {
                if (bossDetectionAttempts >= MaxBossDetectionAttempts)
                    return;

                bossDetectionAttempts++;

                if (!BossBattleDetector.TryDetectBoss(
                        screenshot, battleBounds, out string? bossId, out string? bossName,
                        out bool confirmedWild, out string? matchedSubNpc))
                {
                    if (confirmedWild)
                    {
                        // Definitely a wild encounter, not a boss - no point
                        // spending the rest of the attempt budget re-OCRing a
                        // battle title that will never match a boss name.
                        bossDetectionAttempts = MaxBossDetectionAttempts;
                    }

                    return;
                }

                currentBossId = bossId;
                currentSubNpc = matchedSubNpc;
                bossCooldownRegisteredForThisBattle = false;

                Log.Information("Boss battle detected: {BossName}", bossName);
                StatusChanged?.Invoke($"Boss battle detected: {bossName}");

                // Tell EncounterTracker to stand down for this battle - it's a
                // boss, not a wild Pokemon, so it must not try to OCR an
                // "encounter"/catch attempt out of the boss's team.
                BossBattleActiveChanged?.Invoke(true);
            }

            if (bossCooldownRegisteredForThisBattle)
                return;

            BossBattleOutcome outcome = BossBattleDetector.DetectBattleEnd(screenshot, battleBounds);

            if (outcome == BossBattleOutcome.None)
                return;

            bossCooldownRegisteredForThisBattle = true;

            // Multi-NPC bosses (e.g. Shary & Shaui) must not start their cooldown
            // until EVERY required NPC has been beaten at least once this cycle -
            // regardless of which order the player fights them in. currentSubNpc
            // names which NPC THIS battle was against; progress on whichever
            // other NPC(s) this boss still needs lives in defeatedSubNpcsByBossId
            // across the gap until that NPC's own separate battle window opens.
            bool requiresAllNpcs = BossBattleDetector.MultiNpcRequiresAll.Contains(currentBossId!);

            if (requiresAllNpcs)
            {
                if (outcome == BossBattleOutcome.Lost)
                {
                    // A loss against EITHER required NPC ends the whole attempt
                    // immediately - confirmed by a real user: losing means the
                    // player doesn't get a chance to fight the other NPC at all,
                    // so there's no "still waiting" state worth preserving here.
                    // Clear any progress from an earlier win against the other
                    // NPC this cycle (if there was one) and fall through to
                    // register the cooldown right away, instead of waiting for a
                    // second fight that is never going to happen.
                    defeatedSubNpcsByBossId.Remove(currentBossId!);
                }
                else
                {
                    if (!defeatedSubNpcsByBossId.TryGetValue(currentBossId!, out HashSet<string>? defeatedSoFar))
                    {
                        defeatedSoFar = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        defeatedSubNpcsByBossId[currentBossId!] = defeatedSoFar;
                    }

                    if (currentSubNpc is not null)
                        defeatedSoFar.Add(currentSubNpc);

                    string[] requiredSubNpcs = BossBattleDetector.MultiNpcSubNames[currentBossId!];

                    if (!requiredSubNpcs.All(defeatedSoFar.Contains))
                    {
                        Log.Information(
                            "Boss cooldown not started yet for {BossId} - beaten {DefeatedCount}/{RequiredCount} required NPC(s) so far ({DefeatedNames}).",
                            currentBossId, defeatedSoFar.Count, requiredSubNpcs.Length, string.Join(", ", defeatedSoFar));
                        StatusChanged?.Invoke(
                            $"Defeated {currentSubNpc ?? "one NPC"} - cooldown starts once the other NPC is beaten too.");

                        return;
                    }

                    // Every required NPC has now been beaten - clear this boss's
                    // progress so the next cooldown cycle starts from scratch.
                    defeatedSubNpcsByBossId.Remove(currentBossId!);
                }
            }

            BossCooldownService.RegisterBossDefeat(currentBossId!);

            Log.Information("Boss cooldown started for {BossId} (outcome: {Outcome})", currentBossId, outcome);

            StatusChanged?.Invoke(
                requiresAllNpcs && outcome == BossBattleOutcome.Lost
                    ? $"Lost to {currentSubNpc ?? "the boss"} - the attempt is over, cooldown started."
                    : "Boss battle ended - cooldown started.");

            BossCooldownRegistered?.Invoke(currentBossId!);
        }

        public void Dispose()
        {
            cancellationTokenSource?.Cancel();
            cancellationTokenSource?.Dispose();

            cancellationTokenSource = null;
            trackingTask = null;
        }
    }
}