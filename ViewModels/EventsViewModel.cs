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
    public string PokemonName { get; init; } = "";

    // §103 rewards block (see GuildEvent's reward fields and the Events
    // reference design) - each Has* is precomputed for the same reason
    // HasPokemon is, and HasRewards folds them so the whole Rewards box
    // hides on posts that carry none.
    public string ItemReward { get; init; } = "";
    public string PokemonRewardName { get; init; } = "";
    public Bitmap? PokemonRewardSprite { get; init; }
    public string PokeDollarsText { get; init; } = "";
    public bool HasItemReward { get; init; }
    public bool HasPokemonReward { get; init; }
    public bool HasPokeDollars { get; init; }
    public bool HasRewards { get; init; }

    // §106: IsCommunityEvent lived here to feed the compact "Community
    // events (Max 2)" strip, which duplicated every post it showed - the
    // strip and the property both went with it.

    /// <summary>§106: how many entries have been submitted to this event -
    /// shown on its own View Entries button so the count is visible without
    /// opening the window.</summary>
    public int EntryCount { get; init; }

    public string ViewEntriesText => EntryCount > 0 ? $"View Entries ({EntryCount})" : "View Entries";

    /// <summary>§107: which of the two action buttons this post carries,
    /// chosen on the Create Event form's circular markers. Both default to
    /// true here as well as on the model, so a card built from a post saved
    /// before §107 - or from any code path that forgets to copy them - shows
    /// the same two buttons §106 gave every card. EventsWindow binds
    /// IsVisible straight to these, and EventsWindow.axaml.cs re-checks them
    /// before opening either window so the rule survives a layout change
    /// that puts the buttons somewhere else.</summary>
    public bool AllowPokemonSubmissions { get; init; } = true;
    public bool AllowViewEntries { get; init; } = true;

    /// <summary>Builds one card per currently-posted event, newest first -
    /// the single shared mapping from the raw GuildEvent model to what a
    /// viewer should see.</summary>
    public static List<GuildEventCardItem> LoadAll()
    {
        var items = new List<GuildEventCardItem>();

        foreach (GuildEvent guildEvent in GuildEventService.Events)
        {
            bool hasPokemon = !string.IsNullOrWhiteSpace(guildEvent.PokemonName);
            bool hasItemReward = !string.IsNullOrWhiteSpace(guildEvent.ItemReward);
            bool hasPokemonReward = !string.IsNullOrWhiteSpace(guildEvent.PokemonReward);
            bool hasPokeDollars = guildEvent.PokeDollars > 0;

            items.Add(new GuildEventCardItem
            {
                Id = guildEvent.Id,
                Title = guildEvent.Title,
                Message = guildEvent.Message,
                TypeLabel = EnumFormatHelper.ToDisplayName(guildEvent.Type.ToString()),
                PostedByLine = $"Posted by {guildEvent.PostedBy} - {FormatRelativeTime(guildEvent.PostedAtUtc)}",
                HasPokemon = hasPokemon,
                Sprite = hasPokemon ? PokemonSpriteService.GetEncounterSprite(guildEvent.PokemonName) : null,
                PokemonName = guildEvent.PokemonName,
                ItemReward = guildEvent.ItemReward,
                PokemonRewardName = guildEvent.PokemonReward,
                PokemonRewardSprite = hasPokemonReward ? PokemonSpriteService.GetEncounterSprite(guildEvent.PokemonReward) : null,
                PokeDollarsText = hasPokeDollars ? $"{guildEvent.PokeDollars:N0}" : "",
                HasItemReward = hasItemReward,
                HasPokemonReward = hasPokemonReward,
                HasPokeDollars = hasPokeDollars,
                HasRewards = hasItemReward || hasPokemonReward || hasPokeDollars,
                // §143: the server's count when the post came from it, this
                // machine's cache otherwise - whichever is larger.
                EntryCount = Math.Max(guildEvent.EntryCount, GuildEventEntryService.CountFor(guildEvent.Id)),
                AllowPokemonSubmissions = guildEvent.AllowPokemonSubmissions,
                AllowViewEntries = guildEvent.AllowViewEntries
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

    private const string LocalOnlyStatus =
        "Local test board - no events server is configured, so posts only show up on this machine.";

    [ObservableProperty] private string statusMessage =
        EventsSyncService.IsOnline ? "Loading the shared board..." : LocalOnlyStatus;

    public EventsViewModel()
    {
        // The cached copy first, so the window is never empty while the
        // server answers; then the server's copy replaces it (§143).
        RefreshBoard();

        if (EventsSyncService.IsOnline)
            _ = RefreshFromServerAsync();
    }

    [RelayCommand]
    private Task Refresh() => RefreshFromServerAsync();

    /// <summary>§143. Pulls the board from the events server into the local
    /// cache and rebuilds the cards. Offline (no server configured) it is
    /// just RefreshBoard. A server that cannot be reached leaves the cached
    /// copy on screen and says so, rather than blanking the board.</summary>
    public async Task RefreshFromServerAsync()
    {
        if (!EventsSyncService.IsOnline)
        {
            RefreshBoard();
            StatusMessage = LocalOnlyStatus;
            return;
        }

        try
        {
            IReadOnlyList<GuildEvent> events = await EventsSyncService.FetchEventsAsync();

            GuildEventService.ReplaceAll(events);
            RefreshBoard();

            StatusMessage = events.Count == 1
                ? $"Shared board - 1 event, updated {DateTime.Now:HH:mm}."
                : $"Shared board - {events.Count} events, updated {DateTime.Now:HH:mm}.";
        }
        catch (EventsSyncException ex)
        {
            RefreshBoard();
            StatusMessage = $"Could not update the board ({ex.Message}) - showing the last copy saved on this machine.";
        }
    }

    /// <summary>Rebuilds every card from storage - also the way an entry
    /// submission gets its View Entries count updated (§106), since the
    /// cards are immutable once built.</summary>
    public void RefreshBoard()
    {
        Board.Clear();

        foreach (GuildEventCardItem item in GuildEventCardItem.LoadAll())
            Board.Add(item);

        HasNoEvents = Board.Count == 0;
    }
}
