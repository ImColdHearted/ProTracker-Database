using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foot_Tracker.Models;
using Foot_Tracker.Services;

namespace ProTracker.Companion.ViewModels;

/// <summary>
/// §295. The Pokédex areas on a phone: what the desktop scraper (§281-§287)
/// recorded, read from the same one-file-per-species store. Two ways in -
/// a species, to see where it spawns; a map, to see what spawns there -
/// because in the field the second question is the one that gets asked.
///
/// Read-only. Scanning is the desktop's job; the phone shows the result.
/// </summary>
public sealed partial class PokedexPageViewModel : ObservableObject
{
    public ObservableCollection<PokedexEntry> Species { get; } = new();

    public ObservableCollection<PokedexSpawn> Spawns { get; } = new();

    [ObservableProperty] private string query = string.Empty;

    [ObservableProperty] private string heading = string.Empty;

    [ObservableProperty] private string status = string.Empty;

    [ObservableProperty] private bool hasSpawns;

    private IReadOnlyList<PokedexEntry> all = Array.Empty<PokedexEntry>();

    public PokedexPageViewModel()
    {
        all = PokedexService.All();
        Filter();
    }

    /// <summary>A Search hit or a map result lands here already answered.</summary>
    public void ShowSpecies(string species)
    {
        PokedexEntry? entry = all.FirstOrDefault(
            e => string.Equals(e.Species, species, StringComparison.OrdinalIgnoreCase));

        if (entry is null)
        {
            Heading = species;
            Status = "No areas scanned for this Pokémon yet - scan them on the desktop Admin Console.";
            Spawns.Clear();
            HasSpawns = false;
            return;
        }

        Select(entry);
    }

    public void ShowMap(string mapName)
    {
        Spawns.Clear();

        foreach ((PokedexEntry entry, PokedexSpawn spawn) in PokedexService.SpawnsOn(mapName))
        {
            // The row names the species here, because the map is the heading.
            Spawns.Add(new PokedexSpawn
            {
                MapName = entry.Species,
                Land = spawn.Land,
                Water = spawn.Water,
                Morning = spawn.Morning,
                Day = spawn.Day,
                Night = spawn.Night,
                MembersOnly = spawn.MembersOnly,
                FirstSeenUtc = spawn.FirstSeenUtc,
                LastSeenUtc = spawn.LastSeenUtc,
            });
        }

        Heading = mapName;
        HasSpawns = Spawns.Count > 0;
        Status = HasSpawns
            ? $"{DisplayNumber.Count(Spawns.Count)} {(Spawns.Count == 1 ? "Pokémon spawns" : "Pokémon spawn")} here."
            : "Nothing scanned spawns on this map yet.";
    }

    partial void OnQueryChanged(string value) => Filter();

    private void Filter()
    {
        string needle = Query.Trim();

        Species.Clear();

        foreach (PokedexEntry entry in all
                     .Where(e => needle.Length == 0 || e.Species.Contains(needle, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(e => e.Species, StringComparer.OrdinalIgnoreCase))
        {
            Species.Add(entry);
        }

        if (all.Count == 0)
            Status = "No Pokédex areas on this phone yet. Scan them on the desktop, then import the data here.";
    }

    [RelayCommand]
    private void Select(PokedexEntry? entry)
    {
        if (entry is null)
            return;

        Spawns.Clear();

        foreach (PokedexSpawn spawn in entry.Spawns.OrderBy(s => s.MapName, StringComparer.OrdinalIgnoreCase))
            Spawns.Add(spawn);

        int members = entry.Spawns.Count(s => s.MembersOnly);

        Heading = entry.Species;
        HasSpawns = Spawns.Count > 0;
        Status =
            $"{DisplayNumber.Count(entry.Spawns.Count)} {(entry.Spawns.Count == 1 ? "area" : "areas")} known"
            + (members == 0 ? "." : $", {DisplayNumber.Count(members)} needing membership.");
    }
}
