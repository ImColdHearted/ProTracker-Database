using System;
using System.Collections.Generic;
using System.Linq;
using PokemonSim.Engine;
using PokemonSim.Models;
using PokemonSim.Simulation;
using Xunit;

namespace PokemonSim.Tests
{
    /// <summary>Section 165: the HP stat row's layered read. The wider
    /// stats crop lives on the pixel side, but the rescue re-read, the
    /// impossible-row rejection and the derive-from-the-bar fallback are
    /// all text-side - pinned here on the real Toxapex card numbers.</summary>
    public class Section165Tests
    {
        sealed class ToxapexSource : ISpeciesSource
        {
            readonly SpeciesInfo info = new()
            {
                Name = "Toxapex",
                Types = new List<string> { "Poison", "Water" },
                BaseStats = new Stats
                {
                    HP = 50, Attack = 63, Defense = 152,
                    SpAttack = 53, SpDefense = 142, Speed = 35
                },
                LearnsetMoveNames = new List<string> { "Recover" }
            };

            public IReadOnlyList<string> AllSpeciesNames => new List<string> { info.Name };

            public SpeciesInfo? Find(string name) =>
                name.Equals(info.Name, StringComparison.OrdinalIgnoreCase) ? info : null;
        }

        // The five non-HP rows of the real card (Sassy, level 100), every
        // one exact under the games' formula so it verifies clean.
        const string RowsWithoutHp =
            "ATK: 136 05 000\nDEF: 319 09 006\nSPD: 069 02 000\nSPATK: 117 06 000\nSPDEF: 388 01 252";

        static CardOcrTexts Texts(string statsBlock, string hpBar, params string[] hpRowReads)
        {
            var texts = new CardOcrTexts
            {
                Title = "Toxapex Lv. 100",
                Ability = "Regenerator",
                Nature = "Sassy",
                MovesBlock = "Recover",
                StatsBlock = statsBlock,
            };

            if (hpBar.Length > 0)
                texts.HpCandidates.Add(hpBar);

            texts.HpRowCandidates.AddRange(hpRowReads);
            return texts;
        }

        static ImportedPokemon Parse(CardOcrTexts texts) =>
            CardImportParser.Parse(texts, new ToxapexSource(),
                new[] { "Recover" }, new[] { "Regenerator" });

        [Fact]
        public void ABlockMissingItsHpRow_IsRescuedByTheBandRead()
        {
            ImportedPokemon mon = Parse(Texts(RowsWithoutHp, "274/274", "HP: 01 252"));

            Assert.Equal(1, mon.Ivs[0]);
            Assert.Equal(252, mon.Evs[0]);
            Assert.Equal("rescued", mon.RowStatus["hp"]);
            Assert.Equal("clean", mon.RowStatus["spDefense"]);
            Assert.Contains(mon.Notes, n => n.Contains("rescued by its own re-read"));
        }

        [Fact]
        public void TheRescueSkipsJunkCandidates()
        {
            // No digits, too many digits, then the real line.
            ImportedPokemon mon = Parse(Texts(RowsWithoutHp, "274/274",
                "|||", "12345678", "HP: 01 252"));

            Assert.Equal(1, mon.Ivs[0]);
            Assert.Equal(252, mon.Evs[0]);
            Assert.Equal("rescued", mon.RowStatus["hp"]);
        }

        [Fact]
        public void WhenEvenTheRescueFails_TheBarDerivesTheRow()
        {
            // (44, 444) cannot be repaired into any legal row that gives a
            // 274 bar - the bar itself takes over. 31 + 132/4 = 64 rebuilds
            // 274 exactly; only the cosmetic split differs from the card.
            ImportedPokemon mon = Parse(Texts(RowsWithoutHp, "274/274", "HP: 44 444"));

            Assert.Equal(31, mon.Ivs[0]);
            Assert.Equal(132, mon.Evs[0]);
            Assert.Equal("derived", mon.RowStatus["hp"]);
            Assert.Equal(274, CardImportParser.OfficialHp(50, mon.Ivs[0], mon.Evs[0], 100));
            Assert.Contains(mon.Notes, n => n.Contains("derived from the 274 HP bar"));
        }

        [Fact]
        public void WithoutABar_ASingleReadIsNotTrusted()
        {
            ImportedPokemon mon = Parse(Texts(RowsWithoutHp, "", "HP: 01 252"));

            Assert.Equal(31, mon.Ivs[0]);
            Assert.Equal(0, mon.Evs[0]);
            Assert.Equal("missing", mon.RowStatus["hp"]);
            Assert.Contains(mon.Notes, n => n.Contains("hp row did not read"));
        }

        [Fact]
        public void WithoutABar_TwoAgreeingReadsAreTrusted()
        {
            ImportedPokemon mon = Parse(Texts(RowsWithoutHp, "",
                "HP: 01 252", "HP 01 252 |"));

            Assert.Equal(1, mon.Ivs[0]);
            Assert.Equal(252, mon.Evs[0]);
            Assert.Equal("rescued", mon.RowStatus["hp"]);
            Assert.Contains(mon.Notes, n => n.Contains("two reads agree"));
        }

        [Fact]
        public void AnImpossibleBlockRow_IsRescuedNotKept()
        {
            // IV 73 cannot exist; with no bar to repair against, the row is
            // treated as unread and the band majority takes over.
            ImportedPokemon mon = Parse(Texts(RowsWithoutHp + "\nHP: 73 000", "",
                "HP 23 000", "HP: 23 000"));

            Assert.Equal(23, mon.Ivs[0]);
            Assert.Equal(0, mon.Evs[0]);
            Assert.Equal("rescued", mon.RowStatus["hp"]);
        }

        [Fact]
        public void DeriveHpFromBar_SplitsTheTotalDeterministically()
        {
            // Total 64: IV filled first, the remainder as EVs.
            Assert.Equal((31, 132), CardImportParser.DeriveHpFromBar(50, 100, 274));

            // Total 23 fits inside the IV alone.
            Assert.Equal((23, 0), CardImportParser.DeriveHpFromBar(85, 100, 303));

            // No legal total reproduces a 150 bar on a 50-base species.
            Assert.Null(CardImportParser.DeriveHpFromBar(50, 100, 150));
        }
    }
}