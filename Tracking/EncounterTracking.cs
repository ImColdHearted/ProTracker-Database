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
        private const int ScanDelayMs = 200;


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

            battleEndConfirmed = false;
            waitingForBattleToDisappear = false;

            cancellationTokenSource =
                new CancellationTokenSource();

            trackingTask = Task.Run(
                () => TrackingLoopAsync(
                    cancellationTokenSource.Token
                )
            );

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

            StatusChanged?.Invoke("Tracking stopped.");
        }

        private void RearmForNextEncounter(
    string status)
        {
            encounterAlreadyRegistered = false;
            catchResultAlreadyRegistered = false;
            rareEncounterAlreadyRegistered = false;

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

                await Task.Delay(
                    ScanDelayMs,
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
                // ========================================================
                // PRIMARY END-OF-BATTLE PATH
                // ========================================================

                if (waitingForBattleToDisappear)
                {
                    RearmForNextEncounter(
                        "Confirmed battle ended - ready for next encounter."
                    );

                    return;
                }


                // ========================================================
                // THIRD-LAYER OVERWORLD FALLBACK
                // ========================================================

                // If we already registered an encounter, but never managed
                // to OCR the Run Away / Success message, count how long the
                // battle window remains completely absent.
                if (encounterAlreadyRegistered)
                {
                    consecutiveNoBattleScans++;

                    // ScanDelayMs = 200.
                    //
                    // 8 scans = approximately 1.6 seconds.
                    //
                    // This is deliberately longer than a momentary locator
                    // failure so we don't immediately unlock from one bad
                    // frame.
                    const int overworldConfirmationScans = 3;

                    if (consecutiveNoBattleScans >=
                        overworldConfirmationScans)
                    {
                        RearmForNextEncounter(
                            "Overworld detected - ready for next encounter."
                        );
                    }
                }
                else
                {
                    consecutiveNoBattleScans = 0;
                }

                return;
            }

            // Battle is currently visible again,
            // so any partial overworld confirmation was a false alarm.
            consecutiveNoBattleScans = 0;

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
                !string.IsNullOrWhiteSpace(lastDetectedPokemon))
            {
                RareEncounterType existingRareType =
                    RareEncounterDetector.Detect(
                        screenshot,
                        battleBounds
                    );

                if (existingRareType != RareEncounterType.None)
                {
                    rareEncounterAlreadyRegistered = true;

                    if (existingRareType == RareEncounterType.Shiny)
                    {
                        StatusChanged?.Invoke(
                            $"SHINY encounter detected: {lastDetectedPokemon}"
                        );
                    }
                    else if (existingRareType == RareEncounterType.Form)
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