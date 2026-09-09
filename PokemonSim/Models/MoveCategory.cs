namespace PokemonSim.Models
{
    public enum MoveCategory
    {
        Physical,   // Uses Attack vs Defense
        Special,    // Uses SpAttack vs SpDefense
        Status      // Non-damaging moves (buffs, hazards, etc.)
    }
}