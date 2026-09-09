using PokemonSim.Simulation;
using Xunit;

namespace PokemonSim.Tests
{
    /// <summary>Section 167: the display spread reads in the order the
    /// card prints its rows (Atk/Def/Spe/SpA/SpD/HP), so users verify the
    /// preview against the game at a glance. Internally everything stays
    /// HP-first - only the text reorders.</summary>
    public class Section167Tests
    {
        [Fact]
        public void CardOrderSpread_ReordersButNeverChangesTheValues()
        {
            // Internal order is HP, Atk, Def, SpA, SpD, Spe - distinct
            // markers per slot make any mapping mistake visible.
            var mon = new ImportedPokemon
            {
                Ivs = new[] { 0, 1, 2, 3, 4, 5 },
                Evs = new[] { 10, 11, 12, 13, 14, 15 }
            };

            Assert.Equal(
                "IVs 1/2/5/3/4/0   EVs 11/12/15/13/14/10   (Atk/Def/Spe/SpA/SpD/HP)",
                mon.CardOrderSpread);
        }

        [Fact]
        public void CardOrderSpread_MatchesARealCard()
        {
            // The user's Ferrothorn: internally HP 1/252, Atk 8/0, Def 2/6,
            // SpA 9/0, SpD 9/252, Spe 6/0 - on the card it reads
            // Atk 8, Def 2, Spe 6, SpA 9, SpD 9, HP 1 top to bottom.
            var mon = new ImportedPokemon
            {
                Ivs = new[] { 1, 8, 2, 9, 9, 6 },
                Evs = new[] { 252, 0, 6, 0, 252, 0 }
            };

            Assert.Equal(
                "IVs 8/2/6/9/9/1   EVs 0/6/0/0/252/252   (Atk/Def/Spe/SpA/SpD/HP)",
                mon.CardOrderSpread);
        }
    }
}