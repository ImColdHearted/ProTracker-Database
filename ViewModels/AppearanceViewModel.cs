using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foot_Tracker.Models;
using Foot_Tracker.Services;

namespace Foot_Tracker.ViewModels;

public sealed record BackgroundChoiceOption(string Id, Bitmap? Thumbnail);

/// <summary>
/// Ported from AppearanceForm.cs. WinForms' ColorDialog / OpenFileDialog become
/// Avalonia's built-in ColorPicker control and IStorageProvider file picker
/// (wired up from AppearanceWindow.axaml.cs, since file/color pickers need a
/// TopLevel reference that a ViewModel shouldn't hold directly).
///
/// Background has four modes. The first three share AppearanceSettings.UseCustomBackground:
///   - Preset gradient (UseCustomBackground = false, BackgroundId = preset name)
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
    private static readonly string[] PresetIds =
        ["Midnight", "Blood", "Slate", "Pride", "Pink", "Violet"];

    // A curated, cross-platform-safe list rather than enumerating installed system
    // fonts - Avalonia falls back gracefully (not an exception) if a named font
    // isn't present on a given machine, so listing a few common ones is safe even
    // if they don't all resolve identically on every OS. "Inter" is guaranteed
    // present everywhere since it ships via the Avalonia.Fonts.Inter package.
    private static readonly string[] SystemFontFamilyNames =
        ["Default", "Inter", "Arial", "Segoe UI", "Consolas", "Comic Sans MS", "Times New Roman", "Courier New"];

    private readonly AppearanceSettings _workingSettings = AppearanceSettingsRepository.Load();

    public List<BackgroundChoiceOption> BackgroundChoices { get; }

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

    public IReadOnlyList<string> BorderShadowOptions { get; } =
        ThemeManager.BorderShadowCatalog.Keys.ToList();

    public IReadOnlyList<string> TextShadowOptions { get; } =
        ThemeManager.TextShadowCatalog.Keys.ToList();

    [ObservableProperty] private string? selectedBackgroundId;
    [ObservableProperty] private Bitmap? previewBackgroundImage;
    [ObservableProperty] private IBrush previewBackgroundBrush = Brushes.Black;
    [ObservableProperty] private Color textColor;
    [ObservableProperty] private Color borderColor;
    [ObservableProperty] private Color backgroundCustomColor;
    [ObservableProperty] private string selectedFontFamily = "Default";
    [ObservableProperty] private FontFamily previewFontFamily = ThemeManager.BuildFontFamily("Default");
    [ObservableProperty] private string selectedFontSize = "Default";
    [ObservableProperty] private double previewFontSize = ThemeManager.BuildFontSize("Default");
    [ObservableProperty] private string selectedBorderShadow = "None";
    [ObservableProperty] private Color spriteBoxBackgroundColor;
    [ObservableProperty] private Color boxShadowColor;
    [ObservableProperty] private string selectedTextShadow = "None";
    [ObservableProperty] private Color textShadowColor;
    [ObservableProperty] private IEffect? previewTextShadowEffect;
    [ObservableProperty] private string previewTitleText = "Pro Tracker & Database";
    [ObservableProperty] private string? saveError;
    [ObservableProperty] private bool hasSaveError;

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

    // These only affect the hunting-sprite border boxes on MainWindow (see
    // ThemeManager.BorderShadowCatalog and the Border.huntingSprite style in
    // App.axaml) - no live preview here since this dialog's preview panel
    // doesn't include a sprite box.
    partial void OnSelectedBorderShadowChanged(string value) =>
        _workingSettings.BorderShadowName = value;

    partial void OnSpriteBoxBackgroundColorChanged(Color value) =>
        _workingSettings.SpriteBoxBackgroundColorArgb = AppearanceSettings.ToArgbInt(value);

    partial void OnBoxShadowColorChanged(Color value) =>
        _workingSettings.BoxShadowColorArgb = AppearanceSettings.ToArgbInt(value);

    // Text Shadow feeds a live preview (unlike Border Shadow/Sprite Background
    // above, which don't - this dialog's preview panel has no sprite box, but
    // it does have the sample text Text Shadow is meant to affect). A "Text
    // Highlight" toggle briefly lived here too - removed after trying it out,
    // since a TextBlock's Background fills its whole layout box rather than
    // hugging the glyphs, which didn't look good in practice.
    partial void OnSelectedTextShadowChanged(string value)
    {
        _workingSettings.TextShadowName = value;
        RefreshTextEffectPreview();
    }

    partial void OnTextShadowColorChanged(Color value)
    {
        _workingSettings.TextShadowColorArgb = AppearanceSettings.ToArgbInt(value);
        RefreshTextEffectPreview();
    }

    private void RefreshTextEffectPreview()
    {
        PreviewTextShadowEffect = ThemeManager.BuildTextShadowEffect(SelectedTextShadow, TextShadowColor);
    }

    private string? _pendingCustomImagePath;

    /// <summary>Raised when Save completes successfully - the View closes itself.</summary>
    public event Action? SavedSuccessfully;

    public AppearanceViewModel()
    {
        BackgroundChoices = PresetIds
            .Select(id => new BackgroundChoiceOption(id, LoadThumbnail(id)))
            .ToList();

        TextColor = _workingSettings.TextColor;
        BorderColor = _workingSettings.BorderColor;
        BackgroundCustomColor = AppearanceSettings.FromArgbInt(_workingSettings.CustomBackgroundColorArgb);
        SelectedBackgroundId = _workingSettings.UseCustomBackground || _workingSettings.UseCustomGradient
            ? null
            : _workingSettings.BackgroundId;
        SelectedFontFamily = _workingSettings.FontFamilyName;
        SelectedFontSize = _workingSettings.FontSizeName;
        SelectedBorderShadow = _workingSettings.BorderShadowName;
        SpriteBoxBackgroundColor = _workingSettings.SpriteBoxBackgroundColor;
        BoxShadowColor = _workingSettings.BoxShadowColor;
        SelectedTextShadow = _workingSettings.TextShadowName;
        TextShadowColor = _workingSettings.TextShadowColor;

        RefreshPreview();
        RefreshTextEffectPreview();
    }

    private static Bitmap? LoadThumbnail(string backgroundId)
    {
        string path = ThemeManager.GetBackgroundPath(backgroundId);
        return File.Exists(path) ? new Bitmap(path) : null;
    }

    [RelayCommand]
    private void ChooseBackground(string backgroundId)
    {
        if (string.IsNullOrWhiteSpace(backgroundId))
            return;

        SelectedBackgroundId = backgroundId;
        _workingSettings.BackgroundId = backgroundId;
        _workingSettings.UseCustomBackground = false;
        _workingSettings.CustomBackgroundPath = string.Empty;
        _workingSettings.UseCustomGradient = false;
        _pendingCustomImagePath = null;

        RefreshPreview();
    }

    /// <summary>Called by AppearanceWindow.axaml.cs after IStorageProvider returns a file.</summary>
    public void SetCustomBackground(string filePath)
    {
        _pendingCustomImagePath = filePath;
        _workingSettings.UseCustomBackground = true;
        _workingSettings.CustomBackgroundPath = string.Empty;
        _workingSettings.UseCustomGradient = false;
        SelectedBackgroundId = null;

        RefreshPreview();
    }

    /// <summary>Called by AppearanceWindow.axaml.cs after CustomGradientWindow
    /// returns a confirmed Apply - see CustomGradientViewModel. Takes 2-4
    /// colors (whatever CustomGradientViewModel.Colors held at Apply time).</summary>
    public void SetCustomGradient(IReadOnlyList<Color> colors, string direction)
    {
        SelectedBackgroundId = null;
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
        SelectedBackgroundId = null;
        _workingSettings.UseCustomBackground = true;
        _workingSettings.CustomBackgroundPath = string.Empty;
        _workingSettings.UseCustomGradient = false;
        _pendingCustomImagePath = null;
        _workingSettings.CustomBackgroundColorArgb = AppearanceSettings.ToArgbInt(BackgroundCustomColor);

        RefreshPreview();
    }

    [RelayCommand]
    private void ClearBackground()
    {
        _workingSettings.UseCustomBackground = false;
        _workingSettings.CustomBackgroundPath = string.Empty;
        _workingSettings.UseCustomGradient = false;
        _workingSettings.BackgroundId = "Pride";
        _pendingCustomImagePath = null;
        SelectedBackgroundId = "Pride";

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

    [RelayCommand]
    private void Save()
    {
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

            SavedSuccessfully?.Invoke();
        }
        catch (Exception ex)
        {
            SaveError = $"The appearance settings could not be saved.\n\n{ex.Message}";
        }
    }
}