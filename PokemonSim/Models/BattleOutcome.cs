namespace PokemonSim.Models
{
    /// <summary>Section 154. How a battle stands or ended. Draw covers both
    /// the turn-limit stop and the rare turn where the last Pokemon on both
    /// sides faint together.</summary>
    public enum BattleOutcome
    {
        Unfinished,
        Player1Wins,
        Player2Wins,
        Draw,
        Cancelled
    }
}