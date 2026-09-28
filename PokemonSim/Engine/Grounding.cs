using PokemonSim.Models;

namespace PokemonSim.Engine
{
    /// <summary>Section 154: the one shared answer to "does the ground
    /// affect this Pokemon" - Flying types and Levitate hover. Used by
    /// terrain boosts, Psychic Terrain's priority shield, and the entry
    /// hazards that live on the ground (Spikes, Toxic Spikes; Stealth Rock
    /// floats and hits everything).</summary>
    public static class Grounding
    {
        /// <summary>The stateless answer - kept for callers with no battle
        /// at hand. Prefer the state-aware overload in battle code.</summary>
        public static bool IsGrounded(PokemonState pokemon) =>
            IsGrounded(null, pokemon);

        /// <summary>Section 158: Gravity grounds everything, Ingrain roots
        /// (and therefore grounds), Magnet Rise hovers.</summary>
        public static bool IsGrounded(BattleState? state, PokemonState pokemon)
        {
            if (state != null && state.GravityTurns > 0)
                return true;

            if (pokemon.Rooted)
                return true;

            // §304: Smack Down and Thousand Arrows bring it down and keep
            // it down, over anything that was holding it up.
            if (pokemon.SmackedDown)
                return true;

            if (pokemon.MagnetRiseTurns > 0)
                return false;

            // Section 159: an unpopped Air Balloon keeps its holder up.
            if (Items.HeldItems.Normalize(pokemon.HeldItemId) == "airballoon")
                return false;

            // §304: Roost costs the user its Flying type for the turn,
            // which is exactly what stops it hovering.
            if (pokemon.Types.Contains(PokemonType.Flying) && !pokemon.RoostedThisTurn)
                return false;

            if (pokemon.AbilityId != null &&
                pokemon.AbilityId.Replace(" ", "").ToLowerInvariant() == "levitate")
                return false;

            return true;
        }
    }
}