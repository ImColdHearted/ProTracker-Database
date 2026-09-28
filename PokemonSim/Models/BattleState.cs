using System.Collections.Generic;
using System.Linq;
using PokemonSim.Engine;
using PokemonSim.Engine.Abilities;

namespace PokemonSim.Models
{
    public class BattleState
    {
        public int TurnNumber;

        /// <summary>
        /// §319. What each side has legitimately let the other SEE - the
        /// visibility boundary the observation encoder reads instead of
        /// reading this object.
        ///
        /// It lives on the battle rather than beside it because it has to
        /// survive Clone(): a Monte Carlo rollout that inherited an empty
        /// knowledge would evaluate positions from a different information
        /// state than the one being recorded, and the two would disagree
        /// about what is known without either being wrong.
        ///
        /// The engine fills it by DIFFING the field before and after each
        /// turn. Nothing writes to it from inside a move.
        /// </summary>
        public Observation.BattleKnowledge Knowledge = new();

        /// <summary>
        /// §305. How many Pokemon each side has on the field. Singles is the
        /// only format that exists today and every battle is created in it;
        /// the field is here so that the slot-shaped code written in this
        /// section has something to be shaped BY, rather than being written
        /// twice later.
        /// </summary>
        public BattleFormat Format = BattleFormat.Singles;

        /// <summary>The number of slots per side - the format's own value.</summary>
        public int SlotsPerSide => (int)Format;

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

        // ---- §304 ----

        /// <summary>Wish: a heal that waits in the party slot. The amount
        /// is half the WISHER's maximum HP, fixed when the wish is made,
        /// and it lands on whoever is standing in that slot at the end of
        /// the following turn - which may well be somebody else. Shaped
        /// like HealingWish, which is the same idea without the delay.</summary>
        public int WishTurnsP1;
        public int WishTurnsP2;
        public int WishHealP1;
        public int WishHealP2;

        /// <summary>Plasma Fists: for the rest of the turn, Normal moves
        /// are Electric. A field state, and the shortest one there is.</summary>
        public int IonDelugeTurns;

        /// <summary>§304. Which hit of a multi-hit move is being dealt,
        /// 1-based, and 0 outside the hit loop. Triple Kick and Triple Axel
        /// get stronger with each hit and this is the only thing that can
        /// tell them which one they are on.</summary>
        public int CurrentHitNumber;

        /// <summary>§304. A hit count decided by the move rather than by
        /// its MinHits/MaxHits - Beat Up, which hits once per healthy
        /// unstatused party member. 0 means "use the move's own range".
        /// Set in BeforeMove, read once by the resolver, cleared by it.</summary>
        public int HitCountOverride;

        /// <summary>§304. What each side chose this turn, before anything
        /// has happened. Two moves need to see the future rather than the
        /// past: Upper Hand fails unless its target is about to use a
        /// priority attack, and Pursuit doubles on a target that is about
        /// to switch. ActedThisTurn answers the opposite question and
        /// cannot answer these.
        ///
        /// Set by BattleEngine as the queue is built and cleared at end of
        /// turn, so a move reading it during the turn sees a decision that
        /// has genuinely been made. Null whenever nobody has declared
        /// anything - a replacement after a faint, or a caller driving
        /// MoveResolver directly - and every reader treats null as "no
        /// information", never as "no".</summary>
        public Actions.BattleAction? DeclaredP1;
        public Actions.BattleAction? DeclaredP2;

        public ref int WishTurns(PlayerState side) =>
            ref side == Player1 ? ref WishTurnsP1 : ref WishTurnsP2;

        public ref int WishHeal(PlayerState side) =>
            ref side == Player1 ? ref WishHealP1 : ref WishHealP2;

        /// <summary>The action the owner of this Pokemon declared this
        /// turn, or null if none was.</summary>
        public Actions.BattleAction? DeclaredFor(PokemonState pokemon) =>
            GetOwner(pokemon) == Player1 ? DeclaredP1 : DeclaredP2;

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

        /// <summary>§305. Everything standing on one side of the field. In
        /// singles this yields the single active and nothing else, which is
        /// what every caller written before this section assumed.</summary>
        public IEnumerable<PokemonState> ActivesOf(PlayerState side) => side.Standing();

        /// <summary>§305. The Pokemon in one slot of one side, or null if
        /// that slot is empty or does not exist in this format.</summary>
        public PokemonState? InSlot(PlayerState side, int slot) =>
            slot >= 0 && slot < side.Active.Count ? side.Active[slot] : null;

        /// <summary>The opposing ACTIVE Pokemon (singles). Section 154: this
        /// used to return the whole opposing team, which made Intimidate
        /// lower every bench Pokemon's Attack on entry.</summary>
        /// <summary>§305: every slot on the far side rather than the one
        /// that used to be there. In singles that is the same single
        /// Pokemon, which is why nothing that reads this changed.</summary>
        public IEnumerable<PokemonState> GetOpponents(PokemonState pokemon)
        {
            PlayerState other = Player1.Team.Contains(pokemon) ? Player2 : Player1;

            foreach (PokemonState opponent in other.Standing())
                yield return opponent;
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
                Format = Format,
                // §319: deep, like everything else here. A rollout that
                // shared its knowledge with the visible battle would write
                // imagined turns into the real history.
                Knowledge = Knowledge.Clone(),
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
                WishTurnsP1 = WishTurnsP1,
                WishTurnsP2 = WishTurnsP2,
                WishHealP1 = WishHealP1,
                WishHealP2 = WishHealP2,
                IonDelugeTurns = IonDelugeTurns,
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