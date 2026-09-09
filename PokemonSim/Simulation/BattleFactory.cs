using PokemonSim.Data;
using PokemonSim.Engine;
using PokemonSim.Models;
using System.Collections.Generic;

namespace PokemonSim.Simulation
{
    /// <summary>The dev harness's quick battle. Section 154: seedable, and
    /// the state carries its rng/log like every battle now.</summary>
    public static class BattleFactory
    {
        public static BattleState CreateRandomBattle(int seed = 0)
        {
            var rng = new BattleRng(seed);

            var p1 = PokemonFactory.Create(PokemonDex.Get("Pikachu"), rng);
            var p2 = PokemonFactory.Create(PokemonDex.Get("Charizard"), rng);

            var player1 = new PlayerState
            {
                Name = "AI1",
                Team = new List<PokemonState> { p1 },
                ActivePokemon = p1
            };

            var player2 = new PlayerState
            {
                Name = "AI2",
                Team = new List<PokemonState> { p2 },
                ActivePokemon = p2
            };

            return new BattleState
            {
                Player1 = player1,
                Player2 = player2,
                Rng = new BattleRng(seed)
            };
        }
    }
}