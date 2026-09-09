using PokemonSim.Engine;
using PokemonSim.Engine.Effects;
using System.Collections.Generic;
using System.Linq;

namespace PokemonSim.Models
{
    public class PokemonState
    {
        public required string Species;

        public required List<PokemonType> Types;

        public int Level;

        public int CurrentHP;
        public int MaxHP;

        public IAbility? Ability { get; set; }
        public Dictionary<string, object> AbilityState { get; set; } = new();
        public string? AbilityId { get; set; }
        public List<IMoveEffect> PassiveEffects { get; set; } = new();
        public bool HasMagicGuard { get; set; }

        public StatusCondition Status = StatusCondition.None;

        public int SleepTurns;
        public int ToxicCounter { get; set; } = 0;
        public bool Flinched;

        public int HPIV = 31;
        public int AttackIV = 31;
        public int DefenseIV = 31;
        public int SpAttackIV = 31;
        public int SpDefenseIV = 31;
        public int SpeedIV = 31;

        public int HPEV = 0;
        public int AttackEV = 0;
        public int DefenseEV = 0;
        public int SpAttackEV = 0;
        public int SpDefenseEV = 0;
        public int SpeedEV = 0;

        public Nature Nature = Nature.Hardy;

        public int AttackStage;
        public int DefenseStage;
        public int SpAttackStage;
        public int SpDefenseStage;
        public int SpeedStage;
        public int AccuracyStage;
        public int EvasionStage;

        public bool Protected;

        // Section 154: Protect bookkeeping - the classic diminishing success
        // chance needs to know whether Protect was used this turn and how
        // many times in a row. The engine clears ProtectedThisTurn (and,
        // when a turn passes without Protect, the streak) at end of turn.
        public bool ProtectedThisTurn;
        public int ConsecutiveProtects;

        public bool Charging;

        // Section 154: which move the two-turn charge belongs to - the only
        // legal action on the release turn, so the engine must remember it.
        public MoveState? ChargingMove;

        public int SubstituteHP;

        public class TrapEffect
        {
            public int TurnsRemaining { get; set; }
            public double DamageFraction { get; set; }
            public required string SourceMove { get; set; }

            public TrapEffect Clone() => new TrapEffect
            {
                TurnsRemaining = TurnsRemaining,
                DamageFraction = DamageFraction,
                SourceMove = SourceMove
            };
        }

        public TrapEffect? Trap { get; set; }

        // ---- Section 158: the volatile-state batch that the move and
        // ability completion needs. Everything here is battle-volatile:
        // Clone() copies each field, and SwitchResolver.ResetVolatileState
        // clears each when the Pokemon leaves the field. ----

        /// <summary>Leech Seed: drained an eighth each turn, feeding the
        /// opposing active.</summary>
        public bool LeechSeeded;

        /// <summary>Aqua Ring: heals a sixteenth each turn.</summary>
        public bool AquaRing;

        /// <summary>Confusion - turns remaining (0 = not confused). A
        /// third of actions while confused hit the user instead.</summary>
        public int ConfusionTurns;

        /// <summary>Perish Song countdown. -1 = no song; faints at 0.</summary>
        public int PerishCount = -1;

        /// <summary>Yawn: falls asleep when this counts down to zero.</summary>
        public int YawnTurns;

        public string? EncoreMoveName;
        public int EncoreTurns;

        public int TauntTurns;

        public string? DisabledMoveName;
        public int DisabledTurns;

        /// <summary>Torment: may not repeat its last move.</summary>
        public bool Tormented;

        /// <summary>Ingrain: rooted - heals, cannot switch, counts as
        /// grounded.</summary>
        public bool Rooted;

        public int MagnetRiseTurns;

        public int StockpileCount;

        public bool DestinyBondActive;

        /// <summary>Ghost Curse: loses a quarter each turn.</summary>
        public bool Cursed;

        /// <summary>Salt Cure: an eighth per turn (a quarter for Water and
        /// Steel types).</summary>
        public bool SaltCured;

        // Protect-family variants (cleared with Protected at end of turn).
        public bool KingsShieldUp;

        /// <summary>Section 162: Baneful Bunker - blocks like Protect and
        /// poisons attackers that make contact.</summary>
        public bool BanefulBunkerUp;

        /// <summary>Section 164: purely cosmetic - the §162 importer reads
        /// the summary card's golden S badge, and the battle views show the
        /// shiny artwork. No battle effect, survives everything (a mega
        /// evolution included).</summary>
        public bool IsShiny;

        /// <summary>§199. Purely cosmetic, and null for almost everybody:
        /// an explicit picture to draw this Pokemon with, instead of the
        /// one its species name resolves to. Spelled the way the
        /// counterparts catalog spells its own, relative to the
        /// application folder with forward slashes -
        /// "SharedPokemonLibrary/Assets/Counterparts/Pinkan/Gyarados.png".
        ///
        /// It exists because a counterpart is a sprite SKIN: a Pinkan
        /// Gyarados is a Gyarados to every rule in this engine, so there
        /// is no species name that reaches its artwork. §173's custom
        /// opponents are the one place an author wants to say "that one",
        /// and this is how the file says it.
        ///
        /// Nothing in the engine reads it - it rides along to whatever
        /// draws the battle, the same way IsShiny does. It survives a
        /// mega evolution for the same reason IsShiny does: it is a
        /// property of the slot its author filled in, not of the form
        /// currently on the field.</summary>
        public string? SpritePath;

        /// <summary>§200. Purely cosmetic, and 0 for almost everybody: the
        /// national-dex id of the exact FORM to draw, as the sprite library
        /// names its files (PokeAPI's ids - 25.png, and 10000-and-up for the
        /// regional and alternate forms).
        ///
        /// It exists because a name is ambiguous where a number is not.
        /// "Typhlosion" resolves to the Johto one; the Hisuian form is a
        /// different picture with no spelling of its own that every roster
        /// agrees on, so a boss meant to field it quietly fielded the
        /// ordinary one. The Boss Database has carried dexNumber on every
        /// Pokemon since it was written - see BossPokemonData - and §200 is
        /// that field reaching the battle rather than stopping at the boss
        /// card.
        ///
        /// Nothing in the engine reads it. It loses to SpritePath, which is
        /// an author naming one exact file, and beats the species name,
        /// which is a guess.</summary>
        public int DexNumber;

        public bool QuickGuardUp;
        public bool WideGuardUp;
        public bool Enduring;

        /// <summary>Lock-On: the next move skips its accuracy roll.</summary>
        public bool LockOnActive;

        public bool ImprisonActive;

        public string? LastMoveName;

        // Counter / Mirror Coat / Metal Burst bookkeeping - damage taken
        // this turn by category, cleared at end of turn.
        public int LastPhysicalDamageTaken;
        public int LastSpecialDamageTaken;

        /// <summary>Analytic: whether this Pokemon has already acted this
        /// turn. Cleared at end of turn.</summary>
        public bool ActedThisTurn;

        /// <summary>Truant: true on the turns it loafs around.</summary>
        public bool Loafing;

        /// <summary>Slow Start: the turn this Pokemon entered the field
        /// (its penalty lasts five turns from entry).</summary>
        public int EnteredFieldTurn;

        /// <summary>§197. False until this Pokemon has taken a move action
        /// since it came in. Fake Out and First Impression are the moves that
        /// care, and this is the honest form of the rule they need.
        ///
        /// EnteredFieldTurn cannot answer it. A replacement sent in after a
        /// faint gets EnteredFieldTurn set to the turn that just ENDED, so on
        /// its first real turn the numbers already differ and the move would
        /// be refused; a voluntary switch spends its turn, so the switcher's
        /// first real turn is the next one and the numbers differ there too.
        /// Both of those are the case the report specifically asked to keep
        /// working. Whether the Pokemon has actually had a go yet is the
        /// question being asked, so it is the question stored.
        ///
        /// Set when the move action is taken, not when it succeeds: being
        /// flinched or fully paralysed on the turn you come in still spends
        /// the window, which is how the games play it.</summary>
        public bool HasActedSinceEnteringField;

        // ---- Section 159: held items. ----

        /// <summary>The held item's normalized id (null = holding
        /// nothing). NOT volatile - it survives switching.</summary>
        public string? HeldItemId;

        /// <summary>True once an item was consumed, flung, knocked off or
        /// stolen - what Unburden and the theft moves read. Survives
        /// switching, like the loss itself.</summary>
        public bool LostItem;

        /// <summary>The Choice-item lock: the first move picked while
        /// holding one. Cleared on leaving the field.</summary>
        public string? ChoiceLockedMoveName;

        // ---- Section 161: mega evolution. ----

        /// <summary>True once this Pokemon has mega evolved. Permanent for
        /// the battle - switching out does not undo the forme, so this is
        /// NOT cleared by ResetVolatileState.</summary>
        public bool MegaEvolved;

        // Convenience mirrors of StatResolver's arithmetic for callers that
        // have no BattleState at hand (display code). Battle decisions go
        // through StatResolver, which also runs ability stat effects.
        public int Speed
        {
            get
            {
                double speed = Stats.Speed;

                speed *= StatStageCalculator.GetMultiplier(SpeedStage);

                if (Status == StatusCondition.Paralysis)
                    speed *= 0.5;

                return (int)speed;
            }
        }

        public int Attack
        {
            get
            {
                double value = Stats.Attack;
                value *= StatStageCalculator.GetMultiplier(AttackStage);
                return (int)value;
            }
        }

        public int Defense
        {
            get
            {
                double value = Stats.Defense;
                value *= StatStageCalculator.GetMultiplier(DefenseStage);
                return (int)value;
            }
        }

        public int SpAttack
        {
            get
            {
                double value = Stats.SpAttack;
                value *= StatStageCalculator.GetMultiplier(SpAttackStage);
                return (int)value;
            }
        }

        public int SpDefense
        {
            get
            {
                double value = Stats.SpDefense;
                value *= StatStageCalculator.GetMultiplier(SpDefenseStage);
                return (int)value;
            }
        }

        public required Stats Stats;

        public required List<MoveState> Moves;

        public bool Fainted => CurrentHP <= 0;

        /// <summary>Section 154: a complete copy of everything a battle can
        /// change. The original clone dropped ability identity and state,
        /// IVs/EVs, the toxic counter, the trap, and the Protect/charge
        /// bookkeeping - so a cloned battle drifted from the real one
        /// immediately. Ability and PassiveEffects are deliberately NOT
        /// copied here: effect objects can capture the battle they were
        /// attached to, so BattleState.Clone reattaches them on the cloned
        /// battle through AbilityFactory.Restore using the copied
        /// AbilityId.</summary>
        public PokemonState Clone()
        {
            var clone = new PokemonState
            {
                Species = Species,
                Level = Level,
                CurrentHP = CurrentHP,
                MaxHP = MaxHP,
                AbilityId = AbilityId,
                AbilityState = new Dictionary<string, object>(AbilityState),
                HasMagicGuard = HasMagicGuard,
                Status = Status,
                SleepTurns = SleepTurns,
                ToxicCounter = ToxicCounter,
                Flinched = Flinched,
                HPIV = HPIV,
                AttackIV = AttackIV,
                DefenseIV = DefenseIV,
                SpAttackIV = SpAttackIV,
                SpDefenseIV = SpDefenseIV,
                SpeedIV = SpeedIV,
                HPEV = HPEV,
                AttackEV = AttackEV,
                DefenseEV = DefenseEV,
                SpAttackEV = SpAttackEV,
                SpDefenseEV = SpDefenseEV,
                SpeedEV = SpeedEV,
                Nature = Nature,
                AttackStage = AttackStage,
                DefenseStage = DefenseStage,
                SpAttackStage = SpAttackStage,
                SpDefenseStage = SpDefenseStage,
                SpeedStage = SpeedStage,
                AccuracyStage = AccuracyStage,
                EvasionStage = EvasionStage,
                Protected = Protected,
                ProtectedThisTurn = ProtectedThisTurn,
                ConsecutiveProtects = ConsecutiveProtects,
                Charging = Charging,
                SubstituteHP = SubstituteHP,
                Trap = Trap?.Clone(),
                LeechSeeded = LeechSeeded,
                AquaRing = AquaRing,
                ConfusionTurns = ConfusionTurns,
                PerishCount = PerishCount,
                YawnTurns = YawnTurns,
                EncoreMoveName = EncoreMoveName,
                EncoreTurns = EncoreTurns,
                TauntTurns = TauntTurns,
                DisabledMoveName = DisabledMoveName,
                DisabledTurns = DisabledTurns,
                Tormented = Tormented,
                Rooted = Rooted,
                MagnetRiseTurns = MagnetRiseTurns,
                StockpileCount = StockpileCount,
                DestinyBondActive = DestinyBondActive,
                Cursed = Cursed,
                SaltCured = SaltCured,
                KingsShieldUp = KingsShieldUp,
                BanefulBunkerUp = BanefulBunkerUp,
                QuickGuardUp = QuickGuardUp,
                WideGuardUp = WideGuardUp,
                Enduring = Enduring,
                LockOnActive = LockOnActive,
                ImprisonActive = ImprisonActive,
                LastMoveName = LastMoveName,
                LastPhysicalDamageTaken = LastPhysicalDamageTaken,
                LastSpecialDamageTaken = LastSpecialDamageTaken,
                ActedThisTurn = ActedThisTurn,
                Loafing = Loafing,
                EnteredFieldTurn = EnteredFieldTurn,
                HasActedSinceEnteringField = HasActedSinceEnteringField,
                HeldItemId = HeldItemId,
                LostItem = LostItem,
                ChoiceLockedMoveName = ChoiceLockedMoveName,
                MegaEvolved = MegaEvolved,
                IsShiny = IsShiny,
                SpritePath = SpritePath,
                DexNumber = DexNumber,
                Stats = Stats.Clone(),
                Types = new List<PokemonType>(Types),
                Moves = Moves.Select(m => m.Clone()).ToList()
            };

            // The charging move must be the CLONE's own move instance, not a
            // reference into the original's move list.
            if (ChargingMove != null)
            {
                int index = Moves.IndexOf(ChargingMove);
                clone.ChargingMove = index >= 0 ? clone.Moves[index] : ChargingMove.Clone();
            }

            return clone;
        }
    }
}