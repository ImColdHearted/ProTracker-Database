using System;
using System.Collections.Generic;
using System.Linq;
using PokemonSim.Models;

namespace PokemonSim.Engine
{
    /// <summary>
    /// Section 161. Mega evolution, driven by the section-159 mega stones
    /// that used to ride along inert. The table below maps each of the 24
    /// stones to its owner and its mega forme's ability; the forme's types
    /// and base stats come from the engine pokedex's own "Mega X" entries
    /// at transform time. The transform follows the games: HP never
    /// changes, the other five stats are recomputed from the mega base
    /// stats with the holder's own level, IVs, EVs and nature, and the new
    /// ability activates as if the Pokemon had just switched in (Drought
    /// sets the sun, Trace copies, Sand Stream whips up its storm). One
    /// mega evolution per side per battle, it happens before anything else
    /// in the turn, and it is permanent - switching out does not undo it.
    /// </summary>
    public static class MegaEvolutions
    {
        public sealed class MegaForm
        {
            public required string StoneId;
            public required string BaseSpecies;
            public required string MegaSpecies;
            public required string AbilityId;
        }

        static readonly Dictionary<string, MegaForm> byStone = new(StringComparer.Ordinal);

        static MegaEvolutions()
        {
            Add("aerodactylite", "Aerodactyl", "Mega Aerodactyl", "toughclaws");
            Add("alakazite", "Alakazam", "Mega Alakazam", "trace");
            Add("blastoisinite", "Blastoise", "Mega Blastoise", "megalauncher");
            Add("blazikenite", "Blaziken", "Mega Blaziken", "speedboost");
            Add("charizarditex", "Charizard", "Mega Charizard X", "toughclaws");
            Add("charizarditey", "Charizard", "Mega Charizard Y", "drought");
            Add("diancite", "Diancie", "Mega Diancie", "magicbounce");
            Add("galladite", "Gallade", "Mega Gallade", "innerfocus");
            Add("garchompite", "Garchomp", "Mega Garchomp", "sandforce");
            Add("gardevoirite", "Gardevoir", "Mega Gardevoir", "pixilate");
            Add("gengarite", "Gengar", "Mega Gengar", "shadowtag");
            Add("gyaradosite", "Gyarados", "Mega Gyarados", "moldbreaker");
            Add("heracronite", "Heracross", "Mega Heracross", "skilllink");
            Add("latiasite", "Latias", "Mega Latias", "levitate");
            Add("latiosite", "Latios", "Mega Latios", "levitate");
            Add("lucarionite", "Lucario", "Mega Lucario", "adaptability");
            Add("metagrossite", "Metagross", "Mega Metagross", "toughclaws");
            Add("mewtwonitex", "Mewtwo", "Mega Mewtwo X", "steadfast");
            Add("mewtwonitey", "Mewtwo", "Mega Mewtwo Y", "insomnia");
            Add("salamencite", "Salamence", "Mega Salamence", "aerilate");
            Add("slowbronite", "Slowbro", "Mega Slowbro", "shellarmor");
            Add("steelixite", "Steelix", "Mega Steelix", "sandforce");
            Add("swampertite", "Swampert", "Mega Swampert", "swiftswim");
            Add("tyranitarite", "Tyranitar", "Mega Tyranitar", "sandstream");
        }

        static void Add(string stone, string baseSpecies, string megaSpecies, string abilityId)
        {
            byStone[stone] = new MegaForm
            {
                StoneId = stone,
                BaseSpecies = baseSpecies,
                MegaSpecies = megaSpecies,
                AbilityId = abilityId
            };
        }

        public static IReadOnlyCollection<MegaForm> All => byStone.Values;

        /// <summary>The mega form this stone unlocks, or null for anything
        /// that is not a mega stone.</summary>
        public static MegaForm? ForStone(string? itemId)
        {
            if (string.IsNullOrWhiteSpace(itemId))
                return null;

            return byStone.TryGetValue(Items.HeldItems.Normalize(itemId), out MegaForm? form)
                ? form
                : null;
        }

        /// <summary>Whether this stone belongs to this species - the base
        /// form or the already-mega'd name both count (the §159 item-theft
        /// protection needs the latter).</summary>
        public static bool StoneMatches(string speciesName, string? stoneId)
        {
            MegaForm? form = ForStone(stoneId);

            return form != null &&
                (form.BaseSpecies.Equals(speciesName, StringComparison.OrdinalIgnoreCase) ||
                 form.MegaSpecies.Equals(speciesName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Everything the Mega Evolve button and the computer
        /// players check: the active holds its own stone, nobody on this
        /// side has mega evolved yet, and the engine pokedex knows the
        /// forme. Charging locks the choice out for the release turn, the
        /// same way it locks the move menu.</summary>
        public static bool CanMegaEvolve(BattleState state, PlayerState side, PokemonState user)
        {
            if (user.Fainted || user.MegaEvolved || side.UsedMegaEvolution)
                return false;

            if (user.Charging)
                return false;

            MegaForm? form = ForStone(user.HeldItemId);

            if (form == null ||
                !form.BaseSpecies.Equals(user.Species, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return Data.PokemonDex.TryGet(form.MegaSpecies, out _);
        }

        /// <summary>The transform itself. Returns false (and does nothing)
        /// when the checks above no longer hold - the engine re-checks at
        /// resolution so a stale action flag can never force an illegal
        /// mega.</summary>
        public static bool Perform(BattleState state, PlayerState side, PokemonState user)
        {
            if (!CanMegaEvolve(state, side, user))
                return false;

            MegaForm form = ForStone(user.HeldItemId)!;

            if (!Data.PokemonDex.TryGet(form.MegaSpecies, out PokemonSpecies mega))
                return false;

            string oldName = user.Species;

            state.Log.Write($"{oldName}'s {Items.HeldItems.DisplayName(form.StoneId)} is reacting to the Key Stone!");

            user.Species = mega.Name;
            user.Types = mega.Types.ToList();

            // HP is the one stat mega evolution never touches; the rest are
            // recomputed exactly the way PokemonFactory built them.
            user.Stats.Attack = StatCalculator.CalculateStat(
                mega.BaseStats.Attack, user.AttackIV, user.AttackEV, user.Level,
                NatureCalculator.GetModifier(user.Nature, "Attack"));

            user.Stats.Defense = StatCalculator.CalculateStat(
                mega.BaseStats.Defense, user.DefenseIV, user.DefenseEV, user.Level,
                NatureCalculator.GetModifier(user.Nature, "Defense"));

            user.Stats.SpAttack = StatCalculator.CalculateStat(
                mega.BaseStats.SpAttack, user.SpAttackIV, user.SpAttackEV, user.Level,
                NatureCalculator.GetModifier(user.Nature, "SpAttack"));

            user.Stats.SpDefense = StatCalculator.CalculateStat(
                mega.BaseStats.SpDefense, user.SpDefenseIV, user.SpDefenseEV, user.Level,
                NatureCalculator.GetModifier(user.Nature, "SpDefense"));

            user.Stats.Speed = StatCalculator.CalculateStat(
                mega.BaseStats.Speed, user.SpeedIV, user.SpeedEV, user.Level,
                NatureCalculator.GetModifier(user.Nature, "Speed"));

            // The mega ability replaces the old one outright: fresh ability
            // state, fresh passive effects, and a switch-in style
            // activation so Drought, Sand Stream and Trace fire on the
            // spot. Restore does the rebuild without the activation;
            // OnAttach is the activation.
            user.AbilityId = form.AbilityId;
            user.AbilityState.Clear();
            user.HasMagicGuard = false;

            Abilities.AbilityFactory.Restore(user, state);
            user.Ability?.OnAttach(user, state);

            user.MegaEvolved = true;
            side.UsedMegaEvolution = true;

            state.Log.Write($"{oldName} has Mega Evolved into {user.Species}!");

            return true;
        }
    }
}