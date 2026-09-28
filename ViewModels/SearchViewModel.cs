using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foot_Tracker.Models;
using Foot_Tracker.Services;

namespace Foot_Tracker.ViewModels;

/// <summary>§280. Which of the tracker's own records a search hit is in, and
/// therefore which window opening it should show.</summary>
public enum SearchResultKind
{
    Pokemon,
    Map,
    Player,
}

/// <summary>§280. One hit. <see cref="Key"/> is what the opened window is
/// given - the species name, the map name, the username - and is deliberately
/// separate from <see cref="Title"/>, which is what the row reads as and may
/// carry decoration the window must not be handed.</summary>
public sealed class SearchResultItem
{
    public required SearchResultKind Kind { get; init; }
    public required string Key { get; init; }
    public required string Title { get; init; }
    public required string Detail { get; init; }
    public Bitmap? Sprite { get; init; }

    /// <summary>§403. For a Pokémon: how many published spawn pages list
    /// it - what the row's Locations button opens on the region picture.</summary>
    public int SpawnMapCount { get; init; }

    public bool IsPokemon => Kind == SearchResultKind.Pokemon;

    public string KindLabel => Kind switch
    {
        SearchResultKind.Pokemon => "Pokémon",
        SearchResultKind.Map => "Map",
        _ => "Player",
    };
}

/// <summary>
/// §280. One box that searches the tracker's own records - the Pokémon it
/// knows, the maps it has hunted, and the players it has battled - and opens
/// whichever window already answers that kind of question.
///
/// IT SEARCHES WHAT THIS TRACKER HAS RECORDED, not the game. A Pokémon is
/// matched against the sprite library, which is every species the app knows,
/// and its line says how many you have seen this hunt and on this profile; a
/// map comes from §278's per-map files; a player from the PVP battle log. The
/// pokedex-by-route data a future scraper will bring is not here yet and this
/// window is where it will land.
///
/// NOTHING IS OPENED THAT DID NOT ALREADY EXIST. A Pokémon opens the encounter
/// history (§99/§112), a player opens their battle history (§276), a map opens
/// its own table (§278). The search's whole job is finding the thing and
/// handing its name to the window that already shows it.
///
/// Searching runs off the UI thread, because the all-time count per species is
/// a database query and a search that stutters while typing is worse than one
/// that takes a moment. A typed key cancels whatever the last one started, so
/// only the newest search's results are ever shown.
/// </summary>
public sealed partial class SearchViewModel : ViewModelBase
{
    /// <summary>Below this, a search matches too much to be worth showing -
    /// two letters is what the target picker already asks for.</summary>
    private const int MinimumQueryLength = 2;

    /// <summary>Per kind, so one crowded kind cannot push the others off the
    /// list - a search for "ra" matches a hundred species and would otherwise
    /// bury the one map and the one player that also matched.</summary>
    private const int MaxPerKind = 25;

    private readonly DispatcherTimer typingDelay = new() { Interval = TimeSpan.FromMilliseconds(200) };

    /// <summary>Which search is current. A result set built for an older
    /// generation is dropped rather than rendered - the user has typed since.</summary>
    private int generation;

    public ObservableCollection<SearchResultItem> Results { get; } = new();

    [ObservableProperty] private string? searchText;

    [ObservableProperty] private string statusMessage =
        "Search for a Pokémon, a map you have hunted, or a player you have battled.";

    [ObservableProperty] private bool isSearching;

    /// <summary>Set by the View. Takes the hit that was clicked and opens the
    /// window for it - the view model decides WHAT was found, the view decides
    /// how a window is shown.</summary>
    public Action<SearchResultItem>? OpenResult { get; set; }

    /// <summary>§403. Set by the View: opens a Pokémon hit's spawn
    /// locations on the region picture (SpawnLocationsWindow), the other
    /// answer a Pokémon row offers beside its encounters.</summary>
    public Action<SearchResultItem>? OpenLocations { get; set; }

    public SearchViewModel()
    {
        typingDelay.Tick += (_, _) =>
        {
            typingDelay.Stop();
            _ = RunSearchAsync(SearchText?.Trim() ?? string.Empty);
        };
    }

    partial void OnSearchTextChanged(string? value)
    {
        typingDelay.Stop();

        if ((value?.Trim().Length ?? 0) < MinimumQueryLength)
        {
            generation++;
            Results.Clear();
            IsSearching = false;
            StatusMessage = "Type at least two letters.";
            return;
        }

        typingDelay.Start();
    }

    private async Task RunSearchAsync(string query)
    {
        if (query.Length < MinimumQueryLength)
            return;

        int mine = ++generation;

        IsSearching = true;

        // Everything below reads files and the encounter database, so it runs
        // off the UI thread. The collections it builds are plain lists; only
        // the copy into Results touches anything bound.
        List<SearchResultItem> found = await Task.Run(() => Gather(query));

        if (mine != generation)
            return;

        Results.Clear();

        foreach (SearchResultItem item in found)
            Results.Add(item);

        IsSearching = false;

        StatusMessage = found.Count == 0
            ? $"Nothing found for \"{query}\"."
            : $"{DisplayNumber.Count(found.Count)} {(found.Count == 1 ? "result" : "results")} for \"{query}\".";
    }

    /// <summary>§280. The three searches, in the order the results are shown:
    /// Pokémon, then maps, then players. Static and side-effect free, so it is
    /// safe on the worker thread and can be reasoned about on its own.</summary>
    private static List<SearchResultItem> Gather(string query)
    {
        var results = new List<SearchResultItem>();

        results.AddRange(FindPokemon(query));
        results.AddRange(FindMaps(query));
        results.AddRange(FindPlayers(query));

        return results;
    }

    private static bool Matches(string? value, string query) =>
        value is not null && value.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<SearchResultItem> FindPokemon(string query)
    {
        int client = SessionPersistenceService.ActiveClientNumber;

        foreach (PokemonLibraryEntry entry in PokemonSpriteService.AllPokemon
                     .Where(p => Matches(p.Name, query))
                     .OrderBy(p => p.Name.Length)
                     .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                     .Take(MaxPerKind))
        {
            long lifetime = client >= 1
                ? EncounterDatabaseService.CountFor(entry.Name, client)
                : 0;

            // §403: the published spawn pages (§397) that list it.
            int spawnMaps = SpawnDataService.Current.Count(m =>
                m.Pokemon.Any(p => string.Equals(p.Name, entry.Name, StringComparison.OrdinalIgnoreCase)));

            string encountered = lifetime == 0
                ? "Never encountered on this profile"
                : $"{DisplayNumber.Count(lifetime)} encountered on this profile";

            string mapWord = spawnMaps == 1 ? "map" : "maps";
            string spawns = spawnMaps == 0
                ? "no published map lists it yet"
                : $"spawns on {DisplayNumber.Count(spawnMaps)} published {mapWord}";

            yield return new SearchResultItem
            {
                Kind = SearchResultKind.Pokemon,
                Key = entry.Name,
                Title = entry.Name,
                Detail = encountered + " · " + spawns,
                Sprite = PokemonSpriteService.GetSprite(entry.Name),
                SpawnMapCount = spawnMaps,
            };
        }
    }

    private static IEnumerable<SearchResultItem> FindMaps(string query)
    {
        foreach (MapEncounterTally tally in MapEncounterService.AllMaps()
                     .Where(t => Matches(t.MapName, query))
                     .Take(MaxPerKind))
        {
            string when = tally.LastSeenUtc is DateTime seen
                ? $", last hunted {seen.ToLocalTime():g}"
                : string.Empty;

            yield return new SearchResultItem
            {
                Kind = SearchResultKind.Map,
                Key = tally.MapName,
                Title = tally.MapName,
                Detail =
                    $"{DisplayNumber.Count(tally.TotalEncounters)} encounters across " +
                    $"{DisplayNumber.Count(tally.EncounterCounts.Count)} species{when}",
            };
        }
    }

    private static IEnumerable<SearchResultItem> FindPlayers(string query)
    {
        // The battle log holds one row per battle, so the same name appears as
        // many times as they were fought. Searching wants the PERSON.
        var byName = PvpOpponentService.Opponents
            .Where(o => Matches(o.Name, query))
            .GroupBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxPerKind);

        foreach (var group in byName)
        {
            List<PvpOpponentEntry> battles = group.ToList();

            int wins = battles.Count(b => b.HasOutcome && b.Won);
            int losses = battles.Count(b => b.HasOutcome && !b.Won);

            int faced = battles.Max(b => b.TimesBattled);

            yield return new SearchResultItem
            {
                Kind = SearchResultKind.Player,
                Key = group.Key,
                Title = group.Key,
                Detail =
                    $"Faced {DisplayNumber.Count(faced)} {(faced == 1 ? "time" : "times")} - " +
                    $"{DisplayNumber.Count(wins)} {(wins == 1 ? "win" : "wins")}, " +
                    $"{DisplayNumber.Count(losses)} {(losses == 1 ? "loss" : "losses")}",
            };
        }
    }

    [RelayCommand]
    private void Open(SearchResultItem? item)
    {
        if (item is null)
            return;

        OpenResult?.Invoke(item);
    }
}
