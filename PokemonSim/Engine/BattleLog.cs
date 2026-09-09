using System;
using System.Collections.Generic;

namespace PokemonSim.Engine
{
    /// <summary>
    /// Section 154. The battle's own readable log, owned by its BattleState -
    /// replacing the global Console-writing BattleLogger, which could not
    /// serve two battles at once, could not reach an Avalonia window, and
    /// leaked one battle's lines into another's (parallel simulations
    /// included). Everything the old logger printed is written here instead;
    /// the Simulator window reads Lines, the dev console prints them, and a
    /// cloned battle gets its own Silent log so AI rollouts stay invisible.
    /// </summary>
    public sealed class BattleLog
    {
        private readonly List<string> lines = new();

        /// <summary>A silent log records nothing - the mode cloned states
        /// use, so thousands of rollout battles cost no memory and no one
        /// mistakes their text for the visible battle's.</summary>
        public bool Silent { get; set; }

        public IReadOnlyList<string> Lines => lines;

        public void Write(string message)
        {
            if (!Silent)
                lines.Add(message);
        }

        public void Turn(int turnNumber) => Write($"--- Turn {turnNumber} ---");
    }
}