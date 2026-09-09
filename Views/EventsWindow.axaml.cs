using Avalonia.Controls;
using Avalonia.Interactivity;
using Foot_Tracker.Services;
using Foot_Tracker.ViewModels;

namespace Foot_Tracker.Views;

/// <summary>
/// The guild Events board - a local-only prototype (see GuildEventService)
/// for what reading a giveaway or meetup announcement could look like.
/// Reachable from MainWindow's top-level "Events…" menu with no login
/// required; posting a new entry is a separate window (CreateEventWindow,
/// via File > Admin Login) - see MIGRATION_GUIDE.md §29.
///
/// §106 gave each post two actions: View Entries (who has submitted) and
/// Submit Pokémon (add your own, with a screenshot). Both open windows, so
/// both are Click handlers here rather than ViewModel commands - the same
/// division every other window-opening action in this app follows.
///
/// §107 made each of those two per-post, chosen on the Create Event form's
/// circular markers. The card hides a button the poster turned off, and both
/// handlers below re-check the same flag before opening anything: the XAML
/// binding is what a viewer SEES, the guard is what actually holds, so a
/// future layout that surfaces these buttons another way cannot quietly
/// reopen an event the poster closed.
/// </summary>
public partial class EventsWindow : Window
{
    // One entries window per event at most - clicking View Entries again
    // brings the open one forward rather than stacking duplicates, matching
    // MainWindow's per-Pokemon history windows.
    private readonly Dictionary<string, EventEntriesWindow> _openEntryWindows = new(StringComparer.Ordinal);

    public EventsWindow()
    {
        InitializeComponent();
        DataContext = new EventsViewModel();
    }

    private void ViewEntriesButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: GuildEventCardItem card })
            return;

        // §107: the button is hidden on posts with this turned off, so this
        // is belt-and-braces rather than a path a viewer can reach today.
        if (!card.AllowViewEntries)
            return;

        if (_openEntryWindows.TryGetValue(card.Id, out EventEntriesWindow? existing))
        {
            existing.Activate();
            return;
        }

        var window = new EventEntriesWindow(card.Id, card.Title);

        _openEntryWindows[card.Id] = window;
        window.Closed += (_, _) => _openEntryWindows.Remove(card.Id);

        // §189: unowned - see WindowRegistry.ShowUnowned.
        WindowRegistry.ShowUnowned(window, this);
    }

    private async void SubmitPokemonButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: GuildEventCardItem card })
            return;

        // §107: same guard as View Entries - a post with submissions turned
        // off cannot be submitted to even if something reaches this handler.
        if (!card.AllowPokemonSubmissions)
            return;

        var dialog = new SubmitPokemonWindow(card.Id, card.Title);

        bool submitted = await dialog.ShowDialog<bool?>(this) == true;

        if (!submitted || DataContext is not EventsViewModel vm)
            return;

        // The cards are built once and never mutated, so the new entry count
        // reaches the View Entries button by rebuilding the board.
        vm.RefreshBoard();

        // An entries window already open for this event won't have heard
        // about the submission either.
        if (_openEntryWindows.TryGetValue(card.Id, out EventEntriesWindow? entriesWindow) &&
            entriesWindow.DataContext is EventEntriesViewModel entriesVm)
        {
            // §143: asynchronous now - it pulls the server's list first.
            await entriesVm.Refresh();
        }
    }
}
