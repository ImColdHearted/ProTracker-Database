using PokemonSim.Engine.Abilities;
using PokemonSim.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PokemonSim.Engine
{
    /// <summary>
    /// Builds a battle-ready PokemonState from a species. Section 154: the
    /// random pieces draw from a caller-supplied BattleRng (a bare
    /// new Random() made teams unreproducible), level/nature/ability/moves
    /// can all be chosen (the Simulator's team builder needs that; the old
    /// single Create always rolled level-50 randoms), ability ids go
    /// through AbilityFactory's normalizer, and switch-in work happens at
    /// battle start (BattleInitializer) instead of never.
    /// </summary>
    public static class PokemonFactory
    {
        /// <summary>The old harness entry point: level 50, random nature
        /// (preferring the species' preferred list), random ability, first
        /// four learnset moves.</summary>
        public static PokemonState Create(PokemonSpecies species, BattleRng? rng = null)
        {
            rng ??= new BattleRng(Environment.TickCount);

            string? abilityId = species.Abilities != null && species.Abilities.Count > 0
                ? species.Abilities[rng.Next(species.Abilities.Count)]
                : null;

            Nature nature = species.PreferredNatures.Count > 0
                ? species.PreferredNatures[rng.Next(species.PreferredNatures.Count)]
                : (Nature)rng.Next(Enum.GetValues(typeof(Nature)).Length);

            var moves = species.Learnset
                .Take(4)
                .Select(m => m.Clone())
                .ToList();

            return Create(species, 50, nature, abilityId, moves);
        }

        /// <summary>The team builder's entry point - everything explicit.
        /// Section 162: ivs/evs (HP, Attack, Defense, SpAttack, SpDefense,
        /// Speed order) override the 31/0 defaults BEFORE the stats are
        /// computed - the screenshot importer carries the real spread.</summary>
        public static PokemonState Create(
            PokemonSpecies species,
            int level,
            Nature nature,
            string? abilityId,
            List<MoveState> moves,
            int[]? ivs = null,
            int[]? evs = null)
        {
            level = Math.Clamp(level, 1, 100);

            var pokemon = new PokemonState
            {
                Species = species.Name,
                Types = species.Types.ToList(),
                Level = level,
                Nature = nature,
                Moves = moves,
                Stats = new Stats()
            };

            if (ivs is { Length: 6 })
            {
                pokemon.HPIV = Math.Clamp(ivs[0], 0, 31);
                pokemon.AttackIV = Math.Clamp(ivs[1], 0, 31);
                pokemon.DefenseIV = Math.Clamp(ivs[2], 0, 31);
                pokemon.SpAttackIV = Math.Clamp(ivs[3], 0, 31);
                pokemon.SpDefenseIV = Math.Clamp(ivs[4], 0, 31);
                pokemon.SpeedIV = Math.Clamp(ivs[5], 0, 31);
            }

            // §164: PRO's hard bosses run 400 EVs per stat - past the
            // games' 252 - so the ceiling here is a sanity clamp, not the
            // games' rule. (StatCalculator turns 400 into a flat +100.)
            if (evs is { Length: 6 })
            {
                pokemon.HPEV = Math.Clamp(evs[0], 0, 512);
                pokemon.AttackEV = Math.Clamp(evs[1], 0, 512);
                pokemon.DefenseEV = Math.Clamp(evs[2], 0, 512);
                pokemon.SpAttackEV = Math.Clamp(evs[3], 0, 512);
                pokemon.SpDefenseEV = Math.Clamp(evs[4], 0, 512);
                pokemon.SpeedEV = Math.Clamp(evs[5], 0, 512);
            }

            for (int i = 0; i < pokemon.Moves.Count; i++)
                pokemon.Moves[i].Index = i;

            pokemon.MaxHP = StatCalculator.CalculateHP(
                species.BaseStats.HP,
                pokemon.HPIV,
                pokemon.HPEV,
                pokemon.Level
            );

            pokemon.CurrentHP = pokemon.MaxHP;

            pokemon.Stats = new Stats
            {
                HP = pokemon.MaxHP,

                Attack = StatCalculator.CalculateStat(
                    species.BaseStats.Attack,
                    pokemon.AttackIV,
                    pokemon.AttackEV,
                    pokemon.Level,
                    NatureCalculator.GetModifier(pokemon.Nature, "Attack")
                ),

                Defense = StatCalculator.CalculateStat(
                    species.BaseStats.Defense,
                    pokemon.DefenseIV,
                    pokemon.DefenseEV,
                    pokemon.Level,
                    NatureCalculator.GetModifier(pokemon.Nature, "Defense")
                ),

                SpAttack = StatCalculator.CalculateStat(
                    species.BaseStats.SpAttack,
                    pokemon.SpAttackIV,
                    pokemon.SpAttackEV,
                    pokemon.Level,
                    NatureCalculator.GetModifier(pokemon.Nature, "SpAttack")
                ),

                SpDefense = StatCalculator.CalculateStat(
                    species.BaseStats.SpDefense,
                    pokemon.SpDefenseIV,
                    pokemon.SpDefenseEV,
                    pokemon.Level,
                    NatureCalculator.GetModifier(pokemon.Nature, "SpDefense")
                ),

                Speed = StatCalculator.CalculateStat(
                    species.BaseStats.Speed,
                    pokemon.SpeedIV,
                    pokemon.SpeedEV,
                    pokemon.Level,
                    NatureCalculator.GetModifier(pokemon.Nature, "Speed")
                )
            };

            if (!string.IsNullOrWhiteSpace(abilityId))
            {
                pokemon.AbilityId = AbilityFactory.Normalize(abilityId);

                AbilityFactory.TryCreate(pokemon.AbilityId, out IAbility ability);

                pokemon.Ability = ability;
                pokemon.PassiveEffects.AddRange(ability.GetEffects());
            }

            return pokemon;
        }
    }
}