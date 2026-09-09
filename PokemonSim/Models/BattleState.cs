using System.Collections.Generic;
using System.Linq;
using PokemonSim.Engine;
using PokemonSim.Engine.Abilities;

namespace PokemonSim.Models
{
    public class BattleState
    {
        public int TurnNumber;

        public BattleEnvironment Environment = new BattleEnvironment();

        public required PlayerState Player1;
        public required PlayerState Player2;

        // Entry hazards, per side ("P1" = lying on Player1's side of the
        // field, hitting Player1's Pokemon as they switch in).
        public int ToxicSpikesP1 { get; set; }
        public int ToxicSpikesP2 { get; set; }
        public int SpikesP1 { get; set; }
        public int SpikesP2 { get; set; }
        public bool StealthRockP1 { get; set; }
        public bool StealthRockP2 { get; set; }

        /// <summary>Section 184. Sticky Web: one layer or none, and it
        /// costs the incoming Pokemon a stage of Speed rather than HP -
        /// which is why it is a bool beside the counted hazards.</summary>
        public bool StickyWebP1 { get; set; }
        public bool StickyWebP2 { get; set; }

        // ---- Section 158: side conditions and field states the move
        // completion adds. Plain fields so effects can take them by ref
        // through the side helpers below. ----
        public int ReflectTurnsP1;
        public int ReflectTurnsP2;
        public int LightScreenTurnsP1;
        public int LightScreenTurnsP2;
        public int MistTurnsP1;
        public int MistTurnsP2;
        public int SafeguardTurnsP1;
        public int SafeguardTurnsP2;
        public int TailwindTurnsP1;
        public int TailwindTurnsP2;
        public bool HealingWishP1;
        public bool HealingWishP2;

        /// <summary>Trick Room: while above zero, slower Pokemon act first
        /// within each priority bracket.</summary>
        public int TrickRoomTurns;

        /// <summary>Gravity: while above zero, everything is grounded.</summary>
        public int GravityTurns;

        // Side helpers - "the side that owns this player".
        public ref int ReflectTurns(PlayerState side) =>
            ref side == Player1 ? ref ReflectTurnsP1 : ref ReflectTurnsP2;

        public ref int LightScreenTurns(PlayerState side) =>
            ref side == Player1 ? ref LightScreenTurnsP1 : ref LightScreenTurnsP2;

        public ref int MistTurns(PlayerState side) =>
            ref side == Player1 ? ref MistTurnsP1 : ref MistTurnsP2;

        public ref int SafeguardTurns(PlayerState side) =>
            ref side == Player1 ? ref SafeguardTurnsP1 : ref SafeguardTurnsP2;

        public ref int TailwindTurns(PlayerState side) =>
            ref side == Player1 ? ref TailwindTurnsP1 : ref TailwindTurnsP2;

        public ref bool HealingWish(PlayerState side) =>
            ref side == Player1 ? ref HealingWishP1 : ref HealingWishP2;

        public EventManager Events = new EventManager();
        public bool IgnoreDefenderAbilities { get; set; } = false;

        // ---- Section 154 ----

        /// <summary>Every roll this battle makes. Seeded, deterministic,
        /// cloneable - see BattleRng.</summary>
        public BattleRng Rng = new BattleRng(0);

        /// <summary>The battle's own readable text - see BattleLog.</summary>
        public BattleLog Log = new BattleLog();

        /// <summary>The finite turn limit: reaching it with both sides
        /// standing is a Draw. 300 comfortably outlasts any real fight while
        /// guaranteeing termination even for two healing stall teams.</summary>
        public int MaxTurns = 300;

        public BattleOutcome Outcome = BattleOutcome.Unfinished;

        /// <summary>Set once by BattleInitializer - guards double
        /// initialization and battles run before switch-in effects.</summary>
        public bool Initialized;

        public bool BattleOver =>
            Outcome != BattleOutcome.Unfinished || Player1.HasLost() || Player2.HasLost();

        public PlayerState GetOwner(PokemonState pokemon) =>
            Player1.Team.Contains(pokemon) ? Player1 : Player2;

        public PlayerState GetOpponentOf(PlayerState player) =>
            player == Player1 ? Player2 : Player1;

        /// <summary>The opposing ACTIVE Pokemon (singles). Section 154: this
        /// used to return the whole opposing team, which made Intimidate
        /// lower every bench Pokemon's Attack on entry.</summary>
        public IEnumerable<PokemonState> GetOpponents(PokemonState pokemon)
        {
            PokemonState other = Player1.Team.Contains(pokemon)
                ? Player2.ActivePokemon
                : Player1.ActivePokemon;

            if (!other.Fainted)
                yield return other;
        }

        /// <summary>Section 154: a genuinely independent copy. Beyond the
        /// old field-by-field copy this now carries the hazards (all six
        /// side fields were dropped before), the turn/outcome bookkeeping,
        /// its own EventManager with abilities re-attached to the CLONED
        /// Pokemon (the shared manager used to fire Regenerator/Moxie
        /// against the original battle's objects), its own rng at the same
        /// position, and a Silent log. Mutating the clone can never touch
        /// the visible battle.</summary>
        public BattleState Clone()
        {
            var clone = new BattleState
            {
                TurnNumber = TurnNumber,
                Environment = new BattleEnvironment
                {
                    Weather = Environment.Weather,
                    WeatherTurns = Environment.WeatherTurns,
                    Terrain = Environment.Terrain,
                    TerrainTurns = Environment.TerrainTurns
                },
                Player1 = Player1.Clone(),
                Player2 = Player2.Clone(),
                ToxicSpikesP1 = ToxicSpikesP1,
                ToxicSpikesP2 = ToxicSpikesP2,
                SpikesP1 = SpikesP1,
                SpikesP2 = SpikesP2,
                StealthRockP1 = StealthRockP1,
                StealthRockP2 = StealthRockP2,
                StickyWebP1 = StickyWebP1,
                StickyWebP2 = StickyWebP2,
                ReflectTurnsP1 = ReflectTurnsP1,
                ReflectTurnsP2 = ReflectTurnsP2,
                LightScreenTurnsP1 = LightScreenTurnsP1,
                LightScreenTurnsP2 = LightScreenTurnsP2,
                MistTurnsP1 = MistTurnsP1,
                MistTurnsP2 = MistTurnsP2,
                SafeguardTurnsP1 = SafeguardTurnsP1,
                SafeguardTurnsP2 = SafeguardTurnsP2,
                TailwindTurnsP1 = TailwindTurnsP1,
                TailwindTurnsP2 = TailwindTurnsP2,
                HealingWishP1 = HealingWishP1,
                HealingWishP2 = HealingWishP2,
                TrickRoomTurns = TrickRoomTurns,
                GravityTurns = GravityTurns,
                IgnoreDefenderAbilities = IgnoreDefenderAbilities,
                Rng = Rng.Clone(),
                MaxTurns = MaxTurns,
                Outcome = Outcome,
                Initialized = Initialized,
                Events = new EventManager(),
                Log = new BattleLog { Silent = true }
            };

            foreach (PokemonState pokemon in clone.Player1.Team.Concat(clone.Player2.Team))
                AbilityFactory.Restore(pokemon, clone);

            return clone;
        }
    }
}