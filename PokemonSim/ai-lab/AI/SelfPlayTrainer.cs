using pokemonsim.AI;
using PokemonSim.Engine;
using PokemonSim.Models;
using PokemonSim.Simulation;

namespace PokemonSim.AI
{
    public static class SelfPlayTrainer
    {
        public static void GenerateGames(int games)
        {
            for (int i = 0; i < games; i++)
            {
                var state = BattleFactory.CreateRandomBattle();

                RunGame(state);
            }
        }

        static void RunGame(BattleState state)
        {
            var history = new List<(float[] state, int move)>();

            var engine = new BattleEngine(state);

            while (!state.BattleOver)
            {
                var encoded = BattleStateEncoder.Encode(state);

                var move = MonteCarloAI.ChooseMove(state, 200);

                history.Add((encoded, move.Index));

                engine.RunTurn();
            }

            int result = state.Player1.HasLost() ? -1 : 1;

            TrainingDataWriter.Write(history, result);
        }
    }
}