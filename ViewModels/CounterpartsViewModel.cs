using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foot_Tracker.Models;

namespace Foot_Tracker.ViewModels;

public sealed record CounterpartCardItem(CounterpartEntry Entry, Bitmap? Sprite)
{
    public string Name => Entry.Name;
}

/// <summary>
/// Ported from Forms/Counterparts/Counterparts.cs + CounterpartHoverForm.cs. The
/// hover popup (a topmost, non-activating tool window) becomes an inline detail
/// panel bound to SelectedCard - same pattern as BossDetailViewModel/PokemonSelectorViewModel.
/// </summary>
public sealed partial class CounterpartsViewModel : ViewModelBase
{
    private Dictionary<string, List<CounterpartEntry>> _counterpartGroups =
        new(StringComparer.OrdinalIgnoreCase);

    [ObservableProperty] private string groupTitle = string.Empty;
    [ObservableProperty] private string? statusMessage;
    [ObservableProperty] private CounterpartCardItem? selectedCard;
    [ObservableProperty] private bool hasSelectedCard;

    public ObservableCollection<CounterpartCardItem> Cards { get; } = new();

    public string SpawnLocationsText =>
        SelectedCard is null
            ? string.Empty
            : SelectedCard.Entry.SpawnLocations.Count > 0
                ? string.Join(Environment.NewLine, SelectedCard.Entry.SpawnLocations)
                : "No spawns available.";

    public string NotesText =>
        SelectedCard is null
            ? string.Empty
            : string.IsNullOrWhiteSpace(SelectedCard.Entry.Notes)
                ? "Notes: None"
                : $"Notes: {SelectedCard.Entry.Notes}";

    partial void OnSelectedCardChanged(CounterpartCardItem? value)
    {
        HasSelectedCard = value is not null;
        OnPropertyChanged(nameof(SpawnLocationsText));
        OnPropertyChanged(nameof(NotesText));
    }

    public void Load(string groupName)
    {
        GroupTitle = $"{groupName} Counterpart Pokémon";

        string jsonPath = Path.Combine(AppContext.BaseDirectory, "DataFiles", "counterparts.json");

        if (!File.Exists(jsonPath))
        {
            StatusMessage = $"Counterpart data was not found:\n{jsonPath}";
            return;
        }

        try
        {
            string json = File.ReadAllText(jsonPath);

            _counterpartGroups = JsonSerializer.Deserialize<Dictionary<string, List<CounterpartEntry>>>(
                json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? new Dictionary<string, List<CounterpartEntry>>(StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException ex)
        {
            StatusMessage = $"The counterparts JSON could not be read:\n{ex.Message}";
            return;
        }

        Cards.Clear();

        if (!_counterpartGroups.TryGetValue(groupName, out var entries))
        {
            StatusMessage = $"No counterpart data was found for {groupName}.";
            return;
        }

        foreach (var entry in entries)
        {
            Cards.Add(new CounterpartCardItem(entry, LoadImage(entry.Image)));
        }
    }

    [RelayCommand]
    private void SelectCard(CounterpartCardItem card)
    {
        SelectedCard = SelectedCard == card ? null : card;
    }

    private static Bitmap? LoadImage(string relativeImagePath)
    {
        if (string.IsNullOrWhiteSpace(relativeImagePath))
            return null;

        string normalizedPath = relativeImagePath
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);

        string fullPath = Path.Combine(AppContext.BaseDirectory, normalizedPath);

        return File.Exists(fullPath) ? new Bitmap(fullPath) : null;
    }
}
