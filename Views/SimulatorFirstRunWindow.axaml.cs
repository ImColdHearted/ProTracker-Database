using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Foot_Tracker.Services;

namespace Foot_Tracker.Views;

/// <summary>
/// §221. The one-time note in front of the Simulator, explaining that its
/// Pokemon are imported from a running PRO client rather than picked from a
/// list.
///
/// Same ShowAsync-on-the-window shape as §135's BoundariesWarningWindow, but
/// it answers no question, so it returns nothing: there is no Cancel, and
/// the Simulator opens either way. Closing it through the window chrome
/// counts as seen exactly as the button does - it has been in front of the
/// player either way, and re-offering it next launch would read as a fault
/// rather than as help.
/// </summary>
public partial class SimulatorFirstRunWindow : Window
{
    public SimulatorFirstRunWindow()
    {
        InitializeComponent();
    }

    private void GotItButton_Click(object? sender, RoutedEventArgs e) => Close();

    /// <summary>Shows the notice if this machine has not seen it, and does
    /// nothing at all if it has.</summary>
    public static async Task ShowOnceAsync(Window owner)
    {
        if (FirstRunNoticeService.HasSeen(FirstRunNoticeService.SimulatorImport))
            return;

        await new SimulatorFirstRunWindow().ShowDialog(owner);

        FirstRunNoticeService.MarkSeen(FirstRunNoticeService.SimulatorImport);
    }
}
