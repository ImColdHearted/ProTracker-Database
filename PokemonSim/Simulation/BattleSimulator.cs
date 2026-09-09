using PokemonSim.Engine;
using PokemonSim.Engine.Strategies;
using PokemonSim.Models;
using PokemonSim.Observation;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace PokemonSim.Simulation
{
    public sealed class SimulationResult
    {
        public int Simulations;
        public int Player1Wins;
        public int Player2Wins;
        public int Draws;
        public int Cancelled;

        // Section 155: the lab view wants pace, not just outcomes.
        public long TotalTurns;

        public double AverageTurns =>
            Simulations - Cancelled <= 0 ? 0 : TotalTurns / (double)(Simulations - Cancelled);
    }

    /// <summary>
    /// The original bulk harness: N battles from one template, in parallel.
    /// Section 154 made it compile (it called a BattleInitializer that was
    /// never written); section 155 lets each side's strategy be chosen -
    /// the factories get the battle index so a seeded strategy (Monte
    /// Carlo) can vary per battle while a whole run stays reproducible -
    /// and counts turns. Both sides default to the preserved
    /// RandomMoveStrategy, exactly the old behaviour. Each battle is
    /// seeded from the base seed plus its index, logs stay silent, and
    /// the numbers come back as a result object - printing is the dev
    /// console's job, not a library's.
    /// </summary>
    public static class BattleSimulator
    {
        public static SimulationResult Run(
            BattleState templateState,
            int simulations,
            int baseSeed = 12345,
            CancellationToken cancellationToken = default,
            Func<int, IBattleStrategy>? player1Strategy = null,
            Func<int, IBattleStrategy>? player2Strategy = null,
            Func<int, IBattleTurnObserver?>? observerFactory = null)
        {
            // The original template path: every battle is a clone of one
            // prepared state. Kept verbatim; the factory overload below is
            // §168's generalization.
            return Run(i => templateState.Clone(), simulations, baseSeed,
                cancellationToken, player1Strategy, player2Strategy, observerFactory);
        }

        /// <summary>§168: the same harness with a per-battle STATE FACTORY
        /// instead of one template - the admin Battle Lab builds fresh
        /// random teams for every battle so the observation data does not
        /// learn one matchup by heart - plus an optional per-battle
        /// completion callback for progress reporting (called from worker
        /// threads; the callback synchronizes itself).</summary>
        public static SimulationResult Run(
            Func<int, BattleState> stateFactory,
            int simulations,
            int baseSeed = 12345,
            CancellationToken cancellationToken = default,
            Func<int, IBattleStrategy>? player1Strategy = null,
            Func<int, IBattleStrategy>? player2Strategy = null,
            Func<int, IBattleTurnObserver?>? observerFactory = null,
            Action<int, BattleOutcome>? onBattleCompleted = null)
        {
            var result = new SimulationResult { Simulations = simulations };

            int p1 = 0, p2 = 0, draws = 0, cancelled = 0;
            long turns = 0;

            Parallel.For(0, simulations, i =>
            {
                var state = stateFactory(i);

                state.Rng = new BattleRng(baseSeed + i);
                state.Log.Silent = true;

                BattleInitializer.Initialize(state);

                var engine = new BattleEngine(state);

                // Section 156: a lab battle is observed only when the
                // caller explicitly marked the run - the factory owns the
                // observers' lifetime (creation here, disposal after Run).
                BattleOutcome outcome = engine.RunBattle(
                    player1Strategy?.Invoke(i) ?? new RandomMoveStrategy(),
                    player2Strategy?.Invoke(i) ?? new RandomMoveStrategy(),
                    cancellationToken,
                    observerFactory?.Invoke(i));

                switch (outcome)
                {
                    case BattleOutcome.Player1Wins: Interlocked.Increment(ref p1); break;
                    case BattleOutcome.Player2Wins: Interlocked.Increment(ref p2); break;
                    case BattleOutcome.Cancelled: Interlocked.Increment(ref cancelled); break;
                    default: Interlocked.Increment(ref draws); break;
                }

                if (outcome != BattleOutcome.Cancelled)
                    Interlocked.Add(ref turns, state.TurnNumber);

                onBattleCompleted?.Invoke(i, outcome);
            });

            result.Player1Wins = p1;
            result.Player2Wins = p2;
            result.Draws = draws;
            result.Cancelled = cancelled;
            result.TotalTurns = turns;

            return result;
        }
    }
}