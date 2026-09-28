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

        // §276. The log entry this battle was registered as, so its result and
        // its message lines can be written back onto the battle they belong
        // to. Null whenever currentOpponentName is (and also when the service
        // refused to record - Admin Client - in which case there is simply
        // nothing to update).
        private Foot_Tracker.Models.PvpOpponentEntry? currentBattleEntry;

        // §276. The last line read out of the message box, so the same
        // sentence is not re-offered to the log a dozen times while it sits on
        // screen. Per battle: cleared with currentBattleEntry.
        private string? lastLoggedMessage;

        // §276. Whether any line was kept this battle and still needs writing
        // to disk. AddLogLine only mutates; one save at the end of the battle
        // beats one per line.
        private bool battleLogDirty;

        private int pvpDetectionAttempts;

        // §292. The battle this tracker GAVE UP on without reading a result,
        // kept so that the same opponent reappearing a moment later is the
        // same battle and not a new one. Set only by the force-reset path;
        // cleared by a read result, by a resume, and by Start.
        //
        // The bug it ends: moving the client or the tracker window mid-fight
        // makes the capture fail for a few seconds, the force-reset fires,
        // the window comes back, the same opponent is detected again, and
        // RegisterBattle writes a second entry - with "Faced" and the lifetime
        // PVP tally both stepping up for a battle that never stopped. A
        // real log showed one Aleksa8121 fight as four rows: two dashes a
        // minute apart, a win two minutes on, a loss after that.
        private string? interruptedOpponentName;
        private Foot_Tracker.Models.PvpOpponentEntry? interruptedEntry;
        private DateTime interruptedAtUtc;

        // How long after a force-reset the same name still means the same
        // battle. The gaps in that log were one and two minutes; a genuine
        // rematch against the same player needs both to leave, re-queue and
        // be paired again, and does not happen inside three.
        private static readonly TimeSpan ResumeWindow = TimeSpan.FromMinutes(3);

        // §292. How many trailing characters one read may carry past the
        // other and still be the same name - "Jagenhgar" and "Jagenhgar N",
        // "Shikanokonoko" and "Shikanokonoko I", both from real logs, were the
        // same opponent with a speck of the title OCR'd as a letter.
        private const int NameTailSlack = 2;

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
            currentBattleEntry = null;
            lastLoggedMessage = null;
            battleLogDirty = false;
            pvpDetectionAttempts = 0;
            consecutiveMissedScansWhileActive = 0;
            awaitingWindowClear = false;
            ForgetInterrupted();

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
                //
                // §276: whatever the battle printed before the stop is kept -
                // the lines were read, and the battle really happened. The
                // outcome is NOT guessed, so the entry stays without one.
                EndBattle(readOutcome: null);
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

                // §292: remember it BEFORE letting go, so the opponent turning
                // up again in the next few minutes rejoins this entry instead
                // of opening a second one. Remembered even when the service
                // refused to record (a null entry): the name alone is enough
                // to stop the re-detection being announced as new.
                interruptedOpponentName = currentOpponentName;
                interruptedEntry = currentBattleEntry;
                interruptedAtUtc = DateTime.UtcNow;

                // §276: no result was read, so none is written. The window
                // shows a dash for this battle and the head-to-head record
                // counts it neither way - see PvpOpponentEntry.Outcome.
                EndBattle(readOutcome: null);
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
                // MIGRATION_GUIDE.md §26; None is the only outcome that means
                // "still going", and since §91 the Won/Lost distinction also
                // feeds the lifetime PVP win/loss tallies shown in the
                // Lifetime Stats window.
                // §276: read the message box on every scan of a battle in
                // progress. This is where PRO prints what was sent out, what
                // moved and what an item did, and it is the only place any of
                // it appears - so the lines are captured as PRO wrote them and
                // parsed in a later section, once the wording is confirmed
                // rather than guessed at. See PvpOpponentEntry.BattleLog.
                //
                // No extra cost to the rest of the app: EncounterTracker is
                // paused for the duration of a PVP battle (that is what
                // PvpBattleActiveChanged is for), so nothing else is OCRing
                // this box right now.
                CaptureBattleMessage(screenshot, battleBounds);

                BossBattleOutcome outcome = BossBattleDetector.DetectBattleEnd(screenshot, battleBounds);

                if (outcome != BossBattleOutcome.None)
                {
                    bool won = outcome == BossBattleOutcome.Won;

                    LifetimeStatsService.AddPvpResult(won);

                    Log.Information(
                        "PVP battle against {OpponentName} ended: {Outcome}.",
                        currentOpponentName, won ? "won" : "lost");

                    StatusChanged?.Invoke(
                        $"PVP battle against {currentOpponentName} ended - you {(won ? "won" : "lost")}.");

                    // §276: the same outcome that has fed the lifetime tallies
                    // since §91 now also lands on the battle it belongs to.
                    EndBattle(readOutcome: won);

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

            pvpDetectionAttempts = 0;

            // §292. The same opponent, minutes after the tracker lost sight
            // of the fight: that is the fight, still going. Rejoin the entry
            // it already has - no RegisterBattle, so no second row, no second
            // "Faced", no second lifetime PVP battle.
            if (TryResume(opponentName!))
            {
                CaptureBattleMessage(screenshot, battleBounds);

                Log.Information("PVP battle against {OpponentName} resumed.", currentOpponentName);
                StatusChanged?.Invoke($"PVP battle against {currentOpponentName} resumed.");

                PvpBattleActiveChanged?.Invoke(true);
                return;
            }

            currentOpponentName = opponentName;

            currentBattleEntry = PvpOpponentService.RegisterBattle(opponentName!);
            lastLoggedMessage = null;
            battleLogDirty = false;

            // §276: the line that named the opponent is the first thing this
            // battle printed, so it is captured here rather than waiting for
            // the next scan.
            CaptureBattleMessage(screenshot, battleBounds);

            Log.Information("PVP battle detected: {OpponentName}", opponentName);
            StatusChanged?.Invoke($"PVP battle detected: {opponentName}");

            OpponentDetected?.Invoke(opponentName!);
            PvpBattleActiveChanged?.Invoke(true);
        }

        /// <summary>
        /// §292. Whether a freshly detected opponent is the battle this
        /// tracker force-reset out of moments ago - and if so, picks that
        /// battle back up. Three things have to hold: a battle was
        /// interrupted, it never got a result, and it was recent. The name is
        /// matched with a little tail slack, because the two real
        /// double-counts on record were the same name with one stray
        /// character on the end.
        /// </summary>
        private bool TryResume(string detectedName)
        {
            if (interruptedOpponentName is null)
                return false;

            if (interruptedEntry is not null && interruptedEntry.HasOutcome)
            {
                ForgetInterrupted();
                return false;
            }

            if (DateTime.UtcNow - interruptedAtUtc > ResumeWindow)
            {
                ForgetInterrupted();
                return false;
            }

            if (!SameOpponent(interruptedOpponentName, detectedName))
                return false;

            // The spelling kept is the one already on the row.
            currentOpponentName = interruptedOpponentName;
            currentBattleEntry = interruptedEntry;
            lastLoggedMessage = null;
            battleLogDirty = false;

            ForgetInterrupted();
            return true;
        }

        /// <summary>§292. Equal ignoring case, or equal but for up to
        /// <see cref="NameTailSlack"/> trailing characters on either side.</summary>
        internal static bool SameOpponent(string a, string b)
        {
            a = a.Trim();
            b = b.Trim();

            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
                return true;

            string shorter = a.Length <= b.Length ? a : b;
            string longer = a.Length <= b.Length ? b : a;

            if (shorter.Length == 0)
                return false;

            return longer.Length - shorter.Length <= NameTailSlack
                && longer.StartsWith(shorter, StringComparison.OrdinalIgnoreCase);
        }

        private void ForgetInterrupted()
        {
            interruptedOpponentName = null;
            interruptedEntry = null;
            interruptedAtUtc = default;
        }

        /// <summary>§276. Reads the message box and keeps the line if it is
        /// new to this battle. Quiet about everything else: a blank box
        /// between messages, the same sentence still on screen, a battle the
        /// service refused to record, and a full log are all ordinary.</summary>
        private void CaptureBattleMessage(SKBitmap screenshot, SKRectI battleBounds)
        {
            if (currentBattleEntry is null)
                return;

            string message = CatchDetector.ReadBattleMessage(screenshot, battleBounds);

            if (string.IsNullOrWhiteSpace(message))
                return;

            string cleaned = message.Trim();

            if (cleaned == lastLoggedMessage)
                return;

            lastLoggedMessage = cleaned;

            if (PvpOpponentService.AddLogLine(currentBattleEntry, cleaned))
                battleLogDirty = true;
        }

        /// <summary>
        /// §276. One way out of a battle, whichever way it ended.
        ///
        /// <paramref name="readOutcome"/> is the result if one was actually
        /// READ, and null if the battle simply stopped being visible. Null
        /// writes no outcome at all rather than a loss - see
        /// PvpOpponentEntry.Outcome for why those are different facts - and
        /// the captured lines are saved either way, because they were read
        /// whatever happened afterwards.
        /// </summary>
        private void EndBattle(bool? readOutcome)
        {
            if (readOutcome is bool won)
            {
                // RecordOutcome saves the whole entry, and the lines are
                // already on it, so the log ride along - one write, not two.
                PvpOpponentService.RecordOutcome(currentBattleEntry, won);
                battleLogDirty = false;

                // §292: a battle with a result is over. Nothing detected
                // after this can be it.
                ForgetInterrupted();
            }

            if (battleLogDirty)
                PvpOpponentService.SaveLog(currentBattleEntry);

            currentOpponentName = null;
            currentBattleEntry = null;
            lastLoggedMessage = null;
            battleLogDirty = false;
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
