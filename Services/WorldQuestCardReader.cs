using System;
using System.Collections.Generic;
using System.Linq;
using Foot_Tracker.Services.Simulator;
using Foot_Tracker.Tracking;
using PokemonSim.Data;
using PokemonSim.Simulation;
using Serilog;
using SkiaSharp;

namespace Foot_Tracker.Services;

/// <summary>§258. What a summary-card screenshot came to, for World Quest.</summary>
internal enum WorldQuestCardOutcome
{
    /// <summary>No PRO summary card in the image at all.</summary>
    NoCard,

    /// <summary>A card, but its title is not the quest's species (or did
    /// not read as any species).</summary>
    WrongSpecies,

    /// <summary>The quest's species, but fewer than
    /// <see cref="WorldQuestCardReader.MinimumRowsRead"/> IV rows read -
    /// too little to build a total on.</summary>
    TooFewRows,

    /// <summary>At least a majority of the six IV rows read. When all six
    /// did, <see cref="WorldQuestCardReading.Complete"/> is true and
    /// <see cref="WorldQuestCardReading.ReadTotal"/> is the catch's total;
    /// otherwise it is the sum of the rows that read, and
    /// <see cref="WorldQuestCardReading.Missing"/> names the ones the
    /// player has to add by hand.</summary>
    Read,
}

/// <summary>§258. One summary card as far as World Quest cares: the species
/// the title named, the six IVs in card order (null where a row did not
/// read), and their sum so far.</summary>
internal sealed record WorldQuestCardReading(
    WorldQuestCardOutcome Outcome,
    string Species,
    IReadOnlyList<int?> Ivs,
    int ReadTotal,
    int ReadCount,
    IReadOnlyList<string> Missing)
{
    public bool Complete => Outcome == WorldQuestCardOutcome.Read && ReadCount == 6;

    internal static WorldQuestCardReading Refused(WorldQuestCardOutcome outcome, string species = "") =>
        new(outcome, species, Array.Empty<int?>(), 0, 0, Array.Empty<string>());
}

/// <summary>
/// §258. Reads a World Quest catch's IVs off a screenshot of the Pokemon's
/// SUMMARY CARD - the one the simulator's importer (§162) already finds and
/// OCRs - as the second thing Submit Screenshot tries when there is no catch
/// preview in the image.
///
/// The pixel and text halves are the importer's own, untouched:
/// <see cref="CardOcrService"/> finds the card by its three header wedges and
/// crops the stats block, <see cref="CardImportParser"/> turns that into an
/// <see cref="ImportedPokemon"/>. What this class adds is the reading the
/// quest needs and the importer does not give:
///
/// The importer ASSUMES IV 31 for a row it could not read, and says so in a
/// note - the right default for a battle sim, where a missing IV should not
/// block the import, and exactly the wrong one for a quest total, where it
/// would silently inflate the number. So every row is taken from
/// <see cref="ImportedPokemon.RowStatus"/> instead: a row marked
/// <c>missing</c> is null here, never 31, and the total is the sum of the
/// rows that actually read. A majority of rows (four of six) must read for
/// the card to count at all; below that the reading is refused rather than
/// handed to the player as a number to fix.
///
/// The species gate is the preview detector's own (§233 gate 3), so a card
/// for something other than the quest's species is refused the same way a
/// preview of one is.
/// </summary>
internal static class WorldQuestCardReader
{
    /// <summary>Four of six: a majority. Below this the card is refused
    /// outright; at or above it, the rows that read are summed and the
    /// rest are named for the player to add by hand.</summary>
    public const int MinimumRowsRead = 4;

    // The parser's row order is HP, Attack, Defense, SpAttack, SpDefense,
    // Speed (ImportedPokemon's summary). These are the labels the card
    // itself prints for those rows, which is what the player reads when
    // told which one to add.
    private static readonly string[] RowLabels = { "HP", "ATK", "DEF", "SPATK", "SPDEF", "SPD" };
    private static readonly string[] RowKeys = { "hp", "attack", "defense", "spAttack", "spDefense", "speed" };

    private static readonly ISpeciesSource Species = new TrackerSpeciesSource();

    /// <summary>Runs off the UI thread; touches no property. Null only when
    /// the image is not a card at all - every other refusal comes back as a
    /// reading with its outcome set, so the caller can say why.</summary>
    public static WorldQuestCardReading? Read(SKBitmap frame, string questSpecies)
    {
        CardFrame? card = CardOcrService.FindCard(frame);

        if (card is null)
            return null;

        CardOcrTexts texts = CardOcrService.ReadCard(frame, card);

        ImportedPokemon parsed = CardImportParser.Parse(
            texts, Species, MoveNames(), AbilityLookupService.AllNames);

        string species = parsed.SpeciesName;

        if (species.Length == 0 ||
            (!string.IsNullOrWhiteSpace(questSpecies) &&
             !PreviewIvDetector.IsQuestSpecies(species, questSpecies)))
        {
            Log.Debug(
                "World Quest card reader: the summary card is {Species}, not the quest's {Quest} - ignoring it.",
                species.Length == 0 ? "(unread)" : species, questSpecies);
            return WorldQuestCardReading.Refused(WorldQuestCardOutcome.WrongSpecies, species);
        }

        var ivs = new int?[6];
        var missing = new List<string>();
        int total = 0;
        int read = 0;

        for (int i = 0; i < 6; i++)
        {
            // A row the parser could not read is "missing" - and carries the
            // assumed 31 it was given for the simulator. Never take that here.
            if (!parsed.RowStatus.TryGetValue(RowKeys[i], out string? status) || status == "missing")
            {
                missing.Add(RowLabels[i]);
                continue;
            }

            int iv = Math.Clamp(parsed.Ivs[i], 0, 31);
            ivs[i] = iv;
            total += iv;
            read++;
        }

        if (read < MinimumRowsRead)
        {
            Log.Debug(
                "World Quest card reader: only {Read} of 6 IV rows read ({Missing} missing) - refusing the card.",
                read, string.Join(", ", missing));
            return new WorldQuestCardReading(
                WorldQuestCardOutcome.TooFewRows, species, ivs, total, read, missing);
        }

        return new WorldQuestCardReading(WorldQuestCardOutcome.Read, species, ivs, total, read, missing);
    }

    // The same dictionary the simulator's importer hands the parser, so a
    // card reads identically in both places.
    private static List<string> MoveNames()
    {
        MoveDex.EnsureLoaded();

        return MoveDex.AllNames()
            .Concat(MoveLookupService.AllMoves.Select(m => m.Name))
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
