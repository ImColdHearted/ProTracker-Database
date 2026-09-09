using System.Collections.Generic;
using System.Linq;
using PokemonSim.Actions;
using PokemonSim.Models;

namespace PokemonSim.Engine.Strategies
{
    /// <summary>
    /// Section 154. The baseline opponent, preserved from the original
    /// engine: BuildActionQueue's Player 2 branch picked a uniformly random
    /// move and never volunteered a switch, and that exact behaviour lives
    /// here now - random legal move, switching only when forced (the
    /// replacement pick is uniform too). Rolls come from the battle's own
    /// seeded rng, so a seeded battle against this opponent replays
    /// identically.
    /// </summary>
    public sealed class RandomMoveStrategy : IBattleStrategy
    {
        public string Name => "Random moves";

        public BattleAction ChooseAction(BattleState state, PlayerState self, IReadOnlyList<BattleAction> legalActions)
        {
            var moves = legalActions.Where(a => a.Type == BattleActionType.Move).ToList();

            BattleAction chosen = moves.Count > 0
                ? moves[state.Rng.Next(moves.Count)]
                : legalActions[state.Rng.Next(legalActions.Count)];

            // Section 161: a computer player mega evolves the first turn
            // it legally can - no rng draw, so seeded battles replay
            // identically to before on teams without a stone.
            if (chosen.Type == BattleActionType.Move &&
                MegaEvolutions.CanMegaEvolve(state, self, self.ActivePokemon))
            {
                chosen.MegaEvolve = true;
            }

            return chosen;
        }

        public int ChooseReplacement(BattleState state, PlayerState self, IReadOnlyList<int> legalTeamIndexes)
        {
            return legalTeamIndexes[state.Rng.Next(legalTeamIndexes.Count)];
        }
    }
}