using PokemonSim.Engine;
using PokemonSim.Models;
using PokemonSim.Simulation;

namespace pokemonsim.AI
{
    public static class MonteCarloAI
    {
        static ThreadLocal<Random> rng =
            new ThreadLocal<Random>(() => new Random());

        public static bool IsSimulating = false;

        public static MoveState ChooseMove(BattleState state, int simulationsPerMove = 20)
        {
            var pokemon = state.Player1.ActivePokemon;

            // Shuffle to remove bias
            var moves = pokemon.Moves
                .OrderBy(x => rng.Value!.Next())
                .ToList();

            MoveState bestMove = moves[0];
            double bestScore = double.MinValue;

            foreach (var move in moves)
            {
                double score = EvaluateMove(state, move, simulationsPerMove);

                if (score > bestScore)
                {
                    bestScore = score;
                    bestMove = move;
                }
            }

            return bestMove;
        }

        static double EvaluateMove(BattleState originalState, MoveState move, int simulations)
        {
            bool previous = BattleLogger.Enabled;
            BattleLogger.Enabled = false;

            int wins = 0;

            for (int i = 0; i < simulations; i++)
            {
                var simState = BattleCloner.Clone(originalState);

                // 🔥 Force the move ONCE
                ForceFirstMove(simState, move);

                // 🔥 Let engine handle EVERYTHING else
                MonteCarloAI.IsSimulating = true;

                try
                {
                    var engine = new BattleEngine(simState);
                    engine.RunBattle();
                }
                finally
                {
                    MonteCarloAI.IsSimulating = false;
                }

                if (!simState.Player1.HasLost())
                    wins++;
            }

            BattleLogger.Enabled = previous;

            return (double)wins / simulations;
        }

        static void ForceFirstMove(BattleState state, MoveState move)
        {
            var attacker = state.Player1.ActivePokemon;
            var defender = state.Player2.ActivePokemon;

            if (!attacker.Fainted)
            {
                MoveResolver.Resolve(state, attacker, defender, move);
            }

            if (!defender.Fainted)
            {
                var enemyMove = defender.Moves[
                    rng.Value!.Next(defender.Moves.Count)
                ];

                MoveResolver.Resolve(state, defender, attacker, enemyMove);
            }
        }
    }
}