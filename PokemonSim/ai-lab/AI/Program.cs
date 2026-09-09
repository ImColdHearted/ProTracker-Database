using PokemonSim.Data;
using PokemonSim.Data.PokemonSim.Engine;
using PokemonSim.Engine;
using PokemonSim.Models;
using PokemonSim.Simulation;

namespace PokemonSim
{
    class Program
    {
        static void Main()
        {
            MoveDex.Load("DataFiles/moves.json");

            // 🔥 Add this line to VERIFY it's done
            Console.WriteLine("✅ MoveDex fully loaded");

            PokemonDex.Load("DataFiles/pokedex.json");


            var p1a = PokemonFactory.Create(PokemonDex.Get("Darkrai"));
            var p1b = PokemonFactory.Create(PokemonDex.Get("Glimmora"));
            var p1c = PokemonFactory.Create(PokemonDex.Get("Gliscor"));
            var p1d = PokemonFactory.Create(PokemonDex.Get("Iron Crown"));
            var p1e = PokemonFactory.Create(PokemonDex.Get("Great Tusk"));
            var p1f = PokemonFactory.Create(PokemonDex.Get("Dragonite"));

            var p2a = PokemonFactory.Create(PokemonDex.Get("Clefable"));
            var p2b = PokemonFactory.Create(PokemonDex.Get("Alomomola"));
            var p2c = PokemonFactory.Create(PokemonDex.Get("Cinderace"));
            var p2d = PokemonFactory.Create(PokemonDex.Get("Iron Treads"));
            var p2e = PokemonFactory.Create(PokemonDex.Get("Iron Moth"));
            var p2f = PokemonFactory.Create(PokemonDex.Get("Deoxys: Speed"));

            var player1 = new PlayerState
            {
                Name = "Monte Carlo AI",
                Team = new List<PokemonState> { p1a, p1b, p1c, p1d, p1e, p1f },
                ActivePokemon = p1a
            };

            var player2 = new PlayerState
            {
                Name = "Deep Learn AI",
                Team = new List<PokemonState> { p2a, p2b, p2c, p2d, p2e, p2f },
                ActivePokemon = p2a
            };

            var state = new BattleState
            {
                Player1 = player1,
                Player2 = player2
            };

            BattleInitializer.Initialize(state);

            BattleSimulator.Run(state, 1);
        }
    }
}