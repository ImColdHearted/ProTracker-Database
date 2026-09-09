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

        // §209: Apply no longer closes the window - that is the whole point
        // of it being Apply - so the view model's "saved, close yourself"
        // event is gone. What it hands the view instead are the two file
        // pickers a theme file needs, for the same reason the background
        // image picker lives here: a picker needs a TopLevel.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not AppearanceViewModel vm)
                return;

            vm.RequestThemeSavePath = suggested => PickThemeSavePathAsync(suggested);
            vm.RequestThemeOpenPath = PickThemeOpenPathAsync;
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
                // §141: GIFs are accepted and animate once saved - see
                // AnimatedBackgroundService. The preview below shows the first
                // frame, as a still.
                new FilePickerFileType("Images and animated GIFs")
                {
                    Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp", "*.gif" }
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

    // §103 top-bar Reset - routes to the ViewModel's ResetToDefaults; a
    // Click handler only because the top action row mixes two picker Clicks
    // with commands already, and symmetry reads better than a lone binding.
    private void ResetButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AppearanceViewModel vm)
            vm.ResetToDefaultsCommand.Execute(null);
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }

    // §209. Where to write a theme file somebody else can open. Returns null
    // when the picker is dismissed, which the view model treats as "changed
    // your mind" rather than as a failure.
    private async Task<string?> PickThemeSavePathAsync(string suggestedName)
    {
        var topLevel = TopLevel.GetTopLevel(this);

        if (topLevel is null)
            return null;

        IStorageFile? file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save theme to a file you can share",
            SuggestedFileName = suggestedName,
            DefaultExtension = "json",
            ShowOverwritePrompt = true,
            FileTypeChoices = new[] { ThemeFileType }
        });

        return file?.TryGetLocalPath();
    }

    // §209. Which theme file to read. Same null-means-dismissed contract.
    private async Task<string?> PickThemeOpenPathAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);

        if (topLevel is null)
            return null;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open a shared theme",
            AllowMultiple = false,
            FileTypeFilter = new[] { ThemeFileType }
        });

        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    // *.protheme.json first so the app's own files sort to the top, but plain
    // *.json accepted too - somebody will rename one, and refusing to open a
    // file that parses perfectly well would be pedantry.
    private static readonly FilePickerFileType ThemeFileType =
        new("Pro Tracker theme")
        {
            Patterns = new[] { "*.protheme.json", "*.json" }
        };
}
