using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foot_Tracker.Models;
using Foot_Tracker.Services;

namespace Foot_Tracker.ViewModels;

/// <summary>One row of the entries table - username, Pokemon (sprite plus
/// name, the same shape the session-encounter tables use), and when it was
/// submitted. Immutable: an entry never changes once submitted, so nothing
/// here needs to be observable.</summary>
public sealed class EventEntryRow
{
    public required string EntryId { get; init; }
    public required string Username { get; init; }
    public required string PokemonName { get; init; }
    public Bitmap? Sprite { get; init; }
    public required string SubmittedText { get; init; }

    /// <summary>Kept whole so the detail window doesn't have to look the
    /// entry up again by id.</summary>
    public required EventEntry Entry { get; init; }
}

/// <summary>
/// Backs EventEntriesWindow (MIGRATION_GUIDE.md §106) - every submission to
/// one Events-board post, newest first. Clicking a row opens that entry's
/// detail window (its screenshot, and whatever else gets added underneath
/// later); this list stays deliberately thin - username, Pokemon, timestamp -
/// because it is a scanning view, not a reading one.
/// </summary>
public sealed partial class EventEntriesViewModel : ViewModelBase
{
    private readonly string _eventId;

    public string EventTitle { get; }

    public ObservableCollection<EventEntryRow> Entries { get; } = new();

    [ObservableProperty] private bool hasNoEntries;
    [ObservableProperty] private string summaryText = string.Empty;

    // §143: the last problem talking to the events server, shown after
    // the count so a stale list is never mistaken for a current one.
    private string syncNote = string.Empty;

    public EventEntriesViewModel(string eventId, string eventTitle)
    {
        _eventId = eventId;
        EventTitle = eventTitle;

        // Cached entries first, then the server's (§143).
        RebuildFromCache();

        if (EventsSyncService.IsOnline)
            _ = Refresh();
    }

    /// <summary>§143. Fetches this event's entries from the events server
    /// into the local cache (entries this machine submitted keep their
    /// screenshots), then rebuilds the list. Offline it only rebuilds. Also
    /// what EventsWindow calls after a submission from its own card.</summary>
    [RelayCommand]
    public async Task Refresh()
    {
        if (EventsSyncService.IsOnline)
        {
            try
            {
                IReadOnlyList<EventEntry> server = await EventsSyncService.FetchEntriesAsync(_eventId);

                GuildEventEntryService.MergeServerEntries(_eventId, server);
                syncNote = string.Empty;
            }
            catch (EventsSyncException ex)
            {
                syncNote = $" Could not update from the events server ({ex.Message}) - this is the copy saved on this machine.";
            }
        }

        RebuildFromCache();
    }

    /// <summary>Rebuilds the rows from the local cache alone.</summary>
    public void RebuildFromCache()
    {
        Entries.Clear();

        foreach (EventEntry entry in GuildEventEntryService.EntriesFor(_eventId))
        {
            bool hasPokemon = !string.IsNullOrWhiteSpace(entry.PokemonName);

            Entries.Add(new EventEntryRow
            {
                EntryId = entry.Id,
                Username = entry.Username,
                PokemonName = hasPokemon ? entry.PokemonName : "(none given)",
                Sprite = hasPokemon ? PokemonSpriteService.GetEncounterSprite(entry.PokemonName) : null,
                // Local time for a human reading the list; the entry itself
                // stores UTC.
                SubmittedText = entry.SubmittedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                Entry = entry
            });
        }

        HasNoEntries = Entries.Count == 0;

        SummaryText = (Entries.Count == 1
            ? "1 entry submitted."
            : $"{Entries.Count} entries submitted.") + syncNote;
    }
}
