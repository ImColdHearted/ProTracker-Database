using PokemonSim.Models;

namespace PokemonSim.Engine
{
    /// <summary>
    /// +10%/-10% nature modifiers. Section 154 completed the table: the
    /// original carried only the 13 natures its preferred-nature lists
    /// happened to use, so the other 12 silently behaved as neutral. This is
    /// now the full 25-nature catalog - every non-HP stat boosted by exactly
    /// 4 natures and hindered by exactly 4, the 5 neutral natures falling
    /// through to 1.0 - matching PokemonBattleMath.NatureCatalog in the
    /// tracker (section 89), which the test suite cross-checks.
    /// </summary>
    public static class NatureCalculator
    {
        public static double GetModifier(Nature nature, string stat)
        {
            return (nature, stat) switch
            {
                (Nature.Lonely, "Attack") => 1.1,
                (Nature.Lonely, "Defense") => 0.9,

                (Nature.Brave, "Attack") => 1.1,
                (Nature.Brave, "Speed") => 0.9,

                (Nature.Adamant, "Attack") => 1.1,
                (Nature.Adamant, "SpAttack") => 0.9,

                (Nature.Naughty, "Attack") => 1.1,
                (Nature.Naughty, "SpDefense") => 0.9,

                (Nature.Bold, "Defense") => 1.1,
                (Nature.Bold, "Attack") => 0.9,

                (Nature.Relaxed, "Defense") => 1.1,
                (Nature.Relaxed, "Speed") => 0.9,

                (Nature.Impish, "Defense") => 1.1,
                (Nature.Impish, "SpAttack") => 0.9,

                (Nature.Lax, "Defense") => 1.1,
                (Nature.Lax, "SpDefense") => 0.9,

                (Nature.Timid, "Speed") => 1.1,
                (Nature.Timid, "Attack") => 0.9,

                (Nature.Hasty, "Speed") => 1.1,
                (Nature.Hasty, "Defense") => 0.9,

                (Nature.Jolly, "Speed") => 1.1,
                (Nature.Jolly, "SpAttack") => 0.9,

                (Nature.Naive, "Speed") => 1.1,
                (Nature.Naive, "SpDefense") => 0.9,

                (Nature.Modest, "SpAttack") => 1.1,
                (Nature.Modest, "Attack") => 0.9,

                (Nature.Mild, "SpAttack") => 1.1,
                (Nature.Mild, "Defense") => 0.9,

                (Nature.Quiet, "SpAttack") => 1.1,
                (Nature.Quiet, "Speed") => 0.9,

                (Nature.Rash, "SpAttack") => 1.1,
                (Nature.Rash, "SpDefense") => 0.9,

                (Nature.Calm, "SpDefense") => 1.1,
                (Nature.Calm, "Attack") => 0.9,

                (Nature.Gentle, "SpDefense") => 1.1,
                (Nature.Gentle, "Defense") => 0.9,

                (Nature.Sassy, "SpDefense") => 1.1,
                (Nature.Sassy, "Speed") => 0.9,

                (Nature.Careful, "SpDefense") => 1.1,
                (Nature.Careful, "SpAttack") => 0.9,
                _ => 1.0
            };
        }
    }
}