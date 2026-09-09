namespace PokemonSim.Models
{
    // Section 154: moved out of the global namespace into PokemonSim.Models -
    // a bare global "Stats" collides too easily once this project is
    // referenced by the tracker. Content unchanged.
    public class Stats
    {
        public int HP;
        public int Attack;
        public int Defense;
        public int SpAttack;
        public int SpDefense;
        public int Speed;

        public Stats Clone()
        {
            return new Stats
            {
                HP = HP,
                Attack = Attack,
                Defense = Defense,
                SpAttack = SpAttack,
                SpDefense = SpDefense,
                Speed = Speed
            };
        }
    }
}