using System;
using System.Collections.Generic;
using System.Linq;
using PokemonSim.Engine;
using PokemonSim.Models;
using PokemonSim.Simulation;
using Xunit;

namespace PokemonSim.Tests
{
    /// <summary>Section 166: the whole card read hardened the way 165
    /// hardened the HP row - fuzzy row labels, junk-tolerant separators,
    /// glyph confusions inside digits, a second stats-block read, HP-bar
    /// candidates with the junk-slash pattern, level and id voting, and
    /// the ability supplement. Every literal below is lifted from the
    /// user's real captures.</summary>
    public class Section166Tests
    {
        sealed class ThreeSpeciesSource : ISpeciesSource
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
                ["Gengar"] = Make("Gengar", 60, 65, 60, 130, 75, 110),
                ["Hydreigon"] = Make("Hydreigon", 92, 105, 90, 125, 90, 98),
                ["Amoonguss"] = Make("Amoonguss", 114, 85, 70, 85, 80, 30)
            };

            public IReadOnlyList<string> AllSpeciesNames => species.Keys.OrderBy(n => n).ToList();

            public SpeciesInfo? Find(string name) =>
                species.TryGetValue(name, out SpeciesInfo? info) ? info : null;
        }

        // The real Gengar card (Timid, level 100) minus its speed row -
        // each fact below supplies that row in some corrupted form.
        const string GengarRowsNoSpeed =
            "ATK: 145 27 000\nDEF: 149 24 000\nSPATK: 348 20 252\nSPDEF: 171 15 006\nHP: 15 000";

        static CardOcrTexts GengarTexts(string statsBlock)
        {
            return new CardOcrTexts
            {
                Title = "Gengar Lv. 100",
                Ability = "Cursed Body",
                Nature = "Timid",
                MovesBlock = "Recover",
                StatsBlock = statsBlock,
                HpCandidates = { "245/245" }
            };
        }

        static ImportedPokemon Parse(CardOcrTexts texts) =>
            CardImportParser.Parse(texts, new ThreeSpeciesSource(),
                new[] { "Recover" }, new[] { "Flame Body", "Regenerator", "Cursed Body", "Levitate" });

        [Fact]
        public void AMisreadLabel_StillNamesItsRow()
        {
            // "SPD" read as "SPO" - one edit away from exactly one label.
            ImportedPokemon mon = Parse(GengarTexts(GengarRowsNoSpeed + "\nSPO: 349 30 252"));

            Assert.Equal(30, mon.Ivs[5]);
            Assert.Equal(252, mon.Evs[5]);
            Assert.Equal("clean", mon.RowStatus["speed"]);
        }

        [Fact]
        public void LeadingJunkAndASemicolon_DoNotDropTheRow()
        {
            // "S" read as "5" and ":" as ";" - the old row regex dropped
            // this line whole, which is how speed rows went missing.
            ImportedPokemon mon = Parse(GengarTexts(GengarRowsNoSpeed + "\n5PD; 34930 252"));

            Assert.Equal(30, mon.Ivs[5]);
            Assert.Equal(252, mon.Evs[5]);
            Assert.Equal("clean", mon.RowStatus["speed"]);
        }

        [Fact]
        public void ASecondBlockRead_HealsADroppedRow()
        {
            CardOcrTexts texts = GengarTexts(GengarRowsNoSpeed);
            texts.StatsBlockCandidates.Add(GengarRowsNoSpeed + "\nSPD: 349 30 252");

            ImportedPokemon mon = Parse(texts);

            Assert.Equal(30, mon.Ivs[5]);
            Assert.Equal(252, mon.Evs[5]);
            Assert.Equal("clean", mon.RowStatus["speed"]);
        }

        [Fact]
        public void GlyphConfusions_AndTheJunkSlashBar_StillPinTheHpRow()
        {
            // The real Amoonguss capture: "HP: 2? 262" (? is 7, 262 is a
            // misread 252) and an HP bar that read "4281428" - 428 either
            // side of a slash-turned-glyph. Both repairs land exactly.
            var texts = new CardOcrTexts
            {
                Title = "Amoonguss Lv. 100",
                Ability = "Regenerator",
                Nature = "Bold",
                MovesBlock = "Recover",
                StatsBlock = "ATK: 173 18 000\nDEF: 254 23 252\nSPD: 95 30 000\nSPATK: 204 29 000\nSPDEF: 196 30 006\nHP: 2? 262",
                HpCandidates = { "4281428" }
            };

            ImportedPokemon mon = Parse(texts);

            Assert.Equal(27, mon.Ivs[0]);
            Assert.Equal(252, mon.Evs[0]);
            Assert.Equal("ev-fixed", mon.RowStatus["hp"]);
            Assert.Equal("clean", mon.RowStatus["defense"]);
        }

        static CardOcrTexts HydreigonTexts(string attackRow)
        {
            return new CardOcrTexts
            {
                Title = "Hydreigon Lv. 100",
                Ability = "Levitate",
                Nature = "Modest",
                MovesBlock = "Recover",
                StatsBlock = attackRow + "\nDEF: 210 25 000\nSPD: 289 25 252\nSPATK: 378 26 252\nSPDEF: 215 30 000\nHP: 25 006",
                HpCandidates = { "320/320" }
            };
        }

        [Fact]
        public void AReadableStat_RepairsAnImpossibleIv()
        {
            // The stat digits (209) survived, so the formula pins the true
            // IV back from a misread 58 - repair beats rejection.
            ImportedPokemon mon = Parse(HydreigonTexts("ATK: 209 58 000"));

            Assert.Equal(18, mon.Ivs[1]);
            Assert.Equal(0, mon.Evs[1]);
            Assert.Equal("iv-fixed", mon.RowStatus["attack"]);
        }

        [Fact]
        public void AnUnverifiableImpossibleRow_FallsToTheHonestAssumption()
        {
            // Stat AND iv both junk: nothing can confirm the row, and an
            // IV of 58 must never be shown as fact.
            ImportedPokemon mon = Parse(HydreigonTexts("ATK: 999 58 000"));

            Assert.Equal(31, mon.Ivs[1]);
            Assert.Equal(0, mon.Evs[1]);
            Assert.Equal("missing", mon.RowStatus["attack"]);
            Assert.Contains(mon.Notes, n => n.Contains("attack row read impossibly"));
        }

        [Fact]
        public void TheLevelFallback_IgnoresStrayGlyphNumbers()
        {
            // The real Slowking title shape: a gender glyph read as "1)"
            // sits before the level. First-number picking made it level 1.
            var texts = GengarTexts(GengarRowsNoSpeed);
            texts.Title = "Gengar \u00a2 1) 100) Kanto ID 39475388";

            ImportedPokemon mon = Parse(texts);

            Assert.Equal("Gengar", mon.SpeciesName);
            Assert.Equal(100, mon.Level);
        }

        [Fact]
        public void TheId_IsVotedAcrossEverySource()
        {
            // The real Charizard reads: two sources dropped the lead "2",
            // the title misread one digit - and only the true id contains
            // the shorter runs, so the substring vote picks it.
            var texts = GengarTexts(GengarRowsNoSpeed);
            texts.Title = "Charizard \u00a2 100) Hoenn ID 22133875";
            texts.TitleCandidates.Add("Charizard \u00a2 100) Hoenn IO: 22133874");
            texts.TitleCandidates.Add("Charizara Clv100 Hoenn (p222133675");
            texts.IdCandidates.Add("oenn) IDs2213367,5");
            texts.IdCandidates.Add("Hoenn IDs221 3367.5\"");

            ImportedPokemon mon = Parse(texts);

            Assert.Equal("22133675", mon.GameId);
        }

        [Fact]
        public void AMissingCatalogAbility_ComesFromTheSupplement()
        {
            // The real Solgaleo failure: "Full Metal Body" read perfectly,
            // was absent from the catalog, and fuzzy-snapped onto "Flame
            // Body". The supplement carries it now.
            var texts = GengarTexts(GengarRowsNoSpeed);
            texts.Ability = "Full Metal Body";

            ImportedPokemon mon = Parse(texts);

            Assert.Equal("Full Metal Body", mon.AbilityName);
        }

        [Fact]
        public void ASecondTitleRead_RescuesTheSpecies()
        {
            var texts = GengarTexts(GengarRowsNoSpeed);
            texts.Title = "xX 100";
            texts.TitleCandidates.Add("Gengar Lv 100");

            ImportedPokemon mon = Parse(texts);

            Assert.Equal("Gengar", mon.SpeciesName);
            Assert.Equal(100, mon.Level);
        }
    }
}