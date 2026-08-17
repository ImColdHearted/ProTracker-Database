using Foot_Tracker.Models;
using Serilog;
using System;
using System.Drawing;
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
        private int consecutiveNoBattleScans;
        private bool waitingForBattleToDisappear;
        private bool battleEndConfirmed;
        private int consecutiveBattleScans;
        private const int BattleConfirmationScans = 3;
        private const int NormalScanDelayMs = 200;
        private const int BattleScanDelayMs = 20;
        private const int RareCheckIntervalMs = 100;
        private const int RareCheckWindowMs = 5000;

        private DateTime nextRareCheckUtc =
            DateTime.MinValue;

        private DateTime rareCheckUntilUtc =
            DateTime.MinValue;


        public bool IsRunning =>
            cancellationTokenSource != null &&
            !cancellationTokenSource.IsCancellationRequested;

        public event Action<string>? EncounterDetected;

        public event Action<string>? StatusChanged;

        public event Action<string, RareEncounterType>? RareEncounterDetected;

        public event Action<CatchResult>? CatchResultDetected;

        private string lastDetectedPokemon =
    string.Empty;

        public void Start()
        {
            if (IsRunning)
                return;

            encounterAlreadyRegistered = false;
            catchResultAlreadyRegistered = false;
            rareEncounterAlreadyRegistered = false;
            consecutiveNoBattleScans = 0;
            lastDetectedPokemon = string.Empty;
            consecutiveBattleScans = 0;
            battleEndConfirmed = false;
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
            battleEndConfirmed = false;
            encounterAlreadyRegistered = false;
            rareEncounterAlreadyRegistered = false;
            waitingForBattleToDisappear = false;

            catchResultAlreadyRegistered = false;
            consecutiveNoBattleScans = 0;
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
            rareCheckUntilUtc =
                DateTime.MinValue;

            nextRareCheckUtc =
                DateTime.MinValue;
            battleEndConfirmed = false;
            waitingForBattleToDisappear = false;

            consecutiveNoBattleScans = 0;

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

        private void ScanOnce()
        {
            using Bitmap? screenshot =
                ScreenCapture.CaptureProWindow();

            if (screenshot == null)
            {
                StatusChanged?.Invoke(
                    "Waiting for PROClient..."
                );

                return;
            }

            bool battleExists =
    BattleWindowLocator.TryLocate(
        screenshot,
        out Rectangle battleBounds
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
            consecutiveNoBattleScans = 0;

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

                            battleEndConfirmed = true;
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

                            battleEndConfirmed = false;
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

                            battleEndConfirmed = true;
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
                    Log.Information(
        "Rare encounter detected: {Pokemon} ({RareType})",
        lastDetectedPokemon,
        existingRareType
    );

                    if (existingRareType !=
                        RareEncounterType.None)
                    {
                        rareEncounterAlreadyRegistered = true;

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
                            out string possiblePokemon) &&
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

                        battleEndConfirmed = false;
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
                            out pokemonName))
                    {
                        StatusChanged?.Invoke(
                            "Battle detected - identifying Pokémon..."
                        );

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