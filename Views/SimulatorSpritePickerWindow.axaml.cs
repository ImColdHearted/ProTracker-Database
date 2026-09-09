using Avalonia.Controls;
using Avalonia.Interactivity;
using Foot_Tracker.ViewModels;

namespace Foot_Tracker.Views;

/// <summary>
/// §199. The Simulator's sprite picker dialog. One click on a card is the
/// pick, which closes with true; the caller reads the view model's
/// SelectedPath. Cancel closes with false - the same bool? contract every
/// other Simulator dialog uses.
/// </summary>
public partial class SimulatorSpritePickerWindow : Window
{
    bool confirmWired;

    public SimulatorSpritePickerWindow()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            if (!confirmWired && DataContext is SimulatorSpritePickerViewModel viewModel)
            {
                confirmWired = true;
                viewModel.Confirmed += () => Close(true);
            }
        };
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
