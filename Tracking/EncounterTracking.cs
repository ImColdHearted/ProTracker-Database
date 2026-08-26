using Foot_Tracker.Models;
using Serilog;
using System;
using SkiaSharp;
using Foot_Tracker.Tracking.Capture;
using System.Threading;
using System.Threading.Tasks;
using Foot_Tracker.Services;


namespace Foot_Tracker.Tracking
{
    public sealed class EncounterTracker : IDisposable
    {
        private CancellationTokenSource? cancellationTokenSource;
        private Task? trackingTask;

        private bool encounterAlreadyRegistered;
        private bool catchResultAlreadyRegistered;
        private bool rareEncounterAlreadyRegistered;
        private bool waitingForBattleToDisappear;
        private int consecutiveBattleScans;

        // Guards the "identifying Pokemon" status message from firing on every
        // single scan tick (up to ~50/sec during an active battle, per
        // BattleScanDelayMs) while OCR repeatedly fails to match a Pokemon name -
        // which is EXPECTED and permanent for a boss battle, since a boss's name
        // will never match a wild Pokemon. Without this, the resulting message
        // spam drowns out BossCooldownTracker's own, much less frequent status
        // updates, making boss cooldown detection look broken even when it's
        // working correctly in the background (a separate, independent tracker -
        // see BossCooldownTracker.cs).
        private bool identifyingStatusAlreadyShown;
        private const int BattleConfirmationScans = 3;
        private const int NormalScanDelayMs = 200;
        private const int BattleScanDelayMs = 20;
        private const int RareCheckIntervalMs = 100;
        private const int RareCheckWindowMs = 5000;

        private DateTime nextRareCheckUtc =
            DateTime.MinValue;

        private DateTime rareCheckUntilUtc =
            DateTime.MinValue;

        // Throttle for RouteDetector's corner-of-screen OCR (route name + "Poke
        // Time" readout) - see the CornerInfoDetected event below and its call
        // site in ScanOnce(). Runs far less often than the surrounding battle-
        // detection scan: that text changes at most once every few seconds (the
        // player walks to a new route, or a minute ticks over in-game), not 50
        // times a second like a mid-battle scan needs to.
        private const int CornerCheckIntervalMs = 5000;

        private DateTime nextCornerCheckUtc =
            DateTime.MinValue;

        // Set by MainWindowViewModel from BossCooldownTracker.BossBattleActiveChanged
        // (a separate, independent tracker - see BossCooldownTracker.cs). Boss
        // battles reuse the exact same battle-window UI as wild encounters, so
        // without this flag this tracker would happily OCR the boss's active
        // Pokemon and register it as a "wild encounter" instead of leaving Win/
        // Loss detection to BossCooldownTracker - confirmed via a real tester's
        // log. volatile because BossCooldownTracker's event fires from its own
        // background tracking thread, not this one.
        private volatile bool bossBattleActive;

        public void SetBossBattleActive(bool active) =>
            bossBattleActive = active;

        // Set by MainWindowViewModel from PvpTracker.PvpBattleActiveChanged (a
        // separate, independent tracker - see PvpTracker.cs). PVP battles reuse
        // the exact same battle-window UI as wild encounters/boss battles, so
        // without this flag this tracker would OCR the opponent's active Pokemon
        // and register it as a "wild encounter" - the same interference problem
        // bossBattleActive above exists to prevent, just for PVP instead of boss
        // battles.
        private volatile bool pvpBattleActive;

        public void SetPvpBattleActive(bool active) =>
            pvpBattleActive = active;

        public bool IsRunning =>
            cancellationTokenSource != null &&
            !cancellationTokenSource.IsCancellationRequested;

        public event Action<string>? EncounterDetected;

        public event Action<string>? StatusChanged;

        public event Action<string, RareEncounterType>? RareEncounterDetected;

        public event Action<CatchResult>? CatchResultDetected;

        // Either parameter may be null independently - see RouteDetector.TryDetectCorner.
        public event Action<string?, string?>? CornerInfoDetected;

        private string lastDetectedPokemon =
    string.Empty;

        public void Start()
        {
            if (IsRunning)
                return;

            encounterAlreadyRegistered = false;
            catchResultAlreadyRegistered = false;
            rareEncounterAlreadyRegistered = false;
            lastDetectedPokemon = string.Empty;
            identifyingStatusAlreadyShown = false;
            consecutiveBattleScans = 0;
            waitingForBattleToDisappear = false;

            cancellationTokenSource =
                new CancellationTokenSource();

            trackingTask = Task.Run(
                () => TrackingLoopAsync(
                    cancellationTokenSource.Token
                )
            );
            Log.Information("Encounter tracking started.");
            StatusChanged?.Invoke("Tracking started.");
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
            encounterAlreadyRegistered = false;
            rareEncounterAlreadyRegistered = false;
            waitingForBattleToDisappear = false;

            catchResultAlreadyRegistered = false;
            lastDetectedPokemon = string.Empty;

            Log.Information("Encounter tracking stopped.");
            StatusChanged?.Invoke("Tracking stopped.");
        }

        private int GetCurrentScanDelay()
        {
            // Once we know which Pokemon we're battling,
            // poll much faster so short catch-result messages
            // are not missed at PRO's Max dialogue speed.
            if (encounterAlreadyRegistered &&
                !waitingForBattleToDisappear)
            {
                return BattleScanDelayMs;
            }

            return NormalScanDelayMs;
        }

        private void RearmForNextEncounter(
    string status)
        {
            encounterAlreadyRegistered = false;
            catchResultAlreadyRegistered = false;
            rareEncounterAlreadyRegistered = false;
            consecutiveBattleScans = 0;
            identifyingStatusAlreadyShown = false;
            rareCheckUntilUtc =
                DateTime.MinValue;

            nextRareCheckUtc =
                DateTime.MinValue;
            waitingForBattleToDisappear = false;


            lastDetectedPokemon =
                string.Empty;

            StatusChanged?.Invoke(status);
        }

        private async Task TrackingLoopAsync(
            CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    ScanOnce();
                }
                catch (Exception ex)
                {
                    // Previously this only ever showed ex.Message in the UI - the full
                    // exception (stack trace, inner exception chain) was never logged
                    // anywhere, so a "Report a Problem" submission for one of these
                    // had nothing useful in the log file. Log.Error here writes the
                    // complete exception to Serilog, same as Program.cs's top-level
                    // catch already does for startup failures.
                    Log.Error(ex, "Encounter tracking loop error");

                    StatusChanged?.Invoke(
                        $"Tracking error: {ex.Message}"
                    );
                }

                int delay =
                    GetCurrentScanDelay();

                await Task.Delay(
                    delay,
                    cancellationToken
                );
            }
        }

        private static readonly IWindowCaptureService captureService = WindowCaptureServiceFactory.Instance;

        private static SKBitmap? CaptureScreenshot()
        {
            byte[]? pngBytes = captureService.CaptureSelectedWindowPng();
            return pngBytes is null ? null : ImageOps.DecodePng(pngBytes);
        }

        private void ScanOnce()
        {
            // A boss battle or PVP battle is running - BossCooldownTracker/
            // PvpTracker own this battle window entirely, so this tracker must not
            // touch it at all: not capture, not OCR, not status messages.
            // Otherwise this loop OCRs the opponent's active Pokemon and wrongly
            // registers it as a wild encounter (confirmed via a real tester's log
            // for boss battles - "Erika sends out Ferrothorn!" was picked up here
            // as if Ferrothorn were a wild encounter target - and PVP screens show
            // real Pokemon sprites/names the same way).
            if (bossBattleActive || pvpBattleActive)
                return;

            using SKBitmap? screenshot =
                CaptureScreenshot();

            if (screenshot == null)
            {
                StatusChanged?.Invoke(
                    "Waiting for PROClient..."
                );

                return;
            }

            // ============================================================
            // ROUTE NAME / TIME OF DAY (throttled, independent of battle state)
            // ============================================================
            //
            // Deliberately runs whether or not a battle window is present -
            // the player is on some route and it's some time of day regardless
            // of what else is happening, and this reuses the screenshot already
            // captured above rather than capturing a second time. See
            // RouteDetector.cs for the OCR itself and CornerCheckIntervalMs's
            // declaration comment for why this doesn't run every tick.
            if (DateTime.UtcNow >= nextCornerCheckUtc)
            {
                nextCornerCheckUtc =
                    DateTime.UtcNow.AddMilliseconds(CornerCheckIntervalMs);

                if (RouteDetector.TryDetectCorner(
                        screenshot,
                        out string? cornerRouteName,
                        out string? cornerTimeOfDay))
                {
                    CornerInfoDetected?.Invoke(cornerRouteName, cornerTimeOfDay);
                }
            }

            bool battleExists =
    BattleWindowLocator.TryLocate(
        screenshot,
        out SKRectI battleBounds
    );

            // ============================================================
            // NO BATTLE WINDOW
            // ============================================================

            if (!battleExists)
            {
                consecutiveBattleScans = 0;

                // ========================================================
                // PRIMARY END-OF-BATTLE PATH
                // ========================================================

                if (waitingForBattleToDisappear)
                {
                    RearmForNextEncounter(
                        "Confirmed battle ended - ready for next encounter."
                    );

                    Log.Information(
                        "Confirmed battle ended - ready for next encounter."
                    );

                    return;
                }

                // ========================================================
                // BATTLE TEMPORARILY HIDDEN
                // ========================================================
                //
                // Do NOT re-arm merely because we cannot currently
                // locate the battle window.
                //
                // The player may have opened:
                // - the map
                // - a Pokemon card / summary
                // - another PRO menu
                //
                // If the battle becomes visible again, the existing
                // encounter lock remains intact.

                return;
            }

            // Battle is currently visible again,
            // so any partial no-battle state is cleared.

            // ============================================================
            // CONFIRM NEW BATTLE WINDOW
            // ============================================================

            if (!encounterAlreadyRegistered &&
                    !waitingForBattleToDisappear)
            {
                consecutiveBattleScans++;

                if (consecutiveBattleScans <
                    BattleConfirmationScans)
                {
                    return;
                }
            }

            // ============================================================
            // OLD BATTLE IS ENDING
            // ============================================================

            if (waitingForBattleToDisappear)
            {
                // We already saw either:
                //
                // "Success! You caught..."
                //
                // or
                //
                // "You have run away..."
                //
                // The battle window may remain visible briefly afterward.
                //
                // Absolutely NOTHING in this old battle is allowed to
                // register another encounter.

                return;
            }

            // ============================================================
            // CHECK BATTLE MESSAGE
            // ============================================================

            CatchResult catchResult =
                CatchDetector.Detect(
                    screenshot,
                    battleBounds
                );

            if (catchResult == CatchResult.None)
            {
                // Previous catch-attempt message disappeared.
                // This allows another Pokeball attempt to be counted.

                catchResultAlreadyRegistered = false;
            }
            else if (!catchResultAlreadyRegistered)
            {
                catchResultAlreadyRegistered = true;

                switch (catchResult)
                {
                    // ====================================================
                    // SUCCESSFUL CATCH
                    // ====================================================

                    case CatchResult.Success:

                        CatchResultDetected?.Invoke(
                            CatchResult.Success
                        );

                        waitingForBattleToDisappear = true;
                        Log.Information(
    "Successful catch detected for {Pokemon}",
    lastDetectedPokemon
);

                        StatusChanged?.Invoke(
                            "Successful catch - waiting for battle to close."
                        );

                        // CRITICAL:
                        // Do not continue into encounter detection.
                        return;


                    // ====================================================
                    // FAILED BALL
                    // ====================================================

                    case CatchResult.Failed:

                        // Failed catch does NOT end the battle.

                        Log.Information(
    "Failed catch detected for {Pokemon}",
    lastDetectedPokemon
);

                        StatusChanged?.Invoke(
                            "Failed catch detected."
                        );

                        CatchResultDetected?.Invoke(
                            CatchResult.Failed
                        );

                        break;


                    // ====================================================
                    // RAN AWAY
                    // ====================================================

                    case CatchResult.RunAway:

                        waitingForBattleToDisappear = true;
                        Log.Information(
    "Run away detected - waiting for battle to close."
);

                        StatusChanged?.Invoke(
                            "Run away detected - waiting for battle to close."
                        );

                        // CRITICAL:
                        // Do not scan the old Pokémon again.
                        return;
                }
            }

            // ============================================================
            // ENCOUNTER ALREADY REGISTERED
            // ============================================================
            if (encounterAlreadyRegistered &&
                !rareEncounterAlreadyRegistered &&
                !string.IsNullOrWhiteSpace(lastDetectedPokemon) &&
                DateTime.UtcNow <= rareCheckUntilUtc &&
                DateTime.UtcNow >= nextRareCheckUtc)
            {
                nextRareCheckUtc =
                    DateTime.UtcNow.AddMilliseconds(
                        RareCheckIntervalMs
                    );

                RareEncounterType existingRareType =
                    RareEncounterDetector.Detect(
                        screenshot,
                        battleBounds
                    );

                if (existingRareType !=
                    RareEncounterType.None)
                {
                    rareEncounterAlreadyRegistered = true;

                    // Only log when a rare type is actually found. This check
                    // re-runs every RareCheckIntervalMs for up to
                    // RareCheckWindowMs after every single encounter (rare or
                    // not), so logging unconditionally here previously printed a
                    // "Rare encounter detected: X (None)" line roughly ten times
                    // a second for every ordinary encounter too - which reads as
                    // constant false detections at a glance, and made a real
                    // report's log much harder to search through. The raw OCR
                    // attempt (garbled text or not) is still always logged inside
                    // RareEncounterDetector.Detect() itself, so no diagnostic
                    // information is lost by only logging real hits here.
                    Log.Information(
                        "Rare encounter confirmed: {Pokemon} ({RareType})",
                        lastDetectedPokemon,
                        existingRareType
                    );

                    if (existingRareType ==
                        RareEncounterType.Shiny)
                    {
                        StatusChanged?.Invoke(
                            $"SHINY encounter detected: {lastDetectedPokemon}"
                        );
                    }
                    else if (existingRareType ==
                             RareEncounterType.Form)
                    {
                        StatusChanged?.Invoke(
                            $"Special form encounter detected: {lastDetectedPokemon}"
                        );
                    }

                    RareEncounterDetected?.Invoke(
                        lastDetectedPokemon,
                        existingRareType
                    );
                }
            }
            // ============================================================
            // ENCOUNTER IS ALREADY LOCKED
            // ============================================================
            //
            // The current battle has already been counted.
            // Rare detection may continue during its five-second window,
            // but normal encounter OCR must not run again.
            //
            if (encounterAlreadyRegistered)
            {
                return;
            }

            // ============================================================
            // SECONDARY NEW-BATTLE RECOVERY
            // ============================================================

            string? recoveredPokemonName = null;

            if (encounterAlreadyRegistered)
            {
                // Normally the explicit Run Away / Catch detector
                // unlocks us between battles.
                //
                // However, some clients may fail to OCR that ending text.
                // If we can clearly identify a DIFFERENT wild Pokémon,
                // the old battle cannot still be active.

                if (EncounterDetector.TryDetectEncounter(
                        screenshot,
                        out string possiblePokemon,
                        out _) &&
                    !string.IsNullOrWhiteSpace(possiblePokemon) &&
                    !possiblePokemon.Equals(
                        lastDetectedPokemon,
                        StringComparison.OrdinalIgnoreCase))
                {
                    StatusChanged?.Invoke(
                        $"New Pokémon detected while locked: " +
                        $"{lastDetectedPokemon} -> {possiblePokemon}. " +
                        $"Forcing battle re-arm."
                    );

                    // Clear the stale battle lock.
                    encounterAlreadyRegistered = false;
                    rareEncounterAlreadyRegistered = false;
                    catchResultAlreadyRegistered = false;
                    identifyingStatusAlreadyShown = false;

                    waitingForBattleToDisappear = false;

                    lastDetectedPokemon = string.Empty;

                    // We already successfully OCR'd the new Pokémon,
                    // so don't make Tesseract identify it again below.
                    recoveredPokemonName =
                        possiblePokemon;
                }
                else
                {
                    // Same Pokémon, no Pokémon, or unreadable title.
                    //
                    // Stay locked. This preserves the duplicate protection
                    // when a menu/window covers and uncovers the battle.
                    return;
                }
            }



            if (encounterAlreadyRegistered)
            {
                // We are still inside the same battle.
                //
                // It does NOT matter if:
                //
                // - the title gets covered
                // - a Pokemon summary window covers it
                // - another player walks over it
                // - OCR temporarily fails
                // - the name disappears and comes back
                //
                // This battle has already been counted.
                //
                // Only an explicit battle-ending message can eventually
                // unlock us.

                return;
            }


            // ============================================================
            // DETECT NEW ENCOUNTER
            // ============================================================

            string pokemonName;

            if (!string.IsNullOrWhiteSpace(
                    recoveredPokemonName))
            {
                // The secondary recovery already identified it.
                pokemonName =
                    recoveredPokemonName;
            }
            else
            {
                if (!EncounterDetector.TryDetectEncounter(
                        screenshot,
                        out pokemonName,
                        out bool looksLikeBattleTitle))
                {
                    // Only show "identifying" for something that actually looks
                    // like a real battle title (contains "VS") - without this,
                    // BattleWindowLocator's generic dark-bar heuristic
                    // false-positiving on other dark UI panels (e.g. NPC
                    // dialogue boxes - confirmed via a real tester's screenshot)
                    // left this message stuck on screen indefinitely for
                    // something that was never a battle at all.
                    if (looksLikeBattleTitle && !identifyingStatusAlreadyShown)
                    {
                        identifyingStatusAlreadyShown = true;

                        StatusChanged?.Invoke(
                            "Battle detected - identifying Pokémon..."
                        );
                    }

                    return;
                }

                if (string.IsNullOrWhiteSpace(
                        pokemonName))
                {
                    return;
                }
            }

            // ============================================================
            // REGISTER ENCOUNTER
            // ============================================================

            encounterAlreadyRegistered = true;
            consecutiveBattleScans = 0;
            rareCheckUntilUtc =
                DateTime.UtcNow.AddMilliseconds(
                    RareCheckWindowMs
                );

            nextRareCheckUtc =
                DateTime.MinValue;

            lastDetectedPokemon = pokemonName;

            // A brand-new encounter means any catch result from
            // the previous battle is no longer relevant.
            catchResultAlreadyRegistered = false;

            RareEncounterType rareType =
                RareEncounterDetector.Detect(
                    screenshot,
                    battleBounds
                );

            if (rareType == RareEncounterType.Shiny)
            {
                StatusChanged?.Invoke(
                    $"SHINY encounter detected: {pokemonName}"
                );

                RareEncounterDetected?.Invoke(
                    pokemonName,
                    RareEncounterType.Shiny
                );
            }
            else if (rareType == RareEncounterType.Form)
            {
                StatusChanged?.Invoke(
                    $"Special form encounter detected: {pokemonName}"
                );

                RareEncounterDetected?.Invoke(
                    pokemonName,
                    RareEncounterType.Form
                );
            }
            Log.Information(
    "Encounter detected: {Pokemon}",
    pokemonName
);
            StatusChanged?.Invoke(
                $"Encounter detected: {pokemonName}"
            );

            EncounterDetected?.Invoke(
                pokemonName
            );
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