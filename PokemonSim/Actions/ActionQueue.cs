using System.Collections.Generic;
using System.Linq;

namespace PokemonSim.Actions
{
    public class ActionQueue
    {
        public List<BattleAction> Actions = new();

        /// <summary>Section 158: under Trick Room the SLOWER action wins
        /// its priority bracket - priority order itself is untouched.
        /// §375: a Quick Claw that went off wins its bracket outright,
        /// Trick Room or not - it beats Speed, never Priority.</summary>
        public void Sort(bool slowFirst = false)
        {
            Actions = Actions
                .OrderByDescending(a => a.Priority)
                .ThenByDescending(a => a.QuickClaw ? 1 : 0)
                .ThenBy(a => slowFirst ? a.Speed : -a.Speed)
                // Section 154: seeded coin flip on full ties - see
                // BattleAction.TieBreak.
                .ThenByDescending(a => a.TieBreak)
                .ToList();
        }
    }
}