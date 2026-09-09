using Avalonia.Controls;
using Avalonia.Input;
using Foot_Tracker.Services;
using Foot_Tracker.ViewModels;

namespace Foot_Tracker.Views;

/// <summary>
/// The §106 entries table for one Events post - see EventEntriesViewModel.
/// Clicking any cell in a row opens that entry's detail window, the same
/// click-the-row-to-drill-in shape the session-encounter tables use (every
/// cell carries the handler because a DataGrid cell swallows the press
/// before it reaches the row).
/// </summary>
public partial class EventEntriesWindow : Window
{
    // One detail window per entry at most - clicking the same row again
    // brings the existing window forward instead of stacking duplicates,
    // matching MainWindow's per-Pokemon history windows.
    private readonly Dictionary<string, EventEntryDetailWindow> _openDetails = new(StringComparer.Ordinal);

    public EventEntriesWindow(string eventId, string eventTitle)
    {
        InitializeComponent();
        DataContext = new EventEntriesViewModel(eventId, eventTitle);
    }

    private void EntryRow_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: EventEntryRow row })
            return;

        if (_openDetails.TryGetValue(row.EntryId, out EventEntryDetailWindow? existing))
        {
            existing.Activate();
            return;
        }

        var window = new EventEntryDetailWindow(row.Entry);

        _openDetails[row.EntryId] = window;
        window.Closed += (_, _) => _openDetails.Remove(row.EntryId);

        // §189: unowned - see WindowRegistry.ShowUnowned.
        WindowRegistry.ShowUnowned(window, this);
    }
}
