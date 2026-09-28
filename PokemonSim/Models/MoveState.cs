using System.Collections.Generic;
using System.Linq;

namespace PokemonSim.Models
{
    public class MoveState
    {
        public required string Name;
        public int Power;
        public int Priority = 0;

        // Section 154: 0 or less means the move cannot miss (Aerial Ace,
        // Swift, self-targeted status moves) - the data files simply omit
        // accuracy for those, and the resolver skips the roll.
        public int Accuracy = 100;

        public int MinHits = 1;
        public int MaxHits = 1;

        public int CritStage = 0;

        public int Index;

        public StatusCondition InflictStatus = StatusCondition.None;
        public List<StatChange>? StatChanges;
        public double FlinchChance;
        public double SecondaryChance;
        public double StatusChance = 0;

        public List<StatusCondition>? RandomStatus;

        public WeatherType SetWeather = WeatherType.None;
        public TerrainType SetTerrain = TerrainType.None;

        public List<string>? Effects;

        public int CurrentPP;
        public int MaxPP;

        // Section 154: Struggle - the only typeless move; it ignores the
        // type chart and STAB entirely.
        public bool Typeless;

        // Section 161: set on synthesized Z-Moves only - the resolver lets
        // a quarter of their damage through Protect instead of blocking.
        public bool IsZMove;

        // Section 155: Psyshock-class - Special, but damage runs
        // against the target's Defense.
        public bool UsesTargetDefense;

        // Section 158: the flag batch for the move completion.

        /// <summary>Body Press: the user's Defense is its attacking stat.</summary>
        public bool UsesDefenseAsOffense;

        /// <summary>Foul Play: the TARGET's Attack powers the hit.</summary>
        public bool UsesTargetAttack;

        /// <summary>Tera Blast: runs physical when the user's Attack beats
        /// its Sp. Atk.</summary>
        public bool UsesHigherOffense;

        // ---- §304 ----

        /// <summary>Shell Side Arm: physical or special, whichever would
        /// actually hurt more - which means comparing whole damage rolls,
        /// the target's two defences included, not just the user's two
        /// attacking stats. UsesHigherOffense is the weaker rule beside it
        /// and belongs to Tera Blast.</summary>
        public bool UsesBetterDamage;

        /// <summary>Darkest Lariat and Sacred Sword: the target's stat
        /// stages are not there. Defensive boosts are ignored for damage
        /// and the evasion boost for accuracy - the same pair of rules
        /// Unaware already implements as an ability.</summary>
        public bool IgnoresDefensiveBoosts;

        public bool IgnoresEvasion;

        /// <summary>Thousand Arrows: a Ground move that reaches a Flying
        /// target, and reaches it for neutral rather than for nothing.</summary>
        public bool IgnoresFlyingImmunity;

        /// <summary>Stomp, Body Slam and the rest of the flatteners: twice
        /// the damage into a Minimized target, and they cannot miss it.
        /// Derived from MoveFlags at load time like IsContact, because it
        /// is mechanics knowledge rather than a per-game number.</summary>
        public bool IsFlattening;

        // Derived from MoveFlags at load time, not stored in the data file.
        public bool IsContact;
        public bool IsPulse;
        public bool IsSpread;
        public bool IsPunch;

        public required PokemonType Type;
        public required MoveCategory Category;

        /// <summary>Section 154: a full copy. The original clone dropped
        /// every field below Priority/Accuracy/PP, so cloned battles fought
        /// with moves stripped of their effects, secondary chances, stat
        /// changes and multi-hit counts.</summary>
        public MoveState Clone()
        {
            return new MoveState
            {
                Name = Name,
                Power = Power,
                Priority = Priority,
                Accuracy = Accuracy,
                MinHits = MinHits,
                MaxHits = MaxHits,
                CritStage = CritStage,
                Index = Index,
                InflictStatus = InflictStatus,
                StatChanges = StatChanges?.Select(c => new StatChange { Stat = c.Stat, Stages = c.Stages, Target = c.Target }).ToList(),
                FlinchChance = FlinchChance,
                SecondaryChance = SecondaryChance,
                StatusChance = StatusChance,
                RandomStatus = RandomStatus?.ToList(),
                SetWeather = SetWeather,
                SetTerrain = SetTerrain,
                UsesTargetDefense = UsesTargetDefense,
                UsesDefenseAsOffense = UsesDefenseAsOffense,
                UsesTargetAttack = UsesTargetAttack,
                UsesHigherOffense = UsesHigherOffense,
                UsesBetterDamage = UsesBetterDamage,
                IgnoresDefensiveBoosts = IgnoresDefensiveBoosts,
                IgnoresEvasion = IgnoresEvasion,
                IgnoresFlyingImmunity = IgnoresFlyingImmunity,
                IsFlattening = IsFlattening,
                IsContact = IsContact,
                IsPulse = IsPulse,
                IsSpread = IsSpread,
                IsPunch = IsPunch,
                Effects = Effects?.ToList(),
                CurrentPP = CurrentPP,
                MaxPP = MaxPP,
                Typeless = Typeless,
                IsZMove = IsZMove,
                Type = Type,
                Category = Category
            };
        }
    }
}