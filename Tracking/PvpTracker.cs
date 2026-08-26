using System;
using Serilog;
using SkiaSharp;
using System.Threading;
using System.Threading.Tasks;
using Foot_Tracker.Services;
using Foot_Tracker.Tracking.Capture;

namespace Foot_Tracker.Tracking
{
    /// <summary>
    /// Watches for PVP battles and records each opponent's username into the
    /// "Previously Battled Users" list (see Services/PvpOpponentService.cs) -
    /// independent of EncounterTracker/BossCooldownTracker, same reasoning as
    /// BossCooldownTracker's own doc comment: a dedicated tracker means PVP-title
    /// OCR only runs for battles that are actually PVP, without EncounterTracker
    /// wasting cycles on it during every ordinary wild encounter, and without
    /// BossCooldownTracker needing to know anything about PVP at all.
    ///
    /// Phase 1 only records who was battled - it doesn't yet read anything about
    /// their team. See PvpOpponentService.RegisterBattle's remarks for that
    /// planned follow-up. It DOES watch for the "You won/lost the battle" result
    /// text before considering a battle over, reusing
    /// BossBattleDetector.DetectBattleEnd directly (both battle types render the
    /// exact same result message) - see ScanOnce for why relying on the battle
    /// window merely disappearing isn't good enough on its own to detect the
    /// END of a battle, NOR (see awaitingWindowClear) good enough on its own to
    /// safely start detecting the START of a new one right after.
    /// </summary>
    public sealed class PvpTracker : IDisposable
    {
        private CancellationTokenSource? cancellationTokenSource;
        private Task? trackingTask;

        // The opponent identified for the battle currently in progress, or null
        // if no PVP battle is currently being tracked.
        private string? currentOpponentName;

        private int pvpDetectionAttempts;

        // While a battle is in progress (currentOpponentName is set), counts
        // consecutive scans where the battle window couldn't be located at all -
        // see ScanOnce's "already identified" branch for why this exists.
        private int consecutiveMissedScansWhileActive;

        // Set the moment a battle's "won/lost the battle" text is read, and
        // cleared only once the battle window is confirmed fully gone
        // (BattleWindowLocator fails to locate it). The result screen for
        // "<Player> VS. <Opponent>" typically stays up for a moment after the
        // result text appears - without this gate, the very next scan would
        // immediately re-detect that same still-visible opponent as a brand
        // new battle before the window ever actually closed, double-counting
        // one real battle as two (confirmed via a real tester's screenshots:
        // "Jagenhgar" and "Jagenhgar N" logged ~7 minutes apart for what was
        // one battle that ended by the opponent surrendering). While this is
        // true, ScanOnce skips PVP detection entirely and just waits for the
        // window to clear.
        private bool awaitingWindowClear;

        // Same budget/reasoning as BossCooldownTracker.MaxBossDetectionAttempts -
        // 40 attempts (~20s at ScanDelayMs=500) comfortably covers a battle's intro
        // animation without needing a lucky BattleWindowLocator flicker to get a
        // second chance. confirmedNotPvp (wild encounter or a recognized boss)
        // still exhausts this budget in one attempt instead of spending it all.
        private const int MaxPvpDetectionAttempts = 40;

        // Once a battle is in progress, a single scan where the window can't be
        // located does NOT mean the battle ended - the player may have briefly
        // opened their Pokemon summary, the switch-Pokemon screen, or another PRO
        // menu that temporarily covers the battle view (the exact "don't re-arm on
        // a temporary cover" principle EncounterTracker already applies to wild
        // encounters - see its own ScanOnce). This is the fallback ceiling before
        // giving up anyway, in case the "won/lost the battle" text is ever missed
        // entirely (e.g. a disconnect mid-battle) - 10 scans (~5s) survives a quick
        // menu peek without leaving tracking stuck indefinitely on a battle that
        // truly is gone.
        private const int MissedScansBeforeForceReset = 10;

        // Same lighter cadence as BossCooldownTracker - this may run continuously
        // for the whole app session, independent of Play/hunting.
        private const int ScanDelayMs = 500;

        private static readonly IWindowCaptureService captureService = WindowCaptureServiceFactory.Instance;

        public bool IsRunning =>
            cancellationTokenSource != null &&
            !cancellationTokenSource.IsCancellationRequested;

        /// <summary>True from the moment a PVP opponent is identified until the
        /// battle's "won/lost the battle" result is read (or, as a fallback, the
        /// battle window stays unreadable for a real stretch - see
        /// MissedScansBeforeForceReset). MainWindowViewModel reads this once,
        /// right after EncounterTracker.Start(), to cover the edge case where
        /// hunting is started mid-PVP-battle - same reasoning as
        /// BossCooldownTracker.IsBossBattleActive.</summary>
        public bool IsPvpBattleActive => currentOpponentName is not null;

        public event Action<string>? StatusChanged;

        /// <summary>Raised with the opponent's username once a PVP battle is
        /// automatically detected - PreviouslyBattledUsersViewModel (if its window
        /// is currently open) uses this to refresh its list live instead of only
        /// showing whatever was on disk when the window was opened.</summary>
        public event Action<string>? OpponentDetected;

        /// <summary>
        /// Raised true the moment a PVP opponent is identified, and false once
        /// that battle's window closes. EncounterTracker subscribes to this via
        /// MainWindowViewModel (alongside BossCooldownTracker's identical signal)
        /// so it stops trying to identify/track a "wild encounter" during a PVP
        /// battle - PVP screens show real Pokemon sprites/names too, so without
        /// this EncounterTracker would OCR the opponent's active Pokemon as if it
        /// were a wild encounter, the same interference problem boss battles had.
        /// </summary>
        public event Action<bool>? PvpBattleActiveChanged;

        public void Start()
        {
            if (IsRunning)
                return;

            currentOpponentName = null;
            pvpDetectionAttempts = 0;
            consecutiveMissedScansWhileActive = 0;
            awaitingWindowClear = false;

            cancellationTokenSource = new CancellationTokenSource();
            trackingTask = Task.Run(() => TrackingLoopAsync(cancellationTokenSource.Token));

            Log.Information("PVP tracking started.");
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

            if (currentOpponentName is not null)
            {
                // Safety valve: if this tracker gets stopped mid-battle, don't
                // leave EncounterTracker permanently paused.
                currentOpponentName = null;
                PvpBattleActiveChanged?.Invoke(false);
            }

            Log.Information("PVP tracking stopped.");
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
                    Log.Error(ex, "PVP tracking loop error");
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

            // No status message here for a missing screenshot - EncounterTracker/
            // BossCooldownTracker already report "Waiting for PROClient..." if
            // either of them is also running.
            if (screenshot == null)
                return;

            bool battleExists = BattleWindowLocator.TryLocate(screenshot, out SKRectI battleBounds);

            if (!battleExists)
            {
                // Any previously-ended battle's result screen is now confirmed
                // gone - safe to detect a new battle again (see
                // awaitingWindowClear's comment).
                awaitingWindowClear = false;

                if (currentOpponentName is null)
                {
                    // Not yet identified - nothing recorded for this battle
                    // window yet, so it's safe to reset immediately.
                    pvpDetectionAttempts = 0;
                    return;
                }

                // Already identified - a single missed read does NOT mean the
                // battle ended (see MissedScansBeforeForceReset's comment above).
                // Only give up once the window has stayed unreadable for a real
                // stretch, as a fallback in case the result text is never read.
                consecutiveMissedScansWhileActive++;

                if (consecutiveMissedScansWhileActive < MissedScansBeforeForceReset)
                    return;

                Log.Information(
                    "PVP battle window unreadable for {Scans} consecutive scans - " +
                    "assuming the battle against {OpponentName} ended.",
                    consecutiveMissedScansWhileActive, currentOpponentName);

                currentOpponentName = null;
                consecutiveMissedScansWhileActive = 0;
                PvpBattleActiveChanged?.Invoke(false);
                return;
            }

            consecutiveMissedScansWhileActive = 0;

            if (currentOpponentName is not null)
            {
                // Already identified this battle's opponent - the only thing left
                // to watch for is the result text. Relying on the battle window
                // merely disappearing to mean "battle over" is what caused this
                // opponent to get (re-)detected mid-fight in the first place: a
                // real tester's screenshot showed the SAME ongoing PVP match
                // logged twice under two slightly different OCR'd names
                // ("Shikanokonoko" and "Shikanokonoko I") a couple of minutes
                // apart, from BattleWindowLocator losing the window for a moment
                // (almost certainly the player briefly checking their team/a menu
                // mid-battle) and this tracker wrongly treating that as the battle
                // ending and a new one starting. Checking for "You won/lost the
                // battle" instead - reusing BossBattleDetector.DetectBattleEnd,
                // since boss and PVP battles render the exact same result message -
                // only clears currentOpponentName when the battle has actually
                // concluded. DetectBattleEnd returns which result appeared
                // (BossBattleOutcome.Won/Lost/None), not a plain bool - see
                // MIGRATION_GUIDE.md §26; PVP doesn't care which one, just
                // that the battle is over, so None is the only outcome that
                // means "still going."
                if (BossBattleDetector.DetectBattleEnd(screenshot, battleBounds) != BossBattleOutcome.None)
                {
                    Log.Information("PVP battle against {OpponentName} ended.", currentOpponentName);
                    StatusChanged?.Invoke($"PVP battle against {currentOpponentName} ended.");

                    currentOpponentName = null;

                    // Do NOT start detecting a new battle until this same
                    // result screen actually closes - see awaitingWindowClear's
                    // comment for the double-count bug this prevents.
                    awaitingWindowClear = true;

                    PvpBattleActiveChanged?.Invoke(false);
                }

                return;
            }

            if (awaitingWindowClear)
            {
                // Still the same result screen from the battle that just
                // ended - hasn't closed yet. Wait for battleExists to go
                // false (see above) before treating this as a new battle.
                return;
            }

            if (pvpDetectionAttempts >= MaxPvpDetectionAttempts)
                return;

            pvpDetectionAttempts++;

            if (!PvpBattleDetector.TryDetectPvp(
                    screenshot, battleBounds, out string? opponentName, out bool confirmedNotPvp))
            {
                if (confirmedNotPvp)
                {
                    // Definitely a wild encounter or a recognized boss, not PVP -
                    // no point spending the rest of the attempt budget re-OCRing a
                    // battle title that will never resolve to a PVP opponent.
                    pvpDetectionAttempts = MaxPvpDetectionAttempts;
                }

                return;
            }

            currentOpponentName = opponentName;
            pvpDetectionAttempts = 0;

            PvpOpponentService.RegisterBattle(opponentName!);

            Log.Information("PVP battle detected: {OpponentName}", opponentName);
            StatusChanged?.Invoke($"PVP battle detected: {opponentName}");

            OpponentDetected?.Invoke(opponentName!);
            PvpBattleActiveChanged?.Invoke(true);
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
