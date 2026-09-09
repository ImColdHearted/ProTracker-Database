using System;
using System.Collections.Generic;
using System.Linq;
using PokemonSim.Data;
using PokemonSim.Engine;
using PokemonSim.Models;
using PokemonSim.Simulation;
using Xunit;
using static PokemonSim.Tests.TestKit;

namespace PokemonSim.Tests
{
    /// <summary>Section 162: the screenshot importer's text half and the
    /// trusted team-build path behind it. The three big fixtures are the
    /// EXACT Tesseract output of real PRO screenshots (three client
    /// scales, two Pokemon) - the parser must land the exact build every
    /// time, including the rows it has to repair.</summary>
    public class CardImportTests162
    {
        sealed class TwoSpeciesSource : ISpeciesSource
        {
            static SpeciesInfo Make(string name, int hp, int atk, int def, int spa, int spd, int spe) => new()
            {
                Name = name,
                Types = new List<string> { "Normal" },
                BaseStats = new Stats
                {
                    HP = hp, Attack = atk, Defense = def,
                    SpAttack = spa, SpDefense = spd, Speed = spe
                },
                LearnsetMoveNames = new List<string>()
            };

            readonly Dictionary<string, SpeciesInfo> species = new(StringComparer.OrdinalIgnoreCase)
            {
                ["Volcarona"] = Make("Volcarona", 85, 60, 65, 135, 105, 100),
                ["Toxapex"] = Make("Toxapex", 50, 63, 152, 53, 142, 35),
                ["Snorlax"] = Make("Snorlax", 160, 110, 65, 65, 110, 30)
            };

            public IReadOnlyList<string> AllSpeciesNames => species.Keys.OrderBy(n => n).ToList();

            public SpeciesInfo? Find(string name) =>
                species.TryGetValue(name, out SpeciesInfo? info) ? info : null;
        }

        static readonly string[] MoveDictionary =
        {
            "Fiery Dance", "Hidden Power", "Hidden Power (Ice)", "Hidden Power (Fire)",
            "Giga Drain", "Quiver Dance", "Infestation", "Toxic", "Baneful Bunker",
            "Recover", "Fire Blast", "Toxic Spikes", "Fiery Wrath"
        };

        static readonly string[] AbilityDictionary =
        {
            "Swarm", "Flame Body", "Regenerator", "Merciless", "Limber", "Swift Swim"
        };

        static ImportedPokemon Parse(CardOcrTexts texts) =>
            CardImportParser.Parse(texts, new TwoSpeciesSource(), MoveDictionary, AbilityDictionary);

        // ---- the real-screenshot fixtures ----

        [Fact]
        public void RealCard_Volcarona_ReadsCleanAtBaseScale()
        {
            // Screenshot s1 (1363x803 client): every raw string below is
            // Tesseract's actual output, mojibake and all.
            var texts = new CardOcrTexts
            {
                Title = "Volcarona \u00a2 lv.100 0 Janta = ID: 20359863",
                IdCandidates = { "Johto 20359863" },
                HpCandidates = { "303/303" },
                Ability = "Swarm",
                Nature = "Tirnid",
                MovesBlock = "Fiery Dance\nHidden Power Ice\nGiga Drain\nQuiver Dance",
                StatsBlock = "ATK: 12616 000\nDEF: 16428006\nSPD: 32831 252\nSPATK: 369 31 252\nSPDEF: 246 31 000\nHP: 23 000"
            };

            ImportedPokemon result = Parse(texts);

            Assert.Equal("Volcarona", result.SpeciesName);
            Assert.Equal(100, result.Level);
            Assert.Equal("Timid", result.NatureName);
            Assert.Equal("Swarm", result.AbilityName);
            Assert.Equal("20359863", result.GameId);
            Assert.Equal(new[] { "Fiery Dance", "Hidden Power (Ice)", "Giga Drain", "Quiver Dance" },
                result.MoveNames);
            Assert.Equal(new[] { 23, 16, 28, 31, 31, 31 }, result.Ivs);
            Assert.Equal(new[] { 0, 0, 6, 252, 0, 252 }, result.Evs);
            Assert.All(result.RowStatus.Values, status => Assert.Equal("clean", status));
        }

        [Fact]
        public void RealCard_Toxapex_RepairsItsMisreadHpEv()
        {
            // Screenshot s6 (1453x941 client): the HP row read "01 262" -
            // EV 262 does not exist, and the HP bar's 274 pins the truth
            // back to 252 through the HP formula.
            var texts = new CardOcrTexts
            {
                Title = "Toxapex \u00b0 by 100) Kanto ID: 92446959",
                IdCandidates = { "nto) IDs92446959" },
                HpCandidates = { "4", "274274" },
                Ability = "Regenerator",
                Nature = "Sassy",
                MovesBlock = "Infestation\nToxic\n\nBaneful Bunker\nRecover",
                StatsBlock = "ATK: 13605 000\nDEF: 31909006\nSPD: 69 02 000\nSPATK: 117 06 000\nSPDEF: 38801 252\nHP: 01 262"
            };

            ImportedPokemon result = Parse(texts);

            Assert.Equal("Toxapex", result.SpeciesName);
            Assert.Equal("Sassy", result.NatureName);
            Assert.Equal("Regenerator", result.AbilityName);
            Assert.Equal("92446959", result.GameId);
            Assert.Equal(new[] { "Infestation", "Toxic", "Baneful Bunker", "Recover" },
                result.MoveNames);
            Assert.Equal(new[] { 1, 5, 9, 6, 1, 2 }, result.Ivs);
            Assert.Equal(new[] { 252, 0, 6, 0, 252, 0 }, result.Evs);
            Assert.Equal("ev-fixed", result.RowStatus["hp"]);
            Assert.Contains(result.Notes, n => n.Contains("hp:"));
        }

        [Fact]
        public void RealCard_Toxapex_SurvivesAMangledTitle()
        {
            // Screenshot s7: the title read "Toxapex ? Le 100) Kanto IC
            // 92446969" and the ID candidate carried junk digits from the
            // "ID:" glyphs - the parser still lands the build; the id is
            // best-effort (storage dedupes on the exact build either way).
            var texts = new CardOcrTexts
            {
                Title = "Toxapex ? Le 100) Kanto IC 92446969",
                IdCandidates = { "nto) ID192446559" },
                HpCandidates = { "4274", "274/2743", "2741274", "274/274" },
                Ability = "Regenerator",
                Nature = "Sassy",
                MovesBlock = "Infestation\nToxic\n\nBaneful Bunker\nRecover",
                StatsBlock = "ATK: 136 05 000\nDEF: 31909006\nSPD: 69 02 000\nSPATK: 11706 000\nSPDEF: 38801 252\nHP: 01 252"
            };

            ImportedPokemon result = Parse(texts);

            Assert.Equal("Toxapex", result.SpeciesName);
            Assert.Equal(100, result.Level);
            Assert.Equal("Sassy", result.NatureName);
            Assert.Equal(new[] { 1, 5, 9, 6, 1, 2 }, result.Ivs);
            Assert.Equal(new[] { 252, 0, 6, 0, 252, 0 }, result.Evs);
            Assert.Equal("92446559", result.GameId);
            Assert.All(result.RowStatus.Values, status => Assert.Equal("clean", status));
        }

        // ---- the repair machinery ----

        [Fact]
        public void VerifyRow_SolvesAMisreadIvFromTheStat()
        {
            // Toxapex SPD at level 100, Sassy (0.9): only IV 2 with EV 0
            // lands on 69, so a misread IV of 42 is pinned back uniquely.
            (int iv, int ev, string status) = CardImportParser.VerifyRow(
                baseStat: 35, level: 100, natureMod: 0.9,
                stat: 69, iv: 42, ev: 0, isHp: false, hpMax: null);

            Assert.Equal(2, iv);
            Assert.Equal(0, ev);
            Assert.Equal("iv-fixed", status);
        }

        [Fact]
        public void VerifyRow_RepairsAnEvDigitFromTheStat()
        {
            // Volcarona SPD: IV 31 EV 252 -> 328. An EV misread of 262
            // fails the equation; the digit-confusion table recovers 252.
            (int iv, int ev, string status) = CardImportParser.VerifyRow(
                baseStat: 100, level: 100, natureMod: 1.1,
                stat: 328, iv: 31, ev: 262, isHp: false, hpMax: null);

            Assert.Equal(31, iv);
            Assert.Equal(252, ev);
            Assert.Equal("ev-fixed", status);
        }

        [Fact]
        public void VerifyRow_CapsAnImpossibleHpEvWithoutTheBar()
        {
            (int iv, int ev, string status) = CardImportParser.VerifyRow(
                baseStat: 50, level: 100, natureMod: 1.0,
                stat: 0, iv: 1, ev: 262, isHp: true, hpMax: null);

            Assert.Equal(1, iv);
            Assert.Equal(252, ev);
            Assert.Equal("ev-capped", status);
        }

        [Fact]
        public void Fuzzy_SnapsOcrNoiseToTheDictionaries()
        {
            Assert.Equal("Timid", CardImportParser.Match("Tirnid", Enum.GetNames<Nature>()));
            Assert.Equal("Toxapex", CardImportParser.Match("Toxa pex", new[] { "Toxapex", "Toxicroak" }));
            Assert.Equal("Regenerator", CardImportParser.Match("Regenerstor", AbilityDictionary));
            Assert.Null(CardImportParser.Match("Completely Unrelated", AbilityDictionary));
        }

        [Fact]
        public void HiddenPower_TypedIsExact_AndFallsBackToTheBaseMove()
        {
            var texts = new CardOcrTexts
            {
                Title = "Volcarona Lv. 100 Johto ID: 11111111",
                HpCandidates = { "303/303" },
                Ability = "Swarm",
                Nature = "Timid",
                MovesBlock = "Hidden Power Ground",
                StatsBlock = "ATK: 126 16 000\nDEF: 164 28 006\nSPD: 328 31 252\nSPATK: 369 31 252\nSPDEF: 246 31 000\nHP: 23 000"
            };

            ImportedPokemon result = Parse(texts);

            // "(Ground)" has no typed entry in the dictionary - the parser
            // must NOT fuzz it onto "(Ice)"; it falls back to the plain
            // move with a note.
            Assert.Equal(new[] { "Hidden Power" }, result.MoveNames);
            Assert.Contains(result.Notes, n => n.Contains("Hidden Power (Ground)"));
        }

        [Fact]
        public void OfficialFormulas_MatchTheKnownCards()
        {
            // The exact numbers both screenshots show.
            Assert.Equal(126, CardImportParser.OfficialStat(60, 16, 0, 100, 0.9));
            Assert.Equal(328, CardImportParser.OfficialStat(100, 31, 252, 100, 1.1));
            Assert.Equal(303, CardImportParser.OfficialHp(85, 23, 0, 100));
            Assert.Equal(388, CardImportParser.OfficialStat(142, 1, 252, 100, 1.1));
            Assert.Equal(69, CardImportParser.OfficialStat(35, 2, 0, 100, 0.9));
            Assert.Equal(274, CardImportParser.OfficialHp(50, 1, 252, 100));
        }

        // ---- the trusted team-build path ----

        static TeamSlotPlan ToxapexPlan()
        {
            var plan = new TeamSlotPlan
            {
                SpeciesName = "Toxapex",
                Level = 100,
                Nature = Nature.Sassy,
                AbilityName = "Regenerator",
                Trusted = true,
                Ivs = new[] { 1, 5, 9, 6, 1, 2 },
                Evs = new[] { 252, 0, 6, 0, 252, 0 }
            };

            plan.MoveNames.AddRange(new[] { "Infestation", "Toxic", "Baneful Bunker", "Recover" });
            return plan;
        }

        [Fact]
        public void TrustedBuild_AppliesTheRealSpread_AndMirrorsTheCardsNumbers()
        {
            MoveDex.EnsureLoaded();

            TeamBuildResult result = TeamBuilder.Build(new[] { ToxapexPlan() }, new TwoSpeciesSource());

            Assert.True(result.Ok, string.Join(" | ", result.Errors));

            PokemonState mon = result.Team[0];

            Assert.Equal(274, mon.MaxHP);
            Assert.Equal(319, mon.Stats.Defense);
            Assert.Equal(388, mon.Stats.SpDefense);
            Assert.Equal(69, mon.Stats.Speed);
            Assert.Equal("regenerator", mon.AbilityId);
            Assert.Equal(4, mon.Moves.Count);
            Assert.Contains(mon.Moves, m => m.Name == "Baneful Bunker");
        }

        [Fact]
        public void TrustedBuild_DropsAnUnknownMoveWithAWarning_InsteadOfFailing()
        {
            MoveDex.EnsureLoaded();

            TeamSlotPlan plan = ToxapexPlan();
            plan.MoveNames.Clear();
            plan.MoveNames.AddRange(new[] { "Recover", "Purifying Spring Water" });

            TeamBuildResult result = TeamBuilder.Build(new[] { plan }, new TwoSpeciesSource());

            Assert.True(result.Ok);
            Assert.Single(result.Team[0].Moves);
            Assert.Contains(result.Warnings, w => w.Contains("Purifying Spring Water") && w.Contains("left off"));
        }

        [Fact]
        public void TrustedBuild_AllowsTheCardsAbility_EvenWithoutCatalogData()
        {
            MoveDex.EnsureLoaded();

            // The stub source lists no abilities for anyone - the §162
            // trusted path takes the card's word for it anyway.
            TeamBuildResult result = TeamBuilder.Build(new[] { ToxapexPlan() }, new TwoSpeciesSource());

            Assert.True(result.Ok);
            Assert.DoesNotContain(result.Errors, e => e.Contains("cannot have the ability"));
        }

        [Fact]
        public void UntrustedBuild_StillEnforcesTheOldGates()
        {
            MoveDex.EnsureLoaded();

            TeamSlotPlan plan = ToxapexPlan();
            plan.Trusted = false;

            TeamBuildResult result = TeamBuilder.Build(new[] { plan }, new TwoSpeciesSource());

            // The stub learnset is empty, so every move now fails the gate.
            Assert.False(result.Ok);
        }

        [Fact]
        public void Factory_AppliesIvsAndEvsBeforeComputingStats()
        {
            var species = new PokemonSpecies
            {
                Name = "Toxapex",
                Types = new List<PokemonType> { PokemonType.Poison, PokemonType.Water },
                BaseStats = new Stats
                {
                    HP = 50, Attack = 63, Defense = 152,
                    SpAttack = 53, SpDefense = 142, Speed = 35
                },
                Learnset = new List<MoveState>(),
                Abilities = new List<string>()
            };

            PokemonState mon = PokemonFactory.Create(
                species, 100, Nature.Sassy, null,
                new List<MoveState> { Move("Poke") },
                ivs: new[] { 1, 5, 9, 6, 1, 2 },
                evs: new[] { 252, 0, 6, 0, 252, 0 });

            Assert.Equal(274, mon.MaxHP);
            Assert.Equal(388, mon.Stats.SpDefense);
            Assert.Equal(1, mon.HPIV);
            Assert.Equal(252, mon.HPEV);
        }

        // ---- Baneful Bunker in battle ----

        [Fact]
        public void BanefulBunker_BlocksAndPoisonsOnContact()
        {
            var toxapex = Mon("Toxapex", type: PokemonType.Poison, hp: 300, speed: 200,
                moves: WithEffects(Move("Baneful Bunker", category: MoveCategory.Status, power: 0), "BanefulBunker"));

            var puncher = Mon("Puncher", hp: 300, speed: 10, moves: Move("Punch", power: 60));
            puncher.Moves[0].IsContact = true;

            var (state, engine) = Duel(178, toxapex, puncher);

            Clash(state, engine);

            Assert.Equal(300, toxapex.CurrentHP);
            Assert.Equal(StatusCondition.Poison, puncher.Status);
            Assert.True(LogContains(state, "protected itself!"));
        }

        [Fact]
        public void BanefulBunker_DoesNotPoisonWithoutContact()
        {
            var toxapex = Mon("Toxapex", type: PokemonType.Poison, hp: 300, speed: 200,
                moves: WithEffects(Move("Baneful Bunker", category: MoveCategory.Status, power: 0), "BanefulBunker"));

            var gunner = Mon("Gunner", hp: 300, speed: 10,
                moves: Move("Pebble", power: 60));   // not a contact move

            var (state, engine) = Duel(179, toxapex, gunner);

            Clash(state, engine);

            Assert.Equal(300, toxapex.CurrentHP);
            Assert.Equal(StatusCondition.None, gunner.Status);
        }

        [Fact]
        public void BanefulBunker_IsInTheMoveData()
        {
            MoveDex.EnsureLoaded();

            Assert.True(MoveDex.TryGet("Baneful Bunker", out MoveState move));
            Assert.Equal(4, move.Priority);
            Assert.Equal(MoveCategory.Status, move.Category);
            Assert.Contains("BanefulBunker", move.Effects!);
        }

        static MoveState WithEffects(MoveState move, params string[] effects)
        {
            move.Effects = effects.ToList();
            return move;
        }
    }
}