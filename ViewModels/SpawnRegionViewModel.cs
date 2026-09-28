using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Foot_Tracker.Models;
using Foot_Tracker.Services;

namespace Foot_Tracker.ViewModels;

/// <summary>§397. One link on a region's Spawns page - top-level so the
/// window's compiled DataTemplate binding stays plain.</summary>
public sealed class SpawnMapLink
{
    public string Map { get; init; } = string.Empty;

    /// <summary>"12 Pokémon · published 3 Sep".</summary>
    public string Detail { get; init; } = string.Empty;
}

/// <summary>
/// §397. One region's Spawns page: the maps the admin filed under it, as
/// links, alphabetical - "simple links of the routes as they are added".
/// Opens on whatever this tracker has in hand (SpawnDataService's copy)
/// and asks the events server for a fresh list in the background, so the
/// page is never blank while the network answers and never empty because
/// the network did not. The status line says which of those it is showing.
///
/// Read-only: the pages are published from the Admin Console.
/// </summary>
public sealed partial class SpawnRegionViewModel : ViewModelBase, IDisposable
{
    public string Region { get; }

    public string Title => $"{Region} Spawns";

    public ObservableCollection<SpawnMapLink> Maps { get; } = new();

    [ObservableProperty] private bool hasMaps;

    [ObservableProperty] private string emptyMessage = string.Empty;

    [ObservableProperty] private string statusMessage = string.Empty;

    [ObservableProperty] private bool isRefreshing;

    private bool disposed;

    public SpawnRegionViewModel(string region)
    {
        Region = SpawnRegions.Canonical(region) ?? SpawnRegions.Other;

        Rebuild();
        StatusMessage = DescribeCopy();

        SpawnDataService.Changed += OnSpawnsChanged;
    }

    /// <summary>Asks the server once - on open, and on the Refresh button.
    /// Quiet on failure: the copy in hand stays up and the status line says
    /// where it came from.</summary>
    public async Task RefreshAsync()
    {
        if (IsRefreshing)
            return;

        if (!EventsSyncService.IsOnline)
        {
            StatusMessage = "No events server configured - " + DescribeCopy(lowerCase: true);
            return;
        }

        IsRefreshing = true;
        StatusMessage = "Checking the events server…";

        try
        {
            bool fetched = await SpawnDataService.RefreshAsync();

            // A fetch that changed nothing raises no Changed event, so the
            // list is rebuilt here regardless - it is cheap, and the page
            // must reflect the fetch it just made either way.
            Rebuild();

            StatusMessage = fetched
                ? $"Up to date as of {DateTime.Now:HH:mm}."
                : "The events server couldn't be reached - " + DescribeCopy(lowerCase: true);
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    private void OnSpawnsChanged() => Dispatcher.UIThread.Post(Rebuild);

    private void Rebuild()
    {
        if (disposed)
            return;

        Maps.Clear();

        foreach (SpawnMap map in SpawnDataService.InRegion(Region))
        {
            string when = map.UpdatedUtc is DateTime utc
                ? $" · published {utc.ToLocalTime():d MMM}"
                : string.Empty;

            Maps.Add(new SpawnMapLink { Map = map.Map, Detail = map.PokemonCountText + when });
        }

        HasMaps = Maps.Count > 0;

        EmptyMessage = HasMaps
            ? string.Empty
            : $"Nothing is filed under {Region} yet. Maps appear here as they are published.";
    }

    /// <summary>What the page is showing right now, before or instead of a
    /// fetch: the copy's age, or that there is no copy.</summary>
    private string DescribeCopy(bool lowerCase = false)
    {
        string text = SpawnDataService.FetchedUtc is DateTime fetched
            ? $"Showing the copy from {fetched.ToLocalTime():d MMM HH:mm}."
            : SpawnDataService.Current.Count > 0
                ? "Showing the copy on this machine."
                : "Nothing has been published on this machine yet.";

        return lowerCase ? char.ToLowerInvariant(text[0]) + text[1..] : text;
    }

    public void Dispose()
    {
        disposed = true;
        SpawnDataService.Changed -= OnSpawnsChanged;
    }
}
