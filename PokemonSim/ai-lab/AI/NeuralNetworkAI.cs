using Microsoft.ML.OnnxRuntime;
using PokemonSim.Models;

public class NeuralNetworkAI
{
    static InferenceSession? session;

    static NeuralNetworkAI()
    {
        var path = Path.Combine("DataFiles", "pokemon_ai.onnx");

        if (File.Exists(path))
        {
            session = new InferenceSession(path);
            Console.WriteLine("✅ ONNX model loaded");
        }
        else
        {
            Console.WriteLine($"❌ ONNX model NOT found at: {path}");
        }
    }

    public static MoveState ChooseMove(BattleState state)
    {
        var attacker = state.Player1.ActivePokemon;
        var defender = state.Player2.ActivePokemon;

        if (session == null)
        {
            return attacker.Moves
                .Where(m => m.CurrentPP > 0)
                .OrderByDescending(m => ScoreMove(m, attacker, defender))
                .First();
        }
        var moves = attacker.Moves.Where(m => m.CurrentPP > 0).ToList();
        return moves[new Random().Next(moves.Count)];
    }
    static float ScoreMove(MoveState move, PokemonState attacker, PokemonState defender)
    {
        float score = 0;

        // Base power
        score += move.Power;

        // STAB
        if (attacker.Types.Contains(move.Type))
            score *= 1.5f;

        // Accuracy
        score *= (move.Accuracy / 100f);

        return score;
    }
}