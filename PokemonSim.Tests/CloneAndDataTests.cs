using System;
using System.Collections.Generic;
using System.Linq;
using PokemonSim.Data;
using PokemonSim.Engine;
using PokemonSim.Engine.Abilities;
using PokemonSim.Models;
using Xunit;
using static PokemonSim.Tests.TestKit;

namespace PokemonSim.Tests
{
    public class CloneTests
    {
        static string Fingerprint(BattleState state)
        {
            var parts = new List<string>
            {
                state.TurnNumber.ToString(),
                state.Outcome.ToString(),
                state.Environment.Weather.ToString(),
                state.Environment.WeatherTurns.ToString(),
                state.Environment.Terrain.ToString(),
                state.Environment.TerrainTurns.ToString(),
                $"{state.SpikesP1}/{state.SpikesP2}/{state.ToxicSpikesP1}/{state.ToxicSpikesP2}/{state.StealthRockP1}/{state.StealthRockP2}"
            };

            foreach (var player in new[] { state.Player1, state.Player2 })
            {
                parts.Add(player.Team.IndexOf(player.ActivePokemon).ToString());

                foreach (var p in player.Team)
                {
                    parts.Add($"{p.Species}|{p.CurrentHP}|{p.Status}|{p.SleepTurns}|{p.ToxicCounter}|{p.AttackStage}|{p.DefenseStage}|{p.SpeedStage}|{p.SubstituteHP}|{p.Charging}|{p.Protected}|{p.ConsecutiveProtects}|{p.AbilityId}|{p.HasMagicGuard}|{string.Join(",", p.AbilityState.Select(kv => kv.Key + "=" + kv.Value))}|{string.Join(",", p.Moves.Select(m => m.Name + ":" + m.CurrentPP))}");
                }
            }

            return string.Join("~", parts);
        }

        static (BattleState, BattleEngine) RichBattle()
        {
            var a1 = Mon("Alpha", hp: 300, moves: new[] { Move("Hit A", power: 40), Move("Spare", power: 10) });
            var a2 = Mon("Beta", moves: Move("Hit B", power: 40));
            var b1 = Mon("Gamma", hp: 300, speed: 60, moves: Move("Hit C", power: 40));
            var b2 = Mon("Delta", moves: Move("Hit D", power: 40));

            a1.AbilityId = "regenerator";
            AbilityFactory.TryCreate("regenerator", out var regen);
            a1.Ability = regen;

            var (state, engine) = Battle(99,
                new List<PokemonState> { a1, a2 },
                new List<PokemonState> { b1, b2 });

            state.Environment.Weather = WeatherType.Rain;
            state.Environment.WeatherTurns = 4;
            state.SpikesP2 = 2;
            state.StealthRockP1 = true;

            return (state, engine);
        }

        [Fact]
        public void Clone_CopiesEverything_AndMutationsNeverTouchTheOriginal()
        {
            var (state, _) = RichBattle();

            string before = Fingerprint(state);

            BattleState clone = state.Clone();

            // The copy matches the original field for field...
            Assert.Equal(before, Fingerprint(clone));

            // ...then gets brutalized.
            var engine = new BattleEngine(clone);

            for (int i = 0; i < 8 && clone.Outcome == BattleOutcome.Unfinished; i++)
                Clash(clone, engine);

            clone.Player1.ActivePokemon.CurrentHP = 1;
            clone.Player1.ActivePokemon.Status = StatusCondition.Toxic;
            clone.Player1.ActivePokemon.ToxicCounter = 5;
            clone.Player1.ActivePokemon.AttackStage = 6;
            clone.Player1.ActivePokemon.Moves[0].CurrentPP = 0;
            clone.Player1.ActivePokemon.SubstituteHP = 25;
            clone.Player1.ActivePokemon.AbilityState["flashfire"] = true;
            clone.Player2.ActivePokemon = clone.Player2.Team[1];
            clone.Environment.Weather = WeatherType.Hail;
            clone.Environment.Terrain = TerrainType.Grassy;
            clone.SpikesP1 = 3;
            clone.StealthRockP2 = true;
            clone.TurnNumber = 250;
            clone.Outcome = BattleOutcome.Draw;

            // The visible battle never moved.
            Assert.Equal(before, Fingerprint(state));
        }

        [Fact]
        public void Clone_RngIsIndependentButIdenticallyPositioned()
        {
            var (state, _) = RichBattle();

            BattleState clone = state.Clone();

            // Same position at the moment of cloning: the next roll matches
            // (peeked through throwaway clones so nothing is consumed).
            Assert.Equal(state.Rng.Clone().Next(1_000_000), clone.Rng.Clone().Next(1_000_000));

            // Independent afterwards: rolling the original three times does
            // not move the clone - its next roll is still the shared one.
            int expected = clone.Rng.Clone().Next(1_000_000);

            state.Rng.Next(1_000_000);
            state.Rng.Next(1_000_000);
            state.Rng.Next(1_000_000);

            Assert.Equal(expected, clone.Rng.Next(1_000_000));
        }

        [Fact]
        public void Clone_ReattachesAbilities_ToTheCloneOnly()
        {
            var (state, _) = RichBattle();

            BattleState clone = state.Clone();

            var cloneAlpha = clone.Player1.ActivePokemon;

            Assert.NotNull(cloneAlpha.Ability);
            Assert.Equal("regenerator", cloneAlpha.Ability!.Id);

            // Regenerator on the CLONE: switching out heals the clone's
            // Pokemon and leaves the original's untouched.
            cloneAlpha.CurrentHP = 100;
            int originalHp = state.Player1.ActivePokemon.CurrentHP;

            SwitchResolver.Resolve(clone, clone.Player1, clone.Player1.Team[1]);

            Assert.Equal(100 + cloneAlpha.MaxHP / 3, cloneAlpha.CurrentHP);
            Assert.Equal(originalHp, state.Player1.ActivePokemon.CurrentHP);
        }

        [Fact]
        public void Clone_LogIsSilentAndSeparate()
        {
            var (state, _) = RichBattle();

            int visibleLines = state.Log.Lines.Count;

            BattleState clone = state.Clone();
            var engine = new BattleEngine(clone);

            Clash(clone, engine);

            Assert.True(clone.Log.Silent);
            Assert.Empty(clone.Log.Lines);
            Assert.Equal(visibleLines, state.Log.Lines.Count);
        }
    }

    public class DataTests
    {
        [Fact]
        public void MoveDex_LoadsTheRepairedFile()
        {
            MoveDex.EnsureLoaded();

            Assert.True(MoveDex.Count >= 270, $"only {MoveDex.Count} moves loaded");

            Assert.True(MoveDex.TryGet("Protect", out var protect));
            Assert.Equal(4, protect.Priority);
            Assert.Contains("Protect", protect.Effects!);

            Assert.True(MoveDex.TryGet("Rain Dance", out var rain));
            Assert.Equal(WeatherType.Rain, rain.SetWeather);

            Assert.True(MoveDex.TryGet("Thunder Wave", out var wave));
            Assert.Equal(StatusCondition.Paralysis, wave.InflictStatus);

            // The one deliberately-unsupported field left in the data is a
            // warning, not a silent success.
            Assert.Contains(MoveDex.Warnings, w => w.Contains("Heal Pulse"));
        }

        [Fact]
        public void PokemonDex_LoadsAllSpecies_WithDiagnosticsForMissingMoves()
        {
            PokemonDex.EnsureLoaded();

            // §161 added the missing "Mega Tyranitar" entry (Tyranitarite
            // is a live boss item), bringing 802 to 803.
            Assert.Equal(803, PokemonDex.Count);

            var venusaur = PokemonDex.Get("Venusaur");
            Assert.Equal(80, venusaur.BaseStats.HP);
            Assert.Contains("Overgrow", venusaur.Abilities);
            Assert.NotEmpty(venusaur.Learnset);

            // Section 158 completed the move data: every learnset name now
            // resolves (the 268 gaps and the 15 spelling typos are gone),
            // so the per-species missing-move warnings went with them.
            Assert.Empty(PokemonDex.Warnings);
        }

        [Fact]
        public void UnknownLookups_AreDiagnosticsNotCrashes()
        {
            Assert.False(MoveDex.TryGet("Not A Real Move", out _));

            var ex = Record.Exception(() => MoveDex.Get("Not A Real Move"));
            Assert.IsType<KeyNotFoundException>(ex);
            Assert.Contains("Not A Real Move", ex!.Message);

            Assert.False(AbilityFactory.TryCreate("wonderguard", out var ability));
            Assert.Equal("none", ability.Id);
            Assert.False(AbilityFactory.IsSupported("wonderguard"));
            Assert.True(AbilityFactory.IsSupported("Sand Stream"));
        }

        [Fact]
        public void UnknownMoveEffect_LogsADiagnosticLine()
        {
            var weird = Move("Future Sight-ish", power: 40);
            weird.Effects = new List<string> { "FutureSight" };

            var (state, engine) = Duel(61,
                Mon("Seer", moves: weird),
                Mon("Bag", speed: 1, moves: Move("Wait", category: MoveCategory.Status, power: 0)));

            Clash(state, engine);

            Assert.True(LogContains(state, "not simulated yet"));
            Assert.True(state.Player2.ActivePokemon.CurrentHP < state.Player2.ActivePokemon.MaxHP,
                "the move's plain damage should still land");
        }

        [Fact]
        public void TypeChart_MatchesTheTrackersVerifiedTable()
        {
            Assert.Equal(0.0, TypeChart.GetMultiplier(PokemonType.Electric, PokemonType.Ground));
            Assert.Equal(0.0, TypeChart.GetMultiplier(PokemonType.Normal, PokemonType.Ghost));
            Assert.Equal(0.0, TypeChart.GetMultiplier(PokemonType.Dragon, PokemonType.Fairy));
            Assert.Equal(0.5, TypeChart.GetMultiplier(PokemonType.Dragon, PokemonType.Steel));
            Assert.Equal(2.0, TypeChart.GetMultiplier(PokemonType.Fire, PokemonType.Grass));
            Assert.Equal(2.0, TypeChart.GetMultiplier(PokemonType.Fairy, PokemonType.Dragon));
            Assert.Equal(1.0, TypeChart.GetMultiplier(PokemonType.Normal, PokemonType.Normal));
        }

        [Fact]
        public void NatureTable_IsComplete()
        {
            string[] stats = { "Attack", "Defense", "SpAttack", "SpDefense", "Speed" };

            foreach (string stat in stats)
            {
                int boosted = 0, hindered = 0;

                foreach (Nature nature in Enum.GetValues<Nature>())
                {
                    double modifier = NatureCalculator.GetModifier(nature, stat);

                    if (modifier > 1.0) boosted++;
                    if (modifier < 1.0) hindered++;
                }

                Assert.Equal(4, boosted);
                Assert.Equal(4, hindered);
            }

            Assert.Equal(1.1, NatureCalculator.GetModifier(Nature.Sassy, "SpDefense"));
            Assert.Equal(0.9, NatureCalculator.GetModifier(Nature.Sassy, "Speed"));
        }

        [Fact]
        public void StatFormulas_MatchKnownValues()
        {
            // Venusaur at level 50, 31 IV, 0 EV: HP 155, neutral Attack 102.
            Assert.Equal(155, StatCalculator.CalculateHP(80, 31, 0, 50));
            Assert.Equal(102, StatCalculator.CalculateStat(82, 31, 0, 50, 1.0));
        }
    }
}