using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Foot_Tracker.Models;
using Foot_Tracker.Services;

namespace Foot_Tracker.ViewModels;

/// <summary>
/// §280. One map's encounter table, opened from a search hit.
///
/// Reads §278's own tally and renders it with the same EncounterCountRow the
/// front table uses, so a map's table looks exactly like the hunt's table and
/// there is no second row type to keep in step. Read-only: this is a record of
/// what happened somewhere, not a session to play with.
///
/// A map with no file is a real answer, not an error - per-map tracking is off
/// by default (§278) and a map hunted before it was turned on has nothing
/// written down. The window says which of those it is rather than showing an
/// empty table that reads as "you caught nothing here".
/// </summary>
public sealed partial class MapDetailViewModel : ViewModelBase
{
    public string MapName { get; }

    public string Title => $"{MapName} - Encounters";

    public ObservableCollection<EncounterCountRow> Rows { get; } = new();

    [ObservableProperty] private string summaryLine = string.Empty;

    [ObservableProperty] private bool hasRows;

    [ObservableProperty] private string emptyMessage = string.Empty;

    public MapDetailViewModel(string mapName)
    {
        MapName = mapName;

        MapEncounterTally? tally = MapEncounterService.AllMaps()
            .FirstOrDefault(t => string.Equals(t.MapName, mapName, StringComparison.OrdinalIgnoreCase));

        if (tally is null)
        {
            HasRows = false;
            EmptyMessage =
                $"No per-map table has been recorded for {mapName} on this profile. " +
                "Per-map tracking is off by default - turn it on in File, Tracker Settings.";
            return;
        }

        int total = tally.TotalEncounters;

        foreach (var kvp in tally.EncounterCounts.OrderByDescending(k => k.Value))
        {
            Rows.Add(new EncounterCountRow
            {
                PokemonName = kvp.Key,
                Count = kvp.Value,
                RatePercent = total > 0 ? kvp.Value / (double)total * 100.0 : 0,
                Sprite = PokemonSpriteService.GetSprite(kvp.Key),
                CaughtCount = tally.CaughtCounts.TryGetValue(kvp.Key, out int caught) ? caught : 0,
                RanFromCount = tally.RanFromCounts.TryGetValue(kvp.Key, out int ran) ? ran : 0,
                LastEncounteredUtc =
                    tally.LastEncounteredUtc.TryGetValue(kvp.Key, out DateTime seen) ? seen : null,
            });
        }

        HasRows = Rows.Count > 0;

        EmptyMessage = HasRows
            ? string.Empty
            : $"{mapName} has a table on this profile, but nothing has been recorded in it yet.";

        SummaryLine =
            $"{DisplayNumber.Count(total)} encounters across " +
            $"{DisplayNumber.Count(Rows.Count)} {(Rows.Count == 1 ? "species" : "species")}" +
            (tally.LastSeenUtc is DateTime last ? $", last hunted {last.ToLocalTime():g}." : ".");
    }
}
