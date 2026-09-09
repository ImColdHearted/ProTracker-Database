namespace PokemonSim.Models
{
    public enum BattleEventType
    {
        SwitchIn,
        SwitchOut,
        BeforeMove,
        ModifyDamage,
        DamageDealt,
        Residual,
        AfterDamage,
        ImmunityCheck,
        PokemonFainted,
        EndTurn,
        // expand later
    }
}