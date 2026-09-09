using System.Text.Json;

namespace PokemonSim.AI
{
    public static class TrainingDataWriter
    {
        static string file = "training_data.json";

        public static void Write(
            List<(float[] state, int move)> history,
            int result)
        {
            var data = history.Select(h => new
            {
                state = h.state,
                move = h.move,
                outcome = result
            });

            var json = JsonSerializer.Serialize(data);

            File.AppendAllText(file, json + "\n");
        }
    }
}