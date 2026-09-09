using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using PokemonSim.Data;
using PokemonSim.Engine;
using PokemonSim.Engine.Abilities;
using PokemonSim.Engine.Effects;
using PokemonSim.Models;
using Xunit;
using static PokemonSim.Tests.TestKit;

namespace PokemonSim.Tests
{
    /// <summary>
    /// Section 197. Two bugs from one reported battle, and the sweep the
    /// first of them turned into.
    ///
    /// "Used the water move Scald, with Jellicent, a Pokemon with the Water
    /// Absorb ability, and Pikachu instead absorbed the attack." That is
    /// exactly what the engine did. MoveResolver ran the ATTACKER's passive
    /// effects and the DEFENDER's passive effects through the same
    /// Apply(state, attacker, defender, ...) call, and nothing recorded which
    /// of the two an effect was speaking for. AbsorbEffect always heals the
    /// "defender" parameter, so Jellicent's own ability fired on Jellicent's
    /// own turn, healed the Pokemon it was aiming at, and cancelled the move.
    ///
    /// Reading the rest of the effect list found the same shape in nine more
    /// places and the mirror image of it in eight others - Flash Fire
    /// absorbing its owner's own Fire move, Thick Fat halving its owner's own
    /// Fire and Ice moves, a Technician wall boosting every weak move aimed at
    /// it, Blaze and Torrent and Overgrow reading the wrong Pokemon's HP. So
    /// the fix is a property (EffectSide) rather than a special case, and the
    /// last test here is the guard that keeps the next ability from arriving
    /// unclassified.
    ///
    /// "Fake Out is only usable on the first turn the Pokemon has been sent
    /// out, or if the Pokemon started in battle." Fake Out had no rule at all
    /// - only priority 3 and a flinch - and the rule First Impression used was
    /// the wrong question: it compared EnteredFieldTurn to the turn number,
    /// which is false on the one turn it should be true, because a switch
    /// spends its own turn and a faint replacement is stamped with the turn
    /// that just ended. PokemonState.HasActedSinceEnteringField asks the
    /// question the rule actually asks.
    /// </summary>
    public class Section197Tests
    {
        static void Give(BattleState state, PokemonState pokemon, string ability)
        {
            pokemon.AbilityId = ability;
            AbilityFactory.Restore(pokemon, state);
        }

        static MoveState WithEffects(MoveState move, params string[] effects)
        {
            move.Effects = effects.ToList();
            return move;
        }

        static Dictionary<string, JsonElement> MoveData()
        {
            using JsonDocument document = JsonDocument.Parse(
                File.ReadAllText(SimDataFiles.Resolve("moves.json")));

            return document.RootElement.EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Damage dealt to Player 2 in one exchange, with an optional
        /// ability handed to one side first. Both runs use the same seed and
        /// Restore consumes no rolls, so the two numbers are comparable.</summary>
        static int DamageDealt(int seed, MoveState move, string? p1Ability, string? p2Ability)
        {
            var (state, engine) = Duel(seed,
                Mon("Hitter", moves: move),
                Mon("Target", hp: 600, speed: 1));

            if (p1Ability != null)
                Give(state, state.Player1.ActivePokemon, p1Ability);

            if (p2Ability != null)
                Give(state, state.Player2.ActivePokemon, p2Ability);

            Clash(state, engine);

            return 600 - state.Player2.ActivePokemon.CurrentHP;
        }

        // ---------------- the reported battle -----------------------------

        [Fact]
        public void AnAttackerWithWaterAbsorb_DoesNotAbsorbItsOwnWaterMove()
        {
            var scald = Move("Scald", type: PokemonType.Water, category: MoveCategory.Special);

            var (state, engine) = Duel(1970,
                Mon("Jellicent", PokemonType.Water, moves: scald),
                Mon("Pikachu", PokemonType.Electric, speed: 1));

            Give(state, state.Player1.ActivePokemon, "waterabsorb");

            var pikachu = state.Player2.ActivePokemon;

            Clash(state, engine);

            Assert.True(pikachu.CurrentHP < pikachu.MaxHP);
            Assert.False(LogContains(state, "absorbed the attack"));
        }

        [Fact]
        public void ADefenderWithWaterAbsorb_StillAbsorbs()
        {
            var jet = Move("Jet", type: PokemonType.Water, category: MoveCategory.Special);

            var (state, engine) = Duel(1971, Mon("Squirt", moves: jet), Mon("Sponge", speed: 1));

            var sponge = state.Player2.ActivePokemon;
            Give(state, sponge, "waterabsorb");
            sponge.CurrentHP = 100;

            Clash(state, engine);

            Assert.Equal(100 + sponge.MaxHP / 4, sponge.CurrentHP);
            Assert.True(LogContains(state, "absorbed the attack"));
        }

        // ---------------- the same shape, found by reading ------------------

        [Fact]
        public void AnAttackerWithFlashFire_DoesNotAbsorbItsOwnFireMove()
        {
            var ember = Move("Ember", type: PokemonType.Fire, category: MoveCategory.Special);

            var (state, engine) = Duel(1972,
                Mon("Arcanine", PokemonType.Fire, moves: ember),
                Mon("Target", speed: 1));

            Give(state, state.Player1.ActivePokemon, "flashfire");

            var target = state.Player2.ActivePokemon;

            Clash(state, engine);

            Assert.True(target.CurrentHP < target.MaxHP);
            Assert.False(LogContains(state, "absorbed the fire"));
        }

        [Fact]
        public void AnAttackerWithLevitate_StillLandsItsGroundMove()
        {
            var quake = Move("Quake", type: PokemonType.Ground);

            var (state, engine) = Duel(1973, Mon("Flygon", moves: quake), Mon("Target", speed: 1));

            Give(state, state.Player1.ActivePokemon, "levitate");

            var target = state.Player2.ActivePokemon;

            Clash(state, engine);

            Assert.True(target.CurrentHP < target.MaxHP);
            Assert.False(LogContains(state, "immune due to Levitate"));
        }

        [Fact]
        public void ADefenderWithLevitate_IsStillImmuneToGround()
        {
            var quake = Move("Quake", type: PokemonType.Ground);

            var (state, engine) = Duel(1974, Mon("Digger", moves: quake), Mon("Floaty", speed: 1));

            var floaty = state.Player2.ActivePokemon;
            Give(state, floaty, "levitate");

            Clash(state, engine);

            Assert.Equal(floaty.MaxHP, floaty.CurrentHP);
            Assert.True(LogContains(state, "immune due to Levitate"));
        }

        [Fact]
        public void AnAttackerWithThickFat_DoesNotHalveItsOwnFireMove()
        {
            var flame = Move("Flame", type: PokemonType.Fire, category: MoveCategory.Special);

            int plain = DamageDealt(1975, flame, null, null);
            int padded = DamageDealt(1975, Move("Flame", type: PokemonType.Fire,
                category: MoveCategory.Special), "thickfat", null);

            Assert.True(plain > 0);
            Assert.Equal(plain, padded);
        }

        [Fact]
        public void ADefenderWithThickFat_StillHalvesTheFireMoveAimedAtIt()
        {
            int plain = DamageDealt(1976,
                Move("Flame", type: PokemonType.Fire, category: MoveCategory.Special), null, null);

            int resisted = DamageDealt(1976,
                Move("Flame", type: PokemonType.Fire, category: MoveCategory.Special), null, "thickfat");

            Assert.Equal((int)(plain * 0.5), resisted);
        }

        // ---------------- and the leak in the other direction ---------------

        [Fact]
        public void ADefenderWithTechnician_DoesNotBoostTheWeakMoveAimedAtIt()
        {
            int plain = DamageDealt(1977, Move("Poke", power: 40), null, null);
            int unboosted = DamageDealt(1977, Move("Poke", power: 40), null, "technician");

            Assert.True(plain > 0);
            Assert.Equal(plain, unboosted);
        }

        [Fact]
        public void AnAttackerWithTechnician_StillGetsItsBoost()
        {
            int plain = DamageDealt(1978, Move("Poke", power: 40), null, null);
            int boosted = DamageDealt(1978, Move("Poke", power: 40), "technician", null);

            Assert.Equal((int)(plain * 1.5), boosted);
        }

        [Fact]
        public void ADefenderWithAdaptability_DoesNotBoostTheAttackersMove()
        {
            int plain = DamageDealt(1979, Move("Bash"), null, null);
            int unboosted = DamageDealt(1979, Move("Bash"), null, "adaptability");

            Assert.True(plain > 0);
            Assert.Equal(plain, unboosted);
        }

        [Fact]
        public void AnAttackerWithAdaptability_StillGetsItsStabBoost()
        {
            int plain = DamageDealt(1980, Move("Bash"), null, null);
            int boosted = DamageDealt(1980, Move("Bash"), "adaptability", null);

            Assert.Equal(plain * 4 / 3, boosted);
        }

        [Fact]
        public void AnAttackerWithMoldBreaker_StillIgnoresTheDefendersMultiscale()
        {
            int unscaled = DamageDealt(1981, Move("Bash"), null, null);
            int scaled = DamageDealt(1981, Move("Bash"), null, "multiscale");
            int broken = DamageDealt(1981, Move("Bash"), "moldbreaker", "multiscale");

            Assert.Equal(unscaled / 2, scaled);
            Assert.Equal(unscaled, broken);
        }

        // ---------------- the guard that keeps this from coming back --------

        [Fact]
        public void EveryAbilityOwnedEffect_SaysWhichSideItSpeaksFor()
        {
            // The effects that are right to leave as Either, and why.
            var allowed = new HashSet<string>(StringComparer.Ordinal)
            {
                // CalculateStat effects. ApplyStat receives a StatContext
                // naming its own Pokemon, so the attacker/defender slot the
                // damage pipeline uses never applies to them.
                "ChlorophyllEffect", "SwiftSwimEffect", "GutsEffect",
                "HugePowerEffect", "MarvelScaleEffect", "WeatherSpeedEffect",
                "QuickFeetEffect", "FlareBoostEffect", "SolarPowerEffect",
                "ParadoxBoostEffect", "SlowStartEffect",

                // The documented exception: the real aura boosts both sides'
                // Fairy moves, so running it from either slot is defensible.
                "FairyAuraEffect"
            };

            var unclassified = new List<string>();
            var attackerOnly = new HashSet<string>(StringComparer.Ordinal);
            var defenderOnly = new HashSet<string>(StringComparer.Ordinal);

            foreach (string id in AbilityFactory.SupportedIds)
            {
                if (!AbilityFactory.TryCreate(id, out IAbility ability))
                    continue;

                foreach (IMoveEffect effect in ability.GetEffects())
                {
                    string name = effect.GetType().Name;

                    switch (effect.Side)
                    {
                        case EffectSide.AttackerOnly:
                            attackerOnly.Add(name);
                            break;

                        case EffectSide.DefenderOnly:
                            defenderOnly.Add(name);
                            break;

                        default:
                            if (!allowed.Contains(name))
                                unclassified.Add(id + " -> " + name);
                            break;
                    }
                }
            }

            Assert.True(unclassified.Count == 0,
                "unclassified ability effects: " + string.Join(", ", unclassified.Distinct()));

            // Not a vacuous pass: the property really is being read.
            Assert.Contains("AbsorbEffect", defenderOnly);
            Assert.Contains("LevitateEffect", defenderOnly);
            Assert.Contains("AdaptabilityEffect", attackerOnly);
            Assert.Contains("TechnicianEffect", attackerOnly);
            Assert.True(defenderOnly.Count >= 15, defenderOnly.Count.ToString());
            Assert.True(attackerOnly.Count >= 15, attackerOnly.Count.ToString());
        }

        // ---------------- Fake Out ------------------------------------------

        [Fact]
        public void BothStarters_BeginWithTheirFirstTurnUnspent()
        {
            var (state, engine) = Duel(1982, Mon("A"), Mon("B"));

            Assert.False(state.Player1.ActivePokemon.HasActedSinceEnteringField);
            Assert.False(state.Player2.ActivePokemon.HasActedSinceEnteringField);

            Clash(state, engine);

            Assert.True(state.Player1.ActivePokemon.HasActedSinceEnteringField);
            Assert.True(state.Player2.ActivePokemon.HasActedSinceEnteringField);
        }

        [Fact]
        public void TheActedFlagClones()
        {
            var mon = Mon("Copy");
            mon.HasActedSinceEnteringField = true;

            Assert.True(mon.Clone().HasActedSinceEnteringField);

            mon.HasActedSinceEnteringField = false;

            Assert.False(mon.Clone().HasActedSinceEnteringField);
        }

        [Fact]
        public void FakeOut_LandsOnTheTurnItsUserComesIn()
        {
            var fakeOut = WithEffects(Move("Fake Out", power: 40, priority: 3), "FirstTurnOnly");

            var (state, engine) = Duel(1983, Mon("Lopunny", moves: fakeOut), Mon("Target", hp: 600, speed: 1));

            var target = state.Player2.ActivePokemon;

            engine.RunTurn(
                MoveAction(state, state.Player1.ActivePokemon, fakeOut),
                MoveAction(state, target, target.Moves[0]));

            Assert.True(target.CurrentHP < target.MaxHP);
            Assert.False(LogContains(state, "But it failed!"));
        }

        [Fact]
        public void FakeOut_FailsOnTheFollowingTurn()
        {
            var fakeOut = WithEffects(Move("Fake Out", power: 40, priority: 3), "FirstTurnOnly");

            var (state, engine) = Duel(1984, Mon("Lopunny", moves: fakeOut), Mon("Target", hp: 600, speed: 1));

            var lopunny = state.Player1.ActivePokemon;
            var target = state.Player2.ActivePokemon;

            engine.RunTurn(
                MoveAction(state, lopunny, fakeOut),
                MoveAction(state, target, target.Moves[0]));

            int afterTheFirstTurn = target.CurrentHP;

            engine.RunTurn(
                MoveAction(state, lopunny, fakeOut),
                MoveAction(state, target, target.Moves[0]));

            Assert.Equal(afterTheFirstTurn, target.CurrentHP);
            Assert.True(LogContains(state, "Fake Out only works on the first turn its user is out."));
        }

        [Fact]
        public void FakeOut_WorksForAPokemonThatSwitchedInOnAnEarlierTurn()
        {
            // The case the old rule got wrong. A voluntary switch spends its
            // own turn, so the replacement's first real turn is the NEXT one -
            // by which point EnteredFieldTurn no longer equals TurnNumber.
            var fakeOut = WithEffects(Move("Fake Out", power: 40, priority: 3), "FirstTurnOnly");

            var lead = Mon("Lead", hp: 600);
            var bencher = Mon("Bencher", hp: 600, moves: fakeOut);

            var (state, engine) = Battle(1985,
                new List<PokemonState> { lead, bencher },
                new List<PokemonState> { Mon("Target", hp: 600, speed: 1) });

            var target = state.Player2.ActivePokemon;

            engine.RunTurn(
                SwitchAction(state, lead, bencher),
                MoveAction(state, target, target.Moves[0]));

            Assert.Same(bencher, state.Player1.ActivePokemon);
            Assert.False(bencher.HasActedSinceEnteringField);

            int before = target.CurrentHP;

            engine.RunTurn(
                MoveAction(state, bencher, fakeOut),
                MoveAction(state, target, target.Moves[0]));

            Assert.True(target.CurrentHP < before);
            Assert.True(bencher.HasActedSinceEnteringField);
            Assert.False(LogContains(state, "But it failed!"));
        }

        [Fact]
        public void FakeOutInTheDataFile_CarriesTheFirstTurnRule()
        {
            JsonElement fakeOut = MoveData()["Fake Out"];

            Assert.Equal(3, fakeOut.GetProperty("priority").GetInt32());
            Assert.Equal(1.0, fakeOut.GetProperty("flinchChance").GetDouble());

            var effects = fakeOut.GetProperty("effects").EnumerateArray()
                .Select(e => e.GetString())
                .ToList();

            Assert.Contains("FirstTurnOnly", effects);
            Assert.True(MoveEffectRegistry.IsKnown("FirstTurnOnly"));
            Assert.True(MoveEffectRegistry.IsKnown("FirstImpression"));
        }
    }
}