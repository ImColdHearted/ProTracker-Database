using System;
using System.Collections.Generic;
using System.Linq;

namespace PokemonSim.Simulation
{
    /// <summary>
    /// §327. Which competitive tiers a random team may be drawn from.
    ///
    /// WHY THIS EXISTS. §178 started building random opponents out of real
    /// published sets, and every format that was imported went into one
    /// pool - OU, UU, RU, NU and Ubers together. That pool contains 565
    /// species, and 71 of them have sets in NOTHING but Ubers: Rayquaza,
    /// Arceus, Crowned Zacian, Dialga, Chien-Pao. A random battle could
    /// therefore be a UU wall against a box legendary, which is not a
    /// position any amount of skill is meant to survive.
    ///
    /// That matters for the AI twice over. The corpus it learns from is
    /// made of these battles, so a third of what it sees is decided by the
    /// matchup rather than by play; and the win rate it is measured by is
    /// computed on the same teams, so the measurement inherits the same
    /// noise. Narrowing the default to OU and UU is not about realism, it
    /// is about the battles being winnable by playing well.
    ///
    /// Nothing is removed. Every format that was imported is still there
    /// and still selectable - the panels offer the whole range, and All
    /// restores exactly what §178 did, learnset fallback included.
    /// </summary>
    public sealed class TierFilter
    {
        readonly HashSet<string> tiers;

        TierFilter(string name, IEnumerable<string> allowed)
        {
            Name = name;
            tiers = new HashSet<string>(allowed, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>What the panels call it.</summary>
        public string Name { get; }

        /// <summary>The tiers, lower case, in the order they are named.
        /// Empty for the unrestricted filter.</summary>
        public IReadOnlyList<string> Tiers => tiers.OrderBy(t => t, StringComparer.Ordinal).ToList();

        /// <summary>
        /// §327. Everything, and behaving exactly as the code did before
        /// this section: any species may be drawn, any set may be used,
        /// and a species with no published set still falls back to
        /// sampling its learnset.
        ///
        /// The empty set means "no restriction" rather than "nothing
        /// allowed", which is worth stating plainly because the opposite
        /// reading would silently make every team empty.
        /// </summary>
        public static readonly TierFilter All = new TierFilter("Every format", Array.Empty<string>());

        /// <summary>§327. The default. 352 of the 565 species with
        /// published sets, and none of the 71 that are Ubers-only.</summary>
        public static readonly TierFilter Standard = new TierFilter("OU and UU", new[] { "ou", "uu" });

        /// <summary>The narrowest useful pool - 240 species.</summary>
        public static readonly TierFilter OverUsedOnly = new TierFilter("OU only", new[] { "ou" });

        /// <summary>Everything but Ubers - 494 species. Wider play without
        /// the box legendaries.</summary>
        public static readonly TierFilter NoUbers =
            new TierFilter("OU, UU, RU and NU", new[] { "ou", "uu", "ru", "nu" });

        /// <summary>The choices the panels offer, in the order they offer
        /// them - Standard first because it is the default.</summary>
        public static readonly TierFilter[] Choices =
        {
            Standard,
            OverUsedOnly,
            NoUbers,
            All
        };

        public static TierFilter At(int index) =>
            index >= 0 && index < Choices.Length ? Choices[index] : Standard;

        public static int IndexOf(TierFilter filter)
        {
            for (int i = 0; i < Choices.Length; i++)
            {
                if (ReferenceEquals(Choices[i], filter))
                    return i;
            }

            return 0;
        }

        /// <summary>True when nothing is being excluded.</summary>
        public bool IsUnrestricted => tiers.Count == 0;

        /// <summary>Whether a Smogon format string - "gen9ou", "gen7uu" -
        /// belongs to this filter.</summary>
        public bool Allows(string? format) =>
            IsUnrestricted || tiers.Contains(TierOf(format));

        /// <summary>
        /// §327. The tier out of a Smogon format name: "gen9ou" is "ou".
        ///
        /// Written as "strip a leading gen and its digits" rather than as a
        /// list of known formats, so a generation nobody has imported yet -
        /// gen10ou - is understood the day it arrives rather than silently
        /// excluded from every filter.
        /// </summary>
        public static string TierOf(string? format)
        {
            if (string.IsNullOrWhiteSpace(format))
                return "";

            string text = format.Trim().ToLowerInvariant();

            if (!text.StartsWith("gen", StringComparison.Ordinal))
                return text;

            int at = 3;

            while (at < text.Length && char.IsDigit(text[at]))
                at++;

            return at >= text.Length ? "" : text.Substring(at);
        }

        public override string ToString() => Name;
    }
}
