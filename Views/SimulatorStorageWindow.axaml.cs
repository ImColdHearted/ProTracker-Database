using Avalonia.Controls;
using Avalonia.Interactivity;
using Foot_Tracker.ViewModels;

namespace Foot_Tracker.Views;

/// <summary>
/// §162. The storage picker dialog. §203: picking a card no longer closes
/// it - Done does, and the caller reads the view model's Picked list
/// however the window was dismissed. A single-pick caller (Replace from
/// Storage) sets AllowMultiple false, and then the first click closes it
/// through the same Confirmed event this always used.
/// </summary>
public partial class SimulatorStorageWindow : Window
{
    bool confirmWired;

    public SimulatorStorageWindow()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            if (!confirmWired && DataContext is SimulatorStorageViewModel viewModel)
            {
                confirmWired = true;
                viewModel.Confirmed += () => Close(true);
            }
        };
    }

    // §203: what was Cancel. A click on a Pokemon banks it rather than
    // closing the window, so this is what ends the visit; the caller reads
    // the view model's Picked list whichever way it was dismissed, because
    // closing with the X must not throw away picks already made.
    private void Done_Click(object? sender, RoutedEventArgs e)
    {
        Close(true);
    }
}
