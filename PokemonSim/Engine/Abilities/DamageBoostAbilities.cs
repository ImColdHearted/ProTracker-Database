using System.Collections.Generic;
using PokemonSim.Engine.Effects;
using PokemonSim.Models;

namespace PokemonSim.Engine.Abilities
{
    /// <summary>
    /// Section 158. The attacker-side damage boosters. Unlike the one-file-
    /// per-ability §154 layout, the §158 batch groups small related classes
    /// by mechanism - sixty-odd new abilities in sixty files would bury the
    /// folder. Each ability still follows the same IAbility contract, and
    /// its effect classes live beside it.
    /// </summary>
    public class SwarmAbility : IAbility
    {
        public string Id => "swarm";
        public IEnumerable<IMoveEffect> GetEffects() { yield return new SwarmEffect(); }
        public void OnAttach(PokemonState pokemon, BattleState state) { }
    }

    public class SwarmEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.BeforeDamage;

        // §197: reads and answers for the attacker.
        public override EffectSide Side => EffectSide.AttackerOnly;

        public override void Apply(BattleState state, PokemonState attacker, PokemonState defender,
            MoveState move, ref int damage, ref bool cancelled)
        {
            if (damage > 0 && move.Type == PokemonType.Bug &&
                attacker.CurrentHP <= attacker.MaxHP / 3)
            {
                damage = (int)(damage * 1.5);
            }
        }
    }

    public class SandForceAbility : IAbility
    {
        public string Id => "sandforce";
        public IEnumerable<IMoveEffect> GetEffects() { yield return new SandForceEffect(); }
        public void OnAttach(PokemonState pokemon, BattleState state) { }
    }

    public class SandForceEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.BeforeDamage;

        // §197: reads and answers for the attacker.
        public override EffectSide Side => EffectSide.AttackerOnly;

        public override void Apply(BattleState state, PokemonState attacker, PokemonState defender,
            MoveState move, ref int damage, ref bool cancelled)
        {
            if (damage > 0 && state.Environment.Weather == WeatherType.Sandstorm &&
                (move.Type == PokemonType.Rock || move.Type == PokemonType.Ground || move.Type == PokemonType.Steel))
            {
                damage = (int)(damage * 1.3);
            }
        }
    }

    public class IronFistAbility : IAbility
    {
        public string Id => "ironfist";
        public IEnumerable<IMoveEffect> GetEffects() { yield return new IronFistEffect(); }
        public void OnAttach(PokemonState pokemon, BattleState state) { }
    }

    public class IronFistEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.BeforeDamage;

        // §197: reads and answers for the attacker.
        public override EffectSide Side => EffectSide.AttackerOnly;

        public override void Apply(BattleState state, PokemonState attacker, PokemonState defender,
            MoveState move, ref int damage, ref bool cancelled)
        {
            if (damage > 0 && move.IsPunch)
                damage = (int)(damage * 1.2);
        }
    }

    public class ToughClawsAbility : IAbility
    {
        public string Id => "toughclaws";
        public IEnumerable<IMoveEffect> GetEffects() { yield return new ToughClawsEffect(); }
        public void OnAttach(PokemonState pokemon, BattleState state) { }
    }

    public class ToughClawsEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.BeforeDamage;

        // §197: reads and answers for the attacker.
        public override EffectSide Side => EffectSide.AttackerOnly;

        public override void Apply(BattleState state, PokemonState attacker, PokemonState defender,
            MoveState move, ref int damage, ref bool cancelled)
        {
            if (damage > 0 && move.IsContact)
                damage = (int)(damage * 1.3);
        }
    }

    public class MegaLauncherAbility : IAbility
    {
        public string Id => "megalauncher";
        public IEnumerable<IMoveEffect> GetEffects() { yield return new MegaLauncherEffect(); }
        public void OnAttach(PokemonState pokemon, BattleState state) { }
    }

    public class MegaLauncherEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.BeforeDamage;

        // §197: reads and answers for the attacker.
        public override EffectSide Side => EffectSide.AttackerOnly;

        public override void Apply(BattleState state, PokemonState attacker, PokemonState defender,
            MoveState move, ref int damage, ref bool cancelled)
        {
            if (damage > 0 && move.IsPulse)
                damage = (int)(damage * 1.5);
        }
    }

    public class RecklessAbility : IAbility
    {
        public string Id => "reckless";
        public IEnumerable<IMoveEffect> GetEffects() { yield return new RecklessEffect(); }
        public void OnAttach(PokemonState pokemon, BattleState state) { }
    }

    public class RecklessEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.BeforeDamage;

        // §197: reads and answers for the attacker.
        public override EffectSide Side => EffectSide.AttackerOnly;

        public override void Apply(BattleState state, PokemonState attacker, PokemonState defender,
            MoveState move, ref int damage, ref bool cancelled)
        {
            if (damage > 0 && move.Effects != null)
            {
                foreach (string name in move.Effects)
                {
                    if (name.StartsWith("Recoil"))
                    {
                        damage = (int)(damage * 1.2);
                        return;
                    }
                }
            }
        }
    }

    public class AnalyticAbility : IAbility
    {
        public string Id => "analytic";
        public IEnumerable<IMoveEffect> GetEffects() { yield return new AnalyticEffect(); }
        public void OnAttach(PokemonState pokemon, BattleState state) { }
    }

    public class AnalyticEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.BeforeDamage;

        // §197: reads and answers for the attacker.
        public override EffectSide Side => EffectSide.AttackerOnly;

        public override void Apply(BattleState state, PokemonState attacker, PokemonState defender,
            MoveState move, ref int damage, ref bool cancelled)
        {
            if (damage > 0 && defender.ActedThisTurn)
                damage = (int)(damage * 1.3);
        }
    }

    public class AdaptabilityAbility : IAbility
    {
        public string Id => "adaptability";
        public IEnumerable<IMoveEffect> GetEffects() { yield return new AdaptabilityEffect(); }
        public void OnAttach(PokemonState pokemon, BattleState state) { }
    }

    public class AdaptabilityEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.BeforeDamage;

        // §197: reads and answers for the attacker.
        public override EffectSide Side => EffectSide.AttackerOnly;

        public override void Apply(BattleState state, PokemonState attacker, PokemonState defender,
            MoveState move, ref int damage, ref bool cancelled)
        {
            // STAB 1.5 becomes 2.0 - a four-thirds bump on the total.
            if (damage > 0 && !move.Typeless && attacker.Types.Contains(move.Type))
                damage = damage * 4 / 3;
        }
    }

    public class FairyAuraAbility : IAbility
    {
        public string Id => "fairyaura";
        public IEnumerable<IMoveEffect> GetEffects() { yield return new FairyAuraEffect(); }
        public void OnAttach(PokemonState pokemon, BattleState state) { }
    }

    public class FairyAuraEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.BeforeDamage;

        public override void Apply(BattleState state, PokemonState attacker, PokemonState defender,
            MoveState move, ref int damage, ref bool cancelled)
        {
            // Faithful auras radiate to both sides; the Simulator applies
            // the boost to the aura-bearer's own Fairy moves.
            //
            // §197: deliberately left as EffectSide.Either while every other
            // booster in this file became AttackerOnly. The real ability does
            // boost both sides' Fairy moves, so running it from the defender's
            // list is arguably right; narrowing it would be a balance change
            // rather than the side-leak fix this section is.
            if (damage > 0 && move.Type == PokemonType.Fairy)
                damage = damage * 4 / 3;
        }
    }

    /// <summary>Aerilate / Pixilate: the user's Normal moves convert to the
    /// ability's type (permanently, per that Pokemon's own move instance -
    /// the instance always battles with the ability) and are tagged with
    /// the AteConverted marker for the 1.2x boost.</summary>
    public class AteAbility : IAbility
    {
        readonly string id;
        readonly PokemonType type;

        public AteAbility(string id, PokemonType type)
        {
            this.id = id;
            this.type = type;
        }

        public string Id => id;

        public IEnumerable<IMoveEffect> GetEffects()
        {
            yield return new AteConvertEffect(type);
            yield return new AteBoostEffect();
        }

        public void OnAttach(PokemonState pokemon, BattleState state) { }
    }

    public class AteConvertEffect : BaseMoveEffect
    {
        readonly PokemonType type;

        public AteConvertEffect(PokemonType type)
        {
            this.type = type;
        }

        public override MovePhase Phase => MovePhase.BeforeMove;

        // §197: reads and answers for the attacker.
        public override EffectSide Side => EffectSide.AttackerOnly;

        public override void Apply(BattleState state, PokemonState attacker, PokemonState defender,
            MoveState move, ref int damage, ref bool cancelled)
        {
            if (move.Type == PokemonType.Normal && !move.Typeless &&
                move.Category != MoveCategory.Status)
            {
                move.Type = type;
                move.Effects ??= new List<string>();

                if (!move.Effects.Contains("AteConverted"))
                    move.Effects.Add("AteConverted");
            }
        }
    }

    public class AteBoostEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.BeforeDamage;

        // §197: reads and answers for the attacker.
        public override EffectSide Side => EffectSide.AttackerOnly;

        public override void Apply(BattleState state, PokemonState attacker, PokemonState defender,
            MoveState move, ref int damage, ref bool cancelled)
        {
            if (damage > 0 && move.Effects != null && move.Effects.Contains("AteConverted"))
                damage = (int)(damage * 1.2);
        }
    }

    /// <summary>Libero / Protean: the user becomes the type of the move it
    /// is about to use.</summary>
    public class LiberoAbility : IAbility
    {
        readonly string id;

        public LiberoAbility(string id)
        {
            this.id = id;
        }

        public string Id => id;

        public IEnumerable<IMoveEffect> GetEffects()
        {
            yield return new LiberoEffect();
        }

        public void OnAttach(PokemonState pokemon, BattleState state) { }
    }

    public class LiberoEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.BeforeMove;

        // §197: reads and answers for the attacker.
        public override EffectSide Side => EffectSide.AttackerOnly;

        public override void Apply(BattleState state, PokemonState attacker, PokemonState defender,
            MoveState move, ref int damage, ref bool cancelled)
        {
            if (move.Typeless)
                return;

            if (attacker.Types.Count != 1 || attacker.Types[0] != move.Type)
            {
                attacker.Types = new List<PokemonType> { move.Type };
                state.Log.Write($"{attacker.Species} became the {move.Type} type!");
            }
        }
    }

    /// <summary>Sheer Force: the damage side reuses the effect that has
    /// waited in IMoveEffect.cs since the original engine; the secondary
    /// suppression lives in MoveResolver.ApplySecondaryEffects.</summary>
    public class SheerForceAbility : IAbility
    {
        public string Id => "sheerforce";
        public IEnumerable<IMoveEffect> GetEffects() { yield return new SheerForceEffect(); }
        public void OnAttach(PokemonState pokemon, BattleState state) { }
    }

    /// <summary>Turboblaze / Teravolt: Mold Breaker under another name.</summary>
    public class MoldBreakerCloneAbility : IAbility
    {
        readonly string id;

        public MoldBreakerCloneAbility(string id)
        {
            this.id = id;
        }

        public string Id => id;
        public IEnumerable<IMoveEffect> GetEffects() { yield return new MoldBreakerEffect(); }
        public void OnAttach(PokemonState pokemon, BattleState state) { }
    }
}