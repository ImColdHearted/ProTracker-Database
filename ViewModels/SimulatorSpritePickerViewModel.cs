using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foot_Tracker.Services;

namespace Foot_Tracker.ViewModels;

/// <summary>§199. One pickable picture: the event folder it came from, the
/// name on the file, and the catalog-relative path that is what actually
/// gets saved.</summary>
public sealed partial class SpriteChoiceItem : ObservableObject
{
    public required string EventName { get; init; }
    public required string DisplayName { get; init; }
    /// <summary>§202: FilePath, not Path. A tooltip bound to it reads
    /// "{Binding Path}", which is the binding's own first parameter spelled
    /// the same as the property - correct, and one letter from being a
    /// puzzle. There is a System.IO.Path in scope here too.</summary>
    public required string FilePath { get; init; }

    [ObservableProperty] private Bitmap? sprite;
}

/// <summary>
/// §199. The Simulator's sprite picker: every counterpart skin the library
/// carries, laid out as cards, one click to choose.
///
/// It exists because a counterpart is not reachable by name. A Pinkan
/// Gyarados is a Gyarados to the battle engine - same species, same stats,
/// same everything - so there is no species name that resolves to the pink
/// artwork, and a custom opponent that wants it has to name the FILE. That
/// is exactly the shape the counterparts catalog itself uses, and this
/// window is how the author picks one without typing a path.
///
/// The list is capped rather than paged: the catalog runs to several
/// hundred images across twelve event folders, and loading every one of
/// them to fill a window nobody scrolls to the bottom of is a second of
/// disk for nothing. Typing narrows it, which is how the other pickers in
/// this project behave.
/// </summary>
public sealed partial class SimulatorSpritePickerViewModel : ViewModelBase
{
    /// <summary>How many cards are built at once. The status line always
    /// says how many matched, so a cap is never silent.</summary>
    public const int MaxShown = 120;

    public ObservableCollection<SpriteChoiceItem> Results { get; } = new();

    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private string statusText = "";

    /// <summary>The pick, once <see cref="Confirmed"/> has fired.</summary>
    public string? SelectedPath { get; private set; }

    public event Action? Confirmed;

    public SimulatorSpritePickerViewModel()
    {
        Refill();
    }

    partial void OnSearchTextChanged(string value) => Refill();

    void Refill()
    {
        Results.Clear();

        string needle = (SearchText ?? "").Trim();

        List<SpriteChoiceItem> matches = CounterpartSpriteService.AllVariants
            .Where(v => needle.Length == 0 ||
                        v.Name.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                        v.Event.Contains(needle, StringComparison.OrdinalIgnoreCase))
            .Select(v => new SpriteChoiceItem
            {
                EventName = v.Event,
                // The files carry their shape in the name - Mega_Gyarados,
                // Nidoran_Female - and that is more useful to read than a
                // prettified version that no longer matches the file.
                DisplayName = v.Name,
                FilePath = v.ImagePath
            })
            .ToList();

        foreach (SpriteChoiceItem item in matches.Take(MaxShown))
        {
            Results.Add(item);
            _ = LoadSpriteAsync(item);
        }

        if (matches.Count == 0)
        {
            StatusText = CounterpartSpriteService.AllVariants.Count == 0
                ? "The counterpart catalog is empty - nothing was loaded from SharedPokemonLibrary/Data/Counterparts."
                : $"Nothing matches \"{needle}\".";
        }
        else if (matches.Count > MaxShown)
        {
            StatusText = $"{matches.Count} match - showing the first {MaxShown}. Type to narrow it down.";
        }
        else
        {
            StatusText = matches.Count == 1
                ? "1 sprite. Click it to use it."
                : $"{matches.Count} sprites. Click one to use it.";
        }
    }

    async Task LoadSpriteAsync(SpriteChoiceItem item)
    {
        string path = item.FilePath;

        // The same card-sized, cached read the encounter cards use, so a
        // sprite already on screen elsewhere costs nothing here.
        Bitmap? bitmap = await Task.Run(() => CounterpartSpriteService.GetCardSprite(path));

        if (Results.Contains(item))
            item.Sprite = bitmap;
    }

    /// <summary>One click is the pick - there is nothing else to decide.</summary>
    [RelayCommand]
    private void Pick(SpriteChoiceItem? item)
    {
        if (item == null)
            return;

        SelectedPath = item.FilePath;
        Confirmed?.Invoke();
    }
}
