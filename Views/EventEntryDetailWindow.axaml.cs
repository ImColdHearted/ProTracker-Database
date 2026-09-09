using Avalonia.Controls;
using Foot_Tracker.Models;
using Foot_Tracker.ViewModels;

namespace Foot_Tracker.Views;

/// <summary>
/// One Events-board submission in full (§106) - opened by clicking a row in
/// EventEntriesWindow. Self-constructs its ViewModel from the entry it was
/// handed, the same pattern SessionEncounterHistoryWindow uses.
/// </summary>
public partial class EventEntryDetailWindow : Window
{
    public EventEntryDetailWindow(EventEntry entry)
    {
        InitializeComponent();
        DataContext = new EventEntryDetailViewModel(entry);
        Title = $"Entry - {entry.Username}";
    }
}
