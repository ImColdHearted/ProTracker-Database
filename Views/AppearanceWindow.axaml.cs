using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Foot_Tracker.ViewModels;

namespace Foot_Tracker.Views;

public partial class AppearanceWindow : Window
{
    public AppearanceWindow()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            if (DataContext is AppearanceViewModel vm)
            {
                vm.SavedSuccessfully += () => Close(true);
            }
        };
    }

    // Replaces AppearanceForm's SelectCustomImageButton_Click (OpenFileDialog).
    private async void SelectCustomImageButton_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
            return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select a custom background",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Image Files")
                {
                    Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp" }
                }
            }
        });

        if (files.Count == 0)
            return;

        string? localPath = files[0].TryGetLocalPath();
        if (localPath is null)
            return;

        if (DataContext is AppearanceViewModel vm)
        {
            vm.SetCustomBackground(localPath);
        }
    }

    // Opens the "Create Gradient" dialog (see CustomGradientViewModel) - same
    // reasoning as SelectCustomImageButton_Click above for why this lives in
    // code-behind rather than a ViewModel command: showing a child Window
    // needs a Window/TopLevel reference the ViewModel shouldn't hold directly.
    private async void CreateGradientButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AppearanceViewModel vm)
            return;

        var dialogVm = new CustomGradientViewModel(vm.GradientColorsSeed, vm.GradientDirectionSeed);

        var dialog = new CustomGradientWindow { DataContext = dialogVm };

        bool confirmed = await dialog.ShowDialog<bool?>(this) == true;

        if (confirmed)
        {
            vm.SetCustomGradient(dialogVm.Colors.Select(slot => slot.Color).ToList(), dialogVm.SelectedDirection);
        }
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }
}
