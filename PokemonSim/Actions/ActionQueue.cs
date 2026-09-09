using System.Collections.Generic;
using System.Linq;

namespace PokemonSim.Actions
{
    public class ActionQueue
    {
        public List<BattleAction> Actions = new();

        /// <summary>Section 158: under Trick Room the SLOWER action wins
        /// its priority bracket - priority order itself is untouched.</summary>
        public void Sort(bool slowFirst = false)
        {
            Actions = Actions
                .OrderByDescending(a => a.Priority)
                .ThenBy(a => slowFirst ? a.Speed : -a.Speed)
                // Section 154: seeded coin flip on full ties - see
                // BattleAction.TieBreak.
                .ThenByDescending(a => a.TieBreak)
                .ToList();
        }
    }
}