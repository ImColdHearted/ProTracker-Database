using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foot_Tracker.Services;

namespace Foot_Tracker.ViewModels;

/// <summary>One card in the Simulator's item picker.</summary>
public sealed class SimulatorItemCard
{
    public string Name { get; }
    public Bitmap? Sprite { get; }

    public SimulatorItemCard(string name, Bitmap? sprite)
    {
        Name = name;
        Sprite = sprite;
    }
}

/// <summary>§375. One of the six shelves the picker opens on: a name, the
/// sprite that stands for it on the card, and the items behind it in the
/// order they are shown.</summary>
public sealed class SimulatorItemCategory
{
    public string Name { get; }
    public Bitmap? Face { get; }
    public IReadOnlyList<string> Items { get; }

    /// <summary>The card's tooltip - how many are behind it.</summary>
    public string Tip => $"{Items.Count} items";

    public SimulatorItemCategory(string name, Bitmap? face, IReadOnlyList<string> items)
    {
        Name = name;
        Face = face;
        Items = items;
    }
}

/// <summary>
/// §159. The Simulator's held-item picker - the SimulatorPokemonPicker
/// pattern applied to items: a fixed battle-relevant pool (everything in
/// it actually does something in the engine), a live filter, one click to
/// pick, and a No Item button that confirms with nothing selected. §161:
/// the 24 mega stones and 18 type Z-Crystals joined the pool - a matching
/// stone unlocks the Mega Evolve button in battle, a crystal the Z-Power
/// button.
///
/// §375: the pool is a hundred and forty-seven now (the sixteen other
/// plates, the eighteen Gems, and Focus Band, Shell Bell, Shed Shell, Quick
/// Claw, Razor Fang, Razor Claw, Eject Button and Bright Powder - see
/// HeldItems), and a flat wall of that many cards is not a picker. So it
/// opens on six shelves, as the mockup drew them - Locking Items, Mega
/// Stones, Boosting, Z-Crystals, Berries, Other - each a card with one
/// item's sprite for a face, and shows a shelf's items when one is clicked.
/// The search box still searches everything: typing shows every match
/// across the shelves, clearing it goes back to where it was.
/// </summary>
public sealed partial class SimulatorItemPickerViewModel : ViewModelBase
{
    /// <summary>§375. The six shelves, in the mockup's order, each with the
    /// item whose sprite is on its card and the items behind it. Every name
    /// is implemented by the engine's HeldItems catalog - that is the rule
    /// the footer states, and Section375Tests holds the engine to it.
    /// Within a shelf: the general items first, then the type sets in type
    /// order, then anything that reads as a set of its own.</summary>
    public static readonly IReadOnlyList<(string Name, string Face, string[] Items)> Shelves =
        new (string, string, string[])[]
        {
            ("Locking Items", "Choice Scarf", new[]
            {
                "Choice Band", "Choice Specs", "Choice Scarf", "Assault Vest",
            }),

            ("Mega Stones", "Gengarite", new[]
            {
                "Aerodactylite", "Alakazite", "Blastoisinite", "Blazikenite",
                "Charizardite X", "Charizardite Y", "Diancite", "Galladite",
                "Garchompite", "Gardevoirite", "Gengarite", "Gyaradosite",
                "Heracronite", "Latiasite", "Latiosite", "Lucarionite",
                "Metagrossite", "Mewtwonite X", "Mewtwonite Y", "Salamencite",
                "Slowbronite", "Steelixite", "Swampertite", "Tyranitarite",
            }),

            ("Boosting", "Life Orb", new[]
            {
                "Life Orb", "Expert Belt", "Muscle Band", "Wise Glasses",
                "Light Ball", "Eviolite", "Weakness Policy",
                "Scope Lens", "Razor Claw", "Wide Lens",

                "Silk Scarf", "Charcoal", "Mystic Water", "Magnet", "Miracle Seed",
                "Never-Melt Ice", "Black Belt", "Poison Barb", "Soft Sand", "Sharp Beak",
                "Twisted Spoon", "Silver Powder", "Hard Stone", "Spell Tag", "Dragon Fang",
                "Black Glasses", "Metal Coat",

                "Flame Plate", "Splash Plate", "Zap Plate", "Meadow Plate", "Icicle Plate",
                "Fist Plate", "Toxic Plate", "Earth Plate", "Sky Plate", "Mind Plate",
                "Insect Plate", "Stone Plate", "Spooky Plate", "Draco Plate", "Dread Plate",
                "Iron Plate", "Pixie Plate",

                "Normal Gem", "Fire Gem", "Water Gem", "Electric Gem", "Grass Gem", "Ice Gem",
                "Fighting Gem", "Poison Gem", "Ground Gem", "Flying Gem", "Psychic Gem", "Bug Gem",
                "Rock Gem", "Ghost Gem", "Dragon Gem", "Dark Gem", "Steel Gem", "Fairy Gem",
            }),

            ("Z-Crystals", "Normalium Z", new[]
            {
                "Normalium Z", "Firium Z", "Waterium Z", "Electrium Z",
                "Grassium Z", "Icium Z", "Fightinium Z", "Poisonium Z",
                "Groundium Z", "Flyinium Z", "Psychium Z", "Buginium Z",
                "Rockium Z", "Ghostium Z", "Dragonium Z", "Darkinium Z",
                "Steelium Z", "Fairium Z",
            }),

            ("Berries", "Sitrus Berry", new[]
            {
                "Sitrus Berry", "Lum Berry",

                "Chilan Berry", "Occa Berry", "Passho Berry", "Wacan Berry", "Rindo Berry",
                "Yache Berry", "Chople Berry", "Kebia Berry", "Shuca Berry", "Coba Berry",
                "Payapa Berry", "Tanga Berry", "Charti Berry", "Kasib Berry", "Haban Berry",
                "Colbur Berry", "Babiri Berry", "Roseli Berry",
            }),

            ("Other", "Focus Sash", new[]
            {
                "Leftovers", "Black Sludge", "Shell Bell",
                "Focus Sash", "Focus Band", "Rocky Helmet", "Air Balloon",
                "Eject Button", "Shed Shell", "Quick Claw", "Razor Fang", "Bright Powder",
                "Flame Orb", "Toxic Orb", "Light Clay",
                "Heat Rock", "Damp Rock", "Smooth Rock", "Icy Rock",
            }),
        };

    /// <summary>The whole battle-relevant pool, shelf by shelf - what the
    /// search box searches, and what §159's callers read.</summary>
    public static readonly IReadOnlyList<string> Pool =
        Shelves.SelectMany(shelf => shelf.Items).ToList();

    public ObservableCollection<SimulatorItemCategory> Categories { get; } = new();

    public ObservableCollection<SimulatorItemCard> Cards { get; } = new();

    [ObservableProperty] private string? searchText;

    /// <summary>§375. True while the six shelves are what is shown; false
    /// while a shelf's items, or a search's, are.</summary>
    [ObservableProperty] private bool showingCategories = true;

    /// <summary>§375. The line over the cards: the open shelf and how many
    /// it holds, or what the search found.</summary>
    [ObservableProperty] private string listTitle = "";

    /// <summary>§375. The shelf that is open, if one is - what an emptied
    /// search box goes back to.</summary>
    private SimulatorItemCategory? openCategory;

    /// <summary>The pick once <see cref="Confirmed"/> fired - null means
    /// the No Item button (clear the slot).</summary>
    public string? SelectedName { get; private set; }

    public event Action? Confirmed;

    public SimulatorItemPickerViewModel()
    {
        foreach ((string name, string face, string[] items) in Shelves)
            Categories.Add(new SimulatorItemCategory(name, ItemSpriteService.GetSprite(face), items));
    }

    partial void OnSearchTextChanged(string? value)
    {
        string needle = value?.Trim() ?? string.Empty;

        if (needle.Length > 0)
        {
            Fill(Pool.Where(name => name.Contains(needle, StringComparison.OrdinalIgnoreCase)));
            ListTitle = Cards.Count == 1 ? "Search - 1 item" : $"Search - {Cards.Count} items";
            ShowingCategories = false;
            return;
        }

        if (openCategory != null)
            ShowShelf(openCategory);
        else
            ShowShelves();
    }

    private void Fill(IEnumerable<string> names)
    {
        Cards.Clear();

        foreach (string name in names)
            Cards.Add(new SimulatorItemCard(name, ItemSpriteService.GetSprite(name)));
    }

    private void ShowShelf(SimulatorItemCategory shelf)
    {
        Fill(shelf.Items);
        ListTitle = $"{shelf.Name} - {shelf.Items.Count}";
        ShowingCategories = false;
    }

    private void ShowShelves()
    {
        Cards.Clear();
        ListTitle = string.Empty;
        ShowingCategories = true;
    }

    /// <summary>§375. A shelf card was clicked: its items replace the
    /// shelves. A search in progress is dropped, since the click said
    /// where to look.</summary>
    [RelayCommand]
    private void OpenCategory(SimulatorItemCategory? shelf)
    {
        if (shelf == null)
            return;

        openCategory = shelf;

        if (!string.IsNullOrWhiteSpace(SearchText))
            SearchText = null;   // OnSearchTextChanged shows the shelf
        else
            ShowShelf(shelf);
    }

    /// <summary>§375. One step back: out of a search to wherever it was
    /// typed, or out of a shelf to the six.</summary>
    [RelayCommand]
    private void Back()
    {
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            SearchText = null;
            return;
        }

        openCategory = null;
        ShowShelves();
    }

    [RelayCommand]
    private void PickCard(SimulatorItemCard? card)
    {
        if (card == null)
            return;

        SelectedName = card.Name;
        Confirmed?.Invoke();
    }

    [RelayCommand]
    private void PickNone()
    {
        SelectedName = null;
        Confirmed?.Invoke();
    }
}
