using System;
using System.Collections.Generic;
using System.Linq;
using Foot_Tracker.Services;

namespace Foot_Tracker.ViewModels;

/// <summary>
/// §272. The forms/event-skins column, in one place, because two windows show
/// it now: "Set Target" (PokemonSelectorWindow), where it has always sat beside
/// the search results, and the left-click replace window
/// (SwapPokemonWindow), which gained it here.
///
/// §271 put this behind a right-click on the target sprite instead, in a
/// window of its own. It worked and nobody would ever have found it: the
/// column listing exactly these sprites was already on screen in the window
/// where targets are set, doing nothing when clicked. A second window for a
/// choice that belongs next to the choice it modifies is a worse answer than
/// making the cards that are already there work, so the separate picker is
/// gone and the cards are live.
///
/// What a click does depends on which card it is, and that is the whole rule:
///
///   - A counterpart card (it has an image in the catalog) draws that target
///     with that sprite.
///   - The "Normal" card puts it back to the species sprite.
///   - A regional form, Mega or G-Max is a DIFFERENT Pokemon to hunt, not a
///     skin for this one, so it changes no picture and says where to pick it
///     instead. Those cards are informational, as they were before this
///     section, and silently doing nothing would read as a bug.
/// </summary>
internal static class TargetSkinPicker
{
    /// <summary>The forms column for one species: its own sprite first, then
    /// its regional/Mega forms, then every counterpart the catalog has for it.
    /// Lifted verbatim out of PokemonSelectorViewModel.LoadForms so the swap
    /// window shows the same column rather than a second opinion of it.</summary>
    public static List<PokemonCardItem> BuildForms(string pokemonName)
    {
        var cards = new List<PokemonCardItem>();

        if (string.IsNullOrWhiteSpace(pokemonName))
            return cards;

        string? currentSkin = TargetSpriteService.SkinFor(pokemonName);

        var species = PokemonSpriteService.AllPokemon
            .FirstOrDefault(p => string.Equals(p.Name, pokemonName, StringComparison.OrdinalIgnoreCase));

        if (species is not null)
        {
            cards.Add(new PokemonCardItem(
                species.Name, PokemonSpriteService.GetSprite(species.Name), "Normal")
            {
                IsSelected = currentSkin is null,
            });

            AddShinyCard(cards, species.Name, currentSkin);

            foreach (var form in PokemonSpriteService.GetFormsForSpecies(species.Name).Take(30))
            {
                cards.Add(new PokemonCardItem(
                    form.Name, PokemonSpriteService.GetEncounterSprite(form.Name), GetFormTag(form.Name)));
            }
        }
        else
        {
            // A regional form picked directly - it is its own species here, so
            // it gets its own Normal card and its own counterparts.
            cards.Add(new PokemonCardItem(
                pokemonName, PokemonSpriteService.GetEncounterSprite(pokemonName), "Normal")
            {
                IsSelected = currentSkin is null,
            });

            AddShinyCard(cards, pokemonName, currentSkin);
        }

        string listFor = species?.Name ?? pokemonName;

        foreach (var counterpart in CounterpartSpriteService.GetForPokemon(listFor).Take(50))
        {
            // The CARD sprite, not the raw image: these are the pictures the
            // target box will actually draw (§139), so the choice is between
            // the results rather than between two differently-framed versions
            // of them. A counterpart whose image will not load is left out -
            // it cannot be chosen, so it is not offered.
            var sprite = CounterpartSpriteService.GetCardSprite(counterpart.ImagePath);

            if (sprite is null)
                continue;

            cards.Add(new PokemonCardItem(counterpart.Name, sprite, counterpart.Event, counterpart.ImagePath)
            {
                IsSelected = string.Equals(currentSkin, counterpart.ImagePath, StringComparison.OrdinalIgnoreCase),
            });
        }

        return cards;
    }

    /// <summary>§359. The shiny card, directly under Normal - which is where
    /// it was asked for and where it belongs: it is the same Pokemon, drawn
    /// the other way, not an event skin and not another species.
    ///
    /// Added only when the library actually has the shiny sprite. The one
    /// below it would otherwise be a copy of the one above it that changes
    /// nothing on screen when clicked - see HasShinySprite.
    ///
    /// It carries the reserved skin value rather than an image path, so
    /// Apply treats it like any other skin: a picture for this species, not
    /// a different Pokemon to hunt. Nothing about the hunt changes - the
    /// matcher, the counts and Since Shiny are all untouched, exactly as
    /// §271 says of every skin.</summary>
    private static void AddShinyCard(List<PokemonCardItem> cards, string name, string? currentSkin)
    {
        if (!PokemonSpriteService.HasShinySprite(name))
            return;

        cards.Add(new PokemonCardItem(
            name,
            PokemonSpriteService.GetShinyEncounterSprite(name),
            "Shiny",
            TargetSpriteService.ShinySkin)
        {
            IsSelected = string.Equals(currentSkin, TargetSpriteService.ShinySkin, StringComparison.OrdinalIgnoreCase),
        });
    }

    /// <summary>§361. The most slots one species may hold. Three was asked
    /// for and three is what the column can usefully offer: a form, another
    /// form, and the shiny.</summary>
    public const int MaxPerSpecies = 3;

    /// <summary>
    /// §361. A click on one of those cards ADDS A TARGET SLOT drawn with that
    /// picture, and a click on a card already in the list takes its slot away
    /// again - the same toggle the search grid on the left has always had.
    ///
    /// WHAT THIS REPLACED. §272 made these cards re-skin the one target:
    /// clicking [Halloween] changed what the single Charmander was drawn as.
    /// Hunting Charmander three times - one form, another form, the shiny -
    /// meant searching for Charmander again for each one, and even then all
    /// three slots shared a picture, because the skin was keyed by species.
    /// Nothing is lost by the change: one Halloween slot IS a target drawn
    /// with the Halloween sprite, reached by adding that card instead of
    /// re-skinning the plain one, and now the column has ONE rule instead of
    /// two.
    ///
    /// The picture is written straight to TargetSpriteService as the slot is
    /// added, keyed by species and occurrence, and every slot of that species
    /// is re-numbered afterwards - removing the first of three would
    /// otherwise leave the other two reading the wrong pictures.
    /// </summary>
    public static string Apply(
        string species,
        PokemonCardItem card,
        IEnumerable<PokemonCardItem> column,
        IList<PokemonCardItem> selected,
        int maxTargets)
    {
        if (string.IsNullOrWhiteSpace(species))
            return string.Empty;

        bool normal = card.SkinImagePath is null && string.Equals(card.Tag, "Normal", StringComparison.OrdinalIgnoreCase);

        if (card.SkinImagePath is null && !normal)
        {
            return $"{card.Name} is its own Pokémon to hunt, not a skin for {species} - "
                 + "search for it on the left to add it as a target.";
        }

        string? skin = normal ? null : card.SkinImagePath;

        // Already hunting this species with this exact picture? Then the click
        // means "stop".
        PokemonCardItem? existing = selected.FirstOrDefault(
            s => SameSpecies(s.Name, species)
              && string.Equals(s.SkinImagePath, skin, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            selected.Remove(existing);
            existing.IsSelected = false;
            Renumber(species, selected);
            MarkColumn(column, selected, species);

            return $"{species} is no longer being hunted with its {Describe(card, normal)} sprite.";
        }

        int mine = selected.Count(s => SameSpecies(s.Name, species));

        if (mine >= MaxPerSpecies)
        {
            return $"{species} can be hunted {MaxPerSpecies} ways at once - "
                 + "click one of its cards again to drop it first.";
        }

        if (selected.Count >= maxTargets)
        {
            return $"You can hunt up to {maxTargets} at once - deselect one first.";
        }

        var slot = new PokemonCardItem(species, card.Sprite, card.Tag, skin) { IsSelected = true };

        selected.Add(slot);
        Renumber(species, selected);
        MarkColumn(column, selected, species);

        return $"{species} will be hunted with its {Describe(card, normal)} sprite.";
    }

    /// <summary>
    /// §361. The swap window's click, which is NOT the picker's.
    ///
    /// That window is opened by clicking one target sprite and it replaces
    /// that one target; there is no selection list there to add a slot to, so
    /// a click there still means what §272 made it mean - draw this target
    /// with that picture. It restyles the FIRST slot of the species, because
    /// the window is handed a name and not a position
    /// (MainWindow.axaml.cs builds it from currentName alone).
    ///
    /// The consequence, stated rather than hidden: a species held in more
    /// than one slot is restyled from Set Target, where the slots are
    /// visible and each has its own card. Giving this window the occurrence
    /// as well is a change to how it is opened, and worth doing only if
    /// restyling a duplicated species from here turns out to be something
    /// anybody reaches for.
    /// </summary>
    public static string ApplyReskin(string species, PokemonCardItem card, IEnumerable<PokemonCardItem> column)
    {
        if (string.IsNullOrWhiteSpace(species))
            return string.Empty;

        bool normal = card.SkinImagePath is null && string.Equals(card.Tag, "Normal", StringComparison.OrdinalIgnoreCase);

        if (card.SkinImagePath is null && !normal)
        {
            return $"{card.Name} is its own Pokémon to hunt, not a skin for {species} - "
                 + "search for it on the left to swap to it.";
        }

        TargetSpriteService.Set(species, normal ? null : card.SkinImagePath);

        foreach (PokemonCardItem other in column)
        {
            other.IsSelected = ReferenceEquals(other, card);
        }

        return normal
            ? $"{species} will be drawn with its normal sprite."
            : $"{species} will be drawn with its {card.Tag} sprite.";
    }

    /// <summary>§361. Writes every slot of one species to
    /// TargetSpriteService in the order it will be drawn. Run after ANY add
    /// or remove: the occurrence a slot answers to is its position among its
    /// own species, so taking the first of three away moves the other two.
    /// </summary>
    public static void Renumber(string species, IEnumerable<PokemonCardItem> selected)
    {
        int occurrence = 0;

        foreach (PokemonCardItem slot in selected.Where(s => SameSpecies(s.Name, species)))
        {
            TargetSpriteService.Set(species, slot.SkinImagePath, occurrence);
            occurrence++;
        }

        // The slots that used to exist past the end have to stop answering, or
        // a species dropped from three to two keeps the third one's picture
        // waiting for the next time it is hunted.
        for (int stale = occurrence; stale < MaxPerSpecies; stale++)
        {
            TargetSpriteService.Set(species, null, stale);
        }
    }

    /// <summary>Outlines every card in the column that currently holds a
    /// slot, so the panel shows what is being hunted rather than one
    /// "current" choice.</summary>
    private static void MarkColumn(
        IEnumerable<PokemonCardItem> column, IEnumerable<PokemonCardItem> selected, string species)
    {
        foreach (PokemonCardItem other in column)
        {
            bool otherNormal = other.SkinImagePath is null
                && string.Equals(other.Tag, "Normal", StringComparison.OrdinalIgnoreCase);

            other.IsSelected = (other.SkinImagePath is not null || otherNormal)
                && selected.Any(s => SameSpecies(s.Name, species)
                                  && string.Equals(s.SkinImagePath, other.SkinImagePath,
                                                   StringComparison.OrdinalIgnoreCase));
        }
    }

    private static string Describe(PokemonCardItem card, bool normal) =>
        normal ? "normal" : card.Tag ?? "chosen";

    // The picker's own comparison, deliberately not TargetSpriteService's:
    // these are names the picker itself produced from one catalog, so they
    // match or they are different species.
    private static bool SameSpecies(string? a, string? b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    internal static string GetFormTag(string name)
    {
        if (name.Contains("Alolan", StringComparison.OrdinalIgnoreCase) || name.Contains("Alola", StringComparison.OrdinalIgnoreCase))
            return "Alolan";
        if (name.Contains("Galarian", StringComparison.OrdinalIgnoreCase) || name.Contains("Galar", StringComparison.OrdinalIgnoreCase))
            return "Galarian";
        if (name.Contains("Hisuian", StringComparison.OrdinalIgnoreCase) || name.Contains("Hisui", StringComparison.OrdinalIgnoreCase))
            return "Hisuian";
        if (name.Contains("Mega", StringComparison.OrdinalIgnoreCase))
            return "Mega";
        if (name.Contains("Gmax", StringComparison.OrdinalIgnoreCase))
            return "G-Max";
        return "Form";
    }
}
