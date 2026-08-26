using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foot_Tracker.Models;
using Foot_Tracker.Services;

namespace Foot_Tracker.ViewModels;

/// <summary>Display wrapper around a GuildEvent for the board - formats the
/// raw model into what a viewer should actually see. Immutable on purpose:
/// nothing about an already-posted event changes while the window is open
/// (the relative-time line is computed once, at load/refresh time - a known
/// simplification for this prototype, not a live-updating clock).
///
/// Shared by two windows, not just EventsWindow: RemoveEventViewModel's
/// delete list uses the exact same LoadAll() and the same card layout (see
/// MIGRATION_GUIDE.md), so the two never quietly drift into showing
/// different information about the same event.</summary>
public sealed class GuildEventCardItem
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Message { get; init; }
    public required string TypeLabel { get; init; }
    public required string PostedByLine { get; init; }

    // Optional - most posts aren't about a specific Pokemon. HasPokemon is
    // computed once here (rather than a XAML converter on Sprite) since a
    // PokemonName that fails to resolve to a known sprite should still count
    // as "no image", not show a broken Image control.
    public Bitmap? Sprite { get; init; }
    public bool HasPokemon { get; init; }

    /// <summary>Builds one card per currently-posted event, newest first -
    /// the single shared mapping from the raw GuildEvent model to what a
    /// viewer should see.</summary>
    public static List<GuildEventCardItem> LoadAll()
    {
        var items = new List<GuildEventCardItem>();

        foreach (GuildEvent guildEvent in GuildEventService.Events)
        {
            bool hasPokemon = !string.IsNullOrWhiteSpace(guildEvent.PokemonName);

            items.Add(new GuildEventCardItem
            {
                Id = guildEvent.Id,
                Title = guildEvent.Title,
                Message = guildEvent.Message,
                TypeLabel = EnumFormatHelper.ToDisplayName(guildEvent.Type.ToString()).ToUpperInvariant(),
                PostedByLine = $"Posted by {guildEvent.PostedBy} · {FormatRelativeTime(guildEvent.PostedAtUtc)}",
                HasPokemon = hasPokemon,
                Sprite = hasPokemon ? PokemonSpriteService.GetEncounterSprite(guildEvent.PokemonName) : null
            });
        }

        return items;
    }

    private static string FormatRelativeTime(DateTime postedAtUtc)
    {
        TimeSpan age = DateTime.UtcNow - postedAtUtc;

        if (age < TimeSpan.FromMinutes(1))
            return "just now";
        if (age < TimeSpan.FromHours(1))
            return $"{(int)age.TotalMinutes}m ago";
        if (age < TimeSpan.FromDays(1))
            return $"{(int)age.TotalHours}h ago";

        return $"{(int)age.TotalDays}d ago";
    }
}

/// <summary>
/// Backs EventsWindow - the read-only guild Events board, reachable from
/// MainWindow's top-level "Events…" menu with no login required. This is
/// deliberately the "look whenever you want, nothing pushed at you" half of
/// the feature; posting a new entry (CreateEventWindow) and removing one
/// (RemoveEventWindow) are both separate, gated windows reachable from
/// AdminActionsWindow after a Magma Login - see MIGRATION_GUIDE.md.
///
/// Because composing/removing now happen in different windows entirely,
/// this board has no way to know a change landed while it's already open -
/// RefreshCommand is the manual fix for that (see EventsWindow's "Refresh"
/// button).
/// </summary>
public sealed partial class EventsViewModel : ViewModelBase
{
    public ObservableCollection<GuildEventCardItem> Board { get; } = new();

    [ObservableProperty] private bool hasNoEvents;

    [ObservableProperty] private string statusMessage =
        "Local test board - posts only show up on this machine for now, not on anyone else's tracker.";

    public EventsViewModel()
    {
        RefreshBoard();
    }

    [RelayCommand]
    private void Refresh() => RefreshBoard();

    private void RefreshBoard()
    {
        Board.Clear();

        foreach (GuildEventCardItem item in GuildEventCardItem.LoadAll())
            Board.Add(item);

        HasNoEvents = Board.Count == 0;
    }
}
