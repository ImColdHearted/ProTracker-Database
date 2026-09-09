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