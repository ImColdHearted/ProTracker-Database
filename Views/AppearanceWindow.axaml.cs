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

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }
}
