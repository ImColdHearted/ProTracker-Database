using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Foot_Tracker.Models;
using Foot_Tracker.Services;

namespace Foot_Tracker.ViewModels;

/// <summary>§397. One row of a map's spawn page - top-level for the
/// compiled DataTemplate, like every other row type in this folder.</summary>
public sealed class SpawnPokemonRow
{
    public string Name { get; init; } = string.Empty;

    public Bitmap? Sprite { get; init; }

    /// <summary>The species' type badges, the ones the encounter table's
    /// tooltip and the mockup's cards show - up to two.</summary>
    public Bitmap? TypeIcon1 { get; init; }

    public Bitmap? TypeIcon2 { get; init; }

    public string MethodText { get; init; } = string.Empty;

    public string TimeText { get; init; } = string.Empty;

    /// <summary>"MS" on a members-only row, blank otherwise - the word the
    /// Pokedex's pink name colour stands for (§281).</summary>
    public string MembersText { get; init; } = string.Empty;

    public bool MembersOnly { get; init; }

    /// <summary>§398. The community's matching balls for the ordinary
    /// colouring, as pictures - up to three - and the tooltip that names
    /// them, the shiny's included. Null pictures and HasBalls false for a
    /// Pokémon the list does not carry.</summary>
    public Bitmap? BallIcon1 { get; init; }

    public Bitmap? BallIcon2 { get; init; }

    public Bitmap? BallIcon3 { get; init; }

    public string BallsTip { get; init; } = string.Empty;

    public bool HasBalls { get; init; }
}

/// <summary>
/// §397. One map's spawn page: every species the admin's Pokedex scans
/// named this map for, with the facts §281 reads off the Pokedex row -
/// grass or surf (neither lit means a rod, §287), morning, day, night, and
/// whether the area needs membership - and the sprite and type badges the
/// rest of the app already draws for a species. §398 adds the community's
/// matching balls beside each row (MatchingBallService).
///
/// Read-only, and read from SpawnDataService's copy: the page is what was
/// published, refreshed when the region window refreshes or the console
/// republishes, never composed on the player's machine.
/// </summary>
public sealed partial class SpawnMapViewModel : ViewModelBase, IDisposable
{
    public string MapName { get; }

    public string Title => $"{MapName} - Spawns";

    public ObservableCollection<SpawnPokemonRow> Rows { get; } = new();

    [ObservableProperty] private string summaryLine = string.Empty;

    [ObservableProperty] private bool hasRows;

    [ObservableProperty] private string emptyMessage = string.Empty;

    private bool disposed;

    public SpawnMapViewModel(string mapName)
    {
        MapName = mapName.Trim();

        Rebuild();

        SpawnDataService.Changed += OnSpawnsChanged;
    }

    private void OnSpawnsChanged() => Dispatcher.UIThread.Post(Rebuild);

    private void Rebuild()
    {
        if (disposed)
            return;

        Rows.Clear();

        SpawnMap? page = SpawnDataService.Find(MapName);

        if (page is null)
        {
            HasRows = false;
            SummaryLine = string.Empty;
            EmptyMessage = $"No spawn page is published for {MapName} any more.";
            return;
        }

        foreach (SpawnPokemon pokemon in page.Pokemon)
        {
            IReadOnlyList<string> types = PokemonSpriteService.GetTypes(pokemon.Name);

            // §398: the balls that suit this species, from the community's
            // list; a species it lacks gets a dash and says so.
            MatchingBalls? balls = MatchingBallService.For(pokemon.Name);
            IReadOnlyList<string> regular = balls?.Regular ?? Array.Empty<string>();

            Rows.Add(new SpawnPokemonRow
            {
                Name = pokemon.Name,
                Sprite = PokemonSpriteService.GetDisplaySprite(pokemon.Name),
                TypeIcon1 = types.Count > 0 ? PokemonSpriteService.GetTypeIcon(types[0]) : null,
                TypeIcon2 = types.Count > 1 ? PokemonSpriteService.GetTypeIcon(types[1]) : null,
                MethodText = pokemon.MethodText,
                TimeText = pokemon.TimeText,
                MembersText = pokemon.MembersOnly ? "MS" : string.Empty,
                MembersOnly = pokemon.MembersOnly,
                BallIcon1 = regular.Count > 0 ? MatchingBallService.Sprite(regular[0]) : null,
                BallIcon2 = regular.Count > 1 ? MatchingBallService.Sprite(regular[1]) : null,
                BallIcon3 = regular.Count > 2 ? MatchingBallService.Sprite(regular[2]) : null,
                BallsTip = balls is null
                    ? "Not in the community's matching-balls list."
                    : "Matching balls: " + balls.Describe(),
                HasBalls = balls is not null && balls.Any,
            });
        }

        HasRows = Rows.Count > 0;

        EmptyMessage = HasRows
            ? string.Empty
            : $"{MapName} is published, but no Pokémon has been scanned for it yet.";

        string when = page.UpdatedUtc is DateTime utc
            ? $", published {utc.ToLocalTime():d MMM yyyy}"
            : string.Empty;

        SummaryLine = $"{page.Region} · {page.PokemonCountText}{when}.";
    }

    public void Dispose()
    {
        disposed = true;
        SpawnDataService.Changed -= OnSpawnsChanged;
    }
}
