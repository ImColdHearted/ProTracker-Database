using System;
using PokemonSim.Models;

namespace PokemonSim.Engine
{
    /// <summary>
    /// A switch, voluntary or forced by a faint. Section 154: the outgoing
    /// Pokemon's volatile state now clears (stat stages, Protect streak,
    /// substitute, charge lock, flinch, trap, the toxic clock, Flash Fire's
    /// stored boost - the things the games reset on leaving the field); the
    /// incoming side keeps its hazards, which now clamp at zero HP and
    /// announce a faint; grounded-ness uses the shared Grounding rule
    /// (Levitate finally matters to Spikes and Toxic Spikes); a grounded
    /// Poison-type absorbs Toxic Spikes; and the incoming Pokemon's
    /// switch-in ability runs (Intimidate, Drizzle and friends used to
    /// trigger only if someone called OnAttach by hand, which nothing did).
    /// </summary>
    public static class SwitchResolver
    {
        public static void Resolve(
            BattleState state,
            PlayerState player,
            PokemonState newPokemon,
            bool voluntary = true)
        {
            var old = player.ActivePokemon;

            if (old == newPokemon)
                return;

            if (newPokemon.Fainted)
                return;

            if (voluntary)
            {
                state.Events.Dispatch(
                    new BattleEvent
                    {
                        Type = BattleEventType.SwitchOut,
                        Source = old
                    },
                    state
                );

                state.Log.Write($"{player.Name} withdrew {old.Species}!");
            }

            // Section 158: Natural Cure heals status on the way out.
            if (!old.Fainted && old.Status != StatusCondition.None &&
                Abilities.AbilityFactory.Normalize(old.AbilityId) == "naturalcure")
            {
                old.Status = StatusCondition.None;
                old.SleepTurns = 0;
                old.ToxicCounter = 0;
                state.Log.Write($"{old.Species}'s status was cured by Natural Cure!");
            }

            ResetVolatileState(old);

            player.ActivePokemon = newPokemon;
            newPokemon.EnteredFieldTurn = state.TurnNumber;

            // §197: a fresh Pokemon has not had its go yet, whether it walked
            // in or was sent in over a faint.
            newPokemon.HasActedSinceEnteringField = false;

            // Section 158: Healing Wish blesses the replacement.
            if (state.HealingWish(player))
            {
                state.HealingWish(player) = false;

                if (newPokemon.CurrentHP < newPokemon.MaxHP ||
                    newPokemon.Status != StatusCondition.None)
                {
                    newPokemon.CurrentHP = newPokemon.MaxHP;
                    newPokemon.Status = StatusCondition.None;
                    newPokemon.SleepTurns = 0;
                    newPokemon.ToxicCounter = 0;
                    state.Log.Write($"The healing wish came true for {newPokemon.Species}!");
                }
            }

            state.Log.Write($"{player.Name} sent out {newPokemon.Species}!");

            state.Events.Dispatch(
                new BattleEvent
                {
                    Type = BattleEventType.SwitchIn,
                    Source = newPokemon,
                },
                state
            );

            ApplyToxicSpikes(state, newPokemon, player);
            ApplySpikes(state, newPokemon, player);
            ApplyStealthRock(state, newPokemon, player);
            ApplyStickyWeb(state, newPokemon, player);

            if (newPokemon.Fainted)
            {
                state.Log.Write($"{newPokemon.Species} fainted!");

                state.Events.Dispatch(
                    new BattleEvent
                    {
                        Type = BattleEventType.PokemonFainted,
                        Target = newPokemon
                    },
                    state
                );

                return;
            }

            newPokemon.Ability?.OnAttach(newPokemon, state);
        }

        static void ResetVolatileState(PokemonState pokemon)
        {
            pokemon.AttackStage = 0;
            pokemon.DefenseStage = 0;
            pokemon.SpAttackStage = 0;
            pokemon.SpDefenseStage = 0;
            pokemon.SpeedStage = 0;
            pokemon.AccuracyStage = 0;
            pokemon.EvasionStage = 0;

            pokemon.Protected = false;
            pokemon.ProtectedThisTurn = false;
            pokemon.ConsecutiveProtects = 0;
            pokemon.Charging = false;
            pokemon.ChargingMove = null;
            pokemon.SubstituteHP = 0;
            pokemon.Flinched = false;
            pokemon.Trap = null;
            pokemon.ToxicCounter = 0;

            // Section 158: the volatile batch leaves with its owner.
            pokemon.LeechSeeded = false;
            pokemon.AquaRing = false;
            pokemon.ConfusionTurns = 0;
            pokemon.PerishCount = -1;
            pokemon.YawnTurns = 0;
            pokemon.EncoreMoveName = null;
            pokemon.EncoreTurns = 0;
            pokemon.TauntTurns = 0;
            pokemon.DisabledMoveName = null;
            pokemon.DisabledTurns = 0;
            pokemon.Tormented = false;
            pokemon.Rooted = false;
            pokemon.MagnetRiseTurns = 0;
            pokemon.StockpileCount = 0;
            pokemon.DestinyBondActive = false;
            pokemon.Cursed = false;
            pokemon.SaltCured = false;
            pokemon.KingsShieldUp = false;
            pokemon.QuickGuardUp = false;
            pokemon.WideGuardUp = false;
            pokemon.Enduring = false;
            pokemon.LockOnActive = false;
            pokemon.ImprisonActive = false;
            pokemon.LastMoveName = null;
            pokemon.LastPhysicalDamageTaken = 0;
            pokemon.LastSpecialDamageTaken = 0;
            pokemon.ActedThisTurn = false;
            pokemon.Loafing = false;

            // Section 159: the Choice lock releases on leaving the field;
            // the held item itself (and its loss) persists.
            pokemon.ChoiceLockedMoveName = null;

            if (pokemon.AbilityState.ContainsKey("flashfire"))
                pokemon.AbilityState["flashfire"] = false;
        }

        static void ApplyToxicSpikes(
            BattleState state,
            PokemonState pokemon,
            PlayerState player)
        {
            int layers = player == state.Player1
                ? state.ToxicSpikesP1
                : state.ToxicSpikesP2;

            if (layers == 0) return;

            if (!Grounding.IsGrounded(state, pokemon))
                return;

            // A grounded Poison-type soaks the spikes up on entry.
            if (pokemon.Types.Contains(PokemonType.Poison))
            {
                if (player == state.Player1)
                    state.ToxicSpikesP1 = 0;
                else
                    state.ToxicSpikesP2 = 0;

                state.Log.Write($"{pokemon.Species} absorbed the Toxic Spikes!");
                return;
            }

            // Magic Guard blocks damage, not status - no check here.

            if (pokemon.Status != StatusCondition.None)
                return;

            MoveResolver.TryInflictStatus(
                state,
                pokemon,
                layers == 1 ? StatusCondition.Poison : StatusCondition.Toxic,
                announceFailure: false,
                substituteBlocks: false);
        }

        static void ApplySpikes(
            BattleState state,
            PokemonState pokemon,
            PlayerState player)
        {
            int layers = player == state.Player1
                ? state.SpikesP1
                : state.SpikesP2;

            if (layers == 0) return;

            if (pokemon.HasMagicGuard) return;

            if (!Grounding.IsGrounded(state, pokemon))
                return;

            int damageFraction = layers switch
            {
                1 => 8,
                2 => 6,
                3 => 4,
                _ => 8
            };

            int damage = pokemon.MaxHP / damageFraction;

            pokemon.CurrentHP = Math.Max(0, pokemon.CurrentHP - damage);

            state.Log.Write($"{pokemon.Species} was hurt by Spikes!");
        }

        /// <summary>Section 184. Sticky Web costs a stage of Speed rather
        /// than HP, so Magic Guard does not save you from it - but being
        /// airborne does, exactly as with Spikes.</summary>
        static void ApplyStickyWeb(
            BattleState state,
            PokemonState pokemon,
            PlayerState player)
        {
            bool web = player == state.Player1 ? state.StickyWebP1 : state.StickyWebP2;

            if (!web)
                return;

            if (!Grounding.IsGrounded(state, pokemon))
                return;

            if (pokemon.SpeedStage <= -6)
                return;

            pokemon.SpeedStage = Math.Max(-6, pokemon.SpeedStage - 1);

            state.Log.Write($"{pokemon.Species} was caught in a sticky web! Its Speed fell!");
        }

        static void ApplyStealthRock(
            BattleState state,
            PokemonState pokemon,
            PlayerState player)
        {
            bool active = player == state.Player1
                ? state.StealthRockP1
                : state.StealthRockP2;

            if (!active) return;

            if (pokemon.HasMagicGuard) return;

            double multiplier = 1.0;

            foreach (var type in pokemon.Types)
            {
                multiplier *= TypeChart.GetMultiplier(PokemonType.Rock, type);
            }

            int damage = (int)(pokemon.MaxHP * 0.125 * multiplier);

            pokemon.CurrentHP = Math.Max(0, pokemon.CurrentHP - damage);

            state.Log.Write($"{pokemon.Species} was hurt by Stealth Rock!");
        }
    }
}