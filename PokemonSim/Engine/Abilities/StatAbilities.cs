using System.Collections.Generic;
using PokemonSim.Engine.Effects;
using PokemonSim.Models;

namespace PokemonSim.Engine.Abilities
{
    /// <summary>
    /// Section 158. The CalculateStat crowd - stat multipliers in the
    /// Chlorophyll/Huge Power tradition, grouped by file rather than one
    /// per class (see DamageBoostAbilities for the §158 layout note).
    /// </summary>
    public class PurePowerAbility : IAbility
    {
        public string Id => "purepower";

        // Identical arithmetic to Huge Power - the effect is reused.
        public IEnumerable<IMoveEffect> GetEffects() { yield return new HugePowerEffect(); }
        public void OnAttach(PokemonState pokemon, BattleState state) { }
    }

    /// <summary>The weather speedsters: Sand Rush and Slush Rush (their
    /// chip immunity lives in WeatherEffects).</summary>
    public class WeatherSpeedAbility : IAbility
    {
        readonly string id;
        readonly WeatherType weather;

        public WeatherSpeedAbility(string id, WeatherType weather)
        {
            this.id = id;
            this.weather = weather;
        }

        public string Id => id;

        public IEnumerable<IMoveEffect> GetEffects()
        {
            yield return new WeatherSpeedEffect(weather);
        }

        public void OnAttach(PokemonState pokemon, BattleState state) { }
    }

    public class WeatherSpeedEffect : BaseMoveEffect
    {
        readonly WeatherType weather;

        public WeatherSpeedEffect(WeatherType weather)
        {
            this.weather = weather;
        }

        public override MovePhase Phase => MovePhase.CalculateStat;

        public override void ApplyStat(StatContext context)
        {
            if (context.Stat == "Speed" && context.State.Environment.Weather == weather)
                context.Value *= 2;
        }
    }

    /// <summary>Quick Feet: any status condition means half again the
    /// Speed.</summary>
    public class QuickFeetAbility : IAbility
    {
        public string Id => "quickfeet";
        public IEnumerable<IMoveEffect> GetEffects() { yield return new QuickFeetEffect(); }
        public void OnAttach(PokemonState pokemon, BattleState state) { }
    }

    public class QuickFeetEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.CalculateStat;

        public override void ApplyStat(StatContext context)
        {
            if (context.Stat == "Speed" && context.Pokemon.Status != StatusCondition.None)
                context.Value *= 1.5;
        }
    }

    /// <summary>Flare Boost: a burn fuels the Sp. Atk instead of the body.</summary>
    public class FlareBoostAbility : IAbility
    {
        public string Id => "flareboost";
        public IEnumerable<IMoveEffect> GetEffects() { yield return new FlareBoostEffect(); }
        public void OnAttach(PokemonState pokemon, BattleState state) { }
    }

    public class FlareBoostEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.CalculateStat;

        public override void ApplyStat(StatContext context)
        {
            if (context.Stat == "SpAttack" && context.Pokemon.Status == StatusCondition.Burn)
                context.Value *= 1.5;
        }
    }

    /// <summary>Solar Power: half again the Sp. Atk in sun (the sun's
    /// chip cost lives in AbilityEndOfTurn).</summary>
    public class SolarPowerAbility : IAbility
    {
        public string Id => "solarpower";
        public IEnumerable<IMoveEffect> GetEffects() { yield return new SolarPowerEffect(); }
        public void OnAttach(PokemonState pokemon, BattleState state) { }
    }

    public class SolarPowerEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.CalculateStat;

        public override void ApplyStat(StatContext context)
        {
            if (context.Stat == "SpAttack" && context.State.Environment.Weather == WeatherType.Sun)
                context.Value *= 1.5;
        }
    }

    /// <summary>Protosynthesis (sun) and Quark Drive (Electric Terrain):
    /// the paradox boost to the holder's best base stat - x1.3, or x1.5
    /// when that stat is Speed. Booster Energy is an item and items are
    /// not simulated, so the field condition is the only trigger.</summary>
    public class ParadoxAbility : IAbility
    {
        readonly string id;
        readonly bool sunDriven;

        public ParadoxAbility(string id, bool sunDriven)
        {
            this.id = id;
            this.sunDriven = sunDriven;
        }

        public string Id => id;

        public IEnumerable<IMoveEffect> GetEffects()
        {
            yield return new ParadoxBoostEffect(sunDriven);
        }

        public void OnAttach(PokemonState pokemon, BattleState state) { }
    }

    public class ParadoxBoostEffect : BaseMoveEffect
    {
        readonly bool sunDriven;

        public ParadoxBoostEffect(bool sunDriven)
        {
            this.sunDriven = sunDriven;
        }

        public override MovePhase Phase => MovePhase.CalculateStat;

        public override void ApplyStat(StatContext context)
        {
            bool active = sunDriven
                ? context.State.Environment.Weather == WeatherType.Sun
                : context.State.Environment.Terrain == TerrainType.Electric;

            if (!active || context.Stat != BestStat(context.Pokemon))
                return;

            context.Value *= context.Stat == "Speed" ? 1.5 : 1.3;
        }

        static string BestStat(PokemonState pokemon)
        {
            var stats = pokemon.Stats;

            string best = "Attack";
            int bestValue = stats.Attack;

            if (stats.Defense > bestValue) { best = "Defense"; bestValue = stats.Defense; }
            if (stats.SpAttack > bestValue) { best = "SpAttack"; bestValue = stats.SpAttack; }
            if (stats.SpDefense > bestValue) { best = "SpDefense"; bestValue = stats.SpDefense; }
            if (stats.Speed > bestValue) { best = "Speed"; }

            return best;
        }
    }

    /// <summary>Slow Start: half Attack and Speed for the first five turns
    /// after entering the field.</summary>
    public class SlowStartAbility : IAbility
    {
        public string Id => "slowstart";
        public IEnumerable<IMoveEffect> GetEffects() { yield return new SlowStartEffect(); }
        public void OnAttach(PokemonState pokemon, BattleState state) { }
    }

    public class SlowStartEffect : BaseMoveEffect
    {
        public override MovePhase Phase => MovePhase.CalculateStat;

        public override void ApplyStat(StatContext context)
        {
            if ((context.Stat == "Attack" || context.Stat == "Speed") &&
                context.State.TurnNumber - context.Pokemon.EnteredFieldTurn < 5)
            {
                context.Value *= 0.5;
            }
        }
    }
}