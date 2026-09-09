using System;
using System.Collections.Generic;
using PokemonSim.Engine.Effects;
using PokemonSim.Models;

namespace PokemonSim.Engine.Abilities
{
    /// <summary>
    /// Section 158. Defender-side abilities: intake absorbers (the
    /// VoltAbsorb pattern), damage reducers, status-move shields, and the
    /// contact punishers. All ride the existing phase hooks - a defender's
    /// passives run at each phase unless Mold Breaker suppressed them.
    /// </summary>
    public class MultiscaleAbility : IAbility
    {
        public string Id => "multiscale";
        public IEnumerable<IMoveEffect> GetEffects() { yield return new MultiscaleEffect(); }
        public void OnAttach(PokemonState pokemon, BattleState state) { }
    }

    public class MultiscaleEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.BeforeDamage;

        // §197: the defender's reaction to being hit.
        public override EffectSide Side => EffectSide.DefenderOnly;

        public override void Apply(BattleState state, PokemonState attacker, PokemonState defender,
            MoveState move, ref int damage, ref bool cancelled)
        {
            if (damage > 0 && defender.CurrentHP == defender.MaxHP)
                damage /= 2;
        }
    }

    /// <summary>Purifying Salt: status immunity lives in TryInflictStatus;
    /// this effect is the halved Ghost damage.</summary>
    public class PurifyingSaltAbility : IAbility
    {
        public string Id => "purifyingsalt";
        public IEnumerable<IMoveEffect> GetEffects() { yield return new PurifyingSaltEffect(); }
        public void OnAttach(PokemonState pokemon, BattleState state) { }
    }

    public class PurifyingSaltEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.BeforeDamage;

        // §197: the defender's reaction to being hit.
        public override EffectSide Side => EffectSide.DefenderOnly;

        public override void Apply(BattleState state, PokemonState attacker, PokemonState defender,
            MoveState move, ref int damage, ref bool cancelled)
        {
            if (damage > 0 && move.Type == PokemonType.Ghost)
                damage /= 2;
        }
    }

    /// <summary>The Water Absorb pattern, parameterized: absorb a type,
    /// answer with a heal or a stat boost.</summary>
    public class AbsorbAbility : IAbility
    {
        readonly string id;
        readonly PokemonType type;
        readonly bool heals;
        readonly string boostStat;

        public AbsorbAbility(string id, PokemonType type, bool heals, string boostStat = "")
        {
            this.id = id;
            this.type = type;
            this.heals = heals;
            this.boostStat = boostStat;
        }

        public string Id => id;

        public IEnumerable<IMoveEffect> GetEffects()
        {
            yield return new AbsorbEffect(type, heals, boostStat);
        }

        public void OnAttach(PokemonState pokemon, BattleState state) { }
    }

    public class AbsorbEffect : BaseMoveEffect
    {
        readonly PokemonType type;
        readonly bool heals;
        readonly string boostStat;

        public AbsorbEffect(PokemonType type, bool heals, string boostStat)
        {
            this.type = type;
            this.heals = heals;
            this.boostStat = boostStat;
        }

        public override MovePhase Phase => MovePhase.BeforeMove;

        // §197: the defender's reaction to being hit.
        public override EffectSide Side => EffectSide.DefenderOnly;

        public override void Apply(BattleState state, PokemonState attacker, PokemonState defender,
            MoveState move, ref int damage, ref bool cancelled)
        {
            if (move.Type != type || move.Typeless)
                return;

            if (heals)
            {
                int heal = defender.MaxHP / 4;
                defender.CurrentHP = Math.Min(defender.MaxHP, defender.CurrentHP + heal);
                state.Log.Write($"{defender.Species} absorbed the attack!");
            }
            else
            {
                state.Log.Write($"{defender.Species} absorbed the attack!");
                MoveResolver.ApplyStatChange(state, defender, boostStat, 1);
            }

            cancelled = true;
        }
    }

    /// <summary>Dry Skin: absorbs Water for a quarter, takes a quarter
    /// more from Fire; the weather half lives in AbilityEndOfTurn.</summary>
    public class DrySkinAbility : IAbility
    {
        public string Id => "dryskin";

        public IEnumerable<IMoveEffect> GetEffects()
        {
            yield return new AbsorbEffect(PokemonType.Water, heals: true, boostStat: "");
            yield return new DrySkinFireEffect();
        }

        public void OnAttach(PokemonState pokemon, BattleState state) { }
    }

    public class DrySkinFireEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.BeforeDamage;

        // §197: the defender's reaction to being hit.
        public override EffectSide Side => EffectSide.DefenderOnly;

        public override void Apply(BattleState state, PokemonState attacker, PokemonState defender,
            MoveState move, ref int damage, ref bool cancelled)
        {
            if (damage > 0 && move.Type == PokemonType.Fire)
                damage = (int)(damage * 1.25);
        }
    }

    /// <summary>Good as Gold: immune to the opponent's status moves.</summary>
    public class GoodAsGoldAbility : IAbility
    {
        public string Id => "goodasgold";
        public IEnumerable<IMoveEffect> GetEffects() { yield return new GoodAsGoldEffect(); }
        public void OnAttach(PokemonState pokemon, BattleState state) { }
    }

    public class GoodAsGoldEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.BeforeMove;

        // §197: the defender's reaction to being hit.
        public override EffectSide Side => EffectSide.DefenderOnly;

        public override void Apply(BattleState state, PokemonState attacker, PokemonState defender,
            MoveState move, ref int damage, ref bool cancelled)
        {
            if (move.Category == MoveCategory.Status && MoveResolver.TargetsOpponent(move))
            {
                state.Log.Write($"{defender.Species}'s golden body deflects the move!");
                cancelled = true;
            }
        }
    }

    /// <summary>Magic Bounce: the classic status payloads (a condition, a
    /// stat drop) come straight back at the user. Effect-carried moves
    /// (Leech Seed and friends) are deflected without a mirror - the §158
    /// guide notes the simplification.</summary>
    public class MagicBounceAbility : IAbility
    {
        public string Id => "magicbounce";
        public IEnumerable<IMoveEffect> GetEffects() { yield return new MagicBounceEffect(); }
        public void OnAttach(PokemonState pokemon, BattleState state) { }
    }

    public class MagicBounceEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.BeforeMove;

        // §197: the defender's reaction to being hit.
        public override EffectSide Side => EffectSide.DefenderOnly;

        public override void Apply(BattleState state, PokemonState attacker, PokemonState defender,
            MoveState move, ref int damage, ref bool cancelled)
        {
            if (move.Category != MoveCategory.Status || !MoveResolver.TargetsOpponent(move))
                return;

            cancelled = true;
            state.Log.Write($"{defender.Species} bounced the move back!");

            StatusCondition status = move.InflictStatus;

            if (move.RandomStatus != null && move.RandomStatus.Count > 0)
                status = move.RandomStatus[state.Rng.Next(move.RandomStatus.Count)];

            if (status != StatusCondition.None)
            {
                MoveResolver.TryInflictStatus(state, attacker, status,
                    announceFailure: true, substituteBlocks: false, source: defender);
            }

            if (move.StatChanges != null)
            {
                foreach (var change in move.StatChanges)
                {
                    if (change.Target != "self")
                        MoveResolver.ApplyStatChangeAgainst(state, defender, attacker, change.Stat, change.Stages);
                }
            }
        }
    }

    /// <summary>The contact punisher pattern: a chance of a status for
    /// whoever made contact.</summary>
    public class ContactStatusAbility : IAbility
    {
        readonly string id;
        readonly StatusCondition status;

        public ContactStatusAbility(string id, StatusCondition status)
        {
            this.id = id;
            this.status = status;
        }

        public string Id => id;

        public IEnumerable<IMoveEffect> GetEffects()
        {
            yield return new ContactStatusEffect(status);
        }

        public void OnAttach(PokemonState pokemon, BattleState state) { }
    }

    public class ContactStatusEffect : BaseMoveEffect
    {
        readonly StatusCondition status;

        public ContactStatusEffect(StatusCondition status)
        {
            this.status = status;
        }

        public override MovePhase Phase => MovePhase.AfterDamage;

        // §197: the defender's reaction to being hit.
        public override EffectSide Side => EffectSide.DefenderOnly;

        public override void Apply(BattleState state, PokemonState attacker, PokemonState defender,
            MoveState move, ref int damage, ref bool cancelled)
        {
            if (damage > 0 && move.IsContact && !attacker.Fainted &&
                state.Rng.Chance(0.3))
            {
                MoveResolver.TryInflictStatus(state, attacker, status,
                    announceFailure: false, substituteBlocks: false, source: defender);
            }
        }
    }

    /// <summary>Effect Spore: an even three-way roll behind the 30%.</summary>
    public class EffectSporeAbility : IAbility
    {
        public string Id => "effectspore";
        public IEnumerable<IMoveEffect> GetEffects() { yield return new EffectSporeEffect(); }
        public void OnAttach(PokemonState pokemon, BattleState state) { }
    }

    public class EffectSporeEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.AfterDamage;

        // §197: the defender's reaction to being hit.
        public override EffectSide Side => EffectSide.DefenderOnly;

        public override void Apply(BattleState state, PokemonState attacker, PokemonState defender,
            MoveState move, ref int damage, ref bool cancelled)
        {
            if (damage > 0 && move.IsContact && !attacker.Fainted && state.Rng.Chance(0.3))
            {
                StatusCondition rolled = state.Rng.Next(3) switch
                {
                    0 => StatusCondition.Poison,
                    1 => StatusCondition.Paralysis,
                    _ => StatusCondition.Sleep
                };

                MoveResolver.TryInflictStatus(state, attacker, rolled,
                    announceFailure: false, substituteBlocks: false, source: defender);
            }
        }
    }

    /// <summary>Cursed Body: 30% to disable the move that just hit.</summary>
    public class CursedBodyAbility : IAbility
    {
        public string Id => "cursedbody";
        public IEnumerable<IMoveEffect> GetEffects() { yield return new CursedBodyEffect(); }
        public void OnAttach(PokemonState pokemon, BattleState state) { }
    }

    public class CursedBodyEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.AfterDamage;

        // §197: the defender's reaction to being hit.
        public override EffectSide Side => EffectSide.DefenderOnly;

        public override void Apply(BattleState state, PokemonState attacker, PokemonState defender,
            MoveState move, ref int damage, ref bool cancelled)
        {
            if (damage > 0 && !attacker.Fainted && attacker.DisabledTurns == 0 &&
                move.MaxPP > 0 && state.Rng.Chance(0.3))
            {
                attacker.DisabledMoveName = move.Name;
                attacker.DisabledTurns = 4;
                state.Log.Write($"{attacker.Species}'s {move.Name} was disabled by Cursed Body!");
            }
        }
    }

    /// <summary>Iron Barbs / Rough Skin: an eighth back for contact.</summary>
    public class BarbedBodyAbility : IAbility
    {
        readonly string id;

        public BarbedBodyAbility(string id)
        {
            this.id = id;
        }

        public string Id => id;
        public IEnumerable<IMoveEffect> GetEffects() { yield return new BarbedBodyEffect(); }
        public void OnAttach(PokemonState pokemon, BattleState state) { }
    }

    public class BarbedBodyEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.AfterDamage;

        // §197: the defender's reaction to being hit.
        public override EffectSide Side => EffectSide.DefenderOnly;

        public override void Apply(BattleState state, PokemonState attacker, PokemonState defender,
            MoveState move, ref int damage, ref bool cancelled)
        {
            if (damage > 0 && move.IsContact && !attacker.Fainted && !attacker.HasMagicGuard)
            {
                attacker.CurrentHP = Math.Max(0, attacker.CurrentHP - Math.Max(1, attacker.MaxHP / 8));
                state.Log.Write($"{attacker.Species} was hurt by {defender.Species}'s spiky body!");

                if (attacker.Fainted)
                    state.Log.Write($"{attacker.Species} fainted!");
            }
        }
    }

    /// <summary>Aftermath: a quarter of max HP for whoever lands the
    /// fatal contact hit.</summary>
    public class AftermathAbility : IAbility
    {
        public string Id => "aftermath";
        public IEnumerable<IMoveEffect> GetEffects() { yield return new AftermathEffect(); }
        public void OnAttach(PokemonState pokemon, BattleState state) { }
    }

    public class AftermathEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.AfterDamage;

        // §197: the defender's reaction to being hit.
        public override EffectSide Side => EffectSide.DefenderOnly;

        public override void Apply(BattleState state, PokemonState attacker, PokemonState defender,
            MoveState move, ref int damage, ref bool cancelled)
        {
            if (damage > 0 && move.IsContact && defender.Fainted &&
                !attacker.Fainted && !attacker.HasMagicGuard)
            {
                attacker.CurrentHP = Math.Max(0, attacker.CurrentHP - Math.Max(1, attacker.MaxHP / 4));
                state.Log.Write($"{attacker.Species} is hurt by {defender.Species}'s Aftermath!");

                if (attacker.Fainted)
                    state.Log.Write($"{attacker.Species} fainted!");
            }
        }
    }

    /// <summary>Mummy: contact rewrites the attacker's ability to Mummy.</summary>
    public class MummyAbility : IAbility
    {
        public string Id => "mummy";
        public IEnumerable<IMoveEffect> GetEffects() { yield return new MummyEffect(); }
        public void OnAttach(PokemonState pokemon, BattleState state) { }
    }

    public class MummyEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.AfterDamage;

        // §197: the defender's reaction to being hit.
        public override EffectSide Side => EffectSide.DefenderOnly;

        public override void Apply(BattleState state, PokemonState attacker, PokemonState defender,
            MoveState move, ref int damage, ref bool cancelled)
        {
            if (damage > 0 && move.IsContact && !attacker.Fainted &&
                AbilityFactory.Normalize(attacker.AbilityId) != "mummy")
            {
                attacker.AbilityId = "mummy";
                attacker.HasMagicGuard = false;
                AbilityFactory.Restore(attacker, state);
                state.Log.Write($"{attacker.Species}'s ability became Mummy!");
            }
        }
    }

    /// <summary>Weak Armor: a physical hit trades Defense for Speed.</summary>
    public class WeakArmorAbility : IAbility
    {
        public string Id => "weakarmor";
        public IEnumerable<IMoveEffect> GetEffects() { yield return new WeakArmorEffect(); }
        public void OnAttach(PokemonState pokemon, BattleState state) { }
    }

    public class WeakArmorEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.AfterDamage;

        // §197: the defender's reaction to being hit.
        public override EffectSide Side => EffectSide.DefenderOnly;

        public override void Apply(BattleState state, PokemonState attacker, PokemonState defender,
            MoveState move, ref int damage, ref bool cancelled)
        {
            if (damage > 0 && move.Category == MoveCategory.Physical && !defender.Fainted)
            {
                state.Log.Write($"{defender.Species}'s Weak Armor was triggered!");
                MoveResolver.ApplyStatChange(state, defender, "Defense", -1);
                MoveResolver.ApplyStatChange(state, defender, "Speed", 2);
            }
        }
    }

    /// <summary>Justified: a Dark hit answers with +1 Attack.</summary>
    public class JustifiedAbility : IAbility
    {
        public string Id => "justified";
        public IEnumerable<IMoveEffect> GetEffects() { yield return new JustifiedEffect(); }
        public void OnAttach(PokemonState pokemon, BattleState state) { }
    }

    public class JustifiedEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.AfterDamage;

        // §197: the defender's reaction to being hit.
        public override EffectSide Side => EffectSide.DefenderOnly;

        public override void Apply(BattleState state, PokemonState attacker, PokemonState defender,
            MoveState move, ref int damage, ref bool cancelled)
        {
            if (damage > 0 && move.Type == PokemonType.Dark && !defender.Fainted)
                MoveResolver.ApplyStatChange(state, defender, "Attack", 1);
        }
    }

    /// <summary>Toxic Debris: a physical hit scatters Toxic Spikes on the
    /// attacker's side (two layers at most).</summary>
    public class ToxicDebrisAbility : IAbility
    {
        public string Id => "toxicdebris";
        public IEnumerable<IMoveEffect> GetEffects() { yield return new ToxicDebrisEffect(); }
        public void OnAttach(PokemonState pokemon, BattleState state) { }
    }

    public class ToxicDebrisEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.AfterDamage;

        // §197: the defender's reaction to being hit.
        public override EffectSide Side => EffectSide.DefenderOnly;

        public override void Apply(BattleState state, PokemonState attacker, PokemonState defender,
            MoveState move, ref int damage, ref bool cancelled)
        {
            if (damage <= 0 || move.Category != MoveCategory.Physical)
                return;

            var attackerSide = state.GetOwner(attacker);

            if (attackerSide == state.Player1)
            {
                if (state.ToxicSpikesP1 >= 2) return;
                state.ToxicSpikesP1++;
            }
            else
            {
                if (state.ToxicSpikesP2 >= 2) return;
                state.ToxicSpikesP2++;
            }

            state.Log.Write($"Toxic Spikes scattered around {attackerSide.Name}'s team!");
        }
    }

    /// <summary>Poison Touch: the OWNER's contact moves poison 30% of the
    /// time (an attacker-side passive, unlike its cousins above).</summary>
    public class PoisonTouchAbility : IAbility
    {
        public string Id => "poisontouch";
        public IEnumerable<IMoveEffect> GetEffects() { yield return new PoisonTouchEffect(); }
        public void OnAttach(PokemonState pokemon, BattleState state) { }
    }

    public class PoisonTouchEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.AfterDamage;

        // §197: reads and answers for the attacker.
        public override EffectSide Side => EffectSide.AttackerOnly;

        public override void Apply(BattleState state, PokemonState attacker, PokemonState defender,
            MoveState move, ref int damage, ref bool cancelled)
        {
            // Runs from the ATTACKER's passive list: attacker here is the
            // ability's owner.
            if (damage > 0 && move.IsContact && !defender.Fainted && state.Rng.Chance(0.3))
            {
                MoveResolver.TryInflictStatus(state, defender, StatusCondition.Poison,
                    announceFailure: false, substituteBlocks: defender.SubstituteHP > 0, source: attacker);
            }
        }
    }

    /// <summary>
    /// §204. Disguise: the first damaging move to connect with Mimikyu does
    /// nothing at all except break the disguise, which costs it an eighth of
    /// its maximum HP. Every hit after that lands normally, for the rest of
    /// the battle - the disguise does not come back when it switches out.
    ///
    /// It was reported as not working, and it was not: nothing implemented
    /// it. "Disguise" appeared in exactly one place in this codebase, a list
    /// of form-changing ability NAMES in the card importer, and
    /// AbilityFactory had never heard of it - so a Mimikyu built with it got
    /// no ability object at all and battled with nothing.
    ///
    /// BeforeDamage, not BeforeMove, and the difference matters. BeforeMove
    /// runs before the accuracy roll (see MoveResolver), so a disguise put
    /// there would break on a move that then missed. BeforeDamage runs
    /// inside the hit loop, once the move has actually connected, and
    /// cancelling from there breaks that loop before any damage is applied -
    /// which is also what stops the move's secondary effects, exactly as a
    /// blocked hit should.
    /// </summary>
    public class DisguiseEffect : BaseMoveEffect
    {
        /// <summary>The key the busted disguise is remembered under - on the
        /// Pokemon, so it survives switching out the way the real one
        /// does, and so a second Mimikyu has its own.</summary>
        public const string BustedKey = "disguise.busted";

        public override MovePhase Phase => MovePhase.BeforeDamage;

        // §197: the defender's reaction to being hit.
        public override EffectSide Side => EffectSide.DefenderOnly;

        public static bool IsBusted(PokemonState pokemon) =>
            pokemon.AbilityState.TryGetValue(BustedKey, out var value) &&
            value is bool busted && busted;

        public override void Apply(BattleState state, PokemonState attacker, PokemonState defender,
            MoveState move, ref int damage, ref bool cancelled)
        {
            // Status moves go straight through a disguise, and a hit that
            // was going to do nothing anyway does not spend it.
            if (move.Category == MoveCategory.Status || damage <= 0 || IsBusted(defender))
                return;

            defender.AbilityState[BustedKey] = true;

            state.Log.Write($"{defender.Species}'s disguise served as a decoy!");

            // Generation 8 onward, breaking it costs an eighth of the
            // maximum - and Magic Guard stops that the way it stops every
            // other point of damage that is not a move landing.
            if (!defender.HasMagicGuard)
            {
                defender.CurrentHP = Math.Max(0, defender.CurrentHP - Math.Max(1, defender.MaxHP / 8));

                state.Log.Write($"{defender.Species}'s disguise was busted!");

                if (defender.Fainted)
                    state.Log.Write($"{defender.Species} fainted!");
            }
            else
            {
                state.Log.Write($"{defender.Species}'s disguise was busted!");
            }

            damage = 0;
            cancelled = true;
        }
    }

    public class DisguiseAbility : IAbility
    {
        public string Id => "disguise";
        public IEnumerable<IMoveEffect> GetEffects() { yield return new DisguiseEffect(); }
        public void OnAttach(PokemonState pokemon, BattleState state) { }
    }
}