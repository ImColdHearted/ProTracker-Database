using System.Collections.Generic;
using PokemonSim.Engine.Effects;
using PokemonSim.Models;

namespace PokemonSim.Engine.Abilities
{
    /// <summary>
    /// Section 158. An ability whose object carries no behaviour of its
    /// own, in one of two situations the factory documents case by case:
    ///
    /// 1. The behaviour lives in engine code keyed off the normalized
    ///    AbilityId - the way Levitate has always worked in Grounding.
    ///    Sturdy, Unaware, Contrary, Pressure, No Guard, Inner Focus,
    ///    Truant, Shadow Tag and the rest of the id-checked crowd sit
    ///    here: their ability object exists so the id is SUPPORTED (team
    ///    building stops warning), while MoveResolver, DamageCalculator,
    ///    LegalActions, TryInflictStatus or AbilityEndOfTurn do the work.
    ///
    /// 2. The ability's premise is outside the Simulator (held items,
    ///    allies, wild encounters, forme changes) - Unnerve, Pickup, Run
    ///    Away, Stance Change and friends. Those are deliberately inert,
    ///    the honest translation of "this cannot matter in this battle
    ///    model", and the §158 guide lists every one.
    /// </summary>
    public class MarkerAbility : IAbility
    {
        readonly string id;

        public MarkerAbility(string id)
        {
            this.id = id;
        }

        public string Id => id;

        public IEnumerable<IMoveEffect> GetEffects()
        {
            yield break;
        }

        public void OnAttach(PokemonState pokemon, BattleState state) { }
    }
}