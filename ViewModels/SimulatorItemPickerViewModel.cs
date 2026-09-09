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

/// <summary>
/// §159. The Simulator's held-item picker - the SimulatorPokemonPicker
/// pattern applied to items: a fixed battle-relevant pool (everything in
/// it actually does something in the engine), a live filter, one click to
/// pick, and a No Item button that confirms with nothing selected. §161:
/// the 24 mega stones and 18 type Z-Crystals joined the pool - a matching
/// stone unlocks the Mega Evolve button in battle, a crystal the Z-Power
/// button. (The crystal sprites are not in the asset drop yet, so those
/// cards may show name-only until the PNGs land in Assets/Items.)
/// </summary>
public sealed partial class SimulatorItemPickerViewModel : ViewModelBase
{
    /// <summary>The battle-relevant pool, grouped: staples, the status
    /// orbs and weather rocks, the type boosters, the berries, then §161's
    /// mega stones and Z-Crystals. Every name here is implemented by the
    /// engine's HeldItems catalog.</summary>
    public static readonly string[] Pool =
    {
        "Leftovers", "Black Sludge", "Life Orb", "Choice Band", "Choice Specs",
        "Choice Scarf", "Focus Sash", "Assault Vest", "Eviolite", "Rocky Helmet",
        "Weakness Policy", "Expert Belt", "Muscle Band", "Wise Glasses",
        "Air Balloon", "Light Ball", "Light Clay", "Scope Lens", "Wide Lens",

        "Flame Orb", "Toxic Orb", "Icy Rock", "Damp Rock", "Heat Rock", "Smooth Rock",

        "Silk Scarf", "Charcoal", "Mystic Water", "Magnet", "Miracle Seed",
        "Never-Melt Ice", "Black Belt", "Poison Barb", "Soft Sand", "Sharp Beak",
        "Twisted Spoon", "Silver Powder", "Hard Stone", "Spell Tag", "Dragon Fang",
        "Black Glasses", "Metal Coat", "Pixie Plate",

        "Sitrus Berry", "Lum Berry",

        "Occa Berry", "Passho Berry", "Wacan Berry", "Rindo Berry", "Yache Berry",
        "Chople Berry", "Kebia Berry", "Shuca Berry", "Coba Berry", "Payapa Berry",
        "Tanga Berry", "Charti Berry", "Kasib Berry", "Haban Berry", "Colbur Berry",
        "Babiri Berry", "Chilan Berry", "Roseli Berry",

        "Aerodactylite", "Alakazite", "Blastoisinite", "Blazikenite",
        "Charizardite X", "Charizardite Y", "Diancite", "Galladite",
        "Garchompite", "Gardevoirite", "Gengarite", "Gyaradosite",
        "Heracronite", "Latiasite", "Latiosite", "Lucarionite",
        "Metagrossite", "Mewtwonite X", "Mewtwonite Y", "Salamencite",
        "Slowbronite", "Steelixite", "Swampertite", "Tyranitarite",

        "Normalium Z", "Firium Z", "Waterium Z", "Electrium Z",
        "Grassium Z", "Icium Z", "Fightinium Z", "Poisonium Z",
        "Groundium Z", "Flyinium Z", "Psychium Z", "Buginium Z",
        "Rockium Z", "Ghostium Z", "Dragonium Z", "Darkinium Z",
        "Steelium Z", "Fairium Z"
    };

    public ObservableCollection<SimulatorItemCard> Cards { get; } = new();

    [ObservableProperty] private string? searchText;

    /// <summary>The pick once <see cref="Confirmed"/> fired - null means
    /// the No Item button (clear the slot).</summary>
    public string? SelectedName { get; private set; }

    public event Action? Confirmed;

    public SimulatorItemPickerViewModel()
    {
        Refill(null);
    }

    partial void OnSearchTextChanged(string? value) => Refill(value);

    private void Refill(string? filter)
    {
        Cards.Clear();

        string needle = filter?.Trim() ?? string.Empty;

        foreach (string name in Pool)
        {
            if (needle.Length == 0 || name.Contains(needle, StringComparison.OrdinalIgnoreCase))
                Cards.Add(new SimulatorItemCard(name, ItemSpriteService.GetSprite(name)));
        }
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
