using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foot_Tracker.Models;
using Foot_Tracker.Services;

namespace Foot_Tracker.ViewModels;

/// <summary>
/// Ported from AppearanceForm.cs. WinForms' ColorDialog / OpenFileDialog become
/// Avalonia's built-in ColorPicker control and IStorageProvider file picker
/// (wired up from AppearanceWindow.axaml.cs, since file/color pickers need a
/// TopLevel reference that a ViewModel shouldn't hold directly).
///
/// Background has four modes. The first three share AppearanceSettings.UseCustomBackground:
///   - The built-in look (UseCustomBackground = false, BackgroundId = Slate).
///     §104 removed the six-preset picker; Slate is the only built-in left, so
///     this mode is now reached through Reset rather than a chooser.
///   - Custom image (UseCustomBackground = true, CustomBackgroundPath set)
///   - Custom solid color (UseCustomBackground = true, CustomBackgroundPath empty,
///     CustomBackgroundColorArgb set from the BackgroundCustomColor slider)
/// A fourth mode, custom gradient (see SetCustomGradient), is gated by its own
/// UseCustomGradient flag instead since it needs 2-4 colors and a direction
/// rather than the single color custom-color mode stores - only one of
/// UseCustomBackground/UseCustomGradient is ever true at a time, enforced by
/// every command below clearing the other mode's flags/paths.
/// </summary>
public sealed partial class AppearanceViewModel : ViewModelBase
{
    // A curated, cross-platform-safe list rather than enumerating installed system
    // fonts - Avalonia falls back gracefully (not an exception) if a named font
    // isn't present on a given machine, so listing a few common ones is safe even
    // if they don't all resolve identically on every OS. "Inter" is guaranteed
    // present everywhere since it ships via the Avalonia.Fonts.Inter package.
    private static readonly string[] SystemFontFamilyNames =
        ["Default", "Inter", "Arial", "Segoe UI", "Consolas", "Comic Sans MS", "Times New Roman", "Courier New"];

    private readonly AppearanceSettings _workingSettings = AppearanceSettingsRepository.Load();

    // Read by AppearanceWindow.axaml.cs when opening the Create Gradient
    // dialog, so reopening it picks up whatever gradient (or the model's
    // defaults) is already stored instead of always resetting to scratch.
    public IReadOnlyList<Color> GradientColorsSeed => _workingSettings.CustomGradientColors;
    public string GradientDirectionSeed => _workingSettings.CustomGradientDirection;

    // Custom bundled fonts (Assets/Fonts/, registered in ThemeManager.CustomFontCatalog)
    // show up here automatically - no changes needed in this file when adding one.
    public IReadOnlyList<string> FontFamilyOptions { get; } =
        SystemFontFamilyNames.Concat(ThemeManager.CustomFontCatalog.Keys).ToList();

    public IReadOnlyList<string> FontSizeOptions { get; } =
        ThemeManager.FontSizeCatalog.Keys.ToList();

    [ObservableProperty] private Bitmap? previewBackgroundImage;
    [ObservableProperty] private IBrush previewBackgroundBrush = Brushes.Black;
    [ObservableProperty] private Color textColor;
    [ObservableProperty] private Color borderColor;
    [ObservableProperty] private Color backgroundCustomColor;
    [ObservableProperty] private string selectedFontFamily = "Default";
    [ObservableProperty] private FontFamily previewFontFamily = ThemeManager.BuildFontFamily("Default");
    // §133: named, not written out. These two were "Default" - a name the
    // catalog no longer has - and the second called BuildFontSize with it
    // from a FIELD INITIALISER, so the throw would have happened while
    // constructing the view model, taking the whole Appearance window with
    // it rather than showing a wrong size.
    [ObservableProperty] private string selectedFontSize = ThemeManager.DefaultFontSizeName;
    [ObservableProperty] private double previewFontSize =
        ThemeManager.BuildFontSize(ThemeManager.DefaultFontSizeName);
    [ObservableProperty] private Color spriteBoxBackgroundColor;
    [ObservableProperty] private Color buttonColor;
    [ObservableProperty] private Color statsBackgroundColor;
    [ObservableProperty] private Color encountersBackgroundColor;
    [ObservableProperty] private Color headerBackgroundColor;
    [ObservableProperty] private string previewTitleText = "Pro Tracker & Database";
    [ObservableProperty] private string? saveError;
    [ObservableProperty] private bool hasSaveError;

    // §140. Which client's look this window edits, said plainly at the foot
    // of the window - above all when no client is detected yet, because the
    // edits then go to Client 1's file (see Save below for why that is now
    // allowed).
    [ObservableProperty] private string clientNote = string.Empty;

    partial void OnSaveErrorChanged(string? value) => HasSaveError = !string.IsNullOrEmpty(value);

    partial void OnSelectedFontFamilyChanged(string value)
    {
        _workingSettings.FontFamilyName = value;
        PreviewFontFamily = ThemeManager.BuildFontFamily(value);
    }

    partial void OnSelectedFontSizeChanged(string value)
    {
        _workingSettings.FontSizeName = value;
        // Scaled up a bit for the preview title specifically (it's meant to look
        // like a heading), same relative bump as the fixed FontSize="20" it replaces
        // at the Default size (14 * ~1.43 ≈ 20).
        PreviewFontSize = ThemeManager.BuildFontSize(value) * 1.43;
    }

    partial void OnSpriteBoxBackgroundColorChanged(Color value) =>
        _workingSettings.SpriteBoxBackgroundColorArgb = AppearanceSettings.ToArgbInt(value);

    // §104: the preview mocks the main window's own layout, so Button Color,
    // Stats Background and Encounters Background all demonstrate themselves
    // there - these handlers only need to write the value through.
    partial void OnButtonColorChanged(Color value) =>
        _workingSettings.ButtonColorArgb = AppearanceSettings.ToArgbInt(value);

    // §209. Transparent means "decide it for me" rather than "no header" -
    // see AppearanceSettings.HeaderBackgroundColorArgb.
    partial void OnHeaderBackgroundColorChanged(Color value) =>
        _workingSettings.HeaderBackgroundColorArgb = AppearanceSettings.ToArgbInt(value);

    partial void OnStatsBackgroundColorChanged(Color value) =>
        _workingSettings.StatsBackgroundColorArgb = AppearanceSettings.ToArgbInt(value);

    partial void OnEncountersBackgroundColorChanged(Color value) =>
        _workingSettings.EncountersBackgroundColorArgb = AppearanceSettings.ToArgbInt(value);

    private string? _pendingCustomImagePath;

    public AppearanceViewModel()
    {
        TextColor = _workingSettings.TextColor;
        BorderColor = _workingSettings.BorderColor;
        HeaderBackgroundColor = _workingSettings.HeaderBackgroundColor;
        BackgroundCustomColor = AppearanceSettings.FromArgbInt(_workingSettings.CustomBackgroundColorArgb);
        SelectedFontFamily = _workingSettings.FontFamilyName;
        // §133: normalized, so a saved legacy name selects a real entry
        // instead of leaving the dropdown blank - a ComboBox whose
        // SelectedItem is not in its ItemsSource shows nothing at all.
        SelectedFontSize = ThemeManager.NormalizeFontSizeName(_workingSettings.FontSizeName);
        SpriteBoxBackgroundColor = _workingSettings.SpriteBoxBackgroundColor;
        ButtonColor = _workingSettings.ButtonColor;
        StatsBackgroundColor = _workingSettings.StatsBackgroundColor;
        EncountersBackgroundColor = _workingSettings.EncountersBackgroundColor;

        int activeClient = SessionPersistenceService.ActiveClientNumber;

        ClientNote = activeClient > 0
            ? $"Editing {ClientNamesService.GetDisplayName(activeClient)}'s appearance."
            : $"No PRO client detected yet - this look is saved as {ClientNamesService.GetDisplayName(1)}'s appearance and applies right away.";

        RefreshPreview();
    }

    /// <summary>Called by AppearanceWindow.axaml.cs after IStorageProvider returns a file.</summary>
    public void SetCustomBackground(string filePath)
    {
        _pendingCustomImagePath = filePath;
        _workingSettings.UseCustomBackground = true;
        _workingSettings.CustomBackgroundPath = string.Empty;
        _workingSettings.UseCustomGradient = false;

        RefreshPreview();
    }

    /// <summary>Called by AppearanceWindow.axaml.cs after CustomGradientWindow
    /// returns a confirmed Apply - see CustomGradientViewModel. Takes 2-4
    /// colors (whatever CustomGradientViewModel.Colors held at Apply time).</summary>
    public void SetCustomGradient(IReadOnlyList<Color> colors, string direction)
    {
        _workingSettings.UseCustomBackground = false;
        _workingSettings.CustomBackgroundPath = string.Empty;
        _pendingCustomImagePath = null;
        _workingSettings.UseCustomGradient = true;
        _workingSettings.CustomGradientColorArgbs = colors.Select(AppearanceSettings.ToArgbInt).ToList();
        _workingSettings.CustomGradientDirection = direction;

        RefreshPreview();
    }

    /// <summary>Switches to a plain custom-color background, using whatever the
    /// BackgroundCustomColor slider is currently set to.</summary>
    [RelayCommand]
    private void UseCustomColorBackground()
    {
        _workingSettings.UseCustomBackground = true;
        _workingSettings.CustomBackgroundPath = string.Empty;
        _workingSettings.UseCustomGradient = false;
        _pendingCustomImagePath = null;
        _workingSettings.CustomBackgroundColorArgb = AppearanceSettings.ToArgbInt(BackgroundCustomColor);

        RefreshPreview();
    }

    /// <summary>§103 "Reset": puts every control back to the application
    /// defaults (a fresh AppearanceSettings). Nothing is saved until Apply -
    /// Cancel still walks away from even a reset.</summary>
    [RelayCommand]
    private void ResetToDefaults() => LoadFrom(new AppearanceSettings());

    /// <summary>§209 "Revert": re-reads the saved appearance from disk,
    /// discarding unsaved edits.
    ///
    /// This used to be what the Load button did. §209 gave Load to theme
    /// files, so the command stays - it is the only way back to what you had
    /// before you started fiddling, since Reset goes to the app defaults
    /// instead - and the window reaches it from the Reset row.</summary>
    [RelayCommand]
    private void LoadSaved() => LoadFrom(AppearanceSettingsRepository.Load());

    private void LoadFrom(AppearanceSettings source)
    {
        // Background mode first (these fields aren't observable - the
        // observable properties below write THROUGH to _workingSettings via
        // their own partial handlers, so ordering matters only for these).
        _workingSettings.BackgroundId = source.BackgroundId;
        _workingSettings.UseCustomBackground = source.UseCustomBackground;
        _workingSettings.CustomBackgroundPath = source.CustomBackgroundPath;
        _workingSettings.UseCustomGradient = source.UseCustomGradient;
        _workingSettings.CustomGradientColorArgbs = new List<int>(source.CustomGradientColorArgbs);
        _workingSettings.CustomGradientDirection = source.CustomGradientDirection;
        _pendingCustomImagePath = null;

        TextColor = source.TextColor;
        BorderColor = source.BorderColor;
        BackgroundCustomColor = AppearanceSettings.FromArgbInt(source.CustomBackgroundColorArgb);
        SelectedFontFamily = source.FontFamilyName;
        SelectedFontSize = ThemeManager.NormalizeFontSizeName(source.FontSizeName);
        SpriteBoxBackgroundColor = source.SpriteBoxBackgroundColor;
        ButtonColor = source.ButtonColor;
        StatsBackgroundColor = source.StatsBackgroundColor;
        EncountersBackgroundColor = source.EncountersBackgroundColor;
        HeaderBackgroundColor = source.HeaderBackgroundColor;

        SaveError = null;
        RefreshPreview();
    }

    /// <summary>Drops a custom image/color/gradient and goes back to the one
    /// built-in background (§104: Slate, formerly one preset among six). Kept -
    /// and moved up beside the other background actions - because with the
    /// preset picker gone this is the only way back to the built-in look that
    /// doesn't also reset every color the way ResetToDefaults does.</summary>
    [RelayCommand]
    private void ClearBackground()
    {
        _workingSettings.UseCustomBackground = false;
        _workingSettings.CustomBackgroundPath = string.Empty;
        _workingSettings.UseCustomGradient = false;
        _workingSettings.BackgroundId = AppearanceSettings.DefaultBackgroundId;
        _pendingCustomImagePath = null;

        RefreshPreview();
    }

    partial void OnTextColorChanged(Color value)
    {
        _workingSettings.TextColorArgb = AppearanceSettings.ToArgbInt(value);
    }

    partial void OnBorderColorChanged(Color value)
    {
        _workingSettings.BorderColorArgb = AppearanceSettings.ToArgbInt(value);
    }

    partial void OnBackgroundCustomColorChanged(Color value)
    {
        _workingSettings.CustomBackgroundColorArgb = AppearanceSettings.ToArgbInt(value);

        // Live-update the preview while dragging the slider, but only once the
        // user has actually switched into "custom color" mode - otherwise moving
        // the slider before clicking "Use Custom Color" would prematurely
        // override whatever preset/image is currently selected.
        bool isActiveCustomColorMode =
            _workingSettings.UseCustomBackground &&
            string.IsNullOrWhiteSpace(_pendingCustomImagePath) &&
            string.IsNullOrWhiteSpace(_workingSettings.CustomBackgroundPath);

        if (isActiveCustomColorMode)
        {
            RefreshPreview();
        }
    }

    private void RefreshPreview()
    {
        if (_workingSettings.UseCustomGradient)
        {
            // Bypasses the image-path lookup below entirely - a gradient has
            // no file on disk, just colors and a direction.
            PreviewBackgroundImage = null;
            PreviewBackgroundBrush = ThemeManager.BuildGradientBrush(
                _workingSettings.CustomGradientColors,
                _workingSettings.CustomGradientDirection);
            return;
        }

        string? imagePath = _workingSettings.UseCustomBackground
            ? _pendingCustomImagePath
            : ThemeManager.GetBackgroundPath(_workingSettings.BackgroundId);

        if (!string.IsNullOrWhiteSpace(imagePath) && File.Exists(imagePath))
        {
            PreviewBackgroundImage = new Bitmap(imagePath);
            PreviewBackgroundBrush = Brushes.Transparent;
            return;
        }

        // No image (preset with no gradient art, or custom-color mode) - fall back
        // to a flat brush so the preview never just goes blank.
        PreviewBackgroundImage = null;
        PreviewBackgroundBrush = new SolidColorBrush(_workingSettings.BackgroundColor);
    }

    // ------------------------------------------------------------ §209

    /// <summary>§209. Set by AppearanceWindow.axaml.cs. Asks the user where to
    /// write a theme file, or which one to read - the same reason
    /// SelectCustomImageButton_Click lives in code-behind, that a picker needs
    /// a TopLevel a view model should not hold.</summary>
    public Func<string, Task<string?>>? RequestThemeSavePath { get; set; }

    public Func<Task<string?>>? RequestThemeOpenPath { get; set; }

    /// <summary>§209. What just happened, at the foot of the window beside the
    /// client note. Cleared by the next action.</summary>
    [ObservableProperty] private string statusText = string.Empty;

    [ObservableProperty] private bool hasStatus;

    partial void OnStatusTextChanged(string value) => HasStatus = !string.IsNullOrEmpty(value);

    /// <summary>§209. Writes this client's appearance file and repaints the
    /// whole app, WITHOUT closing - which is the point of having an Apply at
    /// all. Press it as often as you like; each press is a fresh save.
    ///
    /// This is what the Save button did before §209, minus the close. Save now
    /// means "write a file I can send somebody".</summary>
    [RelayCommand]
    private void Apply()
    {
        if (!Persist())
            return;

        StatusText = "Applied.";
    }

    /// <summary>§209. Exports the portable subset of the CURRENT edits - not
    /// the last applied ones - so a theme can be sent without being adopted
    /// first. Colours, gradient, font and font size travel; the picture
    /// background does not, because the image is a file on this disk.</summary>
    [RelayCommand]
    private async Task SaveThemeFile()
    {
        if (RequestThemeSavePath is null)
            return;

        SaveError = null;
        StatusText = string.Empty;

        string suggested = "my-theme" + AppearanceSettingsRepository.ThemeFileExtension;

        string? path = await RequestThemeSavePath(suggested);

        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            AppearanceSettingsRepository.ExportTheme(
                _workingSettings, path, Path.GetFileNameWithoutExtension(path));

            StatusText = $"Theme saved to {Path.GetFileName(path)}. The picture background is not included - colours, gradient and font are.";
        }
        catch (Exception ex)
        {
            SaveError = $"The theme file could not be written.\n\n{ex.Message}";
        }
    }

    /// <summary>§209. Reads a shared theme into the pickers and the preview and
    /// stops there. Nothing is written until Apply, so a theme you dislike
    /// costs you nothing - Cancel walks away and your own look is untouched.
    ///
    /// Merged onto the current settings rather than replacing them, so the
    /// picture background and everything else the file does not carry survive.</summary>
    [RelayCommand]
    private async Task LoadThemeFile()
    {
        if (RequestThemeOpenPath is null)
            return;

        SaveError = null;
        StatusText = string.Empty;

        string? path = await RequestThemeOpenPath();

        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            LoadFrom(AppearanceSettingsRepository.ImportTheme(path, _workingSettings));

            StatusText = $"Loaded {Path.GetFileName(path)}. Nothing is saved yet - press Apply to keep it.";
        }
        catch (Exception ex)
        {
            SaveError = $"That theme could not be loaded.\n\n{ex.Message}";
        }
    }

    /// <summary>§209. The write itself, shared by Apply. True when it worked.</summary>
    private bool Persist()
    {
        // Per-client appearance is keyed by SessionPersistenceService.AppearanceClientNumber,
        // which falls back to "1" before this tracker window has locked onto a real PRO
        // client (see that property's own remarks). §85 refused to save in that state,
        // because the file written would be appearance-client1.json even when this
        // window was about to become client 2/3/4, and nothing moves it afterwards.
        // §140 lifts that refusal by request - players who open Appearance before the
        // game hit that wall every time - and accepts the consequence it guarded
        // against: with no client detected, Apply writes Client 1's appearance, which
        // is the look this window is already showing (ThemeManager loads by the same
        // fallback) and the one it keeps if client 1 is what locks in. A window that
        // later locks onto client 2/3/4 switches to that client's own file, as it
        // always did, and ClientNote at the foot of the window says which file the
        // Apply button writes before it is pressed.
        try
        {
            if (_workingSettings.UseCustomBackground &&
                !string.IsNullOrWhiteSpace(_pendingCustomImagePath))
            {
                _workingSettings.CustomBackgroundPath =
                    AppearanceSettingsRepository.SaveCustomBackground(_pendingCustomImagePath);
            }

            AppearanceSettingsRepository.Save(_workingSettings);
            ThemeManager.Reload();

            SaveError = null;

            return true;
        }
        catch (Exception ex)
        {
            SaveError = $"The appearance settings could not be saved.\n\n{ex.Message}";
            return false;
        }
    }
}