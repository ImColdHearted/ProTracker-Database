using PokemonSim.Models;

namespace PokemonSim.Engine
{
    // Section 154: moved into PokemonSim.Engine (was global), and stat
    // stages plus paralysis now apply HERE - the one place battles read
    // stats from. Before, stages only existed on PokemonState's display
    // properties, which nothing in the damage or speed path used, so
    // Swords Dance, Intimidate and every other stage change had no effect
    // on the actual fight.
    public static class StatResolver
    {
        /// <summary>Section 158: ignoreStages serves Unaware - the raw
        /// stat with abilities and paralysis, but no stage multiplier.</summary>
        public static double GetStat(
            BattleState state,
            PokemonState pokemon,
            string stat,
            bool ignoreStages = false)
        {
            double value = stat switch
            {
                "Speed" => pokemon.Stats.Speed,
                "Attack" => pokemon.Stats.Attack,
                "Defense" => pokemon.Stats.Defense,
                "SpAttack" => pokemon.Stats.SpAttack,
                "SpDefense" => pokemon.Stats.SpDefense,
                _ => 0
            };

            int stage = stat switch
            {
                "Speed" => pokemon.SpeedStage,
                "Attack" => pokemon.AttackStage,
                "Defense" => pokemon.DefenseStage,
                "SpAttack" => pokemon.SpAttackStage,
                "SpDefense" => pokemon.SpDefenseStage,
                _ => 0
            };

            if (!ignoreStages)
                value *= StatStageCalculator.GetMultiplier(stage);

            if (stat == "Speed" && pokemon.Status == StatusCondition.Paralysis)
                value *= 0.5;

            // Section 158: Tailwind doubles the whole side's Speed.
            if (stat == "Speed" && state.TailwindTurns(state.GetOwner(pokemon)) > 0)
                value *= 2;

            var ctx = new StatContext
            {
                State = state,
                Pokemon = pokemon,
                Stat = stat,
                Value = value
            };

            ApplyEffects(state, pokemon, ctx);

            // Section 159: held-item stat multipliers (Choice items,
            // Assault Vest, Eviolite, Light Ball) and Unburden.
            return ctx.Value * Items.HeldItems.StatMultiplier(state, pokemon, stat);
        }

        private static void ApplyEffects(
            BattleState state,
            PokemonState pokemon,
            StatContext ctx)
        {
            foreach (var effect in pokemon.PassiveEffects)
            {
                if (effect.Phase == MovePhase.CalculateStat)
                {
                    effect.ApplyStat(ctx);
                }
            }
        }
    }
}