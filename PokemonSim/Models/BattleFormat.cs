namespace PokemonSim.Models
{
    /// <summary>
    /// §305. How many Pokemon each side has on the field.
    ///
    /// The value IS the slot count, so nothing ever has to translate the
    /// name into a number: Player1.Active has (int)state.Format entries and
    /// so does Player2's.
    ///
    /// Only Singles exists as a playable format today. Doubles is declared
    /// here because the whole point of this section is that every slot-shaped
    /// thing in the engine now says which slot it means; a format that names
    /// the second slot is what makes "slot 0" read as a choice rather than
    /// as the only possibility. Nothing branches on Doubles yet, and the
    /// battery below checks that nothing does.
    /// </summary>
    public enum BattleFormat
    {
        Singles = 1,
        Doubles = 2
    }
}
