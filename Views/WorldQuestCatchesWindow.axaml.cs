using Avalonia.Controls;
using Avalonia.Interactivity;
using Foot_Tracker.Services;

namespace Foot_Tracker.Views;

/// <summary>§254. The list of every catch counted for the running World
/// Quest, with a Remove on each row - what replaced the panel's Remove Last,
/// which could only ever take the newest, when a bad read is not always the
/// newest by the time it is noticed. Holds no state of its own: the opener
/// (MainWindow.WorldQuestCatchesButton_Click) sets its DataContext to the
/// quest view model the stats panel is already showing, and the list and
/// the Remove command are that view model's.
///
/// Closes itself when World Quest mode is left. The view model it shows is
/// stopped at that moment (its quest forgotten, its list emptied), and a
/// window over an empty list for a quest that is no longer being hunted
/// would only invite a click that does nothing.</summary>
public partial class WorldQuestCatchesWindow : Window
{
    public WorldQuestCatchesWindow()
    {
        InitializeComponent();

        // Raised on the UI thread - see WorldQuestMode.ActiveChanged.
        WorldQuestMode.ActiveChanged += OnModeChanged;
    }

    private void OnModeChanged()
    {
        if (!WorldQuestMode.IsActive)
            Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        WorldQuestMode.ActiveChanged -= OnModeChanged;
        base.OnClosed(e);
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();
}
