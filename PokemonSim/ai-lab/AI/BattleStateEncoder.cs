using PokemonSim.Models;

namespace PokemonSim.AI
{
    public static class BattleStateEncoder
    {
        public static float[] Encode(BattleState state)
        {
            var p1 = state.Player1.ActivePokemon;
            var p2 = state.Player2.ActivePokemon;

            return new float[]
            {
                p1.CurrentHP / (float)p1.MaxHP,
                p2.CurrentHP / (float)p2.MaxHP,

                p1.AttackStage,
                p1.DefenseStage,
                p1.SpeedStage,

                p2.AttackStage,
                p2.DefenseStage,
                p2.SpeedStage,

                (float)state.Environment.Weather,
                (float)state.Environment.Terrain
            };
        }
    }
}