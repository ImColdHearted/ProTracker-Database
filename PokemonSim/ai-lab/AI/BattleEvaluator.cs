using PokemonSim.Engine;
using PokemonSim.Models;

namespace pokemonsim.AI
{
    public static class BattleEvaluator
    {
        public static double Evaluate(BattleState state)
        {
            var p1 = state.Player1;
            var p2 = state.Player2;

            double score = 0;

            score += TeamHP(p1) - TeamHP(p2);

            score += TypeAdvantage(
                p1.ActivePokemon,
                p2.ActivePokemon
            ) * 50;

            return score;
        }

        static double TeamHP(PlayerState player)
        {
            return player.Team.Sum(p =>
                (double)p.CurrentHP / p.MaxHP
            );
        }

        static double TypeAdvantage(
            PokemonState attacker,
            PokemonState defender)
        {
            double best = 1;

            foreach (var move in attacker.Moves)
            {
                foreach (var type in defender.Types)
                {
                    double mult =
                        TypeChart.GetMultiplier(
                            move.Type,
                            type
                        );

                    if (mult > best)
                        best = mult;
                }
            }

            return best - 1;
        }
    }
}