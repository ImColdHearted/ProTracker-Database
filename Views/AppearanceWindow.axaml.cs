using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Foot_Tracker.Models;
using Foot_Tracker.ViewModels;

namespace Foot_Tracker.Views;

/// <summary>
/// §381. The Appearance editor's window. Everything that needs a TopLevel -
/// the file pickers, the gradient dialog - lives here, as it did before;
/// so do the canvas clicks, which read the clicked element's Tag and hand
/// it to the view model. Nothing here holds appearance state.
/// </summary>
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

    private AppearanceViewModel? ViewModel => DataContext as AppearanceViewModel;

    // §386. The tracker window this editor was opened over is the owner;
    // once open, ask it how big its panels are and tell the inspector.
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (Owner is MainWindow main && ViewModel is { } vm)
            vm.SetMeasuredSizes(main.MeasureAppearancePanels());
    }

    // §381. Ctrl+Z / Ctrl+Y for the history, unless a text box has the
    // keyboard - the search box's own undo is the one the user means then.
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Handled || ViewModel is not { } vm)
            return;

        if (e.Source is TextBox)
            return;

        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (ctrl && e.Key == Key.Z && !shift)
        {
            if (vm.UndoCommand.CanExecute(null))
                vm.UndoCommand.Execute(null);
            e.Handled = true;
        }
        else if ((ctrl && e.Key == Key.Y) || (ctrl && shift && e.Key == Key.Z))
        {
            if (vm.RedoCommand.CanExecute(null))
                vm.RedoCommand.Execute(null);
            e.Handled = true;
        }
    }

    // §381. A click on the canvas. Each selectable region carries its
    // element's name in Tag; the click is marked handled so the window
    // background behind it - itself selectable - does not also fire.
    private void Element_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ViewModel is not { } vm || sender is not Control control)
            return;

        if (!e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
            return;

        if (control.Tag is string tag && Enum.TryParse(tag, out AppearanceElementKind element))
        {
            vm.SelectElement(element);
            e.Handled = true;
        }
    }

    // §381. A click on a library card makes that picture the background.
    // The card's remove button is a Button, which handles its own press
    // before it can reach here.
    private void AssetCard_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ViewModel is not { } vm || sender is not Control control)
            return;

        if (!e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
            return;

        if (control.DataContext is AppearanceAssetItem item)
        {
            vm.UseAssetCommand.Execute(item);
            e.Handled = true;
        }
    }

    // Replaces AppearanceForm's SelectCustomImageButton_Click (OpenFileDialog).
    // §381: several files at once, and each goes into the library.
    // §384: a button that names a target in its Tag (StatsFrame, say) points
    // the library's picker there first, so the file lands where the button
    // said rather than wherever the picker last was.
    private async void AddPictureButton_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
            return;

        if (sender is Control { Tag: string tag } &&
            Enum.TryParse(tag, out AssetTargetKind target) &&
            ViewModel is { } targetVm)
        {
            targetVm.PointAssetTargetAt(target);
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add pictures to the library",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                // §141: GIFs are accepted and animate once saved - see
                // AnimatedBackgroundService. The canvas shows the first
                // frame, as a still.
                new FilePickerFileType("Images and animated GIFs")
                {
                    Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp", "*.gif" }
                }
            }
        });

        if (files.Count == 0)
            return;

        var paths = new List<string>();

        foreach (IStorageFile file in files)
        {
            string? localPath = file.TryGetLocalPath();

            if (localPath is not null)
                paths.Add(localPath);
        }

        if (paths.Count > 0 && ViewModel is { } vm)
            vm.ImportAssets(paths);
    }

    // §385. Font files for UserFontService: several at once, .ttf and .otf.
    private async void AddFontButton_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
            return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add font files",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Fonts")
                {
                    Patterns = new[] { "*.ttf", "*.otf" }
                }
            }
        });

        if (files.Count == 0)
            return;

        var paths = new List<string>();

        foreach (IStorageFile file in files)
        {
            string? localPath = file.TryGetLocalPath();

            if (localPath is not null)
                paths.Add(localPath);
        }

        if (paths.Count > 0 && ViewModel is { } vm)
            vm.ImportFonts(paths);
    }

    // Opens the "Create Gradient" dialog (see CustomGradientViewModel) - same
    // reasoning as AddPictureButton_Click above for why this lives in
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
