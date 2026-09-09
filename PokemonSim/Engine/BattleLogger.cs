using System;

namespace PokemonSim.Engine
{
    /// <summary>Section 154: retired. Battle text goes through the
    /// BattleState's own BattleLog now (see that class for why a global
    /// Console logger could not serve the Simulator window or two battles
    /// at once). This shim stays so the name and its history are not lost;
    /// nothing in the engine calls it any more.</summary>
    [Obsolete("Battle text goes through BattleState.Log (BattleLog) since section 154.")]
    public static class BattleLogger
    {
        public static bool Enabled = true;

        public static void Log(string message)
        {
            if (Enabled)
                Console.WriteLine(message);
        }

        public static void Turn(int turn)
        {
            if (Enabled)
                Console.WriteLine($"\n--- Turn {turn} ---");
        }
    }
}